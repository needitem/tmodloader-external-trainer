using TerrariaTrainer.Memory;

namespace TerrariaTrainer.Tml;

/// <summary>
/// Extends the character's block reach the clean way — by patching the source of the per-frame reset
/// instead of racing it. <c>Player.ResetEffects</c> runs every frame and sets the static reach to its
/// defaults via two instructions:
///   <c>mov rsi, &amp;tileRangeX; mov dword [rsi], 5</c>  and  <c>mov rdi, &amp;tileRangeY; mov dword [rdi], 4</c>.
/// We rewrite those two immediates (5/4 → N), so the game itself re-establishes the larger reach every
/// frame — no flicker, no external write race. Client-side (the local player's reach). The instructions
/// are re-located + re-patched if the .NET tiered JIT moves ResetEffects.
/// </summary>
public sealed class TileReachPatcher
{
    private readonly TmlEngine _engine;
    private readonly object _lock = new();
    private volatile bool _enabled;
    private int _n = 25;

    private ulong _addr;                 // current ResetEffects code address
    private ulong _trX, _trY;            // tileRangeX / tileRangeY static addresses (stable)
    private int _immX = -1, _immY = -1;  // offsets of the imm32 within the method
    private string _status = "off";

    public TileReachPatcher(TmlEngine engine) => _engine = engine;
    public bool Enabled => _enabled;
    public string Status { get { lock (_lock) return _status; } }

    public void SetValue(int n) { lock (_lock) { _n = Math.Clamp(n, 1, 200); if (_enabled) Locate(); } }
    public void Enable(int n) { lock (_lock) { _n = Math.Clamp(n, 1, 200); _enabled = true; Locate(); } }
    public void Disable() { lock (_lock) { _enabled = false; Restore(); _addr = 0; _immX = _immY = -1; _status = "off"; } }
    public void Clear() { lock (_lock) { _addr = 0; _trX = _trY = 0; _immX = _immY = -1; } }

    /// <summary>Called from the GUI background loop; re-asserts across tiered-JIT relocation.</summary>
    public void Tick() { if (!_enabled) return; try { lock (_lock) { if (_enabled) Locate(); } } catch { } }

    private void Locate()
    {
        var mem = _engine.Mem; var proc = _engine.Proc;
        if (mem == null || proc == null) { _status = "not attached"; return; }

        // If we already have a live patch site whose preamble still matches, just (re)write the imms.
        if (_addr != 0 && _immX >= 0 && SiteIntact(mem)) { WriteImms(mem, _n); _status = $"reach {_n}"; return; }

        if (_trX == 0)
        {
            var s = TmlDiscovery.ResolveStaticFields(proc.Id, "Terraria.Player", "tileRangeX", "tileRangeY");
            s.TryGetValue("tileRangeX", out _trX); s.TryGetValue("tileRangeY", out _trY);
        }
        if (_trX == 0 || _trY == 0) { _status = "tileRange statics not found"; return; }

        var cur = TmlDiscovery.ResolveCurrentAddresses(proc.Id, new[] { ("re", "Terraria.Player", "ResetEffects") });
        if (!cur.TryGetValue("re", out var list) || list.Count == 0) { _status = "ResetEffects not jitted"; return; }
        ulong addr = list[0];
        byte[] code = mem.ReadBytes((IntPtr)addr, 0x1000); // ResetEffects ~0xE85
        int ix = FindReset(code, 0xBE, 0x06, _trX);  // mov rsi,&tileRangeX ; mov dword [rsi], 5
        int iy = FindReset(code, 0xBF, 0x07, _trY);  // mov rdi,&tileRangeY ; mov dword [rdi], 4
        if (ix < 0 || iy < 0) { _status = "reset write not found"; _addr = 0; return; }
        _addr = addr; _immX = ix; _immY = iy;
        WriteImms(mem, _n);
        _status = $"reach {_n} (patched @0x{addr:X})";
    }

    // matches: 48 <movReg> <8-byte staticAddr> C7 <modrm> <imm32> ; returns the imm32's offset.
    private static int FindReset(byte[] code, byte movReg, byte modrm, ulong staticAddr)
    {
        var addrLe = BitConverter.GetBytes(staticAddr);
        for (int i = 0; i + 16 <= code.Length; i++)
        {
            if (code[i] != 0x48 || code[i + 1] != movReg) continue;
            bool ok = true;
            for (int k = 0; k < 8; k++) if (code[i + 2 + k] != addrLe[k]) { ok = false; break; }
            if (!ok) continue;
            if (code[i + 10] != 0xC7 || code[i + 11] != modrm) continue;
            return i + 12; // imm32 position
        }
        return -1;
    }

    private bool SiteIntact(ProcessMemory? mem)
    {
        // Cheap relocation check: the 'mov reg, &tileRangeX' still sits 12 bytes before the imm.
        try
        {
            byte[] b = mem!.ReadBytes((IntPtr)(_addr + (ulong)_immX - 12), 12);
            return b.Length == 12 && b[0] == 0x48 && b[10] == 0xC7;
        }
        catch { return false; }
    }

    private void WriteImms(ProcessMemory mem, int n)
    {
        try
        {
            mem.WriteInt32((IntPtr)(_addr + (ulong)_immX), n);
            mem.WriteInt32((IntPtr)(_addr + (ulong)_immY), n);
        }
        catch { }
    }

    private void Restore()
    {
        var mem = _engine.Mem;
        if (mem == null || _addr == 0 || _immX < 0) return;
        try { mem.WriteInt32((IntPtr)(_addr + (ulong)_immX), 5); mem.WriteInt32((IntPtr)(_addr + (ulong)_immY), 4); } catch { }
    }
}
