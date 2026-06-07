using System.Diagnostics;
using System.Text;
using TerrariaTrainer.Memory;

namespace TerrariaTrainer.Tml;

/// <summary>
/// "Force rare spawns" — makes every naturally-spawned enemy come out as one chosen NPC type, so a
/// rare mob you'd normally wait hours for spawns constantly. Works in single-player AND on a
/// multiplayer Host &amp; Play server (spawning is server-authoritative).
///
/// All NPC creation funnels through <c>NPC.NewNPC(IEntitySource source, int X, int Y, int Type, …)</c>
/// (Type is in r9d). The natural/ambient spawner is <c>NPC.SpawnNPC</c>; bosses, town NPCs, statues
/// and projectile-spawned NPCs call NewNPC from other methods. A cave at NewNPC's entry reads the
/// return address ([rsp], since the entry patch is a jmp that doesn't touch rsp) and, only when it
/// lands inside <c>SpawnNPC</c>'s code range, overwrites r9d with the chosen type. This scopes by
/// caller (bulletproof) rather than by source object. The cave is re-asserted and the SpawnNPC range
/// re-resolved so the tiered JIT can't relocate past it; on disable it's removed.
/// </summary>
public sealed class RareSpawnPatcher
{
    private readonly TmlEngine _client;
    private readonly object _lock = new();
    private volatile bool _enabled;
    private int _type;                       // chosen NPC type to force

    private int _targetPid = -1;
    private ProcessMemory? _mem;
    private bool _isServer;
    private ulong _hotLo, _hotHi, _coldLo, _coldHi;  // SpawnNPC hot+cold code ranges (natural-spawn caller)
    private IntPtr _cfg;                      // 16 bytes: [0]=enabled, [4]=type, [8]=hit counter
    private string _status = "off";

    private sealed class Hook { public IntPtr Cave; public ulong At; public byte[]? Orig; public ulong OrigAddr; }
    private readonly Hook _nn = new();        // NPC.NewNPC

    public RareSpawnPatcher(TmlEngine client) => _client = client;

    public bool Enabled => _enabled;
    public string Status { get { lock (_lock) return _status; } }

    public void SetType(int type) { lock (_lock) { _type = type; if (_mem != null && _cfg != IntPtr.Zero) try { _mem.WriteInt32((IntPtr)(_cfg.ToInt64() + 4), _type); } catch { } } }

    public void Enable() => _enabled = true;
    public void Disable() { lock (_lock) { _enabled = false; RestoreAll(); ResetTarget(); _status = "off"; } }
    public void Clear() { lock (_lock) { _nn.Orig = null; _nn.Cave = IntPtr.Zero; _nn.At = 0; _cfg = IntPtr.Zero; ResetTarget(); } }

    private void ResetTarget()
    {
        _targetPid = -1; _isServer = false; _hotLo = _hotHi = _coldLo = _coldHi = 0;
        try { _mem?.Dispose(); } catch { }
        _mem = null;
    }

    /// <summary>Called ~every 2.5s from the GUI background thread.</summary>
    public void Tick()
    {
        if (!_enabled) return;
        try { lock (_lock) { if (_enabled) EnsureTargetAndHook(); } } catch { /* transient */ }
    }

    private void EnsureTargetAndHook()
    {
        if (_type <= 0) { _status = "pick a rare mob first"; return; }
        var server = TmlDiscovery.FindServerProcess();
        int wantPid = server?.Id ?? _client.Proc?.Id ?? -1;
        if (wantPid < 0) { _status = "no game"; return; }

        if (wantPid != _targetPid)
        {
            RestoreAll(); ResetTarget();
            _targetPid = wantPid;
            _isServer = server != null;
            _mem = ProcessMemory.Attach(Process.GetProcessById(wantPid));
        }
        if (_mem == null) { _status = "no mem"; return; }

        // Resolve SpawnNPC's hot+cold code ranges (the natural-spawn caller) and NewNPC's entry.
        // Both may be reported as precode jmp stubs — resolve to the real code bodies.
        var (hot, hotSz, cold, coldSz) = TmlDiscovery.ResolveMethodRegions(_targetPid, "Terraria.NPC", "SpawnNPC");
        if (hot == 0 || hotSz == 0) { _status = "SpawnNPC not jitted"; return; }
        ulong hotLo = RealCode(hot), hotHi = hotLo + hotSz;
        ulong coldLo = cold != 0 ? cold : 0, coldHi = cold != 0 ? cold + coldSz : 0;
        var cur = TmlDiscovery.ResolveCurrentAddresses(_targetPid, new[] { ("nn", "Terraria.NPC", "NewNPC") });
        if (!cur.TryGetValue("nn", out var list) || list.Count == 0) { _status = "NewNPC not jitted"; return; }
        ulong addr = RealCode(list[0]);

        if (_cfg == IntPtr.Zero)
        {
            _cfg = _mem.AllocNear((IntPtr)addr, 16, Native.MemoryProtection.ExecuteReadWrite);
            if (_cfg == IntPtr.Zero) { _status = "cfg alloc failed"; return; }
            _mem.WriteInt32((IntPtr)(_cfg.ToInt64() + 8), 0); // reset hit counter
        }
        _mem.WriteByte(_cfg, 1);
        _mem.WriteInt32((IntPtr)(_cfg.ToInt64() + 4), _type);

        // (Re)install if the hook is gone OR SpawnNPC relocated (ranges baked into the cave).
        if (!IsHooked(addr) || hotLo != _hotLo || hotHi != _hotHi || coldLo != _coldLo || coldHi != _coldHi)
        {
            _hotLo = hotLo; _hotHi = hotHi; _coldLo = coldLo; _coldHi = coldHi;
            RestoreHookOnly();
            InstallCave(addr);
        }

        int hits = 0; try { hits = _mem.ReadInt32((IntPtr)(_cfg.ToInt64() + 8)); } catch { }
        _status = $"{(_isServer ? "server" : "client")} pid {_targetPid} — type {_type}, overrides={hits}, cold={(cold != 0 ? "yes" : "no")}";
    }

    /// <summary>Follow tiered-JIT precode jmp stubs (E9 rel32) to the real method body. ClrMD's NativeCode
    /// can point at a `jmp realcode` stub; displacing that relative jmp into a cave would corrupt it and
    /// crash the process. We hook the real body instead. Stops if a hop would enter our own cave.</summary>
    private ulong RealCode(ulong addr)
    {
        for (int i = 0; i < 4; i++)
        {
            byte[] b; try { b = _mem!.ReadBytes((IntPtr)addr, 5); } catch { break; }
            if (b.Length < 5 || b[0] != 0xE9) break;
            ulong target = (ulong)((long)addr + 5 + BitConverter.ToInt32(b, 1));
            if (_nn.Cave != IntPtr.Zero)
            {
                ulong cl = (ulong)_nn.Cave.ToInt64();
                if (target >= cl && target < cl + 0xC0) break; // don't follow our own hook
            }
            addr = target;
        }
        return addr;
    }

    private bool IsHooked(ulong addr)
    {
        if (_nn.At != addr || _nn.Cave == IntPtr.Zero) return false;
        try { return _mem!.ReadByte((IntPtr)addr) == 0xE9; } catch { return false; }
    }

    // cave: if source is a natural spawn AND enabled, override r9d (Type) with cfg type
    private void InstallCave(ulong addr)
    {
        var mem = _mem!;
        IntPtr entry = (IntPtr)addr;
        var head = mem.ReadBytes(entry, 24);
        int disp = PrologueLen(head, 5);
        if (disp < 5) { _status = "prologue undecodable"; return; }
        IntPtr cave = mem.AllocNear(entry, 0xC0, Native.MemoryProtection.ExecuteReadWrite);
        if (cave == IntPtr.Zero) { _status = "alloc failed"; return; }

        var b = new List<byte>(); void E(params byte[] x) => b.AddRange(x); void U32(int v) => b.AddRange(BitConverter.GetBytes(v));
        var fixHot = new List<int>(); var fixCold = new List<int>(); var fixOrig = new List<int>();
        // r10, r11 are scratch and not argument registers at NewNPC entry — safe to clobber.
        E(0x4C, 0x8B, 0x1C, 0x24);                             // mov r11, [rsp]   (return address)
        // in HOT range? -> DOIT
        E(0x49, 0xBA); b.AddRange(BitConverter.GetBytes((long)_hotLo)); // mov r10, hotLo
        E(0x4D, 0x39, 0xD3);                                   // cmp r11, r10
        E(0x0F, 0x82); fixCold.Add(b.Count); U32(0);           // jb chkCold
        E(0x49, 0xBA); b.AddRange(BitConverter.GetBytes((long)_hotHi)); // mov r10, hotHi
        E(0x4D, 0x39, 0xD3);                                   // cmp r11, r10
        int jDoit = -1;
        E(0x0F, 0x82); jDoit = b.Count; U32(0);                // jb DOIT (in hot)
        int chkCold = b.Count;                                 // chkCold:
        // in COLD range? -> DOIT, else ORIG
        E(0x49, 0xBA); b.AddRange(BitConverter.GetBytes((long)_coldLo)); // mov r10, coldLo
        E(0x4D, 0x39, 0xD3);                                   // cmp r11, r10
        E(0x0F, 0x82); fixOrig.Add(b.Count); U32(0);           // jb ORIG
        E(0x49, 0xBA); b.AddRange(BitConverter.GetBytes((long)_coldHi)); // mov r10, coldHi
        E(0x4D, 0x39, 0xD3);                                   // cmp r11, r10
        E(0x0F, 0x83); fixOrig.Add(b.Count); U32(0);           // jae ORIG
        int doit = b.Count;                                    // DOIT:
        E(0x49, 0xBB); b.AddRange(BitConverter.GetBytes(_cfg.ToInt64()));    // mov r11, &cfg
        E(0x41, 0x80, 0x3B, 0x00);                             // cmp byte [r11], 0
        E(0x0F, 0x84); fixOrig.Add(b.Count); U32(0);           // je ORIG (disabled)
        E(0x45, 0x8B, 0x4B, 0x04);                             // mov r9d, [r11+4]  (override Type)
        E(0x41, 0xFF, 0x43, 0x08);                             // inc dword [r11+8] (hit counter)
        int orig = b.Count;                                    // ORIG:

        b.AddRange(head.Take(disp));                           // displaced prologue
        b.Add(0xE9); b.AddRange(BitConverter.GetBytes((int)((entry.ToInt64() + disp) - (cave.ToInt64() + b.Count + 4))));
        var code = b.ToArray();
        // patch internal rel32s
        BitConverter.GetBytes(chkCold - (fixCold[0] + 4)).CopyTo(code, fixCold[0]);
        BitConverter.GetBytes(doit - (jDoit + 4)).CopyTo(code, jDoit);
        foreach (var p in fixOrig) BitConverter.GetBytes(orig - (p + 4)).CopyTo(code, p);
        mem.WriteBytes(cave, code);

        var patch = new byte[disp];
        patch[0] = 0xE9;
        BitConverter.GetBytes((int)(cave.ToInt64() - (entry.ToInt64() + 5))).CopyTo(patch, 1);
        for (int k = 5; k < disp; k++) patch[k] = 0x90;

        if (_nn.Cave != IntPtr.Zero && _nn.Cave != cave) { try { mem.Free(_nn.Cave); } catch { } }
        _nn.Orig = head.Take(disp).ToArray(); _nn.OrigAddr = addr;
        mem.WriteBytes(entry, patch);
        _nn.Cave = cave; _nn.At = addr;
    }

    private void RestoreHookOnly()
    {
        if (_mem != null && _nn.Orig != null && _nn.OrigAddr != 0) try { _mem.WriteBytes((IntPtr)_nn.OrigAddr, _nn.Orig); } catch { }
        if (_mem != null && _nn.Cave != IntPtr.Zero) try { _mem.Free(_nn.Cave); } catch { }
        _nn.Orig = null; _nn.OrigAddr = 0; _nn.Cave = IntPtr.Zero; _nn.At = 0;
    }

    private void RestoreAll()
    {
        RestoreHookOnly();
        if (_mem != null && _cfg != IntPtr.Zero) { try { _mem.Free(_cfg); } catch { } }
        _cfg = IntPtr.Zero;
    }

    private static int PrologueLen(byte[] code, int min)
    {
        var dec = Iced.Intel.Decoder.Create(64, code, Iced.Intel.DecoderOptions.None);
        int i = 0;
        while (i < min)
        {
            var ins = dec.Decode();
            if (ins.IsInvalid) return 0;
            // Relocating an IP-relative or branch instruction into a cave would corrupt it -> refuse.
            if (ins.IsIPRelativeMemoryOperand) return 0;
            switch (ins.FlowControl)
            {
                case Iced.Intel.FlowControl.Next: break;
                default: return 0; // call/jmp/jcc/ret/etc. in the displaced bytes
            }
            i += ins.Length;
        }
        return i;
    }
}
