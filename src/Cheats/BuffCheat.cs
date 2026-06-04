using System.Text.Json;
using System.Text.Json.Serialization;
using TerrariaTrainer.Core;

namespace TerrariaTrainer.Cheats;

/// <summary>A buff-toggle cheat ported from a CE Lua timer script.</summary>
public sealed class BuffCheat
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("mode")] public string Mode { get; set; } = "apply"; // apply | clear | maxstack
    [JsonPropertyName("buff")] public int Buff { get; set; }
    [JsonPropertyName("duration")] public int Duration { get; set; } = 219600;
    [JsonPropertyName("cat")] public string Category { get; set; } = "Misc";

    [JsonIgnore] public bool Enabled { get; set; }

    // Terraria Player buff arrays (32-bit .NET): managed array data starts at +8.
    private const int BuffTypeArr = 0xC8;
    private const int BuffTimeArr = 0xCC;
    private const int BuffSlots = 44;        // 0..43
    private const int ArrData = 8;

    public static List<BuffCheat> LoadAll()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Data", "buffs.json");
        if (!File.Exists(path)) return new();
        return JsonSerializer.Deserialize<List<BuffCheat>>(File.ReadAllText(path)) ?? new();
    }

    /// <summary>Called every engine tick while enabled (apply/clear keep state asserted).</summary>
    public void Tick(TrainerEngine engine)
    {
        if (Mode == "maxstack") return; // one-shot, invoked explicitly
        var m = engine.Mem;
        var player = engine.PlayerBase;
        if (m == null || player == IntPtr.Zero) return;

        IntPtr aID = m.ReadPtr((IntPtr)(player.ToInt64() + BuffTypeArr));
        IntPtr aTm = m.ReadPtr((IntPtr)(player.ToInt64() + BuffTimeArr));
        if (aID == IntPtr.Zero || aTm == IntPtr.Zero) return;

        if (Mode == "apply") TickApply(engine, aID, aTm);
        else if (Mode == "clear") TickClear(engine, aID, aTm);
    }

    private void TickApply(TrainerEngine engine, IntPtr aID, IntPtr aTm)
    {
        var m = engine.Mem!;
        // Refresh if already present.
        for (int i = 0; i < BuffSlots; i++)
        {
            if (m.ReadInt32(SlotId(aID, i)) == Buff)
            {
                m.WriteInt32(SlotId(aTm, i), Duration);
                return;
            }
        }
        // Else occupy the first empty slot.
        for (int i = 0; i < BuffSlots; i++)
        {
            if (m.ReadInt32(SlotId(aID, i)) == 0)
            {
                m.WriteInt32(SlotId(aID, i), Buff);
                m.WriteInt32(SlotId(aTm, i), Duration);
                return;
            }
        }
    }

    private void TickClear(TrainerEngine engine, IntPtr aID, IntPtr aTm)
    {
        var m = engine.Mem!;
        for (int i = 0; i < BuffSlots; i++)
        {
            if (m.ReadInt32(SlotId(aID, i)) == Buff)
            {
                m.WriteInt32(SlotId(aID, i), 0);
                m.WriteInt32(SlotId(aTm, i), 0);
            }
        }
    }

    /// <summary>On disable, an applied buff is removed (mirrors the CT [DISABLE] block).</summary>
    public void OnDisable(TrainerEngine engine)
    {
        if (Mode != "apply") return;
        var m = engine.Mem;
        var player = engine.PlayerBase;
        if (m == null || player == IntPtr.Zero) return;
        IntPtr aID = m.ReadPtr((IntPtr)(player.ToInt64() + BuffTypeArr));
        IntPtr aTm = m.ReadPtr((IntPtr)(player.ToInt64() + BuffTimeArr));
        if (aID == IntPtr.Zero || aTm == IntPtr.Zero) return;
        for (int i = 0; i < BuffSlots; i++)
        {
            if (m.ReadInt32(SlotId(aID, i)) == Buff)
            {
                m.WriteInt32(SlotId(aID, i), 0);
                m.WriteInt32(SlotId(aTm, i), 0);
            }
        }
    }

    private static IntPtr SlotId(IntPtr arr, int i) => (IntPtr)(arr.ToInt64() + ArrData + i * 4);

    /// <summary>One-shot: max-stack every inventory item (CT Ctrl+F2 hotkey).</summary>
    public static int MaxStackInventory(TrainerEngine engine)
    {
        var m = engine.Mem;
        var player = engine.PlayerBase;
        if (m == null || player == IntPtr.Zero) return 0;
        IntPtr inv = m.ReadPtr((IntPtr)(player.ToInt64() + 0xD8));
        if (inv == IntPtr.Zero) return 0;
        int changed = 0;
        for (int slot = 0; slot < 50; slot++)
        {
            IntPtr item = m.ReadPtr((IntPtr)(inv.ToInt64() + 8 + slot * 4));
            if (item == IntPtr.Zero) continue;
            int itype = m.ReadInt32((IntPtr)(item.ToInt64() + 0x50));
            int maxS = m.ReadInt32((IntPtr)(item.ToInt64() + 0x68));
            if (itype > 0 && maxS > 1)
            {
                m.WriteInt32((IntPtr)(item.ToInt64() + 0x64), maxS); // stack = maxStack
                changed++;
            }
        }
        return changed;
    }
}
