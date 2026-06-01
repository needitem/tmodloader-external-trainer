using TerrariaTrainer.Memory;

namespace TerrariaTrainer.Core;

/// <summary>
/// Top-level controller: attaches to Terraria, installs the myPlayer hook,
/// resolves CE-style pointer chains, and reads/writes value entries.
/// </summary>
public sealed class TrainerEngine : IDisposable
{
    public const string ProcessName = "Terraria";

    public ProcessMemory? Mem { get; private set; }
    public MyPlayerHook? Hook { get; private set; }

    public bool Attached => Mem is { IsAttached: true } && Mem.IsAlive();
    public bool HookInstalled => Hook is { Installed: true };

    /// <summary>Live player base pointer (0 if not yet captured).</summary>
    public IntPtr PlayerBase => Hook?.GetPlayerBase() ?? IntPtr.Zero;

    public event Action<string>? Log;

    public void Attach()
    {
        Detach();
        var mem = ProcessMemory.Attach(ProcessName)
                  ?? throw new InvalidOperationException("Terraria.exe is not running.");
        Mem = mem;
        Log?.Invoke($"Attached to Terraria (PID {mem.Pid}).");
        if (!mem.IsTarget32Bit())
            Log?.Invoke("WARNING: target appears to be 64-bit. This table targets a 32-bit build; results may be wrong.");
    }

    public void InstallHook()
    {
        if (Mem == null) throw new InvalidOperationException("Attach first.");
        Hook ??= new MyPlayerHook(Mem);
        Hook.Install(msg => Log?.Invoke(msg));
    }

    /// <summary>
    /// Resolve a CE pointer chain to its final address using the live myPlayer slot.
    /// Returns IntPtr.Zero if the chain can't be resolved (null pointer along the way).
    /// </summary>
    public IntPtr Resolve(ValueEntry e)
    {
        if (Mem == null || Hook is not { Installed: true }) return IntPtr.Zero;
        if (e.Symbol != "myPlayer" || e.DerefBase is null) return IntPtr.Zero;

        IntPtr slot = Hook.MyPlayerSlot;
        IntPtr addr;
        if (e.DerefBase == true)
        {
            // "[myPlayer]+const" => read(slot) + const
            IntPtr player = Mem.ReadPtr(slot);
            if (player == IntPtr.Zero) return IntPtr.Zero;
            addr = (IntPtr)(player.ToInt64() + e.BaseConst);
        }
        else
        {
            // bare "myPlayer" => slot address itself (offsets will deref it)
            addr = slot;
        }

        // Apply offsets last-to-first, dereferencing between each (CE semantics).
        for (int i = e.Offsets.Count - 1; i >= 0; i--)
        {
            IntPtr p = Mem.ReadPtr(addr);
            if (p == IntPtr.Zero) return IntPtr.Zero;
            addr = (IntPtr)(p.ToInt64() + e.Offsets[i]);
        }
        return addr;
    }

    public string ReadDisplay(ValueEntry e)
    {
        if (Mem == null) return "—";
        var addr = Resolve(e);
        if (addr == IntPtr.Zero) return "—";
        try
        {
            return e.Type switch
            {
                "4 Bytes" => Mem.ReadInt32(addr).ToString(),
                "2 Bytes" => Mem.ReadInt16(addr).ToString(),
                "Byte" => Mem.ReadByte(addr).ToString(),
                "Float" => Mem.ReadFloat(addr).ToString("0.###"),
                "Double" => Mem.ReadDouble(addr).ToString("0.###"),
                "String" => Mem.ReadUnicodeString(addr, e.Length > 0 ? e.Length : 64),
                _ => "?",
            };
        }
        catch { return "err"; }
    }

    public bool WriteValue(ValueEntry e, string text)
    {
        if (Mem == null) return false;
        var addr = Resolve(e);
        if (addr == IntPtr.Zero) return false;
        try
        {
            switch (e.Type)
            {
                case "4 Bytes": return Mem.WriteInt32(addr, int.Parse(text));
                case "2 Bytes": return Mem.WriteInt16(addr, short.Parse(text));
                case "Byte": return Mem.WriteByte(addr, byte.Parse(text));
                case "Float": return Mem.WriteFloat(addr, float.Parse(text));
                case "Double": return Mem.WriteDouble(addr, double.Parse(text));
                case "String":
                    var bytes = System.Text.Encoding.Unicode.GetBytes(text + "\0");
                    return Mem.WriteBytes(addr, bytes);
                default: return false;
            }
        }
        catch { return false; }
    }

    public void Detach()
    {
        Hook?.Dispose();
        Hook = null;
        Mem?.Dispose();
        Mem = null;
    }

    public void Dispose() => Detach();
}
