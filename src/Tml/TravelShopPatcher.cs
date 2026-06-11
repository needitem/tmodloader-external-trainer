using TerrariaTrainer.Memory;

namespace TerrariaTrainer.Tml;

/// <summary>
/// Re-rolls the Traveling Merchant's stock on demand by invoking <c>Chest.SetupTravelShop()</c>
/// (which re-fills <c>Main.travelShop</c> from the weighted pool) — but a managed method can't be
/// called from the trainer's own thread (GC/CLR safety), so we run it on the GAME thread via a cave.
///
/// A small cave is installed at the entry of <c>Main.DoUpdate_Enter_ToggleChat</c> (a clean, non-
/// detoured, per-frame in-game method). Every frame the cave just checks a flag byte and falls
/// through (harmless). When the user clicks "Re-roll" the trainer sets the flag; the next frame the
/// cave saves the volatile registers, aligns the stack, calls <c>SetupTravelShop</c>, restores, and
/// clears the flag — exactly as if the game had re-rolled the shop. The risky call path only executes
/// on a click; idle frames are a balanced push/pop, so it can't crash while sitting armed.
/// Client-side (the shop lives on the client). Re-resolves addresses each click vs tiered-JIT moves.
/// </summary>
public sealed class TravelShopPatcher
{
    private readonly TmlEngine _engine;
    private readonly object _lock = new();
    private IntPtr _cave, _flag, _entry;
    private ulong _hookAt;
    private byte[]? _orig;
    private string _status = "";

    public TravelShopPatcher(TmlEngine engine) => _engine = engine;
    public string Status { get { lock (_lock) return _status; } }

    /// <summary>Arm + fire one re-roll. Returns false (with a status) if it couldn't be set up.</summary>
    public bool Reroll()
    {
        lock (_lock)
        {
            var mem = _engine.Mem; var proc = _engine.Proc;
            if (mem == null || proc == null) { _status = "not attached"; return false; }

            var cur = TmlDiscovery.ResolveCurrentAddresses(proc.Id, new[]
            {
                ("hook", "Terraria.Main", "DoUpdate_Enter_ToggleChat"),
                ("call", "Terraria.Chest", "SetupTravelShop"),
            });
            if (!cur.TryGetValue("hook", out var hookList) || hookList.Count == 0) { _status = "hook method not jitted"; return false; }
            if (!cur.TryGetValue("call", out var callList) || callList.Count == 0) { _status = "SetupTravelShop not jitted"; return false; }
            ulong hook = RealCode(mem, hookList[0]);     // cave the real body
            ulong setup = callList[0];                   // call the stable entry (jmps to real code if a precode)

            if (_flag == IntPtr.Zero)
                _flag = mem.AllocNear((IntPtr)hook, 8, Native.MemoryProtection.ExecuteReadWrite);
            if (_flag == IntPtr.Zero) { _status = "flag alloc failed"; return false; }

            if (_hookAt != hook || !IsHooked(mem)) { RestoreHook(mem); if (!InstallCave(mem, hook, setup)) return false; }

            mem.WriteByte(_flag, 1);                      // fire: the cave calls SetupTravelShop next frame
            _status = "re-roll queued";
            return true;
        }
    }

    /// <summary>Remove the cave (e.g. on detach / app close).</summary>
    public void Clear()
    {
        lock (_lock)
        {
            var mem = _engine.Mem;
            if (mem != null) RestoreHook(mem);
            if (mem != null && _flag != IntPtr.Zero) { try { mem.Free(_flag); } catch { } }
            _flag = IntPtr.Zero; _hookAt = 0;
        }
    }

    private ulong RealCode(ProcessMemory mem, ulong addr)
    {
        for (int i = 0; i < 4; i++)
        {
            byte[] b; try { b = mem.ReadBytes((IntPtr)addr, 5); } catch { break; }
            if (b.Length < 5 || b[0] != 0xE9) break;
            ulong t = (ulong)((long)addr + 5 + BitConverter.ToInt32(b, 1));
            if (_cave != IntPtr.Zero && t >= (ulong)_cave.ToInt64() && t < (ulong)_cave.ToInt64() + 0xC0) break;
            addr = t;
        }
        return addr;
    }

    private bool IsHooked(ProcessMemory mem)
    {
        try { return _cave != IntPtr.Zero && _hookAt != 0 && mem.ReadByte((IntPtr)_hookAt) == 0xE9; }
        catch { return false; }
    }

    private bool InstallCave(ProcessMemory mem, ulong hook, ulong setup)
    {
        IntPtr entry = (IntPtr)hook;
        var head = mem.ReadBytes(entry, 24);
        int disp = PrologueLen(head, 5);
        if (disp < 5) { _status = "hook prologue unsafe"; return false; }
        IntPtr cave = mem.AllocNear(entry, 0xC0, Native.MemoryProtection.ExecuteReadWrite);
        if (cave == IntPtr.Zero) { _status = "cave alloc failed"; return false; }

        var b = new List<byte>(); void E(params byte[] x) => b.AddRange(x); void U32(int v) => b.AddRange(BitConverter.GetBytes(v));
        E(0x50);                                                  // push rax
        E(0x48, 0xB8); b.AddRange(BitConverter.GetBytes(_flag.ToInt64())); // mov rax, &flag
        E(0x80, 0x38, 0x00);                                      // cmp byte [rax], 0
        E(0x0F, 0x84); int jSkip = b.Count; U32(0);               // je SKIP
        E(0xC6, 0x00, 0x00);                                      // mov byte [rax], 0   (clear flag — one-shot)
        E(0x58);                                                  // pop rax (restore original rax)
        // save volatiles, align, call SetupTravelShop, restore
        E(0x51, 0x52, 0x41, 0x50, 0x41, 0x51, 0x41, 0x52, 0x41, 0x53, 0x50); // push rcx,rdx,r8,r9,r10,r11,rax
        E(0x48, 0x83, 0xEC, 0x20);                                // sub rsp, 0x20  (shadow space, stays 16-aligned)
        E(0x48, 0xB8); b.AddRange(BitConverter.GetBytes((long)setup)); // mov rax, SetupTravelShop
        E(0xFF, 0xD0);                                            // call rax
        E(0x48, 0x83, 0xC4, 0x20);                                // add rsp, 0x20
        E(0x58, 0x41, 0x5B, 0x41, 0x5A, 0x41, 0x59, 0x41, 0x58, 0x5A, 0x59); // pop rax,r11,r10,r9,r8,rdx,rcx
        E(0xE9); int jDone = b.Count; U32(0);                     // jmp DONE
        int skip = b.Count;                                       // SKIP:
        E(0x58);                                                  // pop rax (restore original rax)
        int done = b.Count;                                       // DONE:

        b.AddRange(head.Take(disp));                              // displaced prologue
        b.Add(0xE9); b.AddRange(BitConverter.GetBytes((int)((entry.ToInt64() + disp) - (cave.ToInt64() + b.Count + 4))));
        var code = b.ToArray();
        BitConverter.GetBytes(skip - (jSkip + 4)).CopyTo(code, jSkip);
        BitConverter.GetBytes(done - (jDone + 4)).CopyTo(code, jDone);
        mem.WriteBytes(cave, code);

        var patch = new byte[disp];
        patch[0] = 0xE9;
        BitConverter.GetBytes((int)(cave.ToInt64() - (entry.ToInt64() + 5))).CopyTo(patch, 1);
        for (int k = 5; k < disp; k++) patch[k] = 0x90;

        if (_cave != IntPtr.Zero && _cave != cave) { try { mem.Free(_cave); } catch { } }
        _orig = head.Take(disp).ToArray(); _entry = entry; _hookAt = hook; _cave = cave;
        mem.WriteBytes(entry, patch);
        return true;
    }

    private void RestoreHook(ProcessMemory mem)
    {
        if (_orig != null && _entry != IntPtr.Zero) { try { mem.WriteBytes(_entry, _orig); } catch { } }
        if (_cave != IntPtr.Zero) { try { mem.Free(_cave); } catch { } }
        _orig = null; _entry = IntPtr.Zero; _cave = IntPtr.Zero; _hookAt = 0;
    }

    private static int PrologueLen(byte[] code, int min)
    {
        var dec = Iced.Intel.Decoder.Create(64, code, Iced.Intel.DecoderOptions.None);
        int i = 0;
        while (i < min)
        {
            var ins = dec.Decode();
            if (ins.IsInvalid) return 0;
            if (ins.IsIPRelativeMemoryOperand || ins.FlowControl != Iced.Intel.FlowControl.Next) return 0;
            i += ins.Length;
        }
        return i;
    }
}
