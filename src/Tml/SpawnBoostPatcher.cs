namespace TerrariaTrainer.Tml;

/// <summary>
/// Boosts enemy spawning so rare mobs show up far more often — works in single-player AND on a
/// multiplayer Host &amp; Play server (where spawning is server-authoritative).
///
/// Terraria's <c>NPC.SpawnNPC</c> resets, every spawn frame, <c>spawnRate = NPC.defaultSpawnRate</c>
/// (lower = spawns happen sooner) and <c>maxSpawns = NPC.defaultMaxSpawns</c> (more = more enemies
/// alive at once), then scales them by the player/biome. Those two <b>static</b> ints are the source
/// the game copies from, so writing them once changes every subsequent spawn cycle and persists with
/// no JIT-relocation problem (static storage doesn't move). More spawn attempts ⇒ rare enemies (which
/// keep their low pool weight) appear in minutes instead of hours. We can't externally re-weight the
/// per-NPC spawn pool, so this is the practical "rare mobs spawn well" lever.
/// </summary>
public sealed class SpawnBoostPatcher
{
    private readonly ServerTarget _target;
    private readonly object _lock = new();
    private volatile bool _enabled;

    private int _rate = 30;     // smaller = faster spawns (vanilla 600)
    private int _max = 40;      // more = denser (vanilla 5)

    private int _lastPid = -1;
    private ulong _aDefRate, _aDefMax, _aLiveRate, _aLiveMax;
    private int _origRate = -1, _origMax = -1;
    private string _status = "off";

    public SpawnBoostPatcher(ServerTarget target) => _target = target;

    public bool Enabled => _enabled;
    public string Status { get { lock (_lock) return _status; } }

    public void SetValues(int rate, int max)
    {
        lock (_lock) { _rate = Math.Clamp(rate, 1, 600); _max = Math.Clamp(max, 1, 200); }
    }

    public void Enable() => _enabled = true;
    public void Disable() { lock (_lock) { _enabled = false; Restore(); Reset(); _status = "off"; } }

    /// <summary>Drop all state without touching memory (e.g. when the client detaches).</summary>
    public void Clear() { lock (_lock) { Reset(); } }

    private void Reset()
    {
        _lastPid = -1;
        _aDefRate = _aDefMax = _aLiveRate = _aLiveMax = 0; _origRate = _origMax = -1;
    }

    private void Restore()
    {
        try
        {
            var mem = _target.Mem;
            if (mem != null && _aDefRate != 0 && _origRate >= 0)
            {
                mem.WriteInt32((IntPtr)_aDefRate, _origRate);
                mem.WriteInt32((IntPtr)_aDefMax, _origMax);
            }
        }
        catch { }
    }

    /// <summary>Called ~every 2.5s from the GUI background thread.</summary>
    public void Tick()
    {
        if (!_enabled) return;
        try { lock (_lock) { if (_enabled) Apply(); } } catch { /* transient */ }
    }

    private void Apply()
    {
        var mem = _target.Ensure();
        if (mem == null) { _status = "no game"; return; }

        if (_target.Pid != _lastPid)
        {
            Reset();
            _lastPid = _target.Pid;
            var a = TmlDiscovery.ResolveStaticFields(_target.Pid, "Terraria.NPC",
                "defaultSpawnRate", "defaultMaxSpawns", "spawnRate", "maxSpawns");
            a.TryGetValue("defaultSpawnRate", out _aDefRate);
            a.TryGetValue("defaultMaxSpawns", out _aDefMax);
            a.TryGetValue("spawnRate", out _aLiveRate);
            a.TryGetValue("maxSpawns", out _aLiveMax);
            if (_aDefRate != 0 && _origRate < 0)
            {
                try { _origRate = mem.ReadInt32((IntPtr)_aDefRate); _origMax = mem.ReadInt32((IntPtr)_aDefMax); } catch { }
            }
        }
        if (_aDefRate == 0 || _aDefMax == 0) { _status = "spawn statics not found"; return; }

        // The game copies the defaults into spawnRate/maxSpawns each spawn frame; writing the
        // defaults is what persists. Also poke the live values for an immediate effect this frame.
        mem.WriteInt32((IntPtr)_aDefRate, _rate);
        mem.WriteInt32((IntPtr)_aDefMax, _max);
        if (_aLiveRate != 0) mem.WriteInt32((IntPtr)_aLiveRate, _rate);
        if (_aLiveMax != 0) mem.WriteInt32((IntPtr)_aLiveMax, _max);

        _status = $"{(_target.IsServer ? "server" : "client")} pid {_target.Pid} — spawnRate {_rate}, maxSpawns {_max} (vanilla {_origRate}/{_origMax})";
    }
}
