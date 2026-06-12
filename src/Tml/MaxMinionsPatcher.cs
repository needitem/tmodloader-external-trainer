using TerrariaTrainer.Memory;

namespace TerrariaTrainer.Tml;

/// <summary>
/// Raises the summon-minion cap (<c>Player.maxMinions</c>) the clean way — by rewriting the per-frame
/// reset instead of racing it. <c>Player.ResetEffects</c> runs every frame and resets the cap with
/// <c>mov dword [this + maxMinionsOffset], 1</c>; accessories/armor then add to it. We patch that
/// immediate (1 → N), so the game itself re-establishes the larger cap every frame — no flicker, no
/// external write race. Your summon gear still stacks on top. Client-side (the local player's cap).
/// The instruction is re-located + re-patched if the .NET tiered JIT moves ResetEffects.
/// </summary>
public sealed class MaxMinionsPatcher
{
    private readonly TmlEngine _engine;
    private readonly object _lock = new();
    private volatile bool _enabled;
    private int _n = 10;

    private int _fieldOff = -1;   // Player.maxMinions field offset (== the disp32 in the store)
    private ulong _addr;          // current ResetEffects code address
    private int _imm = -1;        // offset of the imm32 within the method
    private string _status = "off";

    public MaxMinionsPatcher(TmlEngine engine) => _engine = engine;
    public bool Enabled => _enabled;
    public string Status { get { lock (_lock) return _status; } }

    public void SetValue(int n) { lock (_lock) { _n = Math.Clamp(n, 1, 999); if (_enabled) Locate(); } }
    public void Enable(int n) { lock (_lock) { _n = Math.Clamp(n, 1, 999); _enabled = true; Locate(); } }
    public void Disable() { lock (_lock) { _enabled = false; Restore(); _addr = 0; _imm = -1; _status = "off"; } }
    public void Clear() { lock (_lock) { _addr = 0; _imm = -1; } }

    /// <summary>Called from the GUI background loop; re-asserts across tiered-JIT relocation.</summary>
    public void Tick() { if (!_enabled) return; try { lock (_lock) { if (_enabled) Locate(); } } catch { } }

    private void Locate()
    {
        var mem = _engine.Mem; var proc = _engine.Proc;
        if (mem == null || proc == null) { _status = "not attached"; return; }

        if (_fieldOff < 0)
        {
            var f = _engine.Model?.PlayerFields.FirstOrDefault(x => x.Name == "maxMinions");
            if (f == null) { _status = "maxMinions field not found"; return; }
            _fieldOff = f.Offset;
        }

        // Already have a live patch site whose store still matches -> just re-write the imm.
        if (_addr != 0 && _imm >= 0 && SiteIntact(mem)) { WriteImm(mem, _n); _status = $"max minions {_n}"; return; }

        var cur = TmlDiscovery.ResolveCurrentAddresses(proc.Id, new[] { ("re", "Terraria.Player", "ResetEffects") });
        if (!cur.TryGetValue("re", out var list) || list.Count == 0) { _status = "ResetEffects not jitted"; return; }
        ulong addr = list[0];
        byte[] code = mem.ReadBytes((IntPtr)addr, 0x1000);
        int ii = FindStore(code, _fieldOff);   // mov dword [this+maxMinionsOff], 1
        if (ii < 0) { _status = "reset write not found"; _addr = 0; return; }
        _addr = addr; _imm = ii;
        WriteImm(mem, _n);
        _status = $"max minions {_n} (patched @0x{addr:X})";
    }

    // matches: C7 <modrm: mod=10,reg=000,rm≠100> <disp32 == fieldOff> <imm32> ; returns the imm32 offset.
    // (`mov dword [reg+fieldOff], imm` with this in some 64-bit base register; REX prefix is ignored.)
    private static int FindStore(byte[] code, int fieldOff)
    {
        var disp = BitConverter.GetBytes(fieldOff);
        for (int i = 0; i + 10 <= code.Length; i++)
        {
            if (code[i] != 0xC7) continue;
            byte modrm = code[i + 1];
            if ((modrm & 0xC0) != 0x80) continue;       // mod = 10 (disp32)
            if (((modrm >> 3) & 7) != 0) continue;      // reg = 000 (C7 /0 = mov r/m, imm)
            if ((modrm & 7) == 4) continue;             // rm = 100 -> SIB byte follows; skip
            if (code[i + 2] != disp[0] || code[i + 3] != disp[1] || code[i + 4] != disp[2] || code[i + 5] != disp[3]) continue;
            return i + 6; // imm32 position
        }
        return -1;
    }

    private bool SiteIntact(ProcessMemory? mem)
    {
        try
        {
            byte[] b = mem!.ReadBytes((IntPtr)(_addr + (ulong)_imm - 6), 6);
            return b.Length == 6 && b[0] == 0xC7 && (b[1] & 0xC0) == 0x80
                   && b[2] == (byte)_fieldOff && b[3] == (byte)(_fieldOff >> 8);
        }
        catch { return false; }
    }

    private void WriteImm(ProcessMemory mem, int n)
    {
        try { mem.WriteInt32((IntPtr)(_addr + (ulong)_imm), n); } catch { }
    }

    private void Restore()
    {
        var mem = _engine.Mem;
        if (mem == null || _addr == 0 || _imm < 0) return;
        try { mem.WriteInt32((IntPtr)(_addr + (ulong)_imm), 1); } catch { }
    }
}
