using TerrariaTrainer.Memory;

namespace TerrariaTrainer.Tml;

/// <summary>
/// Forces the Traveling Merchant to ARRIVE on demand by running <c>NPC.SpawnOnPlayer(plr, 368)</c> on
/// the world process via the shared <see cref="ServerCaller"/>.
///
/// Crucial detail learned the hard way: <c>SpawnOnPlayer</c> early-returns and does NOTHING when
/// <c>Main.netMode == 1</c> (a multiplayer client) — it only really spawns on the server (netMode 2) or
/// in single-player (netMode 0). So for a Host &amp; Play host the spawn must run on the SERVER, which is
/// what the shared caller targets. The host's player index differs between client and server, so it's
/// found by name-matching the client's character against the server's <c>Main.player[]</c>. Stock is a
/// separate, client-side concern handled by <see cref="TravelShopPatcher"/> (fired alongside this).
/// </summary>
public sealed class TravelMerchantSpawner
{
    private const int TravelingMerchant = 368; // NPCID.TravelingMerchant
    private const int ArrData = 0x10;          // managed array data start
    private const int NameOff = 0x88;          // Player.name (string)

    private readonly TmlEngine _client;
    private readonly ServerCaller _caller;
    private readonly object _lock = new();

    private int _pid = -1;
    private ulong _playerArray, _myPlayerStatic;
    private string _status = "";

    public TravelMerchantSpawner(TmlEngine client, ServerCaller caller) { _client = client; _caller = caller; }
    public string Status { get { lock (_lock) return _status; } }

    /// <summary>Force the Traveling Merchant to arrive.</summary>
    public bool Summon() => SummonType(TravelingMerchant);

    /// <summary>Arm + fire one NPC spawn (any type) on the world process — SpawnOnPlayer drops it next to the
    /// host. Used for the merchant and for force-arriving any town NPC. False (with a status) on failure.</summary>
    public bool SummonType(int type)
    {
        lock (_lock)
        {
            var mem = _caller.Ensure();
            if (mem == null) { _status = _caller.Status; return false; }
            int pid = _caller.Pid;
            bool isServer = _caller.IsServer;

            if (pid != _pid)
            {
                _pid = pid;
                try { var model = TmlDiscovery.Discover(pid); _playerArray = model.StaticPlayerArray; _myPlayerStatic = model.StaticMyPlayer; }
                catch { _playerArray = 0; _myPlayerStatic = 0; }
            }

            int plr = ResolveHostIndex(isServer, mem);
            if (plr < 0) { _status = "couldn't find your character on the world process"; return false; }

            ulong sp = TmlDiscovery.ResolveMethodAddr(pid, "Terraria.NPC", "SpawnOnPlayer");
            if (sp == 0) { _status = "SpawnOnPlayer not warmed up — summon any boss once, then retry"; return false; }

            if (!_caller.Call(sp, plr, type)) { _status = _caller.Status; return false; }
            _status = $"summon queued ({(isServer ? "server" : "single-player")}, player {plr})";
            return true;
        }
    }

    /// <summary>The host's player index on the world process (client and server number players differently).</summary>
    private int ResolveHostIndex(bool isServer, ProcessMemory mem)
    {
        try
        {
            if (!isServer)
                return _myPlayerStatic != 0 ? _client.Mem!.ReadInt32((IntPtr)_myPlayerStatic) : 0; // SP: target == client
            string myName = ReadName(_client.Mem, _client.PlayerBase());
            if (myName.Length == 0 || _playerArray == 0) return -1;
            IntPtr arr = mem.ReadPtr64((IntPtr)_playerArray);
            if (arr == IntPtr.Zero) return -1;
            for (int i = 0; i < 256; i++)
            {
                IntPtr pl = mem.ReadPtr64((IntPtr)(arr.ToInt64() + ArrData + i * 8));
                if (pl != IntPtr.Zero && ReadName(mem, pl) == myName) return i;
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
}
