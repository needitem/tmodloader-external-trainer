using TerrariaTrainer.Memory;

namespace TerrariaTrainer.Tml;

/// <summary>
/// On-demand Traveling-Merchant control, run on the GAME thread (a managed method can't be called
/// from the trainer's own thread without risking GC/CLR state), via a tiny flag-trigger cave:
///   • Re-roll  — invoke <c>Chest.SetupTravelShop()</c> to re-fill <c>Main.travelShop</c>.
///   • Summon   — re-roll, then <c>NPC.SpawnOnPlayer(Main.myPlayer, 368)</c> to make the merchant
///                arrive (SP spawns directly; MP-client forwards the request to the host server).
///
/// The cave sits at the entry of <c>Main.DoUpdate_Enter_ToggleChat</c> (a clean, non-detoured,
/// per-frame in-game method). Each frame it reads a flag byte and, if zero, restores its two saved
/// registers and falls through — a balanced, harmless no-op. When the user clicks, the trainer sets
/// the flag (bit0 = re-roll, bit1 = summon); the next frame the cave saves the volatile registers,
/// aligns the stack, runs the requested call(s), clears the flag (one-shot) and continues. The risky
/// call path only executes on a click; idle frames can't crash while it sits armed.
///
/// Client-side: the shop the host sees is built from the client's own <c>Main.travelShop</c>, so the
/// re-roll must run there. Re-resolves addresses each click to survive tiered-JIT relocation.
/// </summary>
public sealed class TravelShopPatcher
{
    private const int TravelingMerchant = 368; // NPCID.TravelingMerchant

    private readonly TmlEngine _engine;
    private readonly object _lock = new();
    private IntPtr _cave, _flag, _entry;
    private ulong _hookAt;
    private byte[]? _orig;
    private bool _summonCap;   // does the installed cave include the SpawnOnPlayer block?
    private string _status = "";

    public TravelShopPatcher(TmlEngine engine) => _engine = engine;
    public string Status { get { lock (_lock) return _status; } }

    /// <summary>Re-roll the shop stock. Returns false (with a status) if it couldn't be set up.</summary>
    public bool Reroll() => Fire(summon: false);

    /// <summary>Re-roll then force the Traveling Merchant to arrive.</summary>
    public bool Summon() => Fire(summon: true);

    private bool Fire(bool summon)
    {
        lock (_lock)
        {
            var mem = _engine.Mem; var proc = _engine.Proc;
            if (mem == null || proc == null) { _status = "not attached"; return false; }

            var cur = TmlDiscovery.ResolveCurrentAddresses(proc.Id, new[]
            {
                ("hook", "Terraria.Main", "DoUpdate_Enter_ToggleChat"),
                ("setup", "Terraria.Chest", "SetupTravelShop"),
                ("spawn", "Terraria.NPC", "SpawnOnPlayer"),
            });
            if (!cur.TryGetValue("hook", out var hookList) || hookList.Count == 0) { _status = "hook method not jitted"; return false; }
            if (!cur.TryGetValue("setup", out var setupList) || setupList.Count == 0) { _status = "SetupTravelShop not jitted"; return false; }
            ulong hook = RealCode(mem, hookList[0]);     // cave the real body
            ulong setup = setupList[0];                  // call the stable entry (jmps to real code if a precode)

            ulong spawn = 0, myPlayer = _engine.Model?.StaticMyPlayer ?? 0;
            if (summon)
            {
                if (!cur.TryGetValue("spawn", out var spawnList) || spawnList.Count == 0)
                { _status = "SpawnOnPlayer not warmed up — summon any boss once, then retry"; return false; }
                if (myPlayer == 0) { _status = "Main.myPlayer not resolved"; return false; }
                spawn = spawnList[0];
            }

            if (_flag == IntPtr.Zero)
                _flag = mem.AllocNear((IntPtr)hook, 8, Native.MemoryProtection.ExecuteReadWrite);
            if (_flag == IntPtr.Zero) { _status = "flag alloc failed"; return false; }

            // (re)install the cave if it's gone, moved, or lacks the summon block we now need
            if (_hookAt != hook || !IsHooked(mem) || (summon && !_summonCap))
            {
                RestoreHook(mem);
                if (!InstallCave(mem, hook, setup, summon ? spawn : 0, myPlayer)) return false;
            }

            mem.WriteByte(_flag, (byte)(summon ? 0b11 : 0b01)); // fire next frame
            _status = summon ? "summon queued" : "re-roll queued";
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
            if (_cave != IntPtr.Zero && t >= (ulong)_cave.ToInt64() && t < (ulong)_cave.ToInt64() + 0x100) break;
            addr = t;
        }
        return addr;
    }

    private bool IsHooked(ProcessMemory mem)
    {
        try { return _cave != IntPtr.Zero && _hookAt != 0 && mem.ReadByte((IntPtr)_hookAt) == 0xE9; }
        catch { return false; }
    }

    private bool InstallCave(ProcessMemory mem, ulong hook, ulong setup, ulong spawn, ulong myPlayer)
    {
        IntPtr entry = (IntPtr)hook;
        var head = mem.ReadBytes(entry, 24);
        int disp = PrologueLen(head, 5);
        if (disp < 5) { _status = "hook prologue unsafe"; return false; }
        IntPtr cave = mem.AllocNear(entry, 0x100, Native.MemoryProtection.ExecuteReadWrite);
        if (cave == IntPtr.Zero) { _status = "cave alloc failed"; return false; }
        bool summon = spawn != 0 && myPlayer != 0;

        var b = new List<byte>();
        void E(params byte[] x) => b.AddRange(x);
        void U64(long v) => b.AddRange(BitConverter.GetBytes(v));
        void U32(int v) => b.AddRange(BitConverter.GetBytes(v));

        // --- idle-safe header: save rax/rbx, load flag into ebx, bail if zero ---
        E(0x50);                                  // push rax            (A: orig rax)
        E(0x53);                                  // push rbx            (B: orig rbx, non-volatile → survives calls)
        E(0x48, 0xB8); U64(_flag.ToInt64());      // mov  rax, &flag
        E(0x0F, 0xB6, 0x18);                      // movzx ebx, byte [rax]   ebx = flag bits
        E(0x84, 0xDB);                            // test bl, bl
        E(0x0F, 0x84); int jExit = b.Count; U32(0); // jz   EXIT  (flag==0 → idle)
        E(0xC6, 0x00, 0x00);                      // mov  byte [rax], 0      one-shot clear

        // --- save volatiles, build a 16-aligned call frame ---
        E(0x51, 0x52);                            // push rcx, rdx
        E(0x41, 0x50, 0x41, 0x51, 0x41, 0x52, 0x41, 0x53); // push r8,r9,r10,r11   (8 pushes total → rsp≡8)
        E(0x48, 0x83, 0xEC, 0x28);                // sub  rsp, 0x28          0x20 shadow + 8 align → rsp≡0

        // --- always: SetupTravelShop() ---
        E(0x48, 0xB8); U64((long)setup);          // mov  rax, SetupTravelShop
        E(0xFF, 0xD0);                            // call rax

        int jAfter = -1;
        if (summon)
        {
            // --- if (flag & 2) NPC.SpawnOnPlayer(Main.myPlayer, 368) ---
            E(0xF6, 0xC3, 0x02);                  // test bl, 2
            E(0x0F, 0x84); jAfter = b.Count; U32(0); // jz   AFTER
            E(0x48, 0xB8); U64((long)myPlayer);   // mov  rax, &Main.myPlayer
            E(0x8B, 0x08);                        // mov  ecx, [rax]         arg0 = myPlayer
            E(0xBA); U32(TravelingMerchant);      // mov  edx, 368           arg1 = type
            E(0x48, 0xB8); U64((long)spawn);      // mov  rax, SpawnOnPlayer
            E(0xFF, 0xD0);                        // call rax
        }

        int after = b.Count;                      // AFTER:
        E(0x48, 0x83, 0xC4, 0x28);                // add  rsp, 0x28
        E(0x41, 0x5B, 0x41, 0x5A, 0x41, 0x59, 0x41, 0x58); // pop r11,r10,r9,r8
        E(0x5A, 0x59);                            // pop  rdx, rcx
        int exit = b.Count;                       // EXIT:  (idle path lands here)
        E(0x5B);                                  // pop  rbx
        E(0x58);                                  // pop  rax

        b.AddRange(head.Take(disp));              // displaced prologue
        b.Add(0xE9); U32((int)((entry.ToInt64() + disp) - (cave.ToInt64() + b.Count + 4))); // jmp back

        var code = b.ToArray();
        BitConverter.GetBytes(exit - (jExit + 4)).CopyTo(code, jExit);
        if (summon) BitConverter.GetBytes(after - (jAfter + 4)).CopyTo(code, jAfter);
        mem.WriteBytes(cave, code);

        var patch = new byte[disp];
        patch[0] = 0xE9;
        BitConverter.GetBytes((int)(cave.ToInt64() - (entry.ToInt64() + 5))).CopyTo(patch, 1);
        for (int k = 5; k < disp; k++) patch[k] = 0x90;

        _orig = head.Take(disp).ToArray(); _entry = entry; _hookAt = hook; _cave = cave; _summonCap = summon;
        mem.WriteBytes(entry, patch);
        return true;
    }

    private void RestoreHook(ProcessMemory mem)
    {
        if (_orig != null && _entry != IntPtr.Zero) { try { mem.WriteBytes(_entry, _orig); } catch { } }
        if (_cave != IntPtr.Zero) { try { mem.Free(_cave); } catch { } }
        _orig = null; _entry = IntPtr.Zero; _cave = IntPtr.Zero; _hookAt = 0; _summonCap = false;
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
