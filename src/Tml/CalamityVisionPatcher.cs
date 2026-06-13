using TerrariaTrainer.Memory;

namespace TerrariaTrainer.Tml;

/// <summary>
/// "Abyss / cave vision" — cancels Calamity's deep-sea and cave screen-darkening by zeroing the
/// CalamityPlayer floats that drive it (<c>abyssDarkness</c>, <c>darknessIntensity</c>, <c>caveDarkness</c>)
/// every writer tick. Those are recomputed each frame from depth, so we just re-zero them after — the same
/// race the value-freezes win. Client-side (it's a local screen effect). Calamity-only: if Calamity isn't
/// loaded the toggle simply reports unavailable. Field offsets + the CalamityPlayer slot are resolved live
/// (load/version independent); the slot is found by MethodTable so a different modPlayers index still works.
/// </summary>
public sealed class CalamityVisionPatcher
{
    private static readonly string[] Fields = { "abyssDarkness", "darknessIntensity", "caveDarkness" };
    private const int ArrayData = 0x10;

    private readonly TmlEngine _engine;
    private volatile bool _enabled;
    private string _status = "off";

    private volatile int _state = -1;   // -1 resolving, 0 unavailable (no Calamity), 1 ready
    private volatile bool _resolving;
    private int _mpOff, _calIdx = -1;
    private ulong _calMT;
    private int[] _offs = System.Array.Empty<int>();

    public CalamityVisionPatcher(TmlEngine engine) => _engine = engine;
    public bool Enabled => _enabled;
    public string Status => _status;

    public void Enable() { _enabled = true; ResolveAsync(); }
    public void Disable() { _enabled = false; _status = "off"; }
    public void Clear() { _state = -1; _calIdx = -1; }   // new process → re-resolve

    private void ResolveAsync()
    {
        if (_state != -1 || _resolving) return;
        int pid = _engine.Proc?.Id ?? -1;
        if (pid < 0) return;
        _resolving = true;
        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                var r = TmlDiscovery.ResolveCalamityFields(pid, Fields);
                if (r is { } v)
                {
                    _mpOff = v.mpOff; _calMT = v.calMT;
                    _offs = System.Array.FindAll(System.Array.ConvertAll(Fields, f => v.offs.TryGetValue(f, out var o) ? o : 0), o => o != 0);
                    _state = _offs.Length > 0 ? 1 : 0;
                }
                else _state = 0;
            }
            catch { _state = 0; }
            _resolving = false;
        });
    }

    /// <summary>Called from the high-frequency writer loop (already holds engine.Sync).</summary>
    public void Tick()
    {
        if (!_enabled) return;
        try
        {
            if (_state == 0) { _status = "unavailable (no Calamity)"; return; }
            if (_state == -1) { _status = "checking for Calamity…"; return; }
            _status = WriteZeros() ? "abyss/cave darkness off" : "Calamity player not found";
        }
        catch { }
    }

    /// <summary>Zero the darkness floats on the local player's CalamityPlayer. Re-reads the pointer chain each
    /// call (GC-move safe); caches the modPlayers slot and re-verifies its MethodTable.</summary>
    private bool WriteZeros()
    {
        var m = _engine.Mem; IntPtr pb = _engine.PlayerBase();
        if (m == null || pb == IntPtr.Zero || _mpOff == 0) return false;
        IntPtr arr = m.ReadPtr64((IntPtr)(pb.ToInt64() + _mpOff));
        if (arr == IntPtr.Zero) return false;
        int len = m.ReadInt32((IntPtr)(arr.ToInt64() + 8));
        if (len <= 0 || len > 4000) return false;
        IntPtr cur = IntPtr.Zero;
        bool Slot(int i)
        {
            cur = m.ReadPtr64((IntPtr)(arr.ToInt64() + ArrayData + i * 8));
            return cur != IntPtr.Zero && (ulong)m.ReadInt64(cur) == _calMT;
        }
        if (_calIdx < 0 || _calIdx >= len || !Slot(_calIdx))
        {
            _calIdx = -1;
            for (int i = 0; i < len; i++) if (Slot(i)) { _calIdx = i; break; }
            if (_calIdx < 0) return false;
        }
        foreach (var off in _offs) m.WriteFloat((IntPtr)(cur.ToInt64() + off), 0f);
        return true;
    }
}
