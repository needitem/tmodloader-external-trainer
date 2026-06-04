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
        if (h.cave != IntPtr.Zero) _mem.Free(h.cave);
        _entryHooks.Remove(key);
        return true;
    }

    /// <summary>Patch a method's entry so it immediately returns a value (bypasses its logic).</summary>
    public bool PatchReturn(string methodKey, byte[] code)
    {
        if (_entryHooks.ContainsKey(methodKey)) return true;
        if (!_model.Methods.TryGetValue(methodKey, out var m) || m.addr == 0) return false;
        IntPtr entry = (IntPtr)m.addr;
        var orig = _mem.ReadBytes(entry, code.Length);
        var suspended = SuspendTargetThreads();
        try { _mem.WriteBytes(entry, code); FlushInstructionCache(_mem.Handle, entry, (IntPtr)code.Length); }
        finally { foreach (var h in suspended) { ResumeThread(h); CloseHandle(h); } }
        _entryHooks[methodKey] = (entry, orig, IntPtr.Zero);
        return true;
    }

    public bool PatchReturnTrue(string key) => PatchReturn(key, new byte[] { 0xB8, 0x01, 0x00, 0x00, 0x00, 0xC3 }); // mov eax,1; ret
    public bool PatchReturnZero(string key) => PatchReturn(key, new byte[] { 0x31, 0xC0, 0xC3 });                   // xor eax,eax; ret

    /// <summary>Patch a float-returning method to return a constant (value in xmm0).</summary>
    public bool PatchReturnFloat(string key, float value)
    {
        var code = new byte[] { 0xB8, 0, 0, 0, 0, 0x66, 0x0F, 0x6E, 0xC0, 0xC3 }; // mov eax,bits; movd xmm0,eax; ret
        BitConverter.GetBytes(value).CopyTo(code, 1);
        return PatchReturn(key, code);
    }

    public bool CanPatch(string methodKey) => _model.Methods.ContainsKey(methodKey);

    // ---- method-set patches (patch every overload of a name at once) ----
    private readonly Dictionary<string, List<(IntPtr entry, byte[] orig)>> _setHooks = new();

    public bool CanPatchSet(string setKey) =>
        _model.MethodSets.TryGetValue(setKey, out var l) && l.Count > 0;

    /// <summary>Patch every overload in a method set so it immediately returns (code = return stub).</summary>
    public int PatchSetReturn(string setKey, byte[] code)
    {
        if (_setHooks.ContainsKey(setKey)) return _setHooks[setKey].Count;
        if (!_model.MethodSets.TryGetValue(setKey, out var addrs) || addrs.Count == 0) return 0;
        var saved = new List<(IntPtr, byte[])>();
        var suspended = SuspendTargetThreads();
        try
        {
            foreach (var (addr, _) in addrs)
            {
                IntPtr entry = (IntPtr)addr;
                var orig = _mem.ReadBytes(entry, code.Length);
                _mem.WriteBytes(entry, code);
                FlushInstructionCache(_mem.Handle, entry, (IntPtr)code.Length);
                saved.Add((entry, orig));
            }
        }
        finally { foreach (var h in suspended) { ResumeThread(h); CloseHandle(h); } }
        _setHooks[setKey] = saved;
        return saved.Count;
    }

    public void UnpatchSet(string setKey)
    {
        if (!_setHooks.TryGetValue(setKey, out var saved)) return;
        var suspended = SuspendTargetThreads();
        try { foreach (var (entry, orig) in saved) { _mem.WriteBytes(entry, orig); FlushInstructionCache(_mem.Handle, entry, (IntPtr)orig.Length); } }
        finally { foreach (var h in suspended) { ResumeThread(h); CloseHandle(h); } }
        _setHooks.Remove(setKey);
    }

    // return-stubs: universal-zero clears eax AND xmm0 (covers int/bool/float/double returns)
    public int PatchSetReturnZero(string key) => PatchSetReturn(key, new byte[] { 0x31, 0xC0, 0x0F, 0x57, 0xC0, 0xC3 }); // xor eax,eax; xorps xmm0,xmm0; ret
    public int PatchSetReturnTrue(string key) => PatchSetReturn(key, new byte[] { 0xB8, 0x01, 0x00, 0x00, 0x00, 0xC3 }); // mov eax,1; ret

    // ---- inventory accessories: run ApplyEquipFunctional() on every inventory accessory ----
    // Injects an asm loop at UpdateEquips' entry (rcx = this) that, for each inventory item
    // with item.accessory == true, calls player.ApplyEquipFunctional(item, hideVisual=true).
    // Runs on the game thread each frame, so effects apply exactly like equipped accessories.

    public bool HookInventoryAccessories()
    {
        const string KEY = "invAccessories";
        if (_entryHooks.ContainsKey(KEY)) return true;
        if (!_model.Methods.TryGetValue("UpdateEquips", out var ue) || ue.addr == 0) return false;
        if (!_model.Methods.TryGetValue("ApplyEquipFunctional", out var apply) || apply.addr == 0) return false;
        if (!_model.ItemFields.TryGetValue("accessory", out int accOff)) return false;
        int invOff = _model.InventoryOff;

        IntPtr entry = (IntPtr)ue.addr;
        var head = _mem.ReadBytes(entry, 16);
        int disp = PrologueLen(head, 5);
        if (disp < 5) return false;

        IntPtr cave = _mem.AllocNear(entry, 0x200, MemoryProtection.ExecuteReadWrite);
        if (cave == IntPtr.Zero) return false;

        // ---- mini-assembler ----
        var b = new List<byte>();
        var labels = new Dictionary<string, int>();
        var rel32 = new List<(int pos, string label)>();      // internal jumps
        var abs32 = new List<(int pos, ulong target)>();      // call/jmp to absolute addr (rel32)
        void E(params byte[] x) => b.AddRange(x);
        void U32(int v) => b.AddRange(BitConverter.GetBytes(v));
        void L(string n) => labels[n] = b.Count;
        void JccNear(byte cc, string lbl) { E(0x0F, cc); rel32.Add((b.Count, lbl)); U32(0); }
        void JmpNear(string lbl) { E(0xE9); rel32.Add((b.Count, lbl)); U32(0); }
        void CallAbs(ulong target) { E(0xE8); abs32.Add((b.Count, target)); U32(0); }
        void JmpAbs(ulong target) { E(0xE9); abs32.Add((b.Count, target)); U32(0); }
        void IncAbs(ulong target) { E(0xFF, 0x05); abs32.Add((b.Count, target)); U32(0); } // inc dword [rip+...]
        ulong ENTRYCNT = (ulong)(cave.ToInt64() + 0x1F0), CALLCNT = (ulong)(cave.ToInt64() + 0x1F4);
        ulong LOOPCNT = (ulong)(cave.ToInt64() + 0x1F8);
        ulong NONNULL = (ulong)(cave.ToInt64() + 0x1FC);

        IncAbs(ENTRYCNT);                    // count every UpdateEquips entry (proves hook runs)

        // save
        E(0x50, 0x51, 0x52);                 // push rax, rcx, rdx
        E(0x41, 0x50, 0x41, 0x51, 0x41, 0x52, 0x41, 0x53); // push r8,r9,r10,r11
        E(0x53, 0x56, 0x57, 0x41, 0x54);     // push rbx, rsi, rdi, r12   (11 pushes total)

        E(0x48, 0x8B, 0xD9);                 // mov rbx, rcx
        E(0x48, 0x89, 0x1D); abs32.Add((b.Count, (ulong)(cave.ToInt64() + 0x1E0))); U32(0); // mov [rip+PLAYERPTR], rbx
        E(0x48, 0x8B, 0xB3); U32(invOff);    // mov rsi, [rbx+invOff]
        E(0x48, 0x85, 0xF6);                 // test rsi, rsi
        JccNear(0x84, "DONE");               // je DONE
        E(0x45, 0x31, 0xE4);                 // xor r12d, r12d
        L("LOOP");
        IncAbs(LOOPCNT);                     // count loop iterations
        E(0x49, 0x83, 0xFC, 0x32);           // cmp r12, 50
        JccNear(0x8D, "DONE");               // jge DONE
        E(0x4A, 0x8B, 0x7C, 0xE6, 0x10);     // mov rdi, [rsi+r12*8+0x10]
        E(0x48, 0x85, 0xFF);                 // test rdi, rdi
        JccNear(0x84, "NEXT");               // je NEXT
        IncAbs(NONNULL);                     // count non-null items scanned
        E(0x80, 0xBF); U32(accOff); E(0x00); // cmp byte [rdi+accOff], 0
        JccNear(0x84, "NEXT");               // je NEXT
        IncAbs(CALLCNT);                     // count each accessory we call Apply on
        E(0x48, 0x8B, 0xCB);                 // mov rcx, rbx   (this)
        E(0x48, 0x8B, 0xD7);                 // mov rdx, rdi   (item)
        E(0x41, 0xB8, 0x01, 0x00, 0x00, 0x00); // mov r8d, 1   (hideVisual)
        E(0x53, 0x56, 0x57, 0x41, 0x54);     // push rbx, rsi, rdi, r12  (protect loop state across call)
        E(0x48, 0x83, 0xEC, 0x20);           // sub rsp, 0x20  (shadow space)
        CallAbs(apply.addr);                 // call ApplyEquipFunctional
        E(0x48, 0x83, 0xC4, 0x20);           // add rsp, 0x20
        E(0x41, 0x5C, 0x5F, 0x5E, 0x5B);     // pop r12, rdi, rsi, rbx
        L("NEXT");
        E(0x49, 0xFF, 0xC4);                 // inc r12
        JmpNear("LOOP");
        L("DONE");
        // restore (reverse)
        E(0x41, 0x5C, 0x5F, 0x5E, 0x5B);     // pop r12, rdi, rsi, rbx
        E(0x41, 0x5B, 0x41, 0x5A, 0x41, 0x59, 0x41, 0x58); // pop r11,r10,r9,r8
        E(0x5A, 0x59, 0x58);                 // pop rdx, rcx, rax
        b.AddRange(head.Take(disp));         // displaced prologue
        JmpAbs((ulong)(entry.ToInt64() + disp)); // jmp back

        // patch fixups
        var code = b.ToArray();
        foreach (var (pos, lbl) in rel32)
            BitConverter.GetBytes(labels[lbl] - (pos + 4)).CopyTo(code, pos);
        foreach (var (pos, target) in abs32)
            BitConverter.GetBytes((int)((long)target - (cave.ToInt64() + pos + 4))).CopyTo(code, pos);
        _mem.WriteBytes(cave, code);

        // patch UpdateEquips entry -> jmp cave
        var patch = new byte[disp];
        patch[0] = 0xE9;
        BitConverter.GetBytes((int)(cave.ToInt64() - (entry.ToInt64() + 5))).CopyTo(patch, 1);
        for (int k = 5; k < disp; k++) patch[k] = 0x90;

        var suspended = SuspendTargetThreads();
        try { _mem.WriteBytes(entry, patch); FlushInstructionCache(_mem.Handle, entry, (IntPtr)disp); }
        finally { foreach (var h in suspended) { ResumeThread(h); CloseHandle(h); } }

        _entryHooks[KEY] = (entry, head.Take(disp).ToArray(), cave);
        return true;
    }

    public void UnhookInventoryAccessories() => UnhookEntry("invAccessories");

    /// <summary>
    /// Make the 7 vanity/social accessory slots (armor[13..19]) grant their FUNCTIONAL
    /// effects by calling ApplyEquipFunctional on each non-null item from a cave at
    /// UpdateEquips' entry. Unlike the inventory hook there is no accessory check: vanity
    /// slots only ever hold accessories, so every non-null item is applied (also covers
    /// modded/Calamity effects via ApplyEquipFunctional's internal mod hooks).
    /// </summary>
    public bool HookVanityAccessories()
    {
        const string KEY = "vanityAccessories";
        if (_entryHooks.ContainsKey(KEY)) return true;
        if (!_model.Methods.TryGetValue("UpdateEquips", out var ue) || ue.addr == 0) return false;
        if (!_model.Methods.TryGetValue("ApplyEquipFunctional", out var apply) || apply.addr == 0) return false;
        int armorOff = _model.ArmorOff;
        int noFallOff = _model.PlayerFields.FirstOrDefault(f => f.Name == "noFallDmg")?.Offset ?? 0;

        IntPtr entry = (IntPtr)ue.addr;
        var head = _mem.ReadBytes(entry, 16);
        int disp = PrologueLen(head, 5);
        if (disp < 5) return false;

        IntPtr cave = _mem.AllocNear(entry, 0x200, MemoryProtection.ExecuteReadWrite);
        if (cave == IntPtr.Zero) return false;

        var b = new List<byte>();
        var labels = new Dictionary<string, int>();
        var rel32 = new List<(int pos, string label)>();
        var abs32 = new List<(int pos, ulong target)>();
        void E(params byte[] x) => b.AddRange(x);
        void U32(int v) => b.AddRange(BitConverter.GetBytes(v));
        void L(string n) => labels[n] = b.Count;
        void JccNear(byte cc, string lbl) { E(0x0F, cc); rel32.Add((b.Count, lbl)); U32(0); }
        void JmpNear(string lbl) { E(0xE9); rel32.Add((b.Count, lbl)); U32(0); }
        void CallAbs(ulong target) { E(0xE8); abs32.Add((b.Count, target)); U32(0); }
        void JmpAbs(ulong target) { E(0xE9); abs32.Add((b.Count, target)); U32(0); }
        void IncAbs(ulong target) { E(0xFF, 0x05); abs32.Add((b.Count, target)); U32(0); }
        ulong ENTRYCNT = (ulong)(cave.ToInt64() + 0x1F0), CALLCNT = (ulong)(cave.ToInt64() + 0x1F4);
        ulong PLAYERPTR = (ulong)(cave.ToInt64() + 0x1E0);
        ulong FLAGCAP = (ulong)(cave.ToInt64() + 0x1D4); // last live frame's noFallDmg after our applies

        IncAbs(ENTRYCNT);

        // save
        E(0x50, 0x51, 0x52);                 // push rax, rcx, rdx
        E(0x41, 0x50, 0x41, 0x51, 0x41, 0x52, 0x41, 0x53); // push r8,r9,r10,r11
        E(0x53, 0x56, 0x57, 0x41, 0x54);     // push rbx, rsi, rdi, r12

        E(0x48, 0x8B, 0xD9);                 // mov rbx, rcx   (this player)
        E(0x48, 0x89, 0x1D); abs32.Add((b.Count, PLAYERPTR)); U32(0); // mov [rip+PLAYERPTR], rbx
        E(0x48, 0x8B, 0xB3); U32(armorOff);  // mov rsi, [rbx+armorOff]   (armor Item[])
        E(0x48, 0x85, 0xF6);                 // test rsi, rsi
        JccNear(0x84, "VDONE");              // je VDONE
        E(0x41, 0xBC, 0x0D, 0x00, 0x00, 0x00); // mov r12d, 13   (first vanity accessory slot)
        L("LOOP");
        E(0x49, 0x83, 0xFC, 0x14);           // cmp r12, 20
        JccNear(0x8D, "VDONE");              // jge VDONE
        E(0x4A, 0x8B, 0x7C, 0xE6, 0x10);     // mov rdi, [rsi+r12*8+0x10]  (armor[r12])
        E(0x48, 0x85, 0xFF);                 // test rdi, rdi
        JccNear(0x84, "NEXT");               // je NEXT
        IncAbs(CALLCNT);                     // count each vanity item we apply
        E(0x48, 0x8B, 0xCB);                 // mov rcx, rbx   (this)
        E(0x48, 0x8B, 0xD7);                 // mov rdx, rdi   (item)
        E(0x41, 0xB8, 0x01, 0x00, 0x00, 0x00); // mov r8d, 1   (hideVisual)
        E(0x53, 0x56, 0x57, 0x41, 0x54);     // push rbx, rsi, rdi, r12
        E(0x48, 0x83, 0xEC, 0x20);           // sub rsp, 0x20  (shadow space)
        CallAbs(apply.addr);                 // call ApplyEquipFunctional
        E(0x48, 0x83, 0xC4, 0x20);           // add rsp, 0x20
        E(0x41, 0x5C, 0x5F, 0x5E, 0x5B);     // pop r12, rdi, rsi, rbx
        L("NEXT");
        E(0x49, 0xFF, 0xC4);                 // inc r12
        JmpNear("LOOP");
        L("VDONE");
        // capture noFallDmg from this player (rbx still valid) into FLAGCAP for off-frame inspection
        if (noFallOff > 0)
        {
            E(0x0F, 0xB6, 0x83); U32(noFallOff);  // movzx eax, byte [rbx+noFallOff]
            E(0x88, 0x05); abs32.Add((b.Count, FLAGCAP)); U32(0); // mov [rip+FLAGCAP], al
        }
        // restore (reverse)
        E(0x41, 0x5C, 0x5F, 0x5E, 0x5B);     // pop r12, rdi, rsi, rbx
        E(0x41, 0x5B, 0x41, 0x5A, 0x41, 0x59, 0x41, 0x58); // pop r11,r10,r9,r8
        E(0x5A, 0x59, 0x58);                 // pop rdx, rcx, rax
        b.AddRange(head.Take(disp));         // displaced prologue
        JmpAbs((ulong)(entry.ToInt64() + disp));

        var code = b.ToArray();
        foreach (var (pos, lbl) in rel32)
            BitConverter.GetBytes(labels[lbl] - (pos + 4)).CopyTo(code, pos);
        foreach (var (pos, target) in abs32)
            BitConverter.GetBytes((int)((long)target - (cave.ToInt64() + pos + 4))).CopyTo(code, pos);
        _mem.WriteBytes(cave, code);
        _mem.WriteBytes((IntPtr)(cave.ToInt64() + 0x1D0), new byte[0x30]); // zero counter/capture slots (AllocNear may reuse leaked region)

        var patch = new byte[disp];
        patch[0] = 0xE9;
        BitConverter.GetBytes((int)(cave.ToInt64() - (entry.ToInt64() + 5))).CopyTo(patch, 1);
        for (int k = 5; k < disp; k++) patch[k] = 0x90;

        var suspended = SuspendTargetThreads();
        try { _mem.WriteBytes(entry, patch); FlushInstructionCache(_mem.Handle, entry, (IntPtr)disp); }
        finally { foreach (var h in suspended) { ResumeThread(h); CloseHandle(h); } }

        _entryHooks[KEY] = (entry, head.Take(disp).ToArray(), cave);
        return true;
    }

    public void UnhookVanityAccessories() => UnhookEntry("vanityAccessories");

    public bool CanHookDropMultiplier() =>
        _model.Methods.TryGetValue("CommonCode.DropItem", out var d) && d.addr != 0;

    /// <summary>
    /// Multiply NPC loot stacks: cave at CommonCode.DropItem's entry does `imul r8d, r8d, factor`
    /// (r8d = the drop's stack count) before the method runs. Item.NewItem clamps overflows.
    /// </summary>
    public bool HookDropMultiplier(int factor)
    {
        const string KEY = "dropMult";
        UnhookEntry(KEY); // re-install to change the factor
        if (factor < 1) factor = 1;
        if (!_model.Methods.TryGetValue("CommonCode.DropItem", out var d) || d.addr == 0) return false;

        IntPtr entry = (IntPtr)d.addr;
        var head = _mem.ReadBytes(entry, 16);
        int disp = PrologueLen(head, 5);
        if (disp < 5) return false;

        IntPtr cave = _mem.AllocNear(entry, 0x40, MemoryProtection.ExecuteReadWrite);
        if (cave == IntPtr.Zero) return false;

        var cb = new List<byte> { 0x45, 0x69, 0xC0 };              // imul r8d, r8d, imm32
        cb.AddRange(BitConverter.GetBytes(factor));
        cb.AddRange(head.Take(disp));                              // displaced prologue
        cb.Add(0xE9);                                              // jmp back to entry+disp
        cb.AddRange(BitConverter.GetBytes((int)((entry.ToInt64() + disp) - (cave.ToInt64() + cb.Count + 4))));
        _mem.WriteBytes(cave, cb.ToArray());

        var patch = new byte[disp];
        patch[0] = 0xE9;
        BitConverter.GetBytes((int)(cave.ToInt64() - (entry.ToInt64() + 5))).CopyTo(patch, 1);
        for (int k = 5; k < disp; k++) patch[k] = 0x90;

        var suspended = SuspendTargetThreads();
        try { _mem.WriteBytes(entry, patch); FlushInstructionCache(_mem.Handle, entry, (IntPtr)disp); }
        finally { foreach (var h in suspended) { ResumeThread(h); CloseHandle(h); } }

        _entryHooks[KEY] = (entry, head.Take(disp).ToArray(), cave);
        return true;
    }

    public void UnhookDropMultiplier() => UnhookEntry("dropMult");

    /// <summary>True if we have the methods needed to install the vanity-accessory hook.</summary>
    public bool CanHookVanity() =>
        _model.Methods.TryGetValue("UpdateEquips", out var ue) && ue.addr != 0 &&
        _model.Methods.TryGetValue("ApplyEquipFunctional", out var ap) && ap.addr != 0;

    /// <summary>Cave base for an entry hook (diagnostics: counters live at cave+0x1F0/0x1F4).</summary>
    public IntPtr EntryCave(string key) => _entryHooks.TryGetValue(key, out var h) ? h.cave : IntPtr.Zero;

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
