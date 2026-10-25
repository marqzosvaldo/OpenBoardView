using System.Text.Json;

namespace OpenBoardView.AI.Knowledge;

/// <summary>Origen de un dato mostrado al técnico. Permite distinguir hechos de suposiciones.</summary>
public static class DataSources
{
    /// <summary>Dato extraído directamente de las propiedades CAD del esquemático PDF (p. ej. VOLTAGE=12.6V).</summary>
    public const string SchematicPdf = "schematic_pdf";

    /// <summary>Dato escrito a mano por un experto a partir del esquemático (no verificado automáticamente).</summary>
    public const string Curated = "curated";

    /// <summary>Dato deducido por heurística (p. ej. voltaje inferido del nombre de la red).</summary>
    public const string Inferred = "inferred";

    /// <summary>Dato no disponible en PDF ni JSON y sin prefijo de potencia explícito (no se inventa un valor).</summary>
    public const string Unknown = "unknown";

    /// <summary>Tensión heredada de otra red unida por un puente de baja impedancia (0 Ω, shunt, fusible, bobina) según el .brd + PDF.</summary>
    public const string Bridged = "bridged";
}

/// <summary>Tipo de medición que pide un paso del árbol de diagnóstico.</summary>
public static class MeasurementModes
{
    public const string Voltage = "voltage";
    public const string Resistance = "resistance";
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
    string FailDiagnosis,
    string? MeasurementMode = null,   // "voltage" (por defecto) | "resistance"
    string? ExpectedReading = null,   // texto libre cuando la lectura no es un voltaje (ej. "≈0–2 Ω = corto")
    string? Branch = null);           // rama del árbol de decisión (ej. "A · Corto a GND")

public record FlowDef(
    string Id,
    string Title,
    string Symptom,
    List<string> Keywords,
    List<FlowStepDef> Steps,
    string? TargetNet = null);

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

    private static readonly HashSet<string> GenericFlowWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "carga", "bateria", "batería", "enciende", "pantalla", "imagen", "brillo", "verde", "corto"
    };

    /// <summary>
    /// Devuelve el flujo aplicable con mayor especificidad técnica en la consulta (priorizando coincidencia de TargetNet y tokens de red/componente sobre palabras genéricas).
    /// Así "PP5V_S3 en 0V ... no enciende" o "PPVCORE_S0_CPU ausente ... no enciende" elige el flujo específico del riel y nunca el genérico de "no enciende".
    /// </summary>
    public FlowDef? FindFlow(string query)
    {
        string q = (query ?? string.Empty).Trim().ToLowerInvariant();
        if (q.Length == 0) return null;

        FlowDef? best = null;
        int bestScore = 0, bestLen = 0;
        foreach (var knowledge in Applicable)
        {
            foreach (var flow in knowledge.Flows)
            {
                var hits = flow.Keywords
                    .Where(k => !string.IsNullOrWhiteSpace(k) && q.Contains(k.ToLowerInvariant()))
                    .ToList();

                bool targetNetHit = !string.IsNullOrWhiteSpace(flow.TargetNet) &&
                                    q.Contains(flow.TargetNet.ToLowerInvariant());
                bool idHit = !string.IsNullOrWhiteSpace(flow.Id) &&
                             q.Equals(flow.Id.ToLowerInvariant(), StringComparison.OrdinalIgnoreCase);

                if (hits.Count == 0 && !targetNetHit && !idHit) continue;

                int score = hits.Sum(k => GenericFlowWords.Contains(k) ? 1 : (k.Contains('_') || k.Any(char.IsDigit) ? 4 : 2));
                if (targetNetHit) score += 10;
                if (idHit) score += 15;

                int longest = hits.Count > 0 ? hits.Max(k => k.Length) : (flow.TargetNet?.Length ?? 0);
                if (score > bestScore || (score == bestScore && longest > bestLen))
                {
                    best = flow;
                    bestScore = score;
                    bestLen = longest;
                }
            }
        }
        return best;
    }

    private static bool IsVoltageStep(FlowStepDef step) =>
        string.IsNullOrWhiteSpace(step.MeasurementMode) ||
        step.MeasurementMode.Equals(MeasurementModes.Voltage, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Busca si algún paso curado aplicable a la placa define explícitamente el voltaje esperado de esta red.
    /// Los pasos de medición de resistencia se ignoran (su ExpectedVoltage no es una tensión).
    /// </summary>
    public bool TryGetCuratedVoltage(string netName, out double voltage)
    {
        voltage = 0;
        if (string.IsNullOrWhiteSpace(netName)) return false;
        string target = netName.Trim();

        foreach (var kb in Applicable)
        {
            foreach (var flow in kb.Flows)
            {
                foreach (var step in flow.Steps)
                {
                    if (IsVoltageStep(step) &&
                        string.Equals(step.VoltageSource, DataSources.Curated, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(step.OutputNet, target, StringComparison.OrdinalIgnoreCase))
                    {
                        voltage = step.ExpectedVoltage;
                        return true;
                    }
                }
            }

            foreach (var flow in kb.Flows)
            {
                foreach (var step in flow.Steps)
                {
                    if (IsVoltageStep(step) &&
                        string.Equals(step.VoltageSource, DataSources.Curated, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(step.InputNet, target, StringComparison.OrdinalIgnoreCase) &&
                        step.Component.Length > 0 &&
                        char.ToUpperInvariant(step.Component[0]) is 'F' or 'R' or 'L' or 'Q')
                    {
                        voltage = step.ExpectedVoltage;
                        return true;
                    }
                }
            }
        }
        return false;
    }
}
