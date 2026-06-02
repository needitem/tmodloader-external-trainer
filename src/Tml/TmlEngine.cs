using System.Diagnostics;
using System.Text;
using TerrariaTrainer.Memory;

namespace TerrariaTrainer.Tml;

/// <summary>
/// tModLoader (64-bit .NET Core) trainer engine. Discovers the static player
/// path + field offsets once via ClrMD, then resolves and edits live via RPM.
/// </summary>
public sealed class TmlEngine : IDisposable
{
    public ProcessMemory? Mem { get; private set; }
    public TmlModel? Model { get; private set; }
    public Process? Proc { get; private set; }
    public CodeInjector? Injector { get; private set; }

    public bool Attached => Mem is { IsAttached: true } && Mem.IsAlive() && Model != null;

    /// <summary>Serialises memory access between the UI thread and the high-frequency freeze writer.</summary>
    public readonly object Sync = new();

    public event Action<string>? Log;

    // .NET x64 object layout constants.
    private const int ArrayData = 0x10;   // MethodTable(8) + length(8)
    private const int StringData = 0xC;    // MethodTable(8) + length(4)
    private static readonly System.Globalization.CultureInfo Inv = System.Globalization.CultureInfo.InvariantCulture;

    /// <summary>Re-run ClrMD discovery (recovers after a world reload / GC move of static base).</summary>
    public bool Rediscover()
    {
        if (Proc == null) return false;
        try
        {
            Injector?.RestoreAll();
            Model = TmlDiscovery.Discover(Proc.Id);
            if (Mem != null) Injector = new CodeInjector(Mem, Model, Proc);
            _playerStamp = 0; _itemNameCache.Clear(); _prefixNameCache.Clear();
            return true;
        }
        catch (Exception ex) { Log?.Invoke("Re-scan failed: " + ex.Message); return false; }
    }

    public void Attach()
    {
        Detach();
        var proc = TmlDiscovery.FindProcess()
                   ?? throw new InvalidOperationException("tModLoader is not running.");
        Proc = proc;
        Log?.Invoke($"Found tModLoader: PID {proc.Id}");
        Log?.Invoke("Discovering player path + field offsets via ClrMD (snapshot)...");
        Model = TmlDiscovery.Discover(proc.Id);
        Log?.Invoke($"  &Main.myPlayer = 0x{Model.StaticMyPlayer:X}");
        Log?.Invoke($"  &Main.player   = 0x{Model.StaticPlayerArray:X}");
        Log?.Invoke($"  Player fields: {Model.PlayerFields.Count}, item fields: {Model.ItemFields.Count}");

        Mem = ProcessMemory.Attach(proc);
        Injector = new CodeInjector(Mem, Model, proc);
        if (Model.ResetEffectsAddr != 0) Log?.Invoke($"  ResetEffects @ 0x{Model.ResetEffectsAddr:X} (injection ready)");
        var pb = PlayerBase();
        if (pb == IntPtr.Zero) Log?.Invoke("WARNING: player not resolved yet (load into a world).");
        else Log?.Invoke($"Player @ 0x{pb.ToInt64():X}");
    }

    // The player pointer is resolved (3 reads) at most once per ~40 ms and cached, so a
    // full grid refresh (100+ field reads in one tick) pays the resolution cost only once.
    private IntPtr _player;
    // 0 = no cached value yet. (Avoid long.MinValue: `now - long.MinValue` overflows.)
    private long _playerStamp;
    private const long PlayerTtlMs = 40;

    /// <summary>
    /// Live local player object address, cached with a short TTL. Reads/writes in the
    /// same UI tick reuse it; the next tick (≥350 ms) re-resolves, so a GC move or world
    /// reload is picked up automatically.
    /// </summary>
    public IntPtr PlayerBase()
    {
        long now = Environment.TickCount64;
        if (_playerStamp != 0 && now - _playerStamp <= PlayerTtlMs) return _player;
        _player = ResolvePlayer();
        _playerStamp = now == 0 ? 1 : now; // never store the 0 sentinel
        return _player;
    }

    private IntPtr ResolvePlayer()
    {
        if (Mem == null || Model == null) return IntPtr.Zero;
        try
        {
            int idx = Mem.ReadInt32((IntPtr)Model.StaticMyPlayer);
            if (idx < 0 || idx > 255) return IntPtr.Zero;
            IntPtr arr = Mem.ReadPtr64((IntPtr)Model.StaticPlayerArray);
            if (arr == IntPtr.Zero) return IntPtr.Zero;
            return Mem.ReadPtr64((IntPtr)(arr.ToInt64() + ArrayData + idx * 8));
        }
        catch { return IntPtr.Zero; }
    }

    // ---- field read/write ----

    public string ReadField(TmlField f)
    {
        var m = Mem; var pb = PlayerBase();
        if (m == null || pb == IntPtr.Zero) return "—";
        IntPtr a = (IntPtr)(pb.ToInt64() + f.Offset);
        try
        {
            return f.Kind switch
            {
                FieldKind.Int32 => m.ReadInt32(a).ToString(),
                FieldKind.UInt32 => m.ReadUInt32(a).ToString(),
                FieldKind.Int16 => m.ReadInt16(a).ToString(),
                FieldKind.Byte => m.ReadByte(a).ToString(),
                FieldKind.SByte => ((sbyte)m.ReadByte(a)).ToString(),
                FieldKind.Boolean => (m.ReadByte(a) != 0).ToString(),
                FieldKind.Int64 => m.ReadInt64(a).ToString(),
                FieldKind.Single => m.ReadFloat(a).ToString("0.###", Inv),
                FieldKind.Double => m.ReadDouble(a).ToString("0.###", Inv),
                FieldKind.String => ReadDotNetString(m.ReadPtr64(a)),
                _ => "(ref)",
            };
        }
        catch { return "err"; }
    }

    public bool WriteField(TmlField f, string text)
    {
        var m = Mem; var pb = PlayerBase();
        if (m == null || pb == IntPtr.Zero) return false;
        IntPtr a = (IntPtr)(pb.ToInt64() + f.Offset);
        try
        {
            switch (f.Kind)
            {
                case FieldKind.Int32: return m.WriteInt32(a, int.Parse(text, Inv));
                case FieldKind.UInt32: return m.WriteUInt32(a, uint.Parse(text, Inv));
                case FieldKind.Int16: return m.WriteInt16(a, short.Parse(text, Inv));
                case FieldKind.Byte: return m.WriteByte(a, byte.Parse(text, Inv));
                case FieldKind.SByte: return m.WriteByte(a, (byte)sbyte.Parse(text, Inv));
                case FieldKind.Boolean: return m.WriteByte(a, (byte)(ParseBool(text) ? 1 : 0));
                case FieldKind.Int64: return m.WriteBytes(a, BitConverter.GetBytes(long.Parse(text, Inv)));
                case FieldKind.Single: return m.WriteFloat(a, float.Parse(text, Inv));
                case FieldKind.Double: return m.WriteDouble(a, double.Parse(text, Inv));
                default: return false;
            }
        }
        catch { return false; }
    }

    private static bool ParseBool(string s) =>
        s.Equals("true", StringComparison.OrdinalIgnoreCase) || s == "1";

    private string ReadDotNetString(IntPtr strObj)
    {
        if (Mem == null || strObj == IntPtr.Zero) return "";
        int len = Mem.ReadInt32((IntPtr)(strObj.ToInt64() + 8));
        if (len <= 0 || len > 4096) return "";
        var bytes = Mem.ReadBytes((IntPtr)(strObj.ToInt64() + StringData), len * 2);
        return Encoding.Unicode.GetString(bytes);
    }

    // ---- buff helpers (int[] arrays) ----

    public (IntPtr id, IntPtr time, int len) BuffArrays()
    {
        var m = Mem; var pb = PlayerBase();
        if (m == null || pb == IntPtr.Zero || Model == null) return (IntPtr.Zero, IntPtr.Zero, 0);
        IntPtr id = m.ReadPtr64((IntPtr)(pb.ToInt64() + Model.BuffTypeOff));
        IntPtr tm = m.ReadPtr64((IntPtr)(pb.ToInt64() + Model.BuffTimeOff));
        int len = id != IntPtr.Zero ? m.ReadInt32((IntPtr)(id.ToInt64() + 8)) : 0;
        return (id, tm, len);
    }

    public IntPtr BuffSlot(IntPtr arr, int i) => (IntPtr)(arr.ToInt64() + ArrayData + i * 4);

    // ---- item / prefix names (from Terraria.Lang, localized, incl. modded) ----

    private readonly Dictionary<int, string> _itemNameCache = new();
    private readonly Dictionary<int, string> _prefixNameCache = new();

    public string ItemName(int type)
    {
        if (type <= 0) return "";
        if (_itemNameCache.TryGetValue(type, out var n)) return n;
        n = ResolveName(Model?.StaticItemNameCache ?? 0, type);
        _itemNameCache[type] = n;
        return n;
    }

    public string PrefixName(int prefix)
    {
        if (prefix <= 0) return "";
        if (_prefixNameCache.TryGetValue(prefix, out var n)) return n;
        n = ResolveName(Model?.StaticPrefixNames ?? 0, prefix);
        _prefixNameCache[prefix] = n;
        return n;
    }

    /// <summary>name = (LocalizedText[] static)[index]._value</summary>
    private string ResolveName(ulong staticCache, int index)
    {
        var m = Mem;
        if (m == null || Model == null || staticCache == 0 || index < 0) return "";
        try
        {
            IntPtr arr = m.ReadPtr64((IntPtr)staticCache);
            if (arr == IntPtr.Zero) return "";
            int len = m.ReadInt32((IntPtr)(arr.ToInt64() + 8));
            if (index >= len) return "";
            IntPtr lt = m.ReadPtr64((IntPtr)(arr.ToInt64() + ArrayData + index * 8));
            if (lt == IntPtr.Zero) return "";
            IntPtr s = m.ReadPtr64((IntPtr)(lt.ToInt64() + Model.LocalizedTextValueOff));
            return s == IntPtr.Zero ? "" : ReadDotNetString(s);
        }
        catch { return ""; }
    }

    // ---- inventory editor ----

    /// <summary>Inventory array length (59 in 1.4.4: 50 main + 4 coin + 4 ammo + 1 mouse).</summary>
    public int InventoryLength()
    {
        var m = Mem; var pb = PlayerBase();
        if (m == null || pb == IntPtr.Zero || Model == null) return 0;
        IntPtr inv = m.ReadPtr64((IntPtr)(pb.ToInt64() + Model.InventoryOff));
        return inv == IntPtr.Zero ? 0 : m.ReadInt32((IntPtr)(inv.ToInt64() + 8));
    }

    public IntPtr InventoryItem(int slot)
    {
        var m = Mem; var pb = PlayerBase();
        if (m == null || pb == IntPtr.Zero || Model == null) return IntPtr.Zero;
        IntPtr inv = m.ReadPtr64((IntPtr)(pb.ToInt64() + Model.InventoryOff));
        if (inv == IntPtr.Zero) return IntPtr.Zero;
        int len = m.ReadInt32((IntPtr)(inv.ToInt64() + 8));
        if (slot < 0 || slot >= len) return IntPtr.Zero;
        return m.ReadPtr64((IntPtr)(inv.ToInt64() + ArrayData + slot * 8));
    }

    public int ItemInt(int slot, string field)
    {
        var m = Mem; var item = InventoryItem(slot);
        if (m == null || item == IntPtr.Zero || !Model!.ItemFields.TryGetValue(field, out int off)) return 0;
        // prefix/favorited are 1-byte; type/stack/maxStack are int.
        return field is "prefix" or "favorited"
            ? m.ReadByte((IntPtr)(item.ToInt64() + off))
            : m.ReadInt32((IntPtr)(item.ToInt64() + off));
    }

    public bool SetItemInt(int slot, string field, int value)
    {
        var m = Mem; var item = InventoryItem(slot);
        if (m == null || item == IntPtr.Zero || !Model!.ItemFields.TryGetValue(field, out int off)) return false;
        return field is "prefix" or "favorited"
            ? m.WriteByte((IntPtr)(item.ToInt64() + off), (byte)value)
            : m.WriteInt32((IntPtr)(item.ToInt64() + off), value);
    }

    /// <summary>Max-stack every inventory item (Item[] at inventoryOff).</summary>
    public int MaxStackInventory()
    {
        var m = Mem; var pb = PlayerBase();
        if (m == null || pb == IntPtr.Zero || Model == null) return 0;
        IntPtr inv = m.ReadPtr64((IntPtr)(pb.ToInt64() + Model.InventoryOff));
        if (inv == IntPtr.Zero) return 0;
        int len = m.ReadInt32((IntPtr)(inv.ToInt64() + 8));
        int count = Math.Min(len, 58);
        int changed = 0;
        for (int slot = 0; slot < count; slot++)
        {
            IntPtr item = m.ReadPtr64((IntPtr)(inv.ToInt64() + ArrayData + slot * 8));
            if (item == IntPtr.Zero) continue;
            int type = m.ReadInt32((IntPtr)(item.ToInt64() + Model.ItemType));
            int maxS = m.ReadInt32((IntPtr)(item.ToInt64() + Model.ItemMaxStack));
            if (type > 0 && maxS > 1)
            {
                m.WriteInt32((IntPtr)(item.ToInt64() + Model.ItemStack), maxS);
                changed++;
            }
        }
        return changed;
    }

    public void Detach()
    {
        try { Injector?.RestoreAll(); } catch { /* process may be gone */ }
        Injector = null;
        Mem?.Dispose();
        Mem = null;
        Model = null;
        Proc = null;
        _playerStamp = 0;
        _itemNameCache.Clear();
        _prefixNameCache.Clear();
    }

    public void Dispose() => Detach();
}
