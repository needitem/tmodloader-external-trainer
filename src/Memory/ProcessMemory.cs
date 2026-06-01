using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using static TerrariaTrainer.Memory.Native;

namespace TerrariaTrainer.Memory;

/// <summary>
/// Owns a handle to the target process and exposes typed read/write helpers.
/// All target pointers are treated as 32-bit because the game is a 32-bit process.
/// </summary>
public sealed class ProcessMemory : IDisposable
{
    private IntPtr _handle;

    public Process Process { get; }
    public int Pid => Process.Id;
    public bool IsAttached => _handle != IntPtr.Zero;

    /// <summary>Base address of the main module (Terraria.exe).</summary>
    public IntPtr ModuleBase { get; private set; }
    public int ModuleSize { get; private set; }

    private ProcessMemory(Process process, IntPtr handle)
    {
        Process = process;
        _handle = handle;
        try
        {
            var mod = process.MainModule!;
            ModuleBase = mod.BaseAddress;
            ModuleSize = mod.ModuleMemorySize;
        }
        catch
        {
            // MainModule can throw under WOW64 mismatch; AOB scan still works region-wise.
            ModuleBase = IntPtr.Zero;
            ModuleSize = 0;
        }
    }

    /// <summary>Find a running process by name (without extension) and attach.</summary>
    public static ProcessMemory? Attach(string processName)
    {
        var proc = Process.GetProcessesByName(processName).FirstOrDefault();
        if (proc == null) return null;
        return Attach(proc);
    }

    public static ProcessMemory Attach(Process proc)
    {
        IntPtr handle = OpenProcess(ProcessAccess.Trainer, false, proc.Id);
        if (handle == IntPtr.Zero)
            throw new InvalidOperationException(
                $"OpenProcess failed for PID {proc.Id} (Win32 {Marshal.GetLastWin32Error()}). Run the trainer as Administrator.");
        return new ProcessMemory(proc, handle);
    }

    /// <summary>True if the target is a 32-bit (WOW64) process. The CT targets a 32-bit build.</summary>
    public bool IsTarget32Bit()
    {
        if (!Environment.Is64BitOperatingSystem) return true;
        return IsWow64Process(_handle, out bool wow64) && wow64;
    }

    // ---- raw ----

    public bool ReadBytes(IntPtr address, byte[] buffer)
    {
        return ReadProcessMemory(_handle, address, buffer, buffer.Length, out var read)
               && read.ToInt64() == buffer.Length;
    }

    public byte[] ReadBytes(IntPtr address, int count)
    {
        var buf = new byte[count];
        ReadBytes(address, buf);
        return buf;
    }

    public bool WriteBytes(IntPtr address, byte[] buffer)
    {
        // Ensure the page is writable; restore protection afterwards.
        VirtualProtectEx(_handle, address, (IntPtr)buffer.Length, MemoryProtection.ExecuteReadWrite, out var old);
        bool ok = WriteProcessMemory(_handle, address, buffer, buffer.Length, out var written)
                  && written.ToInt64() == buffer.Length;
        VirtualProtectEx(_handle, address, (IntPtr)buffer.Length, old, out _);
        return ok;
    }

    // ---- typed ----

    public int ReadInt32(IntPtr address) => BitConverter.ToInt32(ReadBytes(address, 4));
    public uint ReadUInt32(IntPtr address) => BitConverter.ToUInt32(ReadBytes(address, 4));
    public short ReadInt16(IntPtr address) => BitConverter.ToInt16(ReadBytes(address, 2));
    public byte ReadByte(IntPtr address) => ReadBytes(address, 1)[0];
    public float ReadFloat(IntPtr address) => BitConverter.ToSingle(ReadBytes(address, 4));
    public double ReadDouble(IntPtr address) => BitConverter.ToDouble(ReadBytes(address, 8));

    /// <summary>Read a 32-bit pointer value (vanilla 32-bit target).</summary>
    public IntPtr ReadPtr(IntPtr address) => (IntPtr)ReadUInt32(address);

    public long ReadInt64(IntPtr address) => BitConverter.ToInt64(ReadBytes(address, 8));

    /// <summary>Read a 64-bit pointer value (tModLoader / .NET Core target).</summary>
    public IntPtr ReadPtr64(IntPtr address) => (IntPtr)BitConverter.ToInt64(ReadBytes(address, 8));

    public bool WriteInt32(IntPtr address, int value) => WriteBytes(address, BitConverter.GetBytes(value));
    public bool WriteUInt32(IntPtr address, uint value) => WriteBytes(address, BitConverter.GetBytes(value));
    public bool WriteInt16(IntPtr address, short value) => WriteBytes(address, BitConverter.GetBytes(value));
    public bool WriteByte(IntPtr address, byte value) => WriteBytes(address, new[] { value });
    public bool WriteFloat(IntPtr address, float value) => WriteBytes(address, BitConverter.GetBytes(value));
    public bool WriteDouble(IntPtr address, double value) => WriteBytes(address, BitConverter.GetBytes(value));

    /// <summary>Read a .NET-style UTF-16 string (used for Player.name).</summary>
    public string ReadUnicodeString(IntPtr address, int maxChars)
    {
        var bytes = ReadBytes(address, maxChars * 2);
        var s = Encoding.Unicode.GetString(bytes);
        int nul = s.IndexOf('\0');
        return nul >= 0 ? s[..nul] : s;
    }

    // ---- allocation ----

    public IntPtr Alloc(int size, MemoryProtection protect = MemoryProtection.ExecuteReadWrite)
    {
        IntPtr p = VirtualAllocEx(_handle, IntPtr.Zero, (IntPtr)size,
            AllocationType.Commit | AllocationType.Reserve, protect);
        if (p == IntPtr.Zero)
            throw new InvalidOperationException($"VirtualAllocEx failed (Win32 {Marshal.GetLastWin32Error()}).");
        return p;
    }

    public void Free(IntPtr address) => VirtualFreeEx(_handle, address, IntPtr.Zero, MEM_RELEASE);

    public IntPtr Handle => _handle;

    public bool IsAlive()
    {
        try { return !Process.HasExited; }
        catch { return false; }
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero)
        {
            CloseHandle(_handle);
            _handle = IntPtr.Zero;
        }
    }
}
