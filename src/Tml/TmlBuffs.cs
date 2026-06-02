using TerrariaTrainer.Cheats;

namespace TerrariaTrainer.Tml;

/// <summary>Runs the buff-toggle cheats (data from buffs.json) against tModLoader 64-bit.</summary>
public sealed class TmlBuffs
{
    public List<BuffCheat> Buffs { get; } = BuffCheat.LoadAll();

    public void Tick(TmlEngine engine)
    {
        // Skip the whole pass when nothing is enabled (common case).
        bool any = false;
        foreach (var b in Buffs) if (b.Enabled && b.Mode != "maxstack") { any = true; break; }
        if (!any) return;

        if (!engine.Attached || engine.PlayerBase() == IntPtr.Zero) return;
        var (id, tm, len) = engine.BuffArrays();
        if (id == IntPtr.Zero || tm == IntPtr.Zero || len <= 0) return;
        var m = engine.Mem!;
        int slots = Math.Min(len, 256);

        // One bulk read of the buff-type array; scan it in managed memory instead of a
        // syscall per slot per buff. The local copy is updated after each write so buffs
        // in the same tick don't fight over the same empty slot.
        var typeBytes = m.ReadBytes(id, ArrHeader + slots * 4);
        Span<int> types = stackalloc int[slots];
        for (int i = 0; i < slots; i++) types[i] = BitConverter.ToInt32(typeBytes, ArrHeader + i * 4);

        foreach (var b in Buffs)
        {
            if (!b.Enabled || b.Mode == "maxstack") continue;
            if (b.Mode == "apply") Apply(engine, m, id, tm, types, b);
            else if (b.Mode == "clear") Clear(engine, m, id, tm, types, b);
        }
    }

    private const int ArrHeader = 0x10; // x64 .NET array data offset

    private static void Apply(TmlEngine e, Memory.ProcessMemory m, IntPtr id, IntPtr tm, Span<int> types, BuffCheat b)
    {
        for (int i = 0; i < types.Length; i++)
            if (types[i] == b.Buff) { m.WriteInt32(e.BuffSlot(tm, i), b.Duration); return; }
        for (int i = 0; i < types.Length; i++)
            if (types[i] == 0)
            {
                m.WriteInt32(e.BuffSlot(id, i), b.Buff);
                m.WriteInt32(e.BuffSlot(tm, i), b.Duration);
                types[i] = b.Buff; // reflect in the local snapshot
                return;
            }
    }

    private static void Clear(TmlEngine e, Memory.ProcessMemory m, IntPtr id, IntPtr tm, Span<int> types, BuffCheat b)
    {
        for (int i = 0; i < types.Length; i++)
            if (types[i] == b.Buff)
            {
                m.WriteInt32(e.BuffSlot(id, i), 0);
                m.WriteInt32(e.BuffSlot(tm, i), 0);
                types[i] = 0;
            }
    }

    public void OnDisableBuff(TmlEngine engine, BuffCheat b)
    {
        if (b.Mode != "apply") return;
        var (id, tm, len) = engine.BuffArrays();
        if (id == IntPtr.Zero || tm == IntPtr.Zero) return;
        var m = engine.Mem!;
        int slots = Math.Min(len, 256);
        for (int i = 0; i < slots; i++)
            if (m.ReadInt32(engine.BuffSlot(id, i)) == b.Buff)
            {
                m.WriteInt32(engine.BuffSlot(id, i), 0);
                m.WriteInt32(engine.BuffSlot(tm, i), 0);
            }
    }

    public void DisableAll(TmlEngine engine)
    {
        foreach (var b in Buffs)
            if (b.Enabled) { OnDisableBuff(engine, b); b.Enabled = false; }
    }
}
