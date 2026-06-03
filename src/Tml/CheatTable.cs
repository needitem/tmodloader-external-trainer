using System.Text.Json;
using System.Text.Json.Serialization;
using TerrariaTrainer.Cheats;

namespace TerrariaTrainer.Tml;

public enum RowKind { GroupHeader, Value, Toggle, Buff, Action, Inject, Fast, UseHook, Tools, Craft, Patch, Vanity, PatchSet }

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
    public string InjectValue = "true"; // value written for Inject/Fast/UseHook rows
    public string[] Methods = Array.Empty<string>(); // use-site methods to hook (UseHook rows)
    public string PatchMethod = ""; // method key to patch-return (Patch rows); value in InjectValue
}

internal sealed class TableEntryDto
{
    [JsonPropertyName("group")] public string Group { get; set; } = "";
    [JsonPropertyName("desc")] public string Desc { get; set; } = "";
    [JsonPropertyName("field")] public string Field { get; set; } = "";
    [JsonPropertyName("kind")] public string Kind { get; set; } = "value";
    [JsonPropertyName("value")] public string? Value { get; set; }
    [JsonPropertyName("methods")] public string[]? Methods { get; set; }
    [JsonPropertyName("method")] public string? Method { get; set; }
}

/// <summary>
/// Builds the curated CE-style table: only rows whose fields actually exist in the
/// running game are included (no empty features). Buff toggles are appended.
/// </summary>
public static class CheatTable
{
    public static List<CheatRow> Build(TmlModel model, IReadOnlyList<BuffCheat> buffs, CodeInjector? injector = null)
    {
        var rows = new List<CheatRow>();
        var byName = model.PlayerFields
            .GroupBy(f => f.Name).ToDictionary(g => g.Key, g => g.First());

        // ---- curated value/toggle/inject entries ----
        var dtos = LoadEntries();
        string? lastGroup = null;
        foreach (var d in dtos)
        {
            // Inventory-wide cheats with no Player field.
            if (d.Kind.Equals("tools", StringComparison.OrdinalIgnoreCase))
            {
                EmitGroup(rows, ref lastGroup, d.Group);
                rows.Add(new CheatRow { Kind = RowKind.Tools, Group = d.Group, Desc = d.Desc, InjectValue = d.Value ?? "1" });
                continue;
            }
            if (d.Kind.Equals("craft", StringComparison.OrdinalIgnoreCase))
            {
                EmitGroup(rows, ref lastGroup, d.Group);
                rows.Add(new CheatRow { Kind = RowKind.Craft, Group = d.Group, Desc = d.Desc });
                continue;
            }
            if (d.Kind.Equals("patchset", StringComparison.OrdinalIgnoreCase))
            {
                // d.Method holds "SetKey:ret;SetKey:ret" (ret = zero|true). Include if ANY set is present.
                if (injector == null || d.Method == null) continue;
                var specs = d.Method.Split(';', StringSplitOptions.RemoveEmptyEntries);
                bool any = specs.Any(s => injector.CanPatchSet(s.Split(':')[0]));
                if (!any) continue;
                EmitGroup(rows, ref lastGroup, d.Group);
                rows.Add(new CheatRow { Kind = RowKind.PatchSet, Group = d.Group, Desc = d.Desc, PatchMethod = d.Method });
                continue;
            }
            if (d.Kind.Equals("vanity", StringComparison.OrdinalIgnoreCase))
            {
                if (injector == null || !injector.CanHookVanity()) continue; // methods absent -> skip
                EmitGroup(rows, ref lastGroup, d.Group);
                rows.Add(new CheatRow { Kind = RowKind.Vanity, Group = d.Group, Desc = d.Desc });
                continue;
            }
            if (d.Kind.Equals("patch", StringComparison.OrdinalIgnoreCase))
            {
                if (injector == null || d.Method == null || !injector.CanPatch(d.Method)) continue; // method absent -> skip
                EmitGroup(rows, ref lastGroup, d.Group);
                rows.Add(new CheatRow { Kind = RowKind.Patch, Group = d.Group, Desc = d.Desc, PatchMethod = d.Method, InjectValue = d.Value ?? "1" });
                continue;
            }

            if (!byName.TryGetValue(d.Field, out var field)) continue; // field absent -> skip

            RowKind kind;
            if (d.Kind.Equals("inject", StringComparison.OrdinalIgnoreCase))
            {
                // only offer it if we can actually locate the reset to NOP
                if (injector == null || !injector.CanInject(d.Field)) continue;
                kind = RowKind.Inject;
            }
            else if (d.Kind.Equals("usehook", StringComparison.OrdinalIgnoreCase))
            {
                // only offer it if we can find the value's load instruction(s) to redirect
                if (injector == null || d.Methods == null || injector.FindLoadSites(d.Field, d.Methods).Count == 0) continue;
                kind = RowKind.UseHook;
            }
            else if (d.Kind.Equals("toggle", StringComparison.OrdinalIgnoreCase)) kind = RowKind.Toggle;
            else if (d.Kind.Equals("fast", StringComparison.OrdinalIgnoreCase)) kind = RowKind.Fast;
            else kind = RowKind.Value;

            EmitGroup(rows, ref lastGroup, d.Group);
            rows.Add(new CheatRow { Kind = kind, Group = d.Group, Desc = d.Desc, Field = field,
                InjectValue = d.Value ?? "true", Methods = d.Methods ?? Array.Empty<string>() });
        }

        // ---- inventory action ----
        EmitGroup(rows, ref lastGroup, "🎒 Inventory");
        rows.Add(new CheatRow { Kind = RowKind.Action, Group = "🎒 Inventory", Desc = "Max Stack All Items (click)" });

        // ---- buff toggles ----
        // Effects like speed/defense/vision/mining are delivered as buffs because the
        // game recomputes the raw Player fields every frame (external freezes can't hold
        // them); the in-game buff IS the reliable mechanism and the game applies it.
        const string buffGroup = "🔮 Buffs — speed · defense · vision · mining · immunity (tick On to keep)";
        EmitGroup(rows, ref lastGroup, buffGroup);
        foreach (var b in buffs)
        {
            if (b.Mode == "maxstack") continue; // surfaced as the action row above
            rows.Add(new CheatRow
            {
                Kind = RowKind.Buff,
                Group = buffGroup,
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
