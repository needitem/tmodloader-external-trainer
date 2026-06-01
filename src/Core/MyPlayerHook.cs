using TerrariaTrainer.Memory;
using static TerrariaTrainer.Memory.Native;

namespace TerrariaTrainer.Core;

/// <summary>
/// External re-implementation of the CT base script ("get_LocalPlayer" hook).
///
/// CT script:
///   define(address, Terraria.Main::get_LocalPlayer+10)
///   define(bytes, 8B 44 90 08 C3)          ; mov eax,[eax+edx*4+08] / ret
///   newmem:
///     mov eax,[eax+edx*4+08]
///     mov [myPlayer],eax
///     ret
///   address: jmp newmem
///
/// We AOB-scan for the 5 body bytes, allocate a code cave + a 4-byte slot,
/// write the capturing hook and patch a jmp. The game's own thread runs the
/// hook every frame, so [myPlayer] always holds the live local Player pointer.
/// </summary>
public sealed class MyPlayerHook : IDisposable
{
    // mov eax,[eax+edx*4+08] ; ret   — the original get_LocalPlayer+10 body.
    public const string Signature = "8B 44 90 08 C3";

    private readonly ProcessMemory _mem;
    private IntPtr _hookSite;        // get_LocalPlayer+10 (patched with jmp)
    private IntPtr _newmem;          // code cave
    private IntPtr _myPlayerSlot;    // 4-byte slot holding the captured pointer
    private byte[]? _originalBytes;  // for clean uninstall

    public bool Installed { get; private set; }
    public IntPtr MyPlayerSlot => _myPlayerSlot;
    public IntPtr HookSite => _hookSite;

    public MyPlayerHook(ProcessMemory mem) => _mem = mem;

    /// <summary>The live local player base pointer: value stored at the capture slot.</summary>
    public IntPtr GetPlayerBase()
    {
        if (!Installed) return IntPtr.Zero;
        return _mem.ReadPtr(_myPlayerSlot);
    }

    /// <summary>
    /// Scan for candidates and install the hook on the one that yields a valid
    /// player pointer. Returns the chosen hook site. Throws if none validate.
    /// </summary>
    public IntPtr Install(Action<string>? log = null)
    {
        if (Installed) return _hookSite;

        var scanner = new AobScanner(_mem);
        var candidates = scanner.FindAll(Signature, AobScanner.RegionFilter.Executable);
        log?.Invoke($"AOB '{Signature}': {candidates.Count} candidate(s).");
        if (candidates.Count == 0)
            throw new InvalidOperationException(
                "get_LocalPlayer signature not found. Is Terraria fully loaded into a world?");

        foreach (var site in candidates)
        {
            try
            {
                InstallAt(site);
                if (WaitForValidPlayer(out var pb))
                {
                    log?.Invoke($"Hook OK @ 0x{site.ToInt64():X} -> player 0x{pb.ToInt64():X}");
                    Installed = true;
                    return site;
                }
                log?.Invoke($"Candidate 0x{site.ToInt64():X} did not capture a valid player; reverting.");
                Uninstall();
            }
            catch (Exception ex)
            {
                log?.Invoke($"Candidate 0x{site.ToInt64():X} failed: {ex.Message}");
                try { Uninstall(); } catch { /* best effort */ }
            }
        }

        throw new InvalidOperationException(
            $"Found {candidates.Count} signature match(es) but none produced a valid player pointer. " +
            "Make sure you are in-game (a world is loaded).");
    }

    private void InstallAt(IntPtr site)
    {
        _hookSite = site;
        _originalBytes = _mem.ReadBytes(site, 5);

        _newmem = _mem.Alloc(0x100, MemoryProtection.ExecuteReadWrite);
        _myPlayerSlot = _mem.Alloc(4, MemoryProtection.ReadWrite);
        _mem.WriteUInt32(_myPlayerSlot, 0);

        // newmem:
        //   8B 44 90 08            mov eax,[eax+edx*4+08]
        //   A3 <myPlayerSlot>      mov [myPlayerSlot],eax
        //   C3                     ret
        var code = new List<byte> { 0x8B, 0x44, 0x90, 0x08, 0xA3 };
        code.AddRange(BitConverter.GetBytes((uint)_myPlayerSlot.ToInt64()));
        code.Add(0xC3);
        _mem.WriteBytes(_newmem, code.ToArray());

        // address: jmp newmem  (E9 rel32), rel = newmem - (site + 5)
        int rel = (int)(_newmem.ToInt64() - (site.ToInt64() + 5));
        var jmp = new byte[5];
        jmp[0] = 0xE9;
        BitConverter.GetBytes(rel).CopyTo(jmp, 1);
        _mem.WriteBytes(site, jmp);
    }

    /// <summary>Poll the capture slot briefly until the game runs the hook.</summary>
    private bool WaitForValidPlayer(out IntPtr playerBase)
    {
        playerBase = IntPtr.Zero;
        for (int i = 0; i < 30; i++) // ~1.5s @ 50ms
        {
            Thread.Sleep(50);
            var pb = _mem.ReadPtr(_myPlayerSlot);
            if (IsPlausiblePlayer(pb))
            {
                playerBase = pb;
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Sanity-check a captured pointer: must be readable and expose sane
    /// Life/LifeMax at the known offsets (CT: +42C life, +424 lifeMax).
    /// </summary>
    private bool IsPlausiblePlayer(IntPtr pb)
    {
        if (pb == IntPtr.Zero) return false;
        long v = pb.ToInt64();
        if (v < 0x10000 || v > 0x7FFFFFFF) return false;

        try
        {
            int life = _mem.ReadInt32((IntPtr)(v + 0x42C));
            int lifeMax = _mem.ReadInt32((IntPtr)(v + 0x424));
            // Vanilla life range 0..500+ (allow headroom for modded/buffed).
            return lifeMax is > 0 and <= 1_000_000 && life >= 0 && life <= lifeMax + 1_000_000;
        }
        catch
        {
            return false;
        }
    }

    public void Uninstall()
    {
        if (_hookSite != IntPtr.Zero && _originalBytes != null)
        {
            try { _mem.WriteBytes(_hookSite, _originalBytes); } catch { /* process may be gone */ }
        }
        if (_newmem != IntPtr.Zero) { try { _mem.Free(_newmem); } catch { } _newmem = IntPtr.Zero; }
        if (_myPlayerSlot != IntPtr.Zero) { try { _mem.Free(_myPlayerSlot); } catch { } _myPlayerSlot = IntPtr.Zero; }
        _hookSite = IntPtr.Zero;
        _originalBytes = null;
        Installed = false;
    }

    public void Dispose() => Uninstall();
}
