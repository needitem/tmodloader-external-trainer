using TerrariaTrainer.Memory;

namespace TerrariaTrainer.Tml;

/// <summary>
/// "Abyss / cave vision" — brightens the deep-sea &amp; cave the ROOT way (zero flicker), by forcing the
/// player's abyss glow high IN-GAME every frame. Calamity computes the player's light in the Abyss from
/// <c>CalamityPlayer.abyssPlayerGlowMultiplier</c>; <c>CalamityPlayer.ResetEffects</c> (runs every frame,
/// always JIT'd) resets it via <c>vmovss [calPlayer + glowOff], xmm</c>, and SetAbyssLightLevels then adds
/// a depth bonus on top. We redirect that reset store into a cave that writes a high constant instead, so
/// the game itself establishes a bright glow every frame — no external write race, no flicker. The player
/// then lights up the surroundings. Client-side, Calamity-only. The method address + field offset are
/// resolved live (version-proof); the patch is re-asserted across tiered-JIT relocation and removed on
/// disable. (Earlier method-level no-ops of the darkness systems did nothing — the abyss is dark because
/// it's *unlit*, so the fix is to add light, not remove a darkening pass.)
/// </summary>
public sealed class CalamityVisionPatcher
{
    private const string Type = "CalamityMod.CalPlayer.CalamityPlayer";
    private const string Method = "ResetEffects";

    private readonly TmlEngine _engine;
    private readonly object _lock = new();
    private volatile bool _enabled;
    private float _glow = 10f;            // abyss player-glow multiplier to force
    private string _status = "off";

    private volatile int _state = -1;     // -1 resolving, 0 unavailable (no Calamity), 1 ready
    private volatile bool _resolving;
    private int _glowOff;                 // abyssPlayerGlowMultiplier offset from the CalamityPlayer base

    private ulong _addr;                  // current ResetEffects code address
    private int _pos = -1;                // patch offset within the method (the vmovss store)
    private byte[]? _orig;                // original store bytes (8)
    private IntPtr _cave;

    public CalamityVisionPatcher(TmlEngine engine) => _engine = engine;
    public bool Enabled => _enabled;
    public string Status { get { lock (_lock) return _status; } }

    public void SetGlow(float g) { lock (_lock) { _glow = Math.Clamp(g, 1f, 60f); if (_enabled) { Restore(); } } }
    public void Enable() { lock (_lock) _enabled = true; ResolveAsync(); }
    public void Disable() { lock (_lock) { _enabled = false; Restore(); _status = "off"; } }
    public void Clear() { lock (_lock) { _state = -1; Forget(); } }

    private void ResolveAsync()
    {
        if (_state != -1 || _resolving) return;
        int pid = _engine.Proc?.Id ?? -1;
        if (pid < 0) return;
        _resolving = true;
        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                var r = TmlDiscovery.ResolveCalamityFields(pid, "abyssPlayerGlowMultiplier");
                if (r is { } v && v.offs.TryGetValue("abyssPlayerGlowMultiplier", out var off)) { _glowOff = off; _state = 1; }
                else _state = 0;
            }
            catch { _state = 0; }
            _resolving = false;
        });
    }

    /// <summary>Called from the GUI background (slow) loop; re-asserts across tiered-JIT relocation.</summary>
    public void Tick()
    {
        if (!_enabled) return;
        try { lock (_lock) { if (_enabled) Locate(); } } catch { }
    }

    private void Locate()
    {
        if (_state == 0) { _status = "unavailable (no Calamity)"; return; }
        if (_state == -1) { _status = "checking for Calamity…"; return; }
        var mem = _engine.Mem; var proc = _engine.Proc;
        if (mem == null || proc == null) { _status = "not attached"; return; }

        ulong addr = TmlDiscovery.ResolveMethodAddr(proc.Id, Type, Method);
        if (addr == 0) { _status = "ResetEffects not jitted"; return; }
        if (addr == _addr && _pos >= 0 && IsJmp(mem)) { _status = $"abyss glow {_glow:0} (patched)"; return; } // still good

        Restore();                       // method moved or first install — clear any stale patch
        if (!Install(mem, addr)) return; // status set inside
        _status = $"abyss glow {_glow:0} (patched @0x{addr:X})";
    }

    // Redirect `vmovss [base+glowOff], xmm` → cave that does `mov dword [base+glowOff], <glow>` ; jmp back.
    private bool Install(ProcessMemory mem, ulong addr)
    {
        byte[] code; try { code = mem.ReadBytes((IntPtr)addr, 0x2600); } catch { _status = "read failed"; return false; }
        var disp = BitConverter.GetBytes(_glowOff);
        int pos = -1, rm = 3;
        for (int i = 0; i + 8 <= code.Length; i++)
        {
            if (code[i] != 0xC5 || code[i + 1] != 0xFA || code[i + 2] != 0x11) continue; // vmovss [reg+disp32],xmm
            byte md = code[i + 3]; if ((md & 0xC0) != 0x80 || (md & 7) == 4) continue;
            if (code[i + 4] != disp[0] || code[i + 5] != disp[1] || code[i + 6] != disp[2] || code[i + 7] != disp[3]) continue;
            pos = i; rm = md & 7; break;
        }
        if (pos < 0) { _status = "abyss glow store not found"; return false; }

        IntPtr cave = mem.AllocNear((IntPtr)addr, 0x40, Native.MemoryProtection.ExecuteReadWrite);
        if (cave == IntPtr.Zero) { _status = "cave alloc failed"; return false; }
        var cb = new List<byte> { 0xC7, (byte)(0x80 | rm) };            // mov dword [base+glowOff], imm32
        cb.AddRange(disp); cb.AddRange(BitConverter.GetBytes(_glow));
        cb.Add(0xE9); cb.AddRange(BitConverter.GetBytes((int)((addr + (ulong)pos + 8) - ((ulong)cave.ToInt64() + (ulong)cb.Count + 4))));
        mem.WriteBytes(cave, cb.ToArray());

        var patch = new byte[8]; patch[0] = 0xE9;
        BitConverter.GetBytes((int)(cave.ToInt64() - ((long)addr + pos + 5))).CopyTo(patch, 1);
        for (int k = 5; k < 8; k++) patch[k] = 0x90;

        _orig = code.Skip(pos).Take(8).ToArray(); _addr = addr; _pos = pos; _cave = cave;
        mem.WriteBytes((IntPtr)(addr + (ulong)pos), patch);
        return true;
    }

    private bool IsJmp(ProcessMemory mem)
    { try { return mem.ReadBytes((IntPtr)(_addr + (ulong)_pos), 1)[0] == 0xE9; } catch { return false; } }

    private void Restore()
    {
        var mem = _engine.Mem;
        if (mem != null && _addr != 0 && _pos >= 0 && _orig != null) { try { mem.WriteBytes((IntPtr)(_addr + (ulong)_pos), _orig); } catch { } }
        if (mem != null && _cave != IntPtr.Zero) { try { mem.Free(_cave); } catch { } }
        Forget();
    }

    private void Forget() { _addr = 0; _pos = -1; _orig = null; _cave = IntPtr.Zero; }
}
