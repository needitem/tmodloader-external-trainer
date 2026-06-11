using TerrariaTrainer.Memory;

namespace TerrariaTrainer.Tml;

/// <summary>
/// Forces the Traveling Merchant to ARRIVE on demand by calling <c>NPC.SpawnOnPlayer(plr, 368)</c>.
///
/// Crucial detail learned the hard way: <c>SpawnOnPlayer</c> early-returns and does NOTHING when
/// <c>Main.netMode == 1</c> (a multiplayer client). It only really spawns on the server (netMode 2)
/// or in single-player (netMode 0). So for a Host &amp; Play host the spawn must run on the SERVER
/// process — hence this targets <see cref="ServerTarget"/> (the world-authoritative process; the
/// client itself in single-player) rather than the trainer's own client.
///
/// We can't call a managed method from the trainer's thread, so we run it on the server's GAME thread
/// via a flag-trigger cave at <c>NPC.NewNPC</c>'s entry (the same proven, JIT-stable, server-driven
/// site the rare-spawn feature uses; it fires whenever the world spawns anything). On the flag, the
/// cave preserves NewNPC's own argument registers, calls <c>SpawnOnPlayer(hostIndex, 368)</c>, restores,
/// then runs the displaced prologue so the original NewNPC call proceeds untouched. SpawnOnPlayer
/// re-enters NewNPC internally to create the merchant; the flag is already cleared so it can't recurse.
///
/// The host's player index on the server is found by name-matching the client's character (player
/// indices differ between client and server). Stock is a separate, client-side concern handled by
/// <see cref="TravelShopPatcher"/>, which the UI fires alongside this so the shop has fresh items.
/// </summary>
public sealed class TravelMerchantSpawner
{
    private const int TravelingMerchant = 368; // NPCID.TravelingMerchant
    private const int ArrData = 0x10;          // managed array data start
    private const int NameOff = 0x88;          // Player.name (string)

    private readonly TmlEngine _client;
    private readonly ServerTarget _target;
    private readonly object _lock = new();

    private int _pid = -1;
    private ProcessMemory? _mem;               // non-owning ref to ServerTarget.Mem
    private bool _isServer;
    private ulong _playerArray, _myPlayerStatic;
    private IntPtr _cave, _flag, _entry;
    private ulong _hookAt;
    private byte[]? _orig;
    private string _status = "";

    public TravelMerchantSpawner(TmlEngine client, ServerTarget target) { _client = client; _target = target; }
    public string Status { get { lock (_lock) return _status; } }

    /// <summary>Arm + fire one summon on the world process. Returns false (with a status) on failure.</summary>
    public bool Summon()
    {
        lock (_lock)
        {
            var mem = _target.Ensure();
            int pid = _target.Pid;
            if (mem == null || pid < 0) { _status = "no game"; return false; }

            if (pid != _pid)
            {
                _cave = IntPtr.Zero; _flag = IntPtr.Zero; _hookAt = 0; _orig = null; _entry = IntPtr.Zero;
                _pid = pid; _isServer = _target.IsServer; _mem = mem;
                try { var model = TmlDiscovery.Discover(pid); _playerArray = model.StaticPlayerArray; _myPlayerStatic = model.StaticMyPlayer; }
                catch { _playerArray = 0; _myPlayerStatic = 0; }
            }
            _mem = mem;

            int plr = ResolveHostIndex();
            if (plr < 0) { _status = "couldn't find your character on the world process"; return false; }

            var cur = TmlDiscovery.ResolveCurrentAddresses(pid, new[]
            {
                ("nn", "Terraria.NPC", "NewNPC"),
                ("sp", "Terraria.NPC", "SpawnOnPlayer"),
            });
            if (!cur.TryGetValue("nn", out var nnList) || nnList.Count == 0) { _status = "NewNPC not jitted"; return false; }
            if (!cur.TryGetValue("sp", out var spList) || spList.Count == 0) { _status = "SpawnOnPlayer not jitted"; return false; }
            ulong nn = RealCode(nnList[0]);
            ulong sp = spList[0];

            // NewNPC is shared with "Force Rare Spawns"; refuse rather than fight over the entry patch.
            byte first; try { first = mem.ReadByte((IntPtr)nn); } catch { _status = "read failed"; return false; }
            bool ours = _hookAt == nn && _cave != IntPtr.Zero;
            if (first == 0xE9 && !ours) { _status = "turn OFF 'Force Rare Spawns' first, then summon"; return false; }

            if (_flag == IntPtr.Zero)
                _flag = mem.AllocNear((IntPtr)nn, 8, Native.MemoryProtection.ExecuteReadWrite);
            if (_flag == IntPtr.Zero) { _status = "flag alloc failed"; return false; }

            if (_hookAt != nn || !IsHooked(mem)) { RestoreHook(mem); if (!InstallCave(mem, nn, sp, plr)) return false; }

            mem.WriteByte(_flag, 1);   // fire on the next world spawn tick
            _status = $"summon queued ({(_isServer ? "server" : "single-player")}, player {plr})";
            return true;
        }
    }

    /// <summary>Remove the cave (e.g. on detach / app close). Never disposes the shared ServerTarget mem.</summary>
    public void Clear()
    {
        lock (_lock)
        {
            if (_mem != null) RestoreHook(_mem);
            if (_mem != null && _flag != IntPtr.Zero) { try { _mem.Free(_flag); } catch { } }
            _flag = IntPtr.Zero; _hookAt = 0; _pid = -1; _mem = null;
        }
    }

    /// <summary>The host's player index on the target process (client and server number players differently).</summary>
    private int ResolveHostIndex()
    {
        try
        {
            if (!_isServer)
                return _myPlayerStatic != 0 ? _client.Mem!.ReadInt32((IntPtr)_myPlayerStatic) : 0; // SP: target == client
            string myName = ReadName(_client.Mem, _client.PlayerBase());
            if (myName.Length == 0 || _mem == null || _playerArray == 0) return -1;
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
            IntPtr s = mem.ReadPtr64((IntPtr)(player.ToInt64() + NameOff));
            if (s == IntPtr.Zero) return "";
            int len = mem.ReadInt32((IntPtr)(s.ToInt64() + 0x8));
            if (len is <= 0 or > 64) return "";
            return System.Text.Encoding.Unicode.GetString(mem.ReadBytes((IntPtr)(s.ToInt64() + 0xC), len * 2));
        }
        catch { return ""; }
    }

    private ulong RealCode(ulong addr)
    {
        for (int i = 0; i < 4; i++)
        {
            byte[] b; try { b = _mem!.ReadBytes((IntPtr)addr, 5); } catch { break; }
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

    // Cave at NewNPC entry: on the flag, call SpawnOnPlayer(plr, 368). All of NewNPC's argument
    // registers (rcx,rdx,r8,r9) are saved/restored so its own call continues correctly afterwards.
    private bool InstallCave(ProcessMemory mem, ulong hook, ulong spawn, int plr)
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
        E(0xC6, 0x00, 0x00);                                      // mov byte [rax], 0   (one-shot clear, before the call)
        E(0x58);                                                  // pop rax
        E(0x51, 0x52, 0x41, 0x50, 0x41, 0x51, 0x41, 0x52, 0x41, 0x53, 0x50); // push rcx,rdx,r8,r9,r10,r11,rax (preserve NewNPC args)
        E(0x48, 0x83, 0xEC, 0x20);                                // sub rsp, 0x20  (shadow; 7 pushes already 16-aligned)
        E(0xB9); U32(plr);                                        // mov ecx, plr     (arg0)
        E(0xBA); U32(TravelingMerchant);                          // mov edx, 368     (arg1)
        E(0x48, 0xB8); b.AddRange(BitConverter.GetBytes((long)spawn)); // mov rax, SpawnOnPlayer
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
