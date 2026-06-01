using System.Text.Json;
using System.Text.Json.Serialization;

namespace TerrariaTrainer.Core;

/// <summary>A single editable value, mirrored from Data/values.json (generated from the CT).</summary>
public sealed class ValueEntry
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("desc")] public string Desc { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("group")] public string Group { get; set; } = "";
    [JsonPropertyName("symbol")] public string Symbol { get; set; } = "";
    [JsonPropertyName("baseConst")] public long BaseConst { get; set; }
    [JsonPropertyName("derefBase")] public bool? DerefBase { get; set; }
    [JsonPropertyName("offsets")] public List<long> Offsets { get; set; } = new();
    [JsonPropertyName("supported")] public bool Supported { get; set; }
    [JsonPropertyName("length")] public int Length { get; set; }

    public override string ToString() => Desc;

    public static List<ValueEntry> LoadAll()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Data", "values.json");
        if (!File.Exists(path))
            throw new FileNotFoundException($"values.json not found at {path}");
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<List<ValueEntry>>(json) ?? new();
    }
}
