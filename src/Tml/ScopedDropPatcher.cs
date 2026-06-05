using System.Diagnostics;
using System.Text;
using TerrariaTrainer.Memory;

namespace TerrariaTrainer.Tml;

/// <summary>
/// "100% drop, but only for ME" — even on a multiplayer Host &amp; Play server.
///
/// In MP the loot is rolled on the dedicated SERVER process (a separate `dotnet tModLoader.dll
/// -server`), not the client, so a client-side patch never reaches it. This patcher:
///   1. picks the right process (server if hosting MP, else the client for singleplayer),
///   2. finds MY player index there (Main.myPlayer in SP; name-match in MP),
///   3. installs a conditional cave at Player.RollLuck that returns 0 ONLY when the rolling
///      player's whoAmI == mine (so only my kills drop 100%; other players are unaffected),
///   4. re-asserts every tick so the .NET tiered JIT can't relocate past it.
/// </summary>
public sealed class ScopedDropPatcher
{
    private readonly TmlEngine _client;
    private readonly object _lock = new();
    private volatile bool _enabled;

    private int _targetPid = -1;
    private ProcessMemory? _mem;
    private ulong _playerArray, _myPlayerStatic;
    private bool _isServer;
    private int _myWho = -1;
    private ulong _hookedAt;
    private IntPtr _cave;
    private byte[]? _savedEntry;
    private ulong _savedEntryAddr;
    private string _status = "off";

    public ScopedDropPatcher(TmlEngine client) => _client = client;

    public bool Enabled => _enabled;
    public string Status { get { lock (_lock) return _status; } }

    public void Enable() => _enabled = true;

    public void Disable()
    {
        lock (_lock) { _enabled = false; Restore(); ResetTarget(); _status = "off"; }
    }

    /// <summary>Drop all state without touching memory (e.g. when the client detaches).</summary>
    public void Clear() { lock (_lock) { _savedEntry = null; _cave = IntPtr.Zero; _hookedAt = 0; ResetTarget(); } }

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
            Restore(); ResetTarget();
            _targetPid = wantPid;
            _isServer = server != null;
            var proc = Process.GetProcessById(wantPid);
            _mem = ProcessMemory.Attach(proc);
            var model = TmlDiscovery.Discover(wantPid);
            _playerArray = model.StaticPlayerArray;
            _myPlayerStatic = model.StaticMyPlayer;
            _myWho = ResolveMyIndex();
        }
        if (_mem == null) { _status = "no mem"; return; }

        // SAFETY: re-verify MY index every tick (by name). If my slot changed (reconnect) the
        // cave is rebuilt for the new index; if I'm not in this world the cave is REMOVED — so a
        // different player who later occupies my old slot can never inherit my 100% drop.
        int who = ResolveMyIndex();
        if (who < 0) { Restore(); _myWho = -1; _status = "you are not in this world — drop OFF (others safe)"; return; }
        if (who != _myWho) { _myWho = who; Restore(); } // rebuild cave with the corrected index

        var cur = TmlDiscovery.ResolveCurrentAddresses(_targetPid, new[] { ("RollLuck", "Terraria.Player", "RollLuck") });
        if (!cur.TryGetValue("RollLuck", out var list) || list.Count == 0) { _status = "RollLuck not jitted"; return; }
        ulong addr = list[0];
        byte first;
        try { first = _mem.ReadByte((IntPtr)addr); } catch { return; }
        if (addr != _hookedAt || first != 0xE9) InstallCave(addr);
        _status = $"{(_isServer ? "server" : "client")} pid {_targetPid}, whoAmI {_myWho} — active";
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
                IntPtr pl = _mem.ReadPtr64((IntPtr)(arr.ToInt64() + 0x10 + i * 8));
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

    private void InstallCave(ulong addr)
    {
        var mem = _mem!;
        IntPtr entry = (IntPtr)addr;
        var head = mem.ReadBytes(entry, 24);
        int disp = PrologueLen(head, 5);
        if (disp < 5) { _status = "prologue undecodable"; return; }
        IntPtr cave = mem.AllocNear(entry, 0x80, Native.MemoryProtection.ExecuteReadWrite);
        if (cave == IntPtr.Zero) { _status = "alloc failed"; return; }

        var b = new List<byte>();
        void U32(int v) => b.AddRange(BitConverter.GetBytes(v));
        b.AddRange(new byte[] { 0x81, 0x79, 0x10 }); U32(_myWho);       // cmp dword [rcx+0x10], myWho  (whoAmI)
        b.AddRange(new byte[] { 0x0F, 0x85 }); int jne = b.Count; U32(0); // jne ORIG
        b.AddRange(new byte[] { 0x31, 0xC0, 0xC3 });                    // xor eax,eax; ret   (me -> 0)
        int orig = b.Count;
        b.AddRange(head.Take(disp));                                    // displaced prologue
        b.Add(0xE9); U32((int)((entry.ToInt64() + disp) - (cave.ToInt64() + b.Count + 4)));
        var code = b.ToArray();
        BitConverter.GetBytes(orig - (jne + 4)).CopyTo(code, jne);
        mem.WriteBytes(cave, code);

        var patch = new byte[disp];
        patch[0] = 0xE9;
        BitConverter.GetBytes((int)(cave.ToInt64() - (entry.ToInt64() + 5))).CopyTo(patch, 1);
        for (int k = 5; k < disp; k++) patch[k] = 0x90;

        if (_cave != IntPtr.Zero && _cave != cave) { try { mem.Free(_cave); } catch { } }
        _savedEntry = head.Take(disp).ToArray(); _savedEntryAddr = addr; // for restore
        mem.WriteBytes(entry, patch);
        _cave = cave; _hookedAt = addr;
    }

    private void Restore()
    {
        if (_mem != null && _savedEntry != null && _savedEntryAddr != 0)
            try { _mem.WriteBytes((IntPtr)_savedEntryAddr, _savedEntry); } catch { }
        if (_mem != null && _cave != IntPtr.Zero) try { _mem.Free(_cave); } catch { }
        _savedEntry = null; _savedEntryAddr = 0; _cave = IntPtr.Zero; _hookedAt = 0;
    }

    private static int PrologueLen(byte[] code, int min)
    {
        var dec = Iced.Intel.Decoder.Create(64, code, Iced.Intel.DecoderOptions.None);
        int i = 0;
        while (i < min) { var ins = dec.Decode(); if (ins.IsInvalid) return 0; i += ins.Length; }
        return i;
    }
}
