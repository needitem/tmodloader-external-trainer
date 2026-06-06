using System.Diagnostics;
using System.Text;
using TerrariaTrainer.Memory;

namespace TerrariaTrainer.Tml;

/// <summary>
/// "100% drop, but only for mobs I DAMAGED" — even on a multiplayer Host &amp; Play server.
///
/// In MP the loot is rolled on the dedicated SERVER process (a separate `dotnet tModLoader.dll
/// -server`), not the client, so a client-side patch never reaches it. Worse, the server credits
/// loot to the CLOSEST player, so just standing near a mob that dies on its own would otherwise
/// trigger the 100% drop. To avoid that, two caves cooperate via a shared flag byte:
///   • TryDropping cave: per NPC-loot, set flag = npc.playerInteraction[myWho] (did I/my-minions
///     damage THIS npc).
///   • RollLuck cave: force the roll to 0 ONLY when (rolling player.whoAmI == mine) AND flag == 1.
/// So only mobs I actually damaged drop 100%; untouched nearby deaths and other players' kills
/// roll normally. The index is re-verified by name every tick and the caves are re-asserted so the
/// tiered JIT can't relocate past them; if I'm not in the world the caves are removed (others safe).
/// </summary>
public sealed class ScopedDropPatcher
{
    private const int WhoAmIOff = 0x10;          // Player.whoAmI
    private const int InfoNpcOff = 0x00;         // DropAttemptInfo.npc  (rdx points at &info)
    private const int NpcInteractionOff = 0x50;  // NPC.playerInteraction (bool[])
    private const int ArrData = 0x10;            // managed array data start (bool[] -> 1 byte each)

    private readonly TmlEngine _client;
    private readonly object _lock = new();
    private volatile bool _enabled;

    private int _targetPid = -1;
    private ProcessMemory? _mem;
    private ulong _playerArray, _myPlayerStatic;
    private bool _isServer;
    private int _myWho = -1;
    private IntPtr _flag;                         // shared "I damaged this npc" byte
    private string _status = "off";

    // one installed cave (entry patch + relocatable trampoline)
    private sealed class Hook { public IntPtr Cave; public ulong At; public byte[]? Orig; public ulong OrigAddr; }
    private readonly Hook _rl = new();            // Player.RollLuck
    private readonly Hook _td = new();            // ItemDropResolver.TryDropping

    public ScopedDropPatcher(TmlEngine client) => _client = client;

    public bool Enabled => _enabled;
    public string Status { get { lock (_lock) return _status; } }

    public void Enable() => _enabled = true;
    public void Disable() { lock (_lock) { _enabled = false; RestoreAll(); ResetTarget(); _status = "off"; } }

    /// <summary>Drop all state without touching memory (e.g. when the client detaches).</summary>
    public void Clear() { lock (_lock) { _rl.Orig = null; _rl.Cave = IntPtr.Zero; _rl.At = 0; _td.Orig = null; _td.Cave = IntPtr.Zero; _td.At = 0; _flag = IntPtr.Zero; ResetTarget(); } }

    private void ResetTarget()
    {
        _targetPid = -1; _myWho = -1; _playerArray = 0; _myPlayerStatic = 0; _isServer = false;
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
        var server = TmlDiscovery.FindServerProcess();
        int wantPid = server?.Id ?? _client.Proc?.Id ?? -1;
        if (wantPid < 0) { _status = "no game"; return; }

        if (wantPid != _targetPid)
        {
            RestoreAll(); ResetTarget();
            _targetPid = wantPid;
            _isServer = server != null;
            _mem = ProcessMemory.Attach(Process.GetProcessById(wantPid));
            var model = TmlDiscovery.Discover(wantPid);
            _playerArray = model.StaticPlayerArray;
            _myPlayerStatic = model.StaticMyPlayer;
            _myWho = ResolveMyIndex();
        }
        if (_mem == null) { _status = "no mem"; return; }

        // SAFETY: re-verify MY index by name every tick. Slot changed -> rebuild; gone -> remove.
        int who = ResolveMyIndex();
        if (who < 0) { RestoreAll(); _myWho = -1; _status = "you are not in this world — drop OFF (others safe)"; return; }
        if (who != _myWho) { _myWho = who; RestoreAll(); }

        var cur = TmlDiscovery.ResolveCurrentAddresses(_targetPid, new[]
        {
            ("rl", "Terraria.Player", "RollLuck"),
            ("td", "Terraria.GameContent.ItemDropRules.ItemDropResolver", "TryDropping"),
        });
        if (!cur.TryGetValue("rl", out var rlList) || rlList.Count == 0) { _status = "RollLuck not jitted"; return; }
        ulong rlAddr = rlList[0];

        // allocate the shared flag once (near RollLuck)
        if (_flag == IntPtr.Zero)
        {
            _flag = _mem.AllocNear((IntPtr)rlAddr, 8, Native.MemoryProtection.ExecuteReadWrite);
            if (_flag == IntPtr.Zero) { _status = "flag alloc failed"; return; }
        }

        if (!IsHooked(_rl, rlAddr)) InstallRollLuckCave(rlAddr);
        if (cur.TryGetValue("td", out var tdList) && tdList.Count > 0 && !IsHooked(_td, tdList[0]))
            InstallTryDroppingCave(tdList[0]);

        _status = $"{(_isServer ? "server" : "client")} pid {_targetPid}, whoAmI {_myWho} — active (damaged mobs only)";
    }

    private bool IsHooked(Hook h, ulong addr)
    {
        if (h.At != addr || h.Cave == IntPtr.Zero) return false;
        try { return _mem!.ReadByte((IntPtr)addr) == 0xE9; } catch { return false; }
    }

    private int ResolveMyIndex()
    {
        try
        {
            if (!_isServer) return _client.Mem!.ReadInt32((IntPtr)_myPlayerStatic); // SP: only me anyway
            string myName = ReadName(_client.Mem, _client.PlayerBase());
            if (myName.Length == 0 || _mem == null) return -1;
            IntPtr arr = _mem.ReadPtr64((IntPtr)_playerArray);
            if (arr == IntPtr.Zero) return -1;
            for (int i = 0; i < 256; i++)
            {
                IntPtr pl = _mem.ReadPtr64((IntPtr)(arr.ToInt64() + ArrData + i * 8));
                if (pl != IntPtr.Zero && ReadName(_mem, pl) == myName) return i;
            }
        }
        catch { }
        return -1;
    }

    private static string ReadName(ProcessMemory? mem, IntPtr player)
    {
        if (mem == null || player == IntPtr.Zero) return "";
        try
        {
            IntPtr s = mem.ReadPtr64((IntPtr)(player.ToInt64() + 0x88)); // Player.name (string)
            if (s == IntPtr.Zero) return "";
            int len = mem.ReadInt32((IntPtr)(s.ToInt64() + 0x8));
            if (len <= 0 || len > 60) return "";
            return Encoding.Unicode.GetString(mem.ReadBytes((IntPtr)(s.ToInt64() + 0xC), len * 2));
        }
        catch { return ""; }
    }

    // ---- cave A: RollLuck. force 0 only when (this.whoAmI == myWho) AND flag != 0 ----
    private void InstallRollLuckCave(ulong addr)
    {
        var (mem, entry, head, disp, cave) = BeginCave(addr);
        if (cave == IntPtr.Zero) return;
        var b = new List<byte>(); void E(params byte[] x) => b.AddRange(x); void U32(int v) => b.AddRange(BitConverter.GetBytes(v));
        E(0x81, 0x79, WhoAmIOff); U32(_myWho);          // cmp dword [rcx+whoAmI], myWho
        E(0x0F, 0x85); int j1 = b.Count; U32(0);         // jne ORIG
        E(0x49, 0xBB); b.AddRange(BitConverter.GetBytes(_flag.ToInt64())); // mov r11, flag
        E(0x41, 0x80, 0x3B, 0x00);                       // cmp byte [r11], 0
        E(0x0F, 0x84); int j2 = b.Count; U32(0);         // je ORIG
        E(0x31, 0xC0, 0xC3);                             // xor eax,eax; ret   (me + damaged -> 0)
        int orig = b.Count;
        FinishCave(mem, entry, head, disp, cave, b, _rl, addr, (code) =>
        {
            BitConverter.GetBytes(orig - (j1 + 4)).CopyTo(code, j1);
            BitConverter.GetBytes(orig - (j2 + 4)).CopyTo(code, j2);
        });
    }

    // ---- cave B: TryDropping. flag = info.npc.playerInteraction[myWho] (did I damage this npc) ----
    private void InstallTryDroppingCave(ulong addr)
    {
        var (mem, entry, head, disp, cave) = BeginCave(addr);
        if (cave == IntPtr.Zero) return;
        long flagA = _flag.ToInt64();
        var b = new List<byte>(); void E(params byte[] x) => b.AddRange(x); void U32(int v) => b.AddRange(BitConverter.GetBytes(v));
        E(0x50, 0x41, 0x53);                             // push rax; push r11
        E(0x48, 0x8B, 0x02);                             // mov rax, [rdx]   (info.npc; InfoNpcOff=0)
        E(0x48, 0x85, 0xC0); E(0x0F, 0x84); int jc1 = b.Count; U32(0);     // test rax,rax; jz CLR
        E(0x48, 0x8B, 0x40, NpcInteractionOff);          // mov rax, [rax+0x50]  (playerInteraction[])
        E(0x48, 0x85, 0xC0); E(0x0F, 0x84); int jc2 = b.Count; U32(0);     // test rax,rax; jz CLR
        E(0x0F, 0xB6, 0x80); U32(ArrData + _myWho);      // movzx eax, byte [rax + 0x10 + myWho]
        E(0x49, 0xBB); b.AddRange(BitConverter.GetBytes(flagA)); E(0x41, 0x88, 0x03); // mov r11,flag; mov [r11],al
        E(0xE9); int jdone = b.Count; U32(0);            // jmp DONE
        int clr = b.Count;
        E(0x49, 0xBB); b.AddRange(BitConverter.GetBytes(flagA)); E(0x41, 0xC6, 0x03, 0x00); // mov r11,flag; mov byte[r11],0
        int done = b.Count;
        E(0x41, 0x5B, 0x58);                             // pop r11; pop rax
        FinishCave(mem, entry, head, disp, cave, b, _td, addr, (code) =>
        {
            BitConverter.GetBytes(clr - (jc1 + 4)).CopyTo(code, jc1);
            BitConverter.GetBytes(clr - (jc2 + 4)).CopyTo(code, jc2);
            BitConverter.GetBytes(done - (jdone + 4)).CopyTo(code, jdone);
        });
    }

    private (ProcessMemory mem, IntPtr entry, byte[] head, int disp, IntPtr cave) BeginCave(ulong addr)
    {
        var mem = _mem!;
        IntPtr entry = (IntPtr)addr;
        var head = mem.ReadBytes(entry, 24);
        int disp = PrologueLen(head, 5);
        if (disp < 5) { _status = "prologue undecodable"; return (mem, entry, head, 0, IntPtr.Zero); }
        IntPtr cave = mem.AllocNear(entry, 0x90, Native.MemoryProtection.ExecuteReadWrite);
        if (cave == IntPtr.Zero) _status = "alloc failed";
        return (mem, entry, head, disp, cave);
    }

    private void FinishCave(ProcessMemory mem, IntPtr entry, byte[] head, int disp, IntPtr cave, List<byte> b, Hook h, ulong addr, Action<byte[]> fixups)
    {
        b.AddRange(head.Take(disp));                                  // displaced prologue
        b.Add(0xE9); b.AddRange(BitConverter.GetBytes((int)((entry.ToInt64() + disp) - (cave.ToInt64() + b.Count + 4))));
        var code = b.ToArray();
        fixups(code);                                                // patch the internal jump rel32s
        mem.WriteBytes(cave, code);

        var patch = new byte[disp];
        patch[0] = 0xE9;
        BitConverter.GetBytes((int)(cave.ToInt64() - (entry.ToInt64() + 5))).CopyTo(patch, 1);
        for (int k = 5; k < disp; k++) patch[k] = 0x90;

        if (h.Cave != IntPtr.Zero && h.Cave != cave) { try { mem.Free(h.Cave); } catch { } }
        h.Orig = head.Take(disp).ToArray(); h.OrigAddr = addr;
        mem.WriteBytes(entry, patch);
        h.Cave = cave; h.At = addr;
    }

    private void RestoreAll()
    {
        RestoreHook(_rl); RestoreHook(_td);
        if (_mem != null && _flag != IntPtr.Zero) { try { _mem.Free(_flag); } catch { } }
        _flag = IntPtr.Zero;
    }

    private void RestoreHook(Hook h)
    {
        if (_mem != null && h.Orig != null && h.OrigAddr != 0) try { _mem.WriteBytes((IntPtr)h.OrigAddr, h.Orig); } catch { }
        if (_mem != null && h.Cave != IntPtr.Zero) try { _mem.Free(h.Cave); } catch { }
        h.Orig = null; h.OrigAddr = 0; h.Cave = IntPtr.Zero; h.At = 0;
    }

    private static int PrologueLen(byte[] code, int min)
    {
        var dec = Iced.Intel.Decoder.Create(64, code, Iced.Intel.DecoderOptions.None);
        int i = 0;
        while (i < min) { var ins = dec.Decode(); if (ins.IsInvalid) return 0; i += ins.Length; }
        return i;
    }
}
