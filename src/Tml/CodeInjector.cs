using System.Diagnostics;
using TerrariaTrainer.Memory;
using static TerrariaTrainer.Memory.Native;

namespace TerrariaTrainer.Tml;

/// <summary>
/// "NOP the writes" code injection. Effect fields are written every frame by the player
/// update methods; we locate every store of a target field by its offset and overwrite
/// those instructions with NOPs, so an external write to the field is never overwritten:
///   bool flag reset:  C6 83 dd 00              mov byte [rbx+off], 0    (7 bytes)
///   float store:      C5 FA 11 (rm=011) dd     vmovss [rbx+off], xmmN   (8 bytes)
/// `[rbx+off]` is always `this.field` inside Player methods, so matching by offset is safe.
/// Patches are applied with the target threads suspended (debugger style) + icache flush.
/// </summary>
public sealed class CodeInjector
{
    private readonly ProcessMemory _mem;
    private readonly TmlModel _model;
    private readonly Process _proc;

    // field name -> list of (patched address, original bytes)
    private readonly Dictionary<string, List<(IntPtr addr, byte[] orig)>> _patched = new();

    public CodeInjector(ProcessMemory mem, TmlModel model, Process proc)
    {
        _mem = mem; _model = model; _proc = proc;
    }

    public bool IsPatched(string field) => _patched.ContainsKey(field);
    public bool CanInject(string field) => FindStores(field).Count > 0;

    /// <summary>Every store-instruction site (addr,len) that writes the given field.</summary>
    public List<(IntPtr addr, int len)> FindStores(string field)
    {
        var result = new List<(IntPtr, int)>();
        var f = _model.PlayerFields.FirstOrDefault(x => x.Name == field);
        if (f == null) return result;
        int target = f.Offset;
        bool isFloat = f.Kind is FieldKind.Single or FieldKind.Double;
        var seen = new HashSet<long>();

        // Scan the known per-frame update methods (fall back to ResetEffects only).
        var regions = _model.EffectMethods.Count > 0
            ? _model.EffectMethods
            : new List<(ulong, int)> { (_model.ResetEffectsAddr, 0x3000) };

        foreach (var (addr, size) in regions)
        {
            if (addr == 0) continue;
            int len = Math.Clamp(size, 0x40, 0x20000);
            var code = _mem.ReadBytes((IntPtr)addr, len);
            for (int i = 0; i + 8 <= code.Length; i++)
            {
                long abs = (long)addr + i;
                if (seen.Contains(abs)) continue;

                if (isFloat)
                {
                    // vmovss [rbx+disp32], xmmN
                    if (code[i] == 0xC5 && code[i + 1] == 0xFA && code[i + 2] == 0x11
                        && (code[i + 3] & 0xC7) == 0x83 && BitConverter.ToInt32(code, i + 4) == target)
                    { result.Add(((IntPtr)abs, 8)); seen.Add(abs); }
                }
                else
                {
                    // mov byte [rbx+disp32], 0  (reset only — leave 'set true' stores alone)
                    if (code[i] == 0xC6 && code[i + 1] == 0x83
                        && BitConverter.ToInt32(code, i + 2) == target && code[i + 6] == 0x00)
                    { result.Add(((IntPtr)abs, 7)); seen.Add(abs); }
                }
            }
        }
        return result;
    }

    /// <summary>NOP every store of the field so the game stops overwriting it.</summary>
    public bool Patch(string field)
    {
        if (_patched.ContainsKey(field)) return true;
        var sites = FindStores(field);
        if (sites.Count == 0) return false;

        var saved = new List<(IntPtr, byte[])>();
        var suspended = SuspendTargetThreads();
        try
        {
            foreach (var (addr, len) in sites)
            {
                var orig = _mem.ReadBytes(addr, len);
                var nops = new byte[len];
                Array.Fill(nops, (byte)0x90);
                if (_mem.WriteBytes(addr, nops))
                {
                    FlushInstructionCache(_mem.Handle, addr, (IntPtr)len);
                    saved.Add((addr, orig));
                }
            }
        }
        finally { foreach (var h in suspended) { ResumeThread(h); CloseHandle(h); } }

        if (saved.Count == 0) return false;
        _patched[field] = saved;
        return true;
    }

    public bool Restore(string field)
    {
        if (!_patched.TryGetValue(field, out var sites)) return true;
        var suspended = SuspendTargetThreads();
        try
        {
            foreach (var (addr, orig) in sites)
            {
                _mem.WriteBytes(addr, orig);
                FlushInstructionCache(_mem.Handle, addr, (IntPtr)orig.Length);
            }
        }
        finally { foreach (var h in suspended) { ResumeThread(h); CloseHandle(h); } }
        _patched.Remove(field);
        return true;
    }

    public void RestoreAll()
    {
        foreach (var field in _patched.Keys.ToList()) Restore(field);
        foreach (var field in _useHooks.Keys.ToList()) UnhookUseSites(field);
        foreach (var key in _entryHooks.Keys.ToList()) UnhookEntry(key);
    }

    // ---- use-site hook: redirect a field's load instruction to a constant cell ----
    // For values written in many places (pickSpeed/moveSpeed), don't fight the writers —
    // change the place that READS the value to read our constant instead. The load
    //   vmovss xmmN, [rbx+off]   (C5 FA 10 modrm=10/rm=011 disp32)
    // is rewritten, same length, to
    //   vmovss xmmN, [rip+disp]  (C5 FA 10 modrm=00/rm=101 disp32 -> our cell)

    private readonly Dictionary<string, (List<(IntPtr addr, byte[] orig)> sites, IntPtr cell)> _useHooks = new();

    public bool IsUseHooked(string field) => _useHooks.ContainsKey(field);

    public List<IntPtr> FindLoadSites(string field, IEnumerable<string> methodNames)
    {
        var sites = new List<IntPtr>();
        var f = _model.PlayerFields.FirstOrDefault(x => x.Name == field);
        if (f == null) return sites;
        int off = f.Offset;
        var seen = new HashSet<long>();
        foreach (var name in methodNames)
        {
            if (!_model.Methods.TryGetValue(name, out var m) || m.addr == 0) continue;
            var code = _mem.ReadBytes((IntPtr)m.addr, Math.Clamp(m.size, 0x40, 0x40000));
            for (int i = 0; i + 8 <= code.Length; i++)
            {
                if (code[i] == 0xC5 && code[i + 1] == 0xFA && code[i + 2] == 0x10
                    && (code[i + 3] & 0xC7) == 0x83 && BitConverter.ToInt32(code, i + 4) == off)
                {
                    long abs = (long)m.addr + i;
                    if (seen.Add(abs)) sites.Add((IntPtr)abs);
                }
            }
        }
        return sites;
    }

    public bool HookUseSites(string field, float value, IEnumerable<string> methodNames)
    {
        if (_useHooks.ContainsKey(field)) { WriteCell(field, value); return true; }
        var loadSites = FindLoadSites(field, methodNames);
        if (loadSites.Count == 0) return false;

        // one constant cell within rip-reach of all sites (they cluster in the JIT heap)
        IntPtr cell = _mem.AllocNear(loadSites[0], 8);
        if (cell == IntPtr.Zero) return false;
        _mem.WriteFloat(cell, value);

        var saved = new List<(IntPtr, byte[])>();
        var suspended = SuspendTargetThreads();
        try
        {
            foreach (var site in loadSites)
            {
                var orig = _mem.ReadBytes(site, 8);
                long rip = cell.ToInt64() - (site.ToInt64() + 8);
                if (Math.Abs(rip) >= 0x7FFFFFFF) continue; // out of reach; skip this site
                byte newModrm = (byte)((orig[3] & 0x38) | 0x05); // keep xmm reg, set [rip+disp32]
                var patched = new byte[8] { 0xC5, 0xFA, 0x10, newModrm, 0, 0, 0, 0 };
                BitConverter.GetBytes((int)rip).CopyTo(patched, 4);
                if (_mem.WriteBytes(site, patched))
                {
                    FlushInstructionCache(_mem.Handle, site, (IntPtr)8);
                    saved.Add((site, orig));
                }
            }
        }
        finally { foreach (var h in suspended) { ResumeThread(h); CloseHandle(h); } }

        if (saved.Count == 0) { _mem.Free(cell); return false; }
        _useHooks[field] = (saved, cell);
        return true;
    }

    public void WriteCell(string field, float value)
    {
        if (_useHooks.TryGetValue(field, out var h)) _mem.WriteFloat(h.cell, value);
    }

    public bool UnhookUseSites(string field)
    {
        if (!_useHooks.TryGetValue(field, out var h)) return true;
        var suspended = SuspendTargetThreads();
        try
        {
            foreach (var (addr, orig) in h.sites)
            {
                _mem.WriteBytes(addr, orig);
                FlushInstructionCache(_mem.Handle, addr, (IntPtr)orig.Length);
            }
        }
        finally { foreach (var hd in suspended) { ResumeThread(hd); CloseHandle(hd); } }
        _mem.Free(h.cell);
        _useHooks.Remove(field);
        return true;
    }

    // ---- entry code-cave: force `this.field = value` at a method's entry ----
    // Faithful to the CT approach: write pickSpeed=0.1 in-frame, right before the method
    // uses it, so it doesn't matter how many places wrote it earlier.

    private readonly Dictionary<string, (IntPtr entry, byte[] orig, IntPtr cave)> _entryHooks = new();

    public bool IsEntryHooked(string key) => _entryHooks.ContainsKey(key);

    /// <summary>At method entry (rcx = this), inject `mov dword [rcx+fieldOffset], valueBits`.</summary>
    public bool HookEntryForce(string key, string methodName, int fieldOffset, uint valueBits)
    {
        if (_entryHooks.ContainsKey(key)) { PokeEntryValue(key, fieldOffset, valueBits); return true; }
        if (!_model.Methods.TryGetValue(methodName, out var m) || m.addr == 0) return false;
        IntPtr entry = (IntPtr)m.addr;
        var head = _mem.ReadBytes(entry, 16);
        int disp = PrologueLen(head, 5);
        if (disp < 5) return false; // can't safely relocate the prologue

        IntPtr cave = _mem.AllocNear(entry, 0x40, MemoryProtection.ExecuteReadWrite);
        if (cave == IntPtr.Zero) return false;

        var cb = new List<byte> { 0xC7, 0x81 };                       // mov dword [rcx+disp32], imm32
        cb.AddRange(BitConverter.GetBytes(fieldOffset));
        cb.AddRange(BitConverter.GetBytes(valueBits));
        cb.AddRange(head.Take(disp));                                  // displaced prologue
        cb.Add(0xE9);                                                  // jmp back to entry+disp
        int backRel = (int)((entry.ToInt64() + disp) - (cave.ToInt64() + cb.Count + 4));
        cb.AddRange(BitConverter.GetBytes(backRel));
        _mem.WriteBytes(cave, cb.ToArray());

        var patch = new byte[disp];
        patch[0] = 0xE9;
        BitConverter.GetBytes((int)(cave.ToInt64() - (entry.ToInt64() + 5))).CopyTo(patch, 1);
        for (int k = 5; k < disp; k++) patch[k] = 0x90;

        var suspended = SuspendTargetThreads();
        try { _mem.WriteBytes(entry, patch); FlushInstructionCache(_mem.Handle, entry, (IntPtr)disp); }
        finally { foreach (var h in suspended) { ResumeThread(h); CloseHandle(h); } }

        _entryHooks[key] = (entry, head.Take(disp).ToArray(), cave);
        return true;
    }

    /// <summary>Update the immediate value written by an existing entry hook (re-patches the cave).</summary>
    public void PokeEntryValue(string key, int fieldOffset, uint valueBits)
    {
        if (!_entryHooks.TryGetValue(key, out var h)) return;
        // imm32 lives at cave + 2 (C7 81) + 4 (disp32)
        _mem.WriteBytes((IntPtr)(h.cave.ToInt64() + 6), BitConverter.GetBytes(valueBits));
    }

    public bool UnhookEntry(string key)
    {
        if (!_entryHooks.TryGetValue(key, out var h)) return true;
        var suspended = SuspendTargetThreads();
        try { _mem.WriteBytes(h.entry, h.orig); FlushInstructionCache(_mem.Handle, h.entry, (IntPtr)h.orig.Length); }
        finally { foreach (var s in suspended) { ResumeThread(s); CloseHandle(s); } }
        _mem.Free(h.cave);
        _entryHooks.Remove(key);
        return true;
    }

    /// <summary>Length of whole instructions covering at least <paramref name="min"/> bytes; 0 if undecodable.</summary>
    private static int PrologueLen(byte[] c, int min)
    {
        int i = 0;
        while (i < min)
        {
            int start = i;
            if (i < c.Length && c[i] >= 0x40 && c[i] <= 0x4F) i++; // REX prefix
            if (i >= c.Length) return 0;
            byte op = c[i];
            if (op >= 0x50 && op <= 0x5F) i++;                     // push/pop reg
            else return 0;                                         // unknown prologue -> abort
            if (i == start) return 0;
        }
        return i;
    }

    private List<IntPtr> SuspendTargetThreads()
    {
        var handles = new List<IntPtr>();
        try
        {
            foreach (ProcessThread t in _proc.Threads)
            {
                IntPtr h = OpenThread(THREAD_SUSPEND_RESUME, false, t.Id);
                if (h != IntPtr.Zero) { SuspendThread(h); handles.Add(h); }
            }
        }
        catch { /* best effort */ }
        return handles;
    }
}
