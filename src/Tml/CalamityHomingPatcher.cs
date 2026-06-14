using TerrariaTrainer.Memory;

namespace TerrariaTrainer.Tml;

/// <summary>
/// "Homing projectiles" the clean way — flip Calamity's Grape Beer homing FLAG on permanently, without
/// the Grape Beer BUFF (so none of the alcohol damage penalty). Drinking Grape Beer makes every friendly
/// projectile home; that's driven by <c>CalamityPlayer.grapeBeer</c> (a bool), set by the buff each frame.
/// <c>CalamityPlayer.ResetEffects</c> (runs every frame, always JIT'd) resets it via
/// <c>mov byte [calPlayer + grapeBeerOff], 0</c>. We patch that one immediate from 0 → 1, so the game itself
/// holds the flag true every frame in-game — no buff, no debuff, no external write race / flicker, and
/// Calamity's own AI homes the projectiles. Client-side, Calamity-only. Method addr + field offset resolved
/// live (version-proof); re-asserted across tiered-JIT relocation; the original 0 restored on disable.
/// </summary>
public sealed class CalamityHomingPatcher
{
    private const string Type = "CalamityMod.CalPlayer.CalamityPlayer";
    private const string Method = "ResetEffects";

    private readonly TmlEngine _engine;
    private readonly object _lock = new();
    private volatile bool _enabled;
    private string _status = "off";

    private volatile int _state = -1;   // -1 resolving, 0 unavailable (no Calamity), 1 ready
    private volatile bool _resolving;
    private int _flagOff;               // grapeBeer offset from the CalamityPlayer base

    private ulong _addr;                // current ResetEffects code address
    private int _immPos = -1;           // offset of the imm8 we flip
    private byte _orig;                 // original imm (0)

    public CalamityHomingPatcher(TmlEngine engine) => _engine = engine;
    public bool Enabled => _enabled;
    public string Status { get { lock (_lock) return _status; } }

    public void Enable() { lock (_lock) _enabled = true; ResolveAsync(); }
    public void Disable() { lock (_lock) { _enabled = false; Restore(); _status = "off"; } }
    public void Clear() { lock (_lock) { _state = -1; _addr = 0; _immPos = -1; } }

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
                var r = TmlDiscovery.ResolveCalamityFields(pid, "grapeBeer");
                if (r is { } v && v.offs.TryGetValue("grapeBeer", out var off)) { _flagOff = off; _state = 1; }
                else _state = 0;
            }
            catch { _state = 0; }
            _resolving = false;
        });
    }

    /// <summary>Called from the GUI background (slow) loop; re-asserts across tiered-JIT relocation.</summary>
    public void Tick()
    {
        if (!_enabled) return;
        try { lock (_lock) { if (_enabled) Locate(); } } catch { }
    }

    private void Locate()
    {
        if (_state == 0) { _status = "unavailable (no Calamity)"; return; }
        if (_state == -1) { _status = "checking for Calamity…"; return; }
        var mem = _engine.Mem; var proc = _engine.Proc;
        if (mem == null || proc == null) { _status = "not attached"; return; }

        ulong addr = TmlDiscovery.ResolveMethodAddr(proc.Id, Type, Method);
        if (addr == 0) { _status = "ResetEffects not jitted"; return; }
        if (addr == _addr && _immPos >= 0 && IsByte(mem, 1)) { _status = "homing on (Grape Beer flag forced)"; return; }

        Restore();
        if (!Install(mem, addr)) return;
        _status = "homing on (Grape Beer flag forced)";
    }

    // Find `mov byte [base+grapeBeerOff], 0` (C6 /0) and flip the imm8 to 1.
    private bool Install(ProcessMemory mem, ulong addr)
    {
        byte[] code; try { code = mem.ReadBytes((IntPtr)addr, 0x2600); } catch { _status = "read failed"; return false; }
        var disp = BitConverter.GetBytes(_flagOff);
        int pos = -1;
        for (int i = 0; i + 7 <= code.Length; i++)
        {
            if (code[i] != 0xC6) continue;                       // mov r/m8, imm8
            byte md = code[i + 1]; if ((md & 0xC0) != 0x80 || ((md >> 3) & 7) != 0 || (md & 7) == 4) continue; // [reg+disp32], /0
            if (code[i + 2] != disp[0] || code[i + 3] != disp[1] || code[i + 4] != disp[2] || code[i + 5] != disp[3]) continue;
            if (code[i + 6] != 0x00) continue;                   // the reset-to-false store
            pos = i + 6; break;
        }
        if (pos < 0) { _status = "grapeBeer reset not found"; return false; }
        _orig = 0; _addr = addr; _immPos = pos;
        mem.WriteBytes((IntPtr)(addr + (ulong)pos), new byte[] { 0x01 });
        return true;
    }

    private bool IsByte(ProcessMemory mem, byte val)
    { try { return mem.ReadBytes((IntPtr)(_addr + (ulong)_immPos), 1)[0] == val; } catch { return false; } }

    private void Restore()
    {
        var mem = _engine.Mem;
        if (mem != null && _addr != 0 && _immPos >= 0) { try { mem.WriteBytes((IntPtr)(_addr + (ulong)_immPos), new[] { _orig }); } catch { } }
        _addr = 0; _immPos = -1;
    }
}
