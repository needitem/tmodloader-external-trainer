using TerrariaTrainer.Memory;

namespace TerrariaTrainer.Tml;

/// <summary>
/// "Run a managed INSTANCE method on the CLIENT's game thread" channel — the mirror of
/// <see cref="ServerCaller"/>, but it hooks the local game loop (<c>Main.Update</c>) instead of the
/// server's spawn path, and it can pass a full 64-bit <c>this</c> pointer (rcx) plus one int arg (edx).
///
/// Some edits can't be done by poking memory from the trainer's own thread: giving an inventory slot a
/// real item means running <c>Item.SetDefaults(type)</c> so every field (use-time, damage, max-stack, the
/// ModItem instance, …) is initialised correctly. Writing only <c>type</c> leaves a half-formed item that
/// freezes the player's item-use loop. <c>SetDefaults</c> is an instance method on the slot's Item object,
/// which lives in the CLIENT process — so this caller targets the client, not the world server.
///
/// A one-shot dispatch cave at <c>Main.Update</c>'s entry reads a "pending" byte and, when set, calls
/// <c>method(rcx=this, edx=arg)</c> once, then clears the byte. Main.Update's own registers are preserved
/// and the displaced prologue runs after, so the game loop continues untouched. Re-resolves + re-installs
/// when the JIT relocates the method (Tier0→Tier1).
/// </summary>
public sealed class ClientCaller
{
    private readonly TmlEngine _engine;
    private readonly object _lock = new();

    private int _pid = -1;
    private ProcessMemory? _mem;        // non-owning ref to TmlEngine.Mem (client)
    private IntPtr _cave, _entry;
    private ulong _hookAt, _pendAddr, _methodAddr, _thisAddr, _argAddr;
    private byte[]? _orig;
    private string _status = "";

    public ClientCaller(TmlEngine engine) => _engine = engine;
    public string Status { get { lock (_lock) return _status; } }

    private ProcessMemory? EnsureLocked()
    {
        var mem = _engine.Mem; int pid = _engine.Proc?.Id ?? -1;
        if (mem == null || pid < 0) { _status = "no game"; return null; }
        if (pid != _pid) { _cave = IntPtr.Zero; _entry = IntPtr.Zero; _orig = null; _hookAt = 0; _pid = pid; _mem = mem; }
        _mem = mem;

        var cur = TmlDiscovery.ResolveCurrentAddresses(pid, new[] { ("u", "Terraria.Main", "Update") });
        if (!cur.TryGetValue("u", out var l) || l.Count == 0) { _status = "Main.Update not jitted"; return null; }
        ulong up = l[0];

        if (_hookAt != up || !IsHooked(mem)) { RestoreHook(mem); if (!InstallCave(mem, up)) return null; }
        return mem;
    }

    /// <summary>Queue an instance call <c>method(rcx=thisPtr, edx=arg)</c> on the next client frame.</summary>
    public bool CallInstance(ulong methodAddr, ulong thisPtr, int arg)
    {
        lock (_lock)
        {
            if (methodAddr == 0) { _status = "method not jitted"; return false; }
            if (thisPtr == 0) { _status = "null this"; return false; }
            var mem = EnsureLocked();
            if (mem == null) return false;
            try
            {
                mem.WriteBytes((IntPtr)_methodAddr, BitConverter.GetBytes((long)methodAddr));
                mem.WriteBytes((IntPtr)_thisAddr, BitConverter.GetBytes((long)thisPtr));
                mem.WriteInt32((IntPtr)_argAddr, arg);
                mem.WriteByte((IntPtr)_pendAddr, 1);
            }
            catch { _status = "write failed"; return false; }
            _status = "queued";
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

        // data slot at cave+0x80: pending(+0), method(+8), this(+0x10, 8B), arg(+0x18, 4B)
        ulong dat = (ulong)cave.ToInt64() + 0x80;
        ulong pend = dat, method = dat + 8, thisp = dat + 0x10, argp = dat + 0x18;

        var b = new List<byte>(); void E(params byte[] x) => b.AddRange(x); void U32(int v) => b.AddRange(BitConverter.GetBytes(v));
        E(0x50);                                                  // push rax
        E(0x48, 0xB8); b.AddRange(BitConverter.GetBytes((long)pend)); // mov rax, &pending
        E(0x80, 0x38, 0x00);                                      // cmp byte [rax], 0
        E(0x0F, 0x84); int jSkip = b.Count; U32(0);               // je SKIP
        E(0xC6, 0x00, 0x00);                                      // mov byte [rax], 0   (one-shot clear, before the call)
        E(0x58);                                                  // pop rax
        E(0x51, 0x52, 0x41, 0x50, 0x41, 0x51, 0x41, 0x52, 0x41, 0x53, 0x50); // push rcx,rdx,r8,r9,r10,r11,rax
        E(0x48, 0x83, 0xEC, 0x20);                                // sub rsp, 0x20  (shadow; 7 pushes already 16-aligned)
        E(0x48, 0xB8); b.AddRange(BitConverter.GetBytes((long)dat)); // mov rax, &data
        E(0x48, 0x8B, 0x48, 0x10);                                // mov rcx, [rax+0x10]   this (64-bit)
        E(0x8B, 0x50, 0x18);                                      // mov edx, [rax+0x18]   arg
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
        mem.WriteBytes((IntPtr)dat, new byte[0x20]); // zero the data slot

        var patch = new byte[disp];
        patch[0] = 0xE9;
        BitConverter.GetBytes((int)(cave.ToInt64() - (entry.ToInt64() + 5))).CopyTo(patch, 1);
        for (int k = 5; k < disp; k++) patch[k] = 0x90;

        if (_cave != IntPtr.Zero && _cave != cave) { try { mem.Free(_cave); } catch { } }
        _orig = head.Take(disp).ToArray(); _entry = entry; _hookAt = hook; _cave = cave;
        _pendAddr = pend; _methodAddr = method; _thisAddr = thisp; _argAddr = argp;
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
