using System.Text.Json;
using System.Text.Json.Serialization;
using TerrariaTrainer.Cheats;

namespace TerrariaTrainer.Tml;

public enum RowKind { GroupHeader, Value, Toggle, Buff, Action, Inject, Fast, UseHook, Tools, Craft, Patch, Vanity, PatchSet, DropMult, Crate, ScopedDrop, BuffClear, PatchInt, InfAmmo, SpawnBoost, RareSpawn, Aimbot, SprayRange, TileReach, AnglerQuest }

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
            if (d.Kind.Equals("patchint", StringComparison.OrdinalIgnoreCase))
            {
                if (injector == null || d.Method == null || !injector.CanPatch(d.Method)) continue;
                EmitGroup(rows, ref lastGroup, d.Group);
                rows.Add(new CheatRow { Kind = RowKind.PatchInt, Group = d.Group, Desc = d.Desc, PatchMethod = d.Method, InjectValue = d.Value ?? "2000" });
                continue;
            }
            if (d.Kind.Equals("rarespawn", StringComparison.OrdinalIgnoreCase))
            {
                EmitGroup(rows, ref lastGroup, d.Group);
                rows.Add(new CheatRow { Kind = RowKind.RareSpawn, Group = d.Group, Desc = d.Desc, InjectValue = d.Value ?? "0" });
                continue;
            }
            if (d.Kind.Equals("angler", StringComparison.OrdinalIgnoreCase))
            {
                if (model.AnglerFinishedAddr == 0 && model.AnglerWhoFinishedAddr == 0) continue; // statics not found
                EmitGroup(rows, ref lastGroup, d.Group);
                rows.Add(new CheatRow { Kind = RowKind.AnglerQuest, Group = d.Group, Desc = d.Desc });
                continue;
            }
            if (d.Kind.Equals("tilereach", StringComparison.OrdinalIgnoreCase))
            {
                if (model.TileRangeXAddr == 0) continue; // static not resolved -> skip
                EmitGroup(rows, ref lastGroup, d.Group);
                rows.Add(new CheatRow { Kind = RowKind.TileReach, Group = d.Group, Desc = d.Desc, InjectValue = d.Value ?? "25" });
                continue;
            }
            if (d.Kind.Equals("sprayrange", StringComparison.OrdinalIgnoreCase))
            {
                EmitGroup(rows, ref lastGroup, d.Group);
                rows.Add(new CheatRow { Kind = RowKind.SprayRange, Group = d.Group, Desc = d.Desc, InjectValue = d.Value ?? "16" });
                continue;
            }
            if (d.Kind.Equals("spawnboost", StringComparison.OrdinalIgnoreCase))
            {
                EmitGroup(rows, ref lastGroup, d.Group);
                rows.Add(new CheatRow { Kind = RowKind.SpawnBoost, Group = d.Group, Desc = d.Desc, PatchMethod = d.Method ?? "max", InjectValue = d.Value ?? "60" });
                continue;
            }
            if (d.Kind.Equals("aimbot", StringComparison.OrdinalIgnoreCase))
            {
                EmitGroup(rows, ref lastGroup, d.Group);
                rows.Add(new CheatRow { Kind = RowKind.Aimbot, Group = d.Group, Desc = d.Desc, PatchMethod = d.Method ?? "enemy" });
                continue;
            }
            if (d.Kind.Equals("infammo", StringComparison.OrdinalIgnoreCase))
            {
                EmitGroup(rows, ref lastGroup, d.Group);
                rows.Add(new CheatRow { Kind = RowKind.InfAmmo, Group = d.Group, Desc = d.Desc });
                continue;
            }
            if (d.Kind.Equals("buffclear", StringComparison.OrdinalIgnoreCase))
            {
                EmitGroup(rows, ref lastGroup, d.Group);
                rows.Add(new CheatRow { Kind = RowKind.BuffClear, Group = d.Group, Desc = d.Desc, InjectValue = d.Value ?? "0" });
                continue;
            }
            if (d.Kind.Equals("scopedrop", StringComparison.OrdinalIgnoreCase))
            {
                EmitGroup(rows, ref lastGroup, d.Group);
                rows.Add(new CheatRow { Kind = RowKind.ScopedDrop, Group = d.Group, Desc = d.Desc });
                continue;
            }
            if (d.Kind.Equals("crate", StringComparison.OrdinalIgnoreCase))
            {
                if (injector == null || !injector.CanHookAlwaysCrate()) continue;
                EmitGroup(rows, ref lastGroup, d.Group);
                rows.Add(new CheatRow { Kind = RowKind.Crate, Group = d.Group, Desc = d.Desc });
                continue;
            }
            if (d.Kind.Equals("dropmult", StringComparison.OrdinalIgnoreCase))
            {
                if (injector == null || !injector.CanHookDropMultiplier()) continue;
                EmitGroup(rows, ref lastGroup, d.Group);
                rows.Add(new CheatRow { Kind = RowKind.DropMult, Group = d.Group, Desc = d.Desc, InjectValue = d.Value ?? "5" });
                continue;
            }
            if (d.Kind.Equals("patchset", StringComparison.OrdinalIgnoreCase))
            {
                // d.Method holds "SetKey:ret;SetKey:ret" (ret = zero|true). Include if ANY set is present.
                if (injector == null || d.Method == null) continue;
                var specs = d.Method.Split(';', StringSplitOptions.RemoveEmptyEntries);
                bool any = specs.Any(s => injector.CanPatchSetSource(s.Split(':')[0]));
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

        // ---- traveling merchant action ----
        EmitGroup(rows, ref lastGroup, "🛒 Traveling Merchant");
        rows.Add(new CheatRow { Kind = RowKind.Action, Group = "🛒 Traveling Merchant", Desc = "Summon Traveling Merchant — make him arrive (click)" });
        rows.Add(new CheatRow { Kind = RowKind.Action, Group = "🛒 Traveling Merchant", Desc = "Re-roll Traveling Merchant stock (click)" });

        // ---- buff toggles, grouped by function (mirrors CT's potion categories) ----
        // Effects like speed/defense/vision/mining are delivered as buffs because the
        // game recomputes the raw Player fields every frame (external freezes can't hold
        // them); the in-game buff IS the reliable mechanism and the game applies it.
        // Stable category order; emoji prefixes for the CE-style header look.
        var catOrder = new (string key, string title)[]
        {
            ("Removals and Tools",   "🧹 Buffs — Removals & Tools (tick On to keep)"),
            ("Damage and Combat",    "⚔️ Buffs — Damage & Combat"),
            ("Defense",              "🛡️ Buffs — Defense"),
            ("Regen and Healing",    "❤️ Buffs — Regen & Healing"),
            ("Mobility",             "🏃 Buffs — Mobility"),
            ("Vision and Utility",   "👁️ Buffs — Vision & Utility"),
            ("Mining and Building",  "⛏️ Buffs — Mining & Building"),
            ("Fishing",              "🎣 Buffs — Fishing"),
            ("Spawn Rate",           "🐲 Buffs — Spawn Rate"),
            ("Well Fed",             "🍖 Buffs — Well Fed"),
            ("Summoning",            "👾 Buffs — Summoning"),
            ("Misc",                 "🔮 Buffs — Misc"),
        };
        foreach (var (key, title) in catOrder)
        {
            var inCat = buffs.Where(b => b.Mode != "maxstack" && b.Category == key).ToList();
            if (inCat.Count == 0) continue;
            EmitGroup(rows, ref lastGroup, title);
            foreach (var b in inCat)
                rows.Add(new CheatRow { Kind = RowKind.Buff, Group = title, Desc = b.Name, Buff = b });
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
