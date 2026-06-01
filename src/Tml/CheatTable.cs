using System.Text.Json;
using System.Text.Json.Serialization;
using TerrariaTrainer.Cheats;

namespace TerrariaTrainer.Tml;

public enum RowKind { GroupHeader, Value, Toggle, Buff, Action }

/// <summary>A single row in the Cheat-Engine-style table.</summary>
public sealed class CheatRow
{
    public RowKind Kind;
    public string Group = "";
    public string Desc = "";
    public TmlField? Field;      // for Value/Toggle
    public BuffCheat? Buff;      // for Buff
    public bool Active;          // freeze (Value) / hold-true (Toggle) / enable (Buff)
    public string? FrozenText;   // value to assert while Active (Value rows)
}

internal sealed class TableEntryDto
{
    [JsonPropertyName("group")] public string Group { get; set; } = "";
    [JsonPropertyName("desc")] public string Desc { get; set; } = "";
    [JsonPropertyName("field")] public string Field { get; set; } = "";
    [JsonPropertyName("kind")] public string Kind { get; set; } = "value";
}

/// <summary>
/// Builds the curated CE-style table: only rows whose fields actually exist in the
/// running game are included (no empty features). Buff toggles are appended.
/// </summary>
public static class CheatTable
{
    public static List<CheatRow> Build(TmlModel model, IReadOnlyList<BuffCheat> buffs)
    {
        var rows = new List<CheatRow>();
        var byName = model.PlayerFields
            .GroupBy(f => f.Name).ToDictionary(g => g.Key, g => g.First());

        // ---- curated value/toggle entries ----
        var dtos = LoadEntries();
        string? lastGroup = null;
        foreach (var d in dtos)
        {
            if (!byName.TryGetValue(d.Field, out var field)) continue; // field absent -> skip
            EmitGroup(rows, ref lastGroup, d.Group);
            rows.Add(new CheatRow
            {
                Kind = d.Kind.Equals("toggle", StringComparison.OrdinalIgnoreCase) ? RowKind.Toggle : RowKind.Value,
                Group = d.Group,
                Desc = d.Desc,
                Field = field,
            });
        }

        // ---- inventory action ----
        EmitGroup(rows, ref lastGroup, "🎒 Inventory");
        rows.Add(new CheatRow { Kind = RowKind.Action, Group = "🎒 Inventory", Desc = "Max Stack All Items (click)" });

        // ---- buff toggles ----
        EmitGroup(rows, ref lastGroup, "🔮 Buffs (freeze to keep active)");
        foreach (var b in buffs)
        {
            if (b.Mode == "maxstack") continue; // surfaced as the action row above
            rows.Add(new CheatRow
            {
                Kind = RowKind.Buff,
                Group = "🔮 Buffs (freeze to keep active)",
                Desc = b.Name,
                Buff = b,
            });
        }

        return rows;
    }

    private static void EmitGroup(List<CheatRow> rows, ref string? lastGroup, string group)
    {
        if (group == lastGroup) return;
        lastGroup = group;
        rows.Add(new CheatRow { Kind = RowKind.GroupHeader, Group = group, Desc = group });
    }

    private static List<TableEntryDto> LoadEntries()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Data", "tml_table.json");
        if (!File.Exists(path)) return new();
        return JsonSerializer.Deserialize<List<TableEntryDto>>(File.ReadAllText(path)) ?? new();
    }
}
