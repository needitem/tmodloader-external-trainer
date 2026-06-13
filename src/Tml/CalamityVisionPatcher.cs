using TerrariaTrainer.Memory;

namespace TerrariaTrainer.Tml;

/// <summary>
/// "Abyss / cave vision" — cancels Calamity's deep-sea &amp; cave screen darkening at the ROOT, so there's
/// no flicker. Calamity's lighting consumer <c>EnhancedDarknessSystem.AdjustTransmissiveness</c> loads
/// the player's <c>darknessIntensity</c> and uses it as the blend factor that darkens every light:
///   <c>vmovss xmmN, [calPlayer + darknessIntensityOff]</c>  →  result = lerp(original, dark, xmmN).
/// We rewrite that one load into <c>vxorps xmmN, xmmN, xmmN</c> (+NOPs), so the factor is always 0 and the
/// game itself computes "no darkening" every frame — far cleaner than racing the value from outside.
/// Client-side. Calamity-only. The method address + the field offset are resolved live (version-proof);
/// re-asserted across tiered-JIT relocation. Restores the original load on disable.
/// </summary>
public sealed class CalamityVisionPatcher
{
    private const string DarkType = "CalamityMod.Graphics.EnhancedDarknessSystem";
    private const string DarkMethod = "AdjustTransmissiveness";

    private readonly TmlEngine _engine;
    private readonly object _lock = new();
    private volatile bool _enabled;
    private string _status = "off";

    private volatile int _state = -1;   // -1 resolving, 0 unavailable (no Calamity), 1 ready
    private volatile bool _resolving;
    private int _dnOff;                  // darknessIntensity offset from the CalamityPlayer object base

    private ulong _addr;                 // current AdjustTransmissiveness code address
    private int _pos = -1;               // patch offset within the method
    private byte[]? _orig;               // original bytes we overwrote

    public CalamityVisionPatcher(TmlEngine engine) => _engine = engine;
    public bool Enabled => _enabled;
    public string Status { get { lock (_lock) return _status; } }

    public void Enable() { lock (_lock) { _enabled = true; } ResolveAsync(); }
    public void Disable() { lock (_lock) { _enabled = false; Restore(); _addr = 0; _pos = -1; _status = "off"; } }
    public void Clear() { lock (_lock) { _state = -1; _addr = 0; _pos = -1; _orig = null; } } // new process → re-resolve

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

        // already patched and the site still holds our xor? nothing to do.
        if (_addr != 0 && _pos >= 0 && PatchIntact(mem)) { _status = "darkness off (patched)"; return; }

        ulong addr = TmlDiscovery.ResolveMethodAddr(proc.Id, DarkType, DarkMethod);
        if (addr == 0) { _status = "darkness method not jitted"; _addr = 0; return; }
        byte[] code = mem.ReadBytes((IntPtr)addr, 0x600);
        int pos = FindDarknessLoad(code, _dnOff, out byte[] patch);
        if (pos < 0) { _status = "darkness load not found"; _addr = 0; return; }

        _addr = addr; _pos = pos; _orig = code.Skip(pos).Take(patch.Length).ToArray();
        mem.WriteBytes((IntPtr)(addr + (ulong)pos), patch);
        _status = $"darkness off (patched @0x{addr:X}+0x{pos:X})";
    }

    // Find `vmovss xmmN,[reg+disp32==dnOff]` (VEX C5 FA 10, or legacy F3 0F 10) and build the
    // equivalent `xor xmmN,xmmN` that zeroes the same register, padded with NOPs to the same length.
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
            if ((modrm & 0xC0) != 0x80 || (modrm & 7) == 4) continue; // need [reg+disp32], no SIB
            if (code[i + 4] != disp[0] || code[i + 5] != disp[1] || code[i + 6] != disp[2] || code[i + 7] != disp[3]) continue;
            int n = (modrm >> 3) & 7;        // destination xmm register
            if (vex)
            {
                byte b2 = (byte)(0x80 | ((~n & 0x0F) << 3));        // VEX2 vxorps xmmN,xmmN,xmmN
                patch = new byte[] { 0xC5, b2, 0x57, (byte)(0xC0 | (n << 3) | n), 0x90, 0x90, 0x90, 0x90 };
            }
            else
            {
                patch = new byte[] { 0x0F, 0x57, (byte)(0xC0 | (n << 3) | n), 0x90, 0x90, 0x90, 0x90, 0x90 }; // legacy xorps
            }
            return i;
        }
        return -1;
    }

    private bool PatchIntact(ProcessMemory mem)
    {
        try
        {
            byte[] b = mem.ReadBytes((IntPtr)(_addr + (ulong)_pos), 3);
            return b.Length == 3 && ((b[0] == 0xC5 && b[2] == 0x57) || (b[0] == 0x0F && b[1] == 0x57));
        }
        catch { return false; }
    }

    private void Restore()
    {
        var mem = _engine.Mem;
        if (mem == null || _addr == 0 || _pos < 0 || _orig == null) return;
        try { mem.WriteBytes((IntPtr)(_addr + (ulong)_pos), _orig); } catch { }
    }
}
