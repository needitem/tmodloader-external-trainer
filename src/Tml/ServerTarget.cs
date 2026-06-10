using System.Diagnostics;
using TerrariaTrainer.Memory;

namespace TerrariaTrainer.Tml;

/// <summary>
/// Shared memory channel to the world-authoritative process — the dedicated Host &amp; Play SERVER when
/// one is running, else the client. The scoped-drop / spawn-boost / rare-spawn patchers and the world-
/// map scanner all target the same process; routing them through one <see cref="ProcessMemory"/> avoids
/// opening a separate OS handle per feature (the old code opened 3+ handles to one server).
///
/// This object owns the handle for the trainer's lifetime, re-attaching only when the PID changes (a
/// re-host). Consumers keep their own per-target state (caves, cfg, scope) and reset it when they notice
/// <see cref="Pid"/> changed (via <see cref="TargetChanged"/>); they must NOT dispose the shared memory.
/// </summary>
public sealed class ServerTarget
{
    private readonly TmlEngine _client;
    private readonly object _lock = new();
    public ServerTarget(TmlEngine client) => _client = client;

    public int Pid { get; private set; } = -1;
    public bool IsServer { get; private set; }
    public ProcessMemory? Mem { get; private set; }
    private long _lookupStamp;

    /// <summary>Point at the current world process and (re)attach only on PID change. Returns the shared
    /// <see cref="ProcessMemory"/>, or null if no game is running. The server lookup (a WMI query) is
    /// cached briefly so several consumers calling this in one tick incur a single query.</summary>
    public ProcessMemory? Ensure()
    {
        lock (_lock)
        {
            long now = Environment.TickCount64;
            if (Mem != null && now - _lookupStamp < 1500) return Mem; // recent lookup still valid
            _lookupStamp = now;
            var server = TmlDiscovery.FindServerProcess();
            int want = server?.Id ?? _client.Proc?.Id ?? -1;
            if (want < 0) { Reset(); return null; }
            if (want != Pid)
            {
                Reset();
                try { Mem = ProcessMemory.Attach(Process.GetProcessById(want)); Pid = want; IsServer = server != null; }
                catch { Reset(); return null; }
            }
            return Mem;
        }
    }

    /// <summary>Has the target PID changed since <paramref name="lastSeenPid"/>? Consumers call this to
    /// decide whether to drop stale per-target state (the old process — and its caves — are gone).</summary>
    public bool TargetChanged(int lastSeenPid) => lastSeenPid != Pid;

    public void Reset()
    {
        lock (_lock) { try { Mem?.Dispose(); } catch { } Mem = null; Pid = -1; IsServer = false; }
    }
}
