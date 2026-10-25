using System.Text.Json;

namespace OpenBoardView.AI.Parsers;

public class SchematicIndexCacheDto
{
    public string PdfFileName { get; set; } = string.Empty;
    public int TotalPages { get; set; }
    public Dictionary<string, List<SnippetEntryDto>> ComponentContext { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<SnippetEntryDto>> NetContext { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<int, string> PageTexts { get; set; } = new();
    public Dictionary<string, VoltageEntryDto> NetVoltages { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> NetAliases { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> SwitchNodeNets { get; set; } = new();
    public Dictionary<string, string> ComponentValues { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> NoStuffComponents { get; set; } = new();

    public static SchematicIndexCacheDto FromIndex(SchematicIndex index)
    {
        return new SchematicIndexCacheDto
        {
            PdfFileName = index.PdfFileName,
            TotalPages = index.TotalPages,
            ComponentContext = index.ComponentContext.ToDictionary(
                kv => kv.Key,
                kv => kv.Value.Select(v => new SnippetEntryDto(v.PageNumber, v.Snippet)).ToList(),
                StringComparer.OrdinalIgnoreCase),
            NetContext = index.NetContext.ToDictionary(
                kv => kv.Key,
                kv => kv.Value.Select(v => new SnippetEntryDto(v.PageNumber, v.Snippet)).ToList(),
                StringComparer.OrdinalIgnoreCase),
            PageTexts = new Dictionary<int, string>(index.PageTexts),
            NetVoltages = index.NetVoltages.ToDictionary(
                kv => kv.Key,
                kv => new VoltageEntryDto(kv.Value.Voltage, kv.Value.PageNumber),
                StringComparer.OrdinalIgnoreCase),
            NetAliases = new Dictionary<string, string>(index.NetAliases, StringComparer.OrdinalIgnoreCase),
            SwitchNodeNets = index.SwitchNodeNets.ToList(),
            ComponentValues = new Dictionary<string, string>(index.ComponentValues, StringComparer.OrdinalIgnoreCase),
            NoStuffComponents = index.NoStuffComponents.ToList()
        };
    }

    public SchematicIndex ToIndex()
    {
        var idx = new SchematicIndex
        {
            PdfFileName = PdfFileName,
            TotalPages = TotalPages,
            PageTexts = new Dictionary<int, string>(PageTexts),
            NetAliases = new Dictionary<string, string>(NetAliases, StringComparer.OrdinalIgnoreCase),
            ComponentValues = new Dictionary<string, string>(ComponentValues, StringComparer.OrdinalIgnoreCase),
            SwitchNodeNets = new HashSet<string>(SwitchNodeNets, StringComparer.OrdinalIgnoreCase),
            NoStuffComponents = new HashSet<string>(NoStuffComponents, StringComparer.OrdinalIgnoreCase)
        };

        foreach (var kv in ComponentContext)
        {
            idx.ComponentContext[kv.Key] = kv.Value.Select(s => (s.PageNumber, s.Snippet)).ToList();
        }

        foreach (var kv in NetContext)
        {
            idx.NetContext[kv.Key] = kv.Value.Select(s => (s.PageNumber, s.Snippet)).ToList();
        }

        foreach (var kv in NetVoltages)
        {
            idx.NetVoltages[kv.Key] = (kv.Value.Voltage, kv.Value.PageNumber);
        }

        return idx;
    }
}

public record SnippetEntryDto(int PageNumber, string Snippet);
public record VoltageEntryDto(double Voltage, int PageNumber);

public static class SchematicIndexCacheSerializer
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static string Serialize(SchematicIndex index)
    {
        var dto = SchematicIndexCacheDto.FromIndex(index);
        return JsonSerializer.Serialize(dto, JsonOpts);
    }

    public static SchematicIndex? Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        var dto = JsonSerializer.Deserialize<SchematicIndexCacheDto>(json, JsonOpts);
        return dto?.ToIndex();
    }
}
