using System.Runtime.InteropServices;
using static TerrariaTrainer.Memory.Native;

namespace TerrariaTrainer.Memory;

/// <summary>
/// Array-of-bytes scanner over the target's committed memory regions.
/// Needed because .NET/Mono JIT code (e.g. get_LocalPlayer) lives in
/// dynamically allocated executable regions, not in the module image.
/// </summary>
public sealed class AobScanner
{
    private readonly ProcessMemory _mem;

    public AobScanner(ProcessMemory mem) => _mem = mem;

    /// <summary>A parsed signature: bytes with optional wildcards.</summary>
    public readonly struct Pattern
    {
        public readonly byte[] Bytes;
        public readonly bool[] Mask; // true = match, false = wildcard

        public Pattern(byte[] bytes, bool[] mask) { Bytes = bytes; Mask = mask; }

        /// <summary>Parse "8B 44 90 08 C3" or with "??"/"?" wildcards.</summary>
        public static Pattern Parse(string sig)
        {
            var tokens = sig.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            var bytes = new byte[tokens.Length];
            var mask = new bool[tokens.Length];
            for (int i = 0; i < tokens.Length; i++)
            {
                if (tokens[i] is "?" or "??" or "*")
                {
                    mask[i] = false;
                    bytes[i] = 0;
                }
                else
                {
                    mask[i] = true;
                    bytes[i] = Convert.ToByte(tokens[i], 16);
                }
            }
            return new Pattern(bytes, mask);
        }
    }

    public enum RegionFilter { Executable, Writable, Any }

    /// <summary>Find the first match. Returns IntPtr.Zero if none.</summary>
    public IntPtr FindFirst(string signature, RegionFilter filter = RegionFilter.Executable)
    {
        foreach (var hit in FindAll(signature, filter, limit: 1))
            return hit;
        return IntPtr.Zero;
    }

    /// <summary>Enumerate every match of the signature in the chosen region class.</summary>
    public List<IntPtr> FindAll(string signature, RegionFilter filter = RegionFilter.Executable, int limit = int.MaxValue)
    {
        var pattern = Pattern.Parse(signature);
        var results = new List<IntPtr>();

        IntPtr addr = IntPtr.Zero;
        long max = Environment.Is64BitOperatingSystem && !_mem.IsTarget32Bit()
            ? 0x7FFFFFFFFFFFL
            : 0x7FFFFFFFL; // 32-bit user space

        int mbiSize = Marshal.SizeOf<MEMORY_BASIC_INFORMATION>();

        while (addr.ToInt64() < max)
        {
            if (VirtualQueryEx(_mem.Handle, addr, out var mbi, (IntPtr)mbiSize) == IntPtr.Zero)
                break;

            long regionSize = mbi.RegionSize.ToInt64();
            if (regionSize <= 0) break;

            if (mbi.State == (uint)MemoryState.Commit && PassesFilter(mbi, filter))
            {
                ScanRegion(mbi.BaseAddress, (int)Math.Min(regionSize, int.MaxValue), pattern, results, limit);
                if (results.Count >= limit) break;
            }

            long next = mbi.BaseAddress.ToInt64() + regionSize;
            if (next <= addr.ToInt64()) break; // guard against no progress
            addr = (IntPtr)next;
        }

        return results;
    }

    private static bool PassesFilter(MEMORY_BASIC_INFORMATION mbi, RegionFilter filter)
    {
        var protect = (MemoryProtection)(mbi.Protect & 0xFF);
        if ((mbi.Protect & (uint)MemoryProtection.Guard) != 0) return false;
        if (protect == MemoryProtection.NoAccess) return false;

        return filter switch
        {
            RegionFilter.Executable => protect is MemoryProtection.Execute
                or MemoryProtection.ExecuteRead or MemoryProtection.ExecuteReadWrite,
            RegionFilter.Writable => protect is MemoryProtection.ReadWrite
                or MemoryProtection.ExecuteReadWrite or MemoryProtection.WriteCopy,
            _ => true,
        };
    }

    private void ScanRegion(IntPtr baseAddr, int size, in Pattern pattern, List<IntPtr> results, int limit)
    {
        // Read the region in chunks with overlap so matches spanning chunk
        // boundaries are not missed.
        const int chunk = 0x100000; // 1 MB
        int overlap = pattern.Bytes.Length - 1;
        var buffer = new byte[Math.Min(size, chunk)];

        int offset = 0;
        while (offset < size)
        {
            int toRead = Math.Min(chunk, size - offset);
            if (buffer.Length < toRead) buffer = new byte[toRead];
            var slice = buffer.Length == toRead ? buffer : new byte[toRead];

            if (!_mem.ReadBytes((IntPtr)(baseAddr.ToInt64() + offset), slice))
            {
                // Region partly unreadable; skip ahead.
                offset += toRead;
                continue;
            }

            int searchLen = toRead - pattern.Bytes.Length + 1;
            for (int i = 0; i < searchLen; i++)
            {
                if (MatchAt(slice, i, pattern))
                {
                    results.Add((IntPtr)(baseAddr.ToInt64() + offset + i));
                    if (results.Count >= limit) return;
                }
            }

            if (toRead < chunk) break;
            offset += chunk - overlap; // overlap to catch boundary-spanning matches
        }
    }

    private static bool MatchAt(byte[] data, int index, in Pattern pattern)
    {
        for (int j = 0; j < pattern.Bytes.Length; j++)
        {
            if (pattern.Mask[j] && data[index + j] != pattern.Bytes[j])
                return false;
        }
        return true;
    }
}
