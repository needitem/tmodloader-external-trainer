using System.Diagnostics;
using TerrariaTrainer.Memory;
using static TerrariaTrainer.Memory.Native;

namespace TerrariaTrainer.Tml;

/// <summary>
/// "NOP the reset" code injection: Terraria's Player.ResetEffects clears effect flags
/// every frame with `mov byte ptr [rbx+fieldOffset], 0` (C6 8X disp32 00). Overwriting
/// that 7-byte instruction with NOPs stops the reset, so an external write to the flag
/// sticks permanently. Patches are applied with the target threads suspended (debugger
/// style) so the game never executes a half-written instruction.
/// </summary>
public sealed class CodeInjector
{
    private readonly ProcessMemory _mem;
    private readonly TmlModel _model;
    private readonly Process _proc;

    // field name -> (patched address, original 7 bytes)
    private readonly Dictionary<string, (IntPtr addr, byte[] orig)> _patched = new();

    private const int ScanWindow = 0x4000; // ResetEffects body

    public CodeInjector(ProcessMemory mem, TmlModel model, Process proc)
    {
        _mem = mem; _model = model; _proc = proc;
    }

    public bool IsPatched(string field) => _patched.ContainsKey(field);

    /// <summary>True if this field's reset instruction can be located (so the UI can offer it).</summary>
    public bool CanInject(string field) => FindReset(field).addr != IntPtr.Zero;

    /// <summary>
    /// Locate, inside ResetEffects, the instruction that resets the given field, returning
    /// its address and length. Handles two forms seen in the JIT output:
    ///   bool:  C6 8X dd 00                    mov byte [rbx+off], 0      (7 bytes)
    ///   float: C5 FA 11 (mod=10,rm=011) dd     vmovss [rbx+off], xmmN     (8 bytes)
    /// Matched by the displacement == the field's real offset, so only that field is touched.
    /// </summary>
    public (IntPtr addr, int len) FindReset(string field)
    {
        var f = _model.PlayerFields.FirstOrDefault(x => x.Name == field);
        if (f == null || _model.ResetEffectsAddr == 0) return (IntPtr.Zero, 0);
        int target = f.Offset;
        var code = _mem.ReadBytes((IntPtr)_model.ResetEffectsAddr, ScanWindow);

        for (int i = 0; i + 10 <= code.Length; i++)
        {
            // bool: mov byte ptr [rbx+disp32], 0
            if (code[i] == 0xC6)
            {
                byte modrm = code[i + 1];
                if ((modrm & 0xC0) == 0x80 && (modrm & 0x38) == 0x00 && (modrm & 0x07) != 4
                    && BitConverter.ToInt32(code, i + 2) == target && code[i + 6] == 0x00)
                    return ((IntPtr)(_model.ResetEffectsAddr + (ulong)i), 7);
            }
            // float: vmovss [rbx+disp32], xmmN   (VEX C5 FA 11, modrm mod=10 rm=011)
            if (code[i] == 0xC5 && code[i + 1] == 0xFA && code[i + 2] == 0x11)
            {
                byte modrm = code[i + 3];
                if ((modrm & 0xC7) == 0x83 && BitConverter.ToInt32(code, i + 4) == target)
                    return ((IntPtr)(_model.ResetEffectsAddr + (ulong)i), 8);
            }
        }
        return (IntPtr.Zero, 0);
    }

    /// <summary>NOP the field's reset instruction so the game stops overwriting it.</summary>
    public bool Patch(string field)
    {
        if (_patched.ContainsKey(field)) return true;
        var (addr, len) = FindReset(field);
        if (addr == IntPtr.Zero) return false;
        var orig = _mem.ReadBytes(addr, len);
        var nops = new byte[len];
        Array.Fill(nops, (byte)0x90);
        if (!WriteCodeSafely(addr, nops)) return false;
        _patched[field] = (addr, orig);
        return true;
    }

    /// <summary>Restore the original reset instruction.</summary>
    public bool Restore(string field)
    {
        if (!_patched.TryGetValue(field, out var p)) return true;
        bool ok = WriteCodeSafely(p.addr, p.orig);
        _patched.Remove(field);
        return ok;
    }

    public void RestoreAll()
    {
        foreach (var field in _patched.Keys.ToList()) Restore(field);
    }

    /// <summary>Patch executable code with the target frozen, then flush the icache.</summary>
    private bool WriteCodeSafely(IntPtr addr, byte[] bytes)
    {
        var suspended = SuspendTargetThreads();
        try
        {
            bool ok = _mem.WriteBytes(addr, bytes);
            FlushInstructionCache(_mem.Handle, addr, (IntPtr)bytes.Length);
            return ok;
        }
        finally
        {
            foreach (var h in suspended) { ResumeThread(h); CloseHandle(h); }
        }
    }

    private List<IntPtr> SuspendTargetThreads()
    {
        var handles = new List<IntPtr>();
        try
        {
            foreach (ProcessThread t in _proc.Threads)
            {
                IntPtr h = OpenThread(THREAD_SUSPEND_RESUME, false, t.Id);
                if (h != IntPtr.Zero) { SuspendThread(h); handles.Add(h); }
            }
        }
        catch { /* best effort */ }
        return handles;
    }
}
