using TerrariaTrainer.Memory;

namespace TerrariaTrainer.Tml;

/// <summary>
/// Multiplies NPC loot stacks — on the WORLD-AUTHORITATIVE process. NPC loot is rolled and spawned by
/// the server (in single-player, by the one process), so a client-side patch never touches it; this is
/// why the drop multiplier must target <see cref="ServerTarget"/>, exactly like 100%-drop / spawn-boost.
///
/// Rule-based loot funnels through <c>CommonCode.DropItem(DropAttemptInfo info, int itemId, int stack,
/// bool scattered)</c>. <c>DropAttemptInfo</c> is a large struct passed by hidden pointer (rcx), so
/// itemId is in edx and the stack count is in <b>r8d</b>. A cave at the method's entry does
/// <c>imul r8d, r8d, factor</c> before the body runs; <c>Item.NewItem</c> clamps overflow. The cave is
/// re-asserted (and the address re-resolved by signature) so the tiered JIT can't relocate past it.
/// </summary>
public sealed class DropMultPatcher
{
    private const string Type = "Terraria.GameContent.ItemDropRules.CommonCode";
    private const string Sig = "DropAttemptInfo"; // disambiguates the rule-based DropItem overload

    private readonly ServerTarget _target;
    private readonly object _lock = new();
    private volatile bool _enabled;
    private int _factor = 5;

    private int _targetPid = -1;
    private ProcessMemory? _mem;              // non-owning ref to ServerTarget.Mem
    private bool _isServer;
    private IntPtr _cave, _entry;
    private ulong _hookAt;
    private byte[]? _orig;
    private string _status = "off";

    public DropMultPatcher(ServerTarget target) => _target = target;
    public bool Enabled => _enabled;
    public string Status { get { lock (_lock) return _status; } }

    public void SetFactor(int f) { lock (_lock) { _factor = Math.Clamp(f, 1, 1000); if (_enabled) { RestoreHook(); EnsureTargetAndHook(); } } }
    public void Enable(int f) { lock (_lock) { _factor = Math.Clamp(f, 1, 1000); _enabled = true; EnsureTargetAndHook(); } }
    public void Disable() { lock (_lock) { _enabled = false; RestoreHook(); ResetTarget(); _status = "off"; } }
    public void Clear() { lock (_lock) { _cave = IntPtr.Zero; _entry = IntPtr.Zero; _orig = null; _hookAt = 0; ResetTarget(); } }

    /// <summary>Called ~every few seconds from the GUI background thread; re-asserts across relocation.</summary>
    public void Tick() { if (!_enabled) return; try { lock (_lock) { if (_enabled) EnsureTargetAndHook(); } } catch { } }

    private void ResetTarget() { _targetPid = -1; _isServer = false; _mem = null; }

    private void EnsureTargetAndHook()
    {
        var mem = _target.Ensure();
        int wantPid = _target.Pid;
        if (mem == null || wantPid < 0) { _status = "no game"; return; }

        if (wantPid != _targetPid)
        {
            RestoreHook(); ResetTarget();
            _targetPid = wantPid; _isServer = _target.IsServer; _mem = mem;
        }
        if (_mem == null) { _status = "no mem"; return; }

        ulong raw = TmlDiscovery.ResolveMethodAddr(wantPid, Type, "DropItem", Sig);
        if (raw == 0) { _status = "DropItem not jitted (kill a mob to warm it)"; return; }
        ulong addr = RealCode(raw);

        if (_hookAt != addr || !IsHooked(addr)) { RestoreHook(); if (!InstallCave(addr)) return; }
        _status = $"{(_isServer ? "server" : "single-player")} x{_factor} (patched @0x{addr:X})";
    }

    private ulong RealCode(ulong addr)
    {
        for (int i = 0; i < 4; i++)
        {
            byte[] b; try { b = _mem!.ReadBytes((IntPtr)addr, 5); } catch { break; }
            if (b.Length < 5 || b[0] != 0xE9) break;
            ulong t = (ulong)((long)addr + 5 + BitConverter.ToInt32(b, 1));
            if (_cave != IntPtr.Zero && t >= (ulong)_cave.ToInt64() && t < (ulong)_cave.ToInt64() + 0x40) break;
            addr = t;
        }
        return addr;
    }

    private bool IsHooked(ulong addr)
    {
        try { return _cave != IntPtr.Zero && _hookAt == addr && _mem!.ReadByte((IntPtr)addr) == 0xE9; }
        catch { return false; }
    }

    private bool InstallCave(ulong addr)
    {
        var mem = _mem!;
        IntPtr entry = (IntPtr)addr;
        var head = mem.ReadBytes(entry, 16);
        int disp = PrologueLen(head, 5);
        if (disp < 5) { _status = "prologue unsafe"; return false; }
        IntPtr cave = mem.AllocNear(entry, 0x40, Native.MemoryProtection.ExecuteReadWrite);
        if (cave == IntPtr.Zero) { _status = "cave alloc failed"; return false; }

        var cb = new List<byte> { 0x45, 0x69, 0xC0 };                 // imul r8d, r8d, imm32  (scale the stack count)
        cb.AddRange(BitConverter.GetBytes(_factor));
        cb.AddRange(head.Take(disp));                                 // displaced prologue
        cb.Add(0xE9);                                                 // jmp back to entry+disp
        cb.AddRange(BitConverter.GetBytes((int)((entry.ToInt64() + disp) - (cave.ToInt64() + cb.Count + 4))));
        mem.WriteBytes(cave, cb.ToArray());

        var patch = new byte[disp];
        patch[0] = 0xE9;
        BitConverter.GetBytes((int)(cave.ToInt64() - (entry.ToInt64() + 5))).CopyTo(patch, 1);
        for (int k = 5; k < disp; k++) patch[k] = 0x90;

        if (_cave != IntPtr.Zero && _cave != cave) { try { mem.Free(_cave); } catch { } }
        _orig = head.Take(disp).ToArray(); _entry = entry; _hookAt = addr; _cave = cave;
        mem.WriteBytes(entry, patch);
        return true;
    }

    private void RestoreHook()
    {
        if (_mem != null && _orig != null && _entry != IntPtr.Zero) { try { _mem.WriteBytes(_entry, _orig); } catch { } }
        if (_mem != null && _cave != IntPtr.Zero) { try { _mem.Free(_cave); } catch { } }
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
