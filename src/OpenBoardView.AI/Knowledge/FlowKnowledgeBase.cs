using System.Text.Json;

namespace OpenBoardView.AI.Knowledge;

/// <summary>Origen de un dato mostrado al técnico. Permite distinguir hechos de suposiciones.</summary>
public static class DataSources
{
    /// <summary>Dato escrito a mano por un experto a partir del esquemático (no verificado automáticamente).</summary>
    public const string Curated = "curated";

    /// <summary>Dato deducido por heurística (p. ej. voltaje inferido del nombre de la red).</summary>
    public const string Inferred = "inferred";
}

public record FlowStepDef(
    string Component,
    string Stage,
    string InputNet,
    string OutputNet,
    double ExpectedVoltage,
    string VoltageSource,
    string PinToMeasure,
    string HowItWorks,
    string FailDiagnosis);

public record FlowDef(
    string Id,
    string Title,
    string Symptom,
    List<string> Keywords,
    List<FlowStepDef> Steps);

public record BoardKnowledge(
    string Family,
    string? Description,
    List<string> BoardMatch,
    List<FlowDef> Flows);

/// <summary>
/// Base de conocimiento de flujos de diagnóstico, cargada desde archivos JSON.
/// Cada archivo declara a qué placas aplica (<see cref="BoardKnowledge.BoardMatch"/>); los flujos de una
/// familia distinta NUNCA se aplican a la placa cargada, en vez de mostrar componentes que no existen.
/// </summary>
public class FlowKnowledgeBase
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public IReadOnlyList<BoardKnowledge> Loaded { get; }
    public IReadOnlyList<BoardKnowledge> Applicable { get; }
    public IReadOnlyList<string> LoadErrors { get; }

    public FlowKnowledgeBase(IEnumerable<BoardKnowledge> loaded, string boardFileName, IEnumerable<string>? loadErrors = null)
    {
        Loaded = loaded.ToList();
        LoadErrors = loadErrors?.ToList() ?? new List<string>();
        Applicable = Loaded.Where(k => Matches(k, boardFileName)).ToList();
    }

    public static FlowKnowledgeBase Empty(string boardFileName) =>
        new(Array.Empty<BoardKnowledge>(), boardFileName);

    public static FlowKnowledgeBase LoadFromDirectories(IEnumerable<string> directories, string boardFileName)
    {
        var loaded = new List<BoardKnowledge>();
        var errors = new List<string>();

        foreach (var dir in directories.Where(Directory.Exists).Distinct())
        {
            foreach (var file in Directory.GetFiles(dir, "*.json", SearchOption.TopDirectoryOnly).OrderBy(f => f, StringComparer.Ordinal))
            {
                try
                {
                    var knowledge = JsonSerializer.Deserialize<BoardKnowledge>(File.ReadAllText(file), JsonOptions);
                    if (knowledge == null || knowledge.Flows == null || knowledge.BoardMatch == null)
                    {
                        errors.Add($"{Path.GetFileName(file)}: formato inválido (faltan 'boardMatch' o 'flows').");
                        continue;
                    }
                    loaded.Add(knowledge);
                }
                catch (Exception ex)
                {
                    errors.Add($"{Path.GetFileName(file)}: {ex.Message}");
                }
            }
        }

        return new FlowKnowledgeBase(loaded, boardFileName, errors);
    }

    private static bool Matches(BoardKnowledge k, string boardFileName) =>
        k.BoardMatch.Any(m => !string.IsNullOrWhiteSpace(m) &&
                              boardFileName.Contains(m, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Devuelve el primer flujo aplicable a la placa cuya lista de palabras clave aparezca en la consulta.
    /// El orden en el archivo JSON define la prioridad.
    /// </summary>
    public FlowDef? FindFlow(string query)
    {
        string q = (query ?? string.Empty).Trim().ToLowerInvariant();
        foreach (var knowledge in Applicable)
        {
            foreach (var flow in knowledge.Flows)
            {
                if (flow.Keywords.Any(k => !string.IsNullOrWhiteSpace(k) && q.Contains(k.ToLowerInvariant())))
                    return flow;
            }
        }
        return null;
    }
}
