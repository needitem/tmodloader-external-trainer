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
    /// <summary>
    /// Re-resolve ONE method's current native-code address (cheap, no full re-scan) so a cave/patch
    /// installs at the live code after the .NET tiered JIT relocated it. Updates model.Methods[key].
    /// </summary>
    public void RefreshMethodAddress(string key)
    {
        if (Model == null || Proc == null) return;
        if (!Model.MethodSources.TryGetValue(key, out var src)) return;
        try
        {
            var cur = TmlDiscovery.ResolveCurrentAddresses(Proc.Id, new[] { (key, src.type, src.method) });
            if (cur.TryGetValue(key, out var list) && list.Count > 0)
            {
                int size = Model.Methods.TryGetValue(key, out var m) ? m.size : 0x4000;
                Model.Methods[key] = (list[0], size);
            }
        }
        catch { /* best effort */ }
    }

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

    public void Attach() => Attach(null);

    public void Attach(int? pid)
    {
        Detach();
        var proc = (pid is int p ? System.Diagnostics.Process.GetProcessById(p) : TmlDiscovery.FindProcess())
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
    private IntPtr _inv;
    private long _invStamp;
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

    /// <summary>Live inventory-array address, cached against the same generation as <see cref="PlayerBase"/>
    /// (it moves only when the player object does). Saves the redundant pointer-chase that inventory
    /// reads/writes otherwise repeat — the grid refresh alone did it hundreds of times per tick.</summary>
    public IntPtr InventoryBase()
    {
        var pb = PlayerBase();
        if (pb == IntPtr.Zero || Mem == null || Model == null) return IntPtr.Zero;
        if (_invStamp == _playerStamp && _inv != IntPtr.Zero) return _inv;
        _inv = Mem.ReadPtr64((IntPtr)(pb.ToInt64() + Model.InventoryOff));
        _invStamp = _playerStamp;
        return _inv;
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

    // ---- biome (zone) detection + forcing ----
    // Biomes are bit-flags packed into Player.zone1..zone5 (BitsByte). The game recomputes them
    // each frame from nearby tiles, so forcing = OR the bit in at high frequency.
    private static readonly (int z, byte mask, string name)[] BiomeBits =
    {
        (0,0x01,"Dungeon"),(0,0x02,"Corruption"),(0,0x04,"Hallow"),(0,0x08,"Meteor"),
        (0,0x10,"Jungle"),(0,0x20,"Snow"),(0,0x40,"Crimson"),
        (1,0x20,"Desert"),(1,0x40,"Mushroom"),(1,0x80,"Underground Desert"),
        (2,0x20,"Beach"),(2,0x40,"Rain"),(2,0x80,"Sandstorm"),
        (3,0x40,"Graveyard"),
    };

    private int ZoneOff()
    {
        var f = Model?.PlayerFields.FirstOrDefault(x => x.Name == "zone1");
        return f?.Offset ?? 0xA51;
    }

    /// <summary>Names of every biome zone currently active at the player.</summary>
    public string CurrentBiomeText()
    {
        var m = Mem; var pb = PlayerBase();
        if (m == null || pb == IntPtr.Zero) return "";
        int z = ZoneOff();
        var bytes = m.ReadBytes((IntPtr)(pb.ToInt64() + z), 5);
        var names = new List<string>();
        foreach (var (zi, mask, name) in BiomeBits)
            if ((bytes[zi] & mask) != 0) names.Add(name);
        return names.Count == 0 ? "Forest/Surface" : string.Join(", ", names);
    }

    // ---- block/tile reach (the character's "arm reach" for place/mine/use) ----
    // Effective reach = Player.tileRangeX(static) + Player.blockRange(per-player). blockRange is the
    // Extendo-Grip bonus and is reset every frame in ResetEffects, so it (and the statics) must be
    // re-asserted by the high-freq writer to actually hold.
    private int _blockRangeOff = -2;
    public void SetTileReach(int x, int y)
    {
        var m = Mem;
        if (m == null || Model == null) return;
        if (Model.TileRangeXAddr != 0) m.WriteInt32((IntPtr)Model.TileRangeXAddr, x);
        if (Model.TileRangeYAddr != 0) m.WriteInt32((IntPtr)Model.TileRangeYAddr, y);
        if (_blockRangeOff == -2) _blockRangeOff = FieldOffset("blockRange");
        var pb = PlayerBase();
        if (pb != IntPtr.Zero && _blockRangeOff >= 0) m.WriteInt32((IntPtr)(pb.ToInt64() + _blockRangeOff), Math.Max(x, y));
    }
    public bool HasTileReach => Model?.TileRangeXAddr != 0;

    // ---- Clentaminator spray range ----
    // Spray projectiles (Projectile.aiStyle == 31) decelerate and expire, capping the Clentaminator's
    // reach. While active, the high-freq writer keeps each of MY sprays alive (timeLeft topped up) and
    // re-asserts a constant cruise speed in its current direction, so it flies straight across the world
    // converting tiles the whole way — effectively infinite range. Client-side (the local player owns the
    // spray; the server still applies the tile conversion it requests).
    private const int ProjActive = 0x24, ProjVel = 0x34, ProjAiStyle = 0xD8, ProjTimeLeft = 0xDC, ProjOwner = 0xCC;

    public int BoostSprays(float cruise)
    {
        var m = Mem;
        if (m == null || Model == null || Model.ProjectileArray == 0) return 0;
        IntPtr arr = m.ReadPtr64((IntPtr)Model.ProjectileArray);
        if (arr == IntPtr.Zero) return 0;
        int len = m.ReadInt32((IntPtr)(arr.ToInt64() + 8));
        if (len <= 0 || len > 4000) len = 1000;
        int myPlayer = Model.StaticMyPlayer != 0 ? m.ReadInt32((IntPtr)Model.StaticMyPlayer) : -1;
        // Bulk-read the whole reference array once (8 KB) rather than 1000 individual reads.
        byte[] refs = m.ReadBytes((IntPtr)(arr.ToInt64() + ArrayData), len * 8);
        if (refs.Length < len * 8) return 0;
        int n = 0;
        for (int i = 0; i < len; i++)
        {
            long b = BitConverter.ToInt64(refs, i * 8);
            if (b == 0) continue;
            if (m.ReadByte((IntPtr)(b + ProjActive)) == 0) continue;          // not active
            if (m.ReadInt32((IntPtr)(b + ProjAiStyle)) != 31) continue;       // not a spray
            if (myPlayer >= 0 && m.ReadInt32((IntPtr)(b + ProjOwner)) != myPlayer) continue; // not mine
            m.WriteInt32((IntPtr)(b + ProjTimeLeft), 600);                    // keep it alive
            float vx = m.ReadFloat((IntPtr)(b + ProjVel)), vy = m.ReadFloat((IntPtr)(b + ProjVel + 4));
            float sp = (float)Math.Sqrt(vx * vx + vy * vy);
            if (sp > 0.1f)
            {
                m.WriteFloat((IntPtr)(b + ProjVel), vx / sp * cruise);
                m.WriteFloat((IntPtr)(b + ProjVel + 4), vy / sp * cruise);
            }
            n++;
        }
        return n;
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

    /// <summary>Remove a buff (by id) from the local player's buff array. Called at high freq to
    /// suppress short-lived debuffs (Mana Sickness, etc.) effectively the moment they're applied.</summary>
    public void ClearBuff(int buffId)
    {
        var (id, tm, len) = BuffArrays();
        if (id == IntPtr.Zero || Mem == null) return;
        if (len <= 0 || len > 64) len = 44;
        for (int i = 0; i < len; i++)
            if (Mem.ReadInt32(BuffSlot(id, i)) == buffId)
            { Mem.WriteInt32(BuffSlot(id, i), 0); if (tm != IntPtr.Zero) Mem.WriteInt32(BuffSlot(tm, i), 0); }
    }

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
        IntPtr inv = InventoryBase();
        return inv == IntPtr.Zero ? 0 : m.ReadInt32((IntPtr)(inv.ToInt64() + 8));
    }

    // per-slot ammo "ratchet": remembers the highest stack seen so firing can't drop it
    // (picking up more ammo raises the floor). Count stays put instead of inflating to max.
    private readonly Dictionary<int, (int type, int stack)> _ammoFloor = new();
    public void ResetAmmoFreeze() => _ammoFloor.Clear();

    /// <summary>
    /// "Infinite ammo" without inflating counts: if an ammo stack dropped below what it was
    /// (a shot consumed it), restore it; if it grew (pickup), raise the remembered floor.
    /// Independent of the mod-hooked consume path, so it works in MP too.
    /// </summary>
    public int TopAmmo()
    {
        var m = Mem; var pb = PlayerBase();
        if (m == null || pb == IntPtr.Zero || Model == null) return 0;
        int aOff = Model.ItemFields.GetValueOrDefault("ammo", 0);
        if (aOff == 0) return 0;
        IntPtr inv = InventoryBase();
        if (inv == IntPtr.Zero) return 0;
        int len = m.ReadInt32((IntPtr)(inv.ToInt64() + 8));
        if (len < 58) return 0;
        int tOff = Model.ItemType, sOff = Model.ItemStack;
        int n = 0;
        // only the 4 dedicated ammo slots (54..57) — the ammo you actually have equipped.
        // (Avoids freezing gel/sand/blocks/coins in the main inventory, which are also "ammo".)
        for (int i = 54; i <= 57; i++)
        {
            IntPtr it = m.ReadPtr64((IntPtr)(inv.ToInt64() + ArrayData + i * 8));
            int ty = it == IntPtr.Zero ? 0 : m.ReadInt32((IntPtr)(it.ToInt64() + tOff));
            int am = it == IntPtr.Zero ? 0 : m.ReadInt32((IntPtr)(it.ToInt64() + aOff));
            bool isAmmo = it != IntPtr.Zero && ty != 0 && !(ty >= 71 && ty <= 74) && am != 0 && am != 71;
            if (!isAmmo) { _ammoFloor.Remove(i); continue; }                  // slot no longer holds (non-coin) ammo
            int stk = m.ReadInt32((IntPtr)(it.ToInt64() + sOff));
            if (_ammoFloor.TryGetValue(i, out var prev) && prev.type == ty && stk < prev.stack && stk > 0)
            { m.WriteInt32((IntPtr)(it.ToInt64() + sOff), prev.stack); n++; }   // restore the consumed shot(s)
            else _ammoFloor[i] = (ty, stk);                                    // first sight / pickup / swap -> set floor
        }
        return n;
    }

    public IntPtr InventoryItem(int slot)
    {
        var m = Mem; var pb = PlayerBase();
        if (m == null || pb == IntPtr.Zero || Model == null) return IntPtr.Zero;
        IntPtr inv = InventoryBase();
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

    /// <summary>Real memory offset of a Player field by name, or -1.</summary>
    public int FieldOffset(string name) => Model?.PlayerFields.FirstOrDefault(f => f.Name == name)?.Offset ?? -1;

    /// <summary>Craft Anywhere: mark every crafting station + liquid as nearby (recomputed each
    /// frame, so call at high frequency). _adjTile is a bool[] of station proximity flags.</summary>
    public void SetCraftAnywhere()
    {
        var m = Mem; var pb = PlayerBase();
        if (m == null || pb == IntPtr.Zero) return;
        int adjTileOff = FieldOffset("_adjTile");
        if (adjTileOff >= 0)
        {
            IntPtr arr = m.ReadPtr64((IntPtr)(pb.ToInt64() + adjTileOff));
            if (arr != IntPtr.Zero)
            {
                int len = m.ReadInt32((IntPtr)(arr.ToInt64() + 8));
                for (int i = 0; i < Math.Min(len, 700); i++)
                    m.WriteByte((IntPtr)(arr.ToInt64() + 0x10 + i), 1);
            }
        }
        foreach (var f in new[] { "adjWater", "adjHoney", "adjLava", "alchemyTable" })
        {
            int off = FieldOffset(f);
            if (off >= 0) m.WriteByte((IntPtr)(pb.ToInt64() + off), 1);
        }
    }

    // ---- fast mining/tools: lower the use-time of pickaxes/drills/axes/hammers ----
    // Mining speed for a tool = its useTime/useAnimation. Setting them low = fast mining.
    // Safe (plain item-field writes). Originals are snapshotted so disable restores them.

    private readonly Dictionary<int, (int type, int useTime, int useAnim, int reuse)> _toolSnapshot = new();

    public bool IsToolSlot(int slot)
        => ItemInt(slot, "pick") > 0 || ItemInt(slot, "axe") > 0 || ItemInt(slot, "hammer") > 0;

    /// <summary>Set every inventory mining tool to fast use-time (snapshotting originals once).</summary>
    public int ApplyFastTools(int useTime, int useAnim)
    {
        if (Mem == null || PlayerBase() == IntPtr.Zero) return 0;
        int n = 0;
        for (int slot = 0; slot < 50; slot++)
        {
            int type = ItemInt(slot, "type");
            if (type == 0 || !IsToolSlot(slot)) continue;
            if (!_toolSnapshot.ContainsKey(slot) || _toolSnapshot[slot].type != type)
                _toolSnapshot[slot] = (type, ItemInt(slot, "useTime"), ItemInt(slot, "useAnimation"), ItemInt(slot, "reuseDelay"));
            SetItemInt(slot, "useTime", useTime);
            SetItemInt(slot, "useAnimation", useAnim);
            SetItemInt(slot, "reuseDelay", 0);
            n++;
        }
        return n;
    }

    private int _selectedItemOff = -1;
    private int SelectedItemOff => _selectedItemOff >= 0 ? _selectedItemOff
        : (_selectedItemOff = Model?.PlayerFields.FirstOrDefault(f => f.Name == "selectedItem")?.Offset ?? 0x4C0);

    /// <summary>Lightweight: force the currently-held tool's use-time low (call at high frequency,
    /// because Calamity's HoldItem hook recomputes the held item's stats every frame).</summary>
    public void AssertHeldTool(int useTime, int useAnim)
    {
        var m = Mem; var pb = PlayerBase();
        if (m == null || pb == IntPtr.Zero) return;
        int sel = m.ReadInt32((IntPtr)(pb.ToInt64() + SelectedItemOff));
        if (sel < 0 || sel > 58) return;
        if (ItemInt(sel, "type") == 0 || !IsToolSlot(sel)) return;
        SetItemInt(sel, "useTime", useTime);
        SetItemInt(sel, "useAnimation", useAnim);
        SetItemInt(sel, "reuseDelay", 0);
    }

    /// <summary>High-frequency assert: write every cached tool slot's use-time (no scan).
    /// Matches the verified diag behaviour (all tools, every 2ms).</summary>
    public void AssertCachedTools(int useTime, int useAnim)
    {
        if (Mem == null || PlayerBase() == IntPtr.Zero || _toolSnapshot.Count == 0) return;
        foreach (var slot in _toolSnapshot.Keys)
        {
            SetItemInt(slot, "useTime", useTime);
            SetItemInt(slot, "useAnimation", useAnim);
            SetItemInt(slot, "reuseDelay", 0);
        }
    }

    /// <summary>Restore the original use-time on every tool we changed.</summary>
    public void RestoreFastTools()
    {
        if (Mem != null && PlayerBase() != IntPtr.Zero)
        {
            foreach (var (slot, snap) in _toolSnapshot)
            {
                if (ItemInt(slot, "type") != snap.type) continue; // item changed; skip
                SetItemInt(slot, "useTime", snap.useTime);
                SetItemInt(slot, "useAnimation", snap.useAnim);
                SetItemInt(slot, "reuseDelay", snap.reuse);
            }
        }
        _toolSnapshot.Clear();
    }

    /// <summary>Max-stack every inventory item (Item[] at inventoryOff).</summary>
    public int MaxStackInventory()
    {
        var m = Mem; var pb = PlayerBase();
        if (m == null || pb == IntPtr.Zero || Model == null) return 0;
        IntPtr inv = InventoryBase();
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
