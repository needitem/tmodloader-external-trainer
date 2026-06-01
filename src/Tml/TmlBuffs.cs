using TerrariaTrainer.Cheats;

namespace TerrariaTrainer.Tml;

/// <summary>Runs the buff-toggle cheats (data from buffs.json) against tModLoader 64-bit.</summary>
public sealed class TmlBuffs
{
    public List<BuffCheat> Buffs { get; } = BuffCheat.LoadAll();

    public void Tick(TmlEngine engine)
    {
        if (!engine.Attached || engine.PlayerBase() == IntPtr.Zero) return;
        var (id, tm, len) = engine.BuffArrays();
        if (id == IntPtr.Zero || tm == IntPtr.Zero || len <= 0) return;
        var m = engine.Mem!;
        int slots = Math.Min(len, 256);

        foreach (var b in Buffs)
        {
            if (!b.Enabled || b.Mode == "maxstack") continue;
            if (b.Mode == "apply") Apply(engine, m, id, tm, slots, b);
            else if (b.Mode == "clear") Clear(engine, m, id, tm, slots, b);
        }
    }

    private static void Apply(TmlEngine e, Memory.ProcessMemory m, IntPtr id, IntPtr tm, int slots, BuffCheat b)
    {
        for (int i = 0; i < slots; i++)
            if (m.ReadInt32(e.BuffSlot(id, i)) == b.Buff)
            {
                m.WriteInt32(e.BuffSlot(tm, i), b.Duration);
                return;
            }
        for (int i = 0; i < slots; i++)
            if (m.ReadInt32(e.BuffSlot(id, i)) == 0)
            {
                m.WriteInt32(e.BuffSlot(id, i), b.Buff);
                m.WriteInt32(e.BuffSlot(tm, i), b.Duration);
                return;
            }
    }

    private static void Clear(TmlEngine e, Memory.ProcessMemory m, IntPtr id, IntPtr tm, int slots, BuffCheat b)
    {
        for (int i = 0; i < slots; i++)
            if (m.ReadInt32(e.BuffSlot(id, i)) == b.Buff)
            {
                m.WriteInt32(e.BuffSlot(id, i), 0);
                m.WriteInt32(e.BuffSlot(tm, i), 0);
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
