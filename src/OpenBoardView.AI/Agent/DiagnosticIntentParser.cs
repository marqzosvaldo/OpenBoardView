using System.Text.Json.Serialization;
using OpenBoardView.AI.Graph;
using OpenBoardView.AI.Knowledge;

namespace OpenBoardView.AI.Agent;

/// <summary>
/// Módulo 1: Estructura extraída por el Parser de Intención y Middleware antes de invocar el motor de rutas (.brd) y la UI.
/// </summary>
public record DiagnosticIntentDto(
    [property: JsonPropertyName("symptom_id")] string SymptomId,
    [property: JsonPropertyName("primary_net")] string PrimaryNet,
    [property: JsonPropertyName("secondary_nets")] List<string> SecondaryNets,
    [property: JsonPropertyName("primary_components")] List<string> PrimaryComponents,
    [property: JsonPropertyName("diagnostic_mode")] string DiagnosticMode);

public static class DiagnosticIntentParser
{
    public static DiagnosticIntentDto Parse(
        string? rawPrompt,
        CircuitGraphBuilder graph,
        FlowKnowledgeBase knowledge,
        CircuitMazeDebugger mazeDebugger)
    {
        string prompt = (rawPrompt ?? string.Empty).Trim();
        string lower = prompt.ToLowerInvariant();

        bool isShortInjection = lower.Contains("inyect") ||
                                (lower.Contains("simula") && lower.Contains("corto"));
        string defaultMode = isShortInjection ? "SHORT_INJECTION" : "DECISION_TREE";

        // 1. Coincidencia prioritaria con PPVCORE_S0_CPU (Alimentación VCORE del Procesador)
        if (lower.Contains("ppvcore_s0_cpu") ||
            lower.Contains("ppvcore") ||
            lower.Contains("vcore") ||
            lower.Contains("u7400") ||
            lower.Contains("isl95831") ||
            lower.Contains("l7510") ||
            lower.Contains("l7520") ||
            lower.Contains("cpu_vr_en") ||
            lower.Contains("cpuimvp_vr_on") ||
            (lower.Contains("all_sys_pwrgd") && (lower.Contains("cpu") || lower.Contains("r7402") || lower.Contains("q7950"))))
        {
            return new DiagnosticIntentDto(
                SymptomId: "CPU_VCORE_ABSENT",
                PrimaryNet: "PPVCORE_S0_CPU",
                SecondaryNets: new List<string> { "ALL_SYS_PWRGD", "CPU_VR_EN", "PP5V_S0" },
                PrimaryComponents: new List<string> { "L7510", "L7520", "U7400", "Q7950", "R7402" },
                DiagnosticMode: defaultMode);
        }

        // 2. Coincidencia con Árbol de Decisión PP5V_S3
        if (lower.Contains("pp5v_s3") ||
            lower.Contains("5v_s3") ||
            lower.Contains("p5vs3") ||
            lower.Contains("l7260") ||
            lower.Contains("c6213") ||
            lower.Contains("q7221") ||
            lower.Contains("q7911") ||
            lower.Contains("pm_slp_s4_l") ||
            lower.Contains("u7200"))
        {
            return new DiagnosticIntentDto(
                SymptomId: "PP5V_S3_DIAGNOSTIC",
                PrimaryNet: "PP5V_S3",
                SecondaryNets: new List<string> { "PP5V_S5", "P5VS3_ENTRIP", "P5VS3_EN_L", "PM_SLP_S4_L" },
                PrimaryComponents: new List<string> { "L7260", "C6213", "U7200", "Q7221", "Q7911", "U1800" },
                DiagnosticMode: defaultMode);
        }

        // 3. Coincidencia con flujo curado aplicable en la base de conocimiento
        var matchedFlow = knowledge.FindFlow(prompt);
        if (matchedFlow != null)
        {
            string primaryNet = !string.IsNullOrWhiteSpace(matchedFlow.TargetNet)
                ? matchedFlow.TargetNet
                : (matchedFlow.Steps.FirstOrDefault()?.OutputNet ?? "PPBUS_G3H");

            var secondaryNets = matchedFlow.Steps
                .SelectMany(s => new[] { s.InputNet, s.OutputNet })
                .Where(n => !string.IsNullOrWhiteSpace(n) && !n.Equals(primaryNet, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(5)
                .ToList();

            var primaryComponents = matchedFlow.Steps
                .Select(s => s.Component)
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            string symptomId = matchedFlow.Id switch
            {
                "charging-ppbus" => "PPBUS_G3H_CHARGING_DIAGNOSTIC",
                "pp5v-s3-zero-volts" => "PP5V_S3_DIAGNOSTIC",
                "ppvcore-s0-cpu" => "CPU_VCORE_ABSENT",
                "magsafe-green-light" => "MAGSAFE_3V42_DIAGNOSTIC",
                "backlight" => "BACKLIGHT_DIAGNOSTIC",
                _ => matchedFlow.Id.ToUpperInvariant().Replace('-', '_')
            };

            return new DiagnosticIntentDto(
                SymptomId: symptomId,
                PrimaryNet: primaryNet,
                SecondaryNets: secondaryNets,
                PrimaryComponents: primaryComponents,
                DiagnosticMode: defaultMode);
        }

        // 4. Extracción dinámica desde el grafo .brd
        string? extractedNet = mazeDebugger.ExtractTargetNetFromQuery(prompt);
        string resolvedPrimaryNet = extractedNet ?? "PPBUS_G3H";

        var hops = graph.GetUpstreamPath(null, resolvedPrimaryNet, maxDepth: 2);
        var dynSecondaryNets = hops
            .Select(h => h.ToNet)
            .Where(n => !n.Equals(resolvedPrimaryNet, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(4)
            .ToList();

        var dynComponents = hops
            .Select(h => h.ThroughComponentId)
            .Concat(graph.GetComponentsOnNet(resolvedPrimaryNet).Where(c => !c.IsNoStuff).Select(c => c.ComponentId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .ToList();

        return new DiagnosticIntentDto(
            SymptomId: $"DYNAMIC_{resolvedPrimaryNet}",
            PrimaryNet: resolvedPrimaryNet,
            SecondaryNets: dynSecondaryNets,
            PrimaryComponents: dynComponents,
            DiagnosticMode: defaultMode);
    }
}
