using TerrariaTrainer.Memory;

namespace TerrariaTrainer.Tml;

/// <summary>
/// "Abyss / cave vision" — cancels Calamity's deep-sea &amp; cave darkness at the ROOT (no flicker) by
/// neutralising the two systems that actually darken the world:
///   1. <c>LightingEffectsSystem.ModifyLightingBrightness</c> — tML's lighting-brightness hook; Calamity
///      reads <c>caveDarkness</c> and scales ALL light down (this is what blacks out the screen AND the
///      minimap). We turn the whole method into an immediate <c>ret</c> so it never touches the scale.
///   2. <c>EnhancedDarknessSystem.AdjustTransmissiveness</c> — lerps light colour toward dark by
///      <c>darknessIntensity</c>. We rewrite the field load into <c>vxorps</c> so the factor is always 0.
/// Both run in-game every frame, so patching them = the game itself computes "no darkness" — far cleaner
/// than racing the value from outside. Client-side, Calamity-only. Addresses/offsets resolved live and
/// re-asserted across tiered-JIT relocation; both sites restored on disable.
/// </summary>
public sealed class CalamityVisionPatcher
{
    private const string BrightType = "CalamityMod.Systems.LightingEffectsSystem";
    private const string BrightMethod = "ModifyLightingBrightness";
    private const string TransType = "CalamityMod.Graphics.EnhancedDarknessSystem";
    private const string TransMethod = "AdjustTransmissiveness";

    private readonly TmlEngine _engine;
    private readonly object _lock = new();
    private volatile bool _enabled;
    private string _status = "off";

    private volatile int _state = -1;   // -1 resolving, 0 unavailable (no Calamity), 1 ready
    private volatile bool _resolving;
    private int _dnOff;                  // darknessIntensity offset (object base) for the transmissiveness site

    private sealed class Site { public ulong Addr; public int Pos = -1; public byte[]? Orig; }
    private readonly Site _bright = new();   // ModifyLightingBrightness → ret
    private readonly Site _trans = new();    // AdjustTransmissiveness load → xor

    public CalamityVisionPatcher(TmlEngine engine) => _engine = engine;
    public bool Enabled => _enabled;
    public string Status { get { lock (_lock) return _status; } }

    public void Enable() { lock (_lock) _enabled = true; ResolveAsync(); }
    public void Disable() { lock (_lock) { _enabled = false; Restore(_bright); Restore(_trans); _status = "off"; } }
    public void Clear() { lock (_lock) { _state = -1; Forget(_bright); Forget(_trans); } } // new process → re-resolve

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
                var r = TmlDiscovery.ResolveCalamityFields(pid, "darknessIntensity");
                if (r is { } v && v.offs.TryGetValue("darknessIntensity", out var off)) { _dnOff = off; _state = 1; }
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

        bool b = EnsureBrightness(mem, proc.Id);
        bool t = EnsureTransmissiveness(mem, proc.Id);
        _status = (b, t) switch
        {
            (true, true) => "darkness off (brightness+transmissiveness)",
            (true, false) => "darkness off (brightness only)",
            (false, true) => "darkness off (transmissiveness only)",
            _ => "darkness method not jitted (enter the area once)",
        };
    }

    // ModifyLightingBrightness exists only to scale lighting down → make the whole method an immediate ret.
    private bool EnsureBrightness(ProcessMemory mem, int pid)
    {
        if (_bright.Addr != 0 && _bright.Pos == 0 && IsByte(mem, _bright.Addr, 0xC3)) return true;
        ulong addr = RealCode(mem, TmlDiscovery.ResolveMethodAddr(pid, BrightType, BrightMethod));
        if (addr == 0) return false;
        byte first; try { first = mem.ReadBytes((IntPtr)addr, 1)[0]; } catch { return false; }
        _bright.Orig = new[] { first }; _bright.Addr = addr; _bright.Pos = 0;
        mem.WriteBytes((IntPtr)addr, new byte[] { 0xC3 }); // ret
        return true;
    }

    // AdjustTransmissiveness: rewrite `vmovss xmmN,[reg+darknessIntensity]` → `vxorps xmmN` (factor 0).
    private bool EnsureTransmissiveness(ProcessMemory mem, int pid)
    {
        if (_trans.Addr != 0 && _trans.Pos >= 0 && TransIntact(mem)) return true;
        ulong addr = RealCode(mem, TmlDiscovery.ResolveMethodAddr(pid, TransType, TransMethod));
        if (addr == 0) return false;
        byte[] code; try { code = mem.ReadBytes((IntPtr)addr, 0x600); } catch { return false; }
        int pos = FindDarknessLoad(code, _dnOff, out byte[] patch);
        if (pos < 0) return false;
        _trans.Orig = code.Skip(pos).Take(patch.Length).ToArray(); _trans.Addr = addr; _trans.Pos = pos;
        mem.WriteBytes((IntPtr)(addr + (ulong)pos), patch);
        return true;
    }

    private static int FindDarknessLoad(byte[] code, int dnOff, out byte[] patch)
    {
        patch = System.Array.Empty<byte>();
        var disp = BitConverter.GetBytes(dnOff);
        for (int i = 0; i + 8 <= code.Length; i++)
        {
            bool vex = code[i] == 0xC5 && code[i + 1] == 0xFA && code[i + 2] == 0x10;
            bool leg = code[i] == 0xF3 && code[i + 1] == 0x0F && code[i + 2] == 0x10;
            if (!vex && !leg) continue;
            byte modrm = code[i + 3];
            if ((modrm & 0xC0) != 0x80 || (modrm & 7) == 4) continue;
            if (code[i + 4] != disp[0] || code[i + 5] != disp[1] || code[i + 6] != disp[2] || code[i + 7] != disp[3]) continue;
            int n = (modrm >> 3) & 7;
            if (vex)
            {
                byte b2 = (byte)(0x80 | ((~n & 0x0F) << 3));
                patch = new byte[] { 0xC5, b2, 0x57, (byte)(0xC0 | (n << 3) | n), 0x90, 0x90, 0x90, 0x90 };
            }
            else patch = new byte[] { 0x0F, 0x57, (byte)(0xC0 | (n << 3) | n), 0x90, 0x90, 0x90, 0x90, 0x90 };
            return i;
        }
        return -1;
    }

    private ulong RealCode(ProcessMemory mem, ulong addr)
    {
        for (int i = 0; i < 4 && addr != 0; i++)
        {
            byte[] b; try { b = mem.ReadBytes((IntPtr)addr, 5); } catch { break; }
            if (b.Length < 5 || b[0] != 0xE9) break;
            addr = (ulong)((long)addr + 5 + BitConverter.ToInt32(b, 1));
        }
        return addr;
    }

    private static bool IsByte(ProcessMemory mem, ulong addr, byte val)
    { try { return mem.ReadBytes((IntPtr)addr, 1)[0] == val; } catch { return false; } }

    private bool TransIntact(ProcessMemory mem)
    {
        try { byte[] b = mem.ReadBytes((IntPtr)(_trans.Addr + (ulong)_trans.Pos), 3);
            return (b[0] == 0xC5 && b[2] == 0x57) || (b[0] == 0x0F && b[1] == 0x57); }
        catch { return false; }
    }

    private void Restore(Site s)
    {
        var mem = _engine.Mem;
        if (mem != null && s.Addr != 0 && s.Pos >= 0 && s.Orig != null)
            try { mem.WriteBytes((IntPtr)(s.Addr + (ulong)s.Pos), s.Orig); } catch { }
        Forget(s);
    }

    private static void Forget(Site s) { s.Addr = 0; s.Pos = -1; s.Orig = null; }
}
