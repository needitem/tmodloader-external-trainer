using TerrariaTrainer.Memory;

namespace TerrariaTrainer.Tml;

/// <summary>
/// One shared "run this managed static on the world process's GAME thread" channel. Several features
/// (force the Traveling Merchant to arrive, trigger weather/invasion events) need to call a vanilla
/// static on the server — a thing you can't do from the trainer's own thread without risking GC/CLR
/// state — and they'd otherwise each fight over the same hook site. Routing them through one cave fixes
/// that: a single flag-trigger cave at <c>NPC.NewNPC</c>'s entry (clean, JIT-stable, driven every world
/// spawn tick) reads a "pending" byte and, when set, calls whatever <c>method(ecx=arg0, edx=arg1)</c>
/// the caller wrote into the cave's data slot, then clears the byte. NewNPC's own argument registers are
/// preserved so its call continues untouched; the called static re-enters NewNPC only after the flag is
/// cleared, so it can't recurse. Targets <see cref="ServerTarget"/> (the server in MP, the client in SP).
///
/// NewNPC is shared with "Force Rare Spawns"; we refuse (clear status) rather than fight its entry patch.
/// </summary>
public sealed class ServerCaller
{
    private readonly ServerTarget _target;
    private readonly object _lock = new();

    private int _pid = -1;
    private ProcessMemory? _mem;        // non-owning ref to ServerTarget.Mem
    private bool _isServer;
    private IntPtr _cave, _entry;
    private ulong _hookAt, _pendAddr, _methodAddr, _arg0Addr, _arg1Addr;
    private byte[]? _orig;
    private string _status = "";

    public ServerCaller(ServerTarget target) => _target = target;
    public bool IsServer { get { lock (_lock) return _isServer; } }
    public ProcessMemory? Mem { get { lock (_lock) return _mem; } }
    public int Pid { get { lock (_lock) return _pid; } }
    public string Status { get { lock (_lock) return _status; } }

    /// <summary>Attach to the world process and ensure the dispatch cave is installed. Returns the shared
    /// memory (never dispose it) or null with a reason in <see cref="Status"/>.</summary>
    public ProcessMemory? Ensure()
    {
        lock (_lock) { return EnsureLocked(); }
    }

    private ProcessMemory? EnsureLocked()
    {
        var mem = _target.Ensure();
        int pid = _target.Pid;
        if (mem == null || pid < 0) { _status = "no game"; return null; }
        if (pid != _pid) { _cave = IntPtr.Zero; _entry = IntPtr.Zero; _orig = null; _hookAt = 0; _pid = pid; _isServer = _target.IsServer; _mem = mem; }
        _mem = mem;

        var cur = TmlDiscovery.ResolveCurrentAddresses(pid, new[] { ("nn", "Terraria.NPC", "NewNPC") });
        if (!cur.TryGetValue("nn", out var l) || l.Count == 0) { _status = "NewNPC not jitted"; return null; }
        ulong nn = RealCode(l[0]);

        byte first; try { first = mem.ReadByte((IntPtr)nn); } catch { _status = "read failed"; return null; }
        bool ours = _hookAt == nn && _cave != IntPtr.Zero;
        if (first == 0xE9 && !ours) { _status = "turn OFF 'Force Rare Spawns' first"; return null; }

        if (_hookAt != nn || !IsHooked(mem)) { RestoreHook(mem); if (!InstallCave(mem, nn)) return null; }
        return mem;
    }

    /// <summary>Queue a static call <c>method(ecx=arg0, edx=arg1)</c> to run on the next world spawn tick.
    /// 0-arg / 1-arg statics simply ignore the extra register(s).</summary>
    public bool Call(ulong methodAddr, int arg0 = 0, int arg1 = 0)
    {
        lock (_lock)
        {
            if (methodAddr == 0) { _status = "method not jitted"; return false; }
            var mem = EnsureLocked();
            if (mem == null) return false;
            try
            {
                mem.WriteBytes((IntPtr)_methodAddr, BitConverter.GetBytes((long)methodAddr));
                mem.WriteInt32((IntPtr)_arg0Addr, arg0);
                mem.WriteInt32((IntPtr)_arg1Addr, arg1);
                mem.WriteByte((IntPtr)_pendAddr, 1);
            }
            catch { _status = "write failed"; return false; }
            _status = $"queued ({(_isServer ? "server" : "single-player")})";
            return true;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            if (_mem != null) RestoreHook(_mem);
            _pid = -1; _mem = null; _hookAt = 0;
        }
    }

    private ulong RealCode(ulong addr)
    {
        for (int i = 0; i < 4; i++)
        {
            byte[] b; try { b = _mem!.ReadBytes((IntPtr)addr, 5); } catch { break; }
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

    private bool InstallCave(ProcessMemory mem, ulong hook)
    {
        IntPtr entry = (IntPtr)hook;
        var head = mem.ReadBytes(entry, 24);
        int disp = PrologueLen(head, 5);
        if (disp < 5) { _status = "hook prologue unsafe"; return false; }
        IntPtr cave = mem.AllocNear(entry, 0x100, Native.MemoryProtection.ExecuteReadWrite);
        if (cave == IntPtr.Zero) { _status = "cave alloc failed"; return false; }

        // data slot lives past the code, at cave+0x80: pending(+0), method(+8), arg0(+0x10), arg1(+0x14)
        ulong dat = (ulong)cave.ToInt64() + 0x80;
        ulong pend = dat, method = dat + 8, arg0 = dat + 0x10, arg1 = dat + 0x14;

        var b = new List<byte>(); void E(params byte[] x) => b.AddRange(x); void U32(int v) => b.AddRange(BitConverter.GetBytes(v));
        E(0x50);                                                  // push rax
        E(0x48, 0xB8); b.AddRange(BitConverter.GetBytes((long)pend)); // mov rax, &pending
        E(0x80, 0x38, 0x00);                                      // cmp byte [rax], 0
        E(0x0F, 0x84); int jSkip = b.Count; U32(0);               // je SKIP
        E(0xC6, 0x00, 0x00);                                      // mov byte [rax], 0   (one-shot clear, before the call)
        E(0x58);                                                  // pop rax
        E(0x51, 0x52, 0x41, 0x50, 0x41, 0x51, 0x41, 0x52, 0x41, 0x53, 0x50); // push rcx,rdx,r8,r9,r10,r11,rax (preserve NewNPC args)
        E(0x48, 0x83, 0xEC, 0x20);                                // sub rsp, 0x20  (shadow; 7 pushes already 16-aligned)
        E(0x48, 0xB8); b.AddRange(BitConverter.GetBytes((long)dat)); // mov rax, &data
        E(0x8B, 0x48, 0x10);                                      // mov ecx, [rax+0x10]   arg0
        E(0x8B, 0x50, 0x14);                                      // mov edx, [rax+0x14]   arg1
        E(0x48, 0x8B, 0x40, 0x08);                                // mov rax, [rax+0x08]   method
        E(0xFF, 0xD0);                                            // call rax
        E(0x48, 0x83, 0xC4, 0x20);                                // add rsp, 0x20
        E(0x58, 0x41, 0x5B, 0x41, 0x5A, 0x41, 0x59, 0x41, 0x58, 0x5A, 0x59); // pop rax,r11,r10,r9,r8,rdx,rcx
        E(0xE9); int jDone = b.Count; U32(0);                     // jmp DONE
        int skip = b.Count;                                       // SKIP:
        E(0x58);                                                  // pop rax
        int done = b.Count;                                       // DONE:

        b.AddRange(head.Take(disp));                              // displaced prologue
        b.Add(0xE9); b.AddRange(BitConverter.GetBytes((int)((entry.ToInt64() + disp) - (cave.ToInt64() + b.Count + 4))));
        var code = b.ToArray();
        BitConverter.GetBytes(skip - (jSkip + 4)).CopyTo(code, jSkip);
        BitConverter.GetBytes(done - (jDone + 4)).CopyTo(code, jDone);
        if (code.Length > 0x80) { _status = "cave code too big"; try { mem.Free(cave); } catch { } return false; }
        mem.WriteBytes(cave, code);
        mem.WriteBytes((IntPtr)dat, new byte[0x20]); // zero the data slot (AllocNear may reuse a leaked region)

        var patch = new byte[disp];
        patch[0] = 0xE9;
        BitConverter.GetBytes((int)(cave.ToInt64() - (entry.ToInt64() + 5))).CopyTo(patch, 1);
        for (int k = 5; k < disp; k++) patch[k] = 0x90;

        if (_cave != IntPtr.Zero && _cave != cave) { try { mem.Free(_cave); } catch { } }
        _orig = head.Take(disp).ToArray(); _entry = entry; _hookAt = hook; _cave = cave;
        _pendAddr = pend; _methodAddr = method; _arg0Addr = arg0; _arg1Addr = arg1;
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
