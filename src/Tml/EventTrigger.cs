namespace TerrariaTrainer.Tml;

/// <summary>
/// Triggers world events on demand, on the world-authoritative process (the server in MP, the client in
/// SP) — weather, invasions, blood moon, eclipse. Two mechanisms:
///   • method events run a vanilla static on the game thread via the shared <see cref="ServerCaller"/>
///     (<c>Main.StartRain</c>, <c>Main.StartSlimeRain(true)</c>, <c>Sandstorm.StartSandstorm</c>,
///     <c>Main.StartInvasion(1)</c>=Goblin Army, <c>Main.StartInvasion(3)</c>=Pirates);
///   • flag events just write a <c>Main</c> static bool on the server (<c>bloodMoon</c>, <c>eclipse</c>)
///     and let the world loop run the event — no cave needed.
/// (Blizzard has no dedicated trigger: it's rain seen from the snow biome, so it maps to StartRain.)
/// Server-authoritative, so all of this runs on <see cref="ServerTarget"/>, never the host's client.
/// </summary>
public sealed class EventTrigger
{
    private readonly ServerTarget _target;
    private readonly ServerCaller _caller;
    private readonly object _lock = new();
    private string _status = "";

    public EventTrigger(ServerTarget target, ServerCaller caller) { _target = target; _caller = caller; }
    public string Status { get { lock (_lock) return _status; } }

    /// <summary>Run a vanilla static <c>Type.Method(arg)</c> on the world process's game thread.</summary>
    public bool CallStatic(string type, string method, int arg = 0, string? sig = null)
    {
        lock (_lock)
        {
            if (_caller.Ensure() == null) { _status = _caller.Status; return false; }
            ulong addr = TmlDiscovery.ResolveMethodAddr(_caller.Pid, type, method, sig);
            if (addr == 0) { _status = $"{method} not jitted"; return false; }
            if (!_caller.Call(addr, arg)) { _status = _caller.Status; return false; }
            _status = "event queued";
            return true;
        }
    }

    /// <summary>Set a <c>Main</c> static bool = true on the world process (e.g. bloodMoon / eclipse).</summary>
    public bool SetFlag(string field)
    {
        lock (_lock)
        {
            var mem = _target.Ensure();
            if (mem == null || _target.Pid < 0) { _status = "no game"; return false; }
            var s = TmlDiscovery.ResolveStaticFields(_target.Pid, "Terraria.Main", field);
            if (!s.TryGetValue(field, out var addr) || addr == 0) { _status = $"Main.{field} not found"; return false; }
            try { mem.WriteByte((IntPtr)addr, 1); } catch { _status = "write failed"; return false; }
            _status = "flag set";
            return true;
        }
    }
}
