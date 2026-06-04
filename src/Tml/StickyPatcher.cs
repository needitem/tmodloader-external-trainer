namespace TerrariaTrainer.Tml;

/// <summary>
/// Keeps method-entry "return stub" patches alive across .NET tiered-JIT relocations.
///
/// The CLR recompiles hot methods (Tier0 -> Tier1) at a NEW code address and abandons the
/// old code, so a one-shot entry patch silently stops taking effect. This component re-resolves
/// each patched method's CURRENT native code (via a fresh ClrMD snapshot) every tick and
/// re-writes the stub, so the live code is always patched. Restarts are handled separately by
/// the GUI's auto-reattach + config restore.
/// </summary>
public sealed class StickyPatcher
{
    private readonly TmlEngine _engine;
    private readonly Dictionary<string, byte[]> _active = new();                  // key -> stub bytes
    private readonly Dictionary<string, Dictionary<ulong, byte[]>> _saved = new(); // key -> (addr -> original bytes)
    private readonly object _lock = new();

    public StickyPatcher(TmlEngine engine) => _engine = engine;

    public bool AnyActive { get { lock (_lock) return _active.Count > 0; } }

    /// <summary>Begin keeping <paramref name="key"/> (a model.MethodSources key) patched to <paramref name="stub"/>.</summary>
    public void Register(string key, byte[] stub)
    {
        lock (_lock) { _active[key] = stub; if (!_saved.ContainsKey(key)) _saved[key] = new(); }
        TickOnce(); // apply immediately so the effect is instant
    }

    /// <summary>Stop patching <paramref name="key"/> and restore every address it touched.</summary>
    public void Unregister(string key)
    {
        lock (_lock)
        {
            _active.Remove(key);
            if (_saved.TryGetValue(key, out var addrs))
            {
                var mem = _engine.Mem;
                if (mem != null)
                    foreach (var (addr, orig) in addrs) { try { mem.WriteBytes((IntPtr)addr, orig); } catch { } }
                _saved.Remove(key);
            }
        }
    }

    /// <summary>Drop all state (e.g. on detach) without touching memory.</summary>
    public void Clear() { lock (_lock) { _active.Clear(); _saved.Clear(); } }

    /// <summary>Re-resolve every active patch's current code and re-write its stub. Call ~every 2-3s.</summary>
    public void TickOnce()
    {
        var model = _engine.Model; var mem = _engine.Mem; var proc = _engine.Proc;
        if (model == null || mem == null || proc == null) return;

        List<(string key, string type, string method)> reqs;
        Dictionary<string, byte[]> stubs;
        lock (_lock)
        {
            if (_active.Count == 0) return;
            stubs = new(_active);
            reqs = new();
            foreach (var key in _active.Keys)
                if (model.MethodSources.TryGetValue(key, out var src)) reqs.Add((key, src.type, src.method));
        }
        if (reqs.Count == 0) return;

        Dictionary<string, List<ulong>> current;
        try { current = TmlDiscovery.ResolveCurrentAddresses(proc.Id, reqs); }
        catch { return; }

        lock (_lock)
        {
            foreach (var (key, addrs) in current)
            {
                if (!stubs.TryGetValue(key, out var stub)) continue;
                if (!_saved.TryGetValue(key, out var saved)) { saved = new(); _saved[key] = saved; }
                foreach (var addr in addrs)
                {
                    try
                    {
                        var cur = mem.ReadBytes((IntPtr)addr, stub.Length);
                        if (cur.Length != stub.Length) continue;
                        bool isStub = true;
                        for (int i = 0; i < stub.Length; i++) if (cur[i] != stub[i]) { isStub = false; break; }
                        if (isStub) continue;                       // already patched at this address
                        if (!saved.ContainsKey(addr)) saved[addr] = cur; // remember the real code for restore
                        mem.WriteBytes((IntPtr)addr, stub);
                    }
                    catch { }
                }
            }
        }
    }
}
