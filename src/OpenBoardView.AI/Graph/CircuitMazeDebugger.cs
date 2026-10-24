using OpenBoardView.AI.Knowledge;
using OpenBoardView.AI.Models;

namespace OpenBoardView.AI.Graph;

public record MazeStepDto(
    int StepNumber,
    string StageTitle,
    string ComponentId,
    string Category,
    string Quadrant,
    string Side,
    int X,
    int Y,
    string InputNet,
    string OutputNet,
    double ExpectedVoltage,
    string PinToMeasure,
    string HowItWorks,
    string FailDiagnosis,
    string Status, // "PASSED", "ACTIVE_STEP", "BLOCKED_FAULT", "PENDING"
    string DataSource = DataSources.Curated, // "curated" (escrito por experto) | "inferred" (heurística)
    bool ComponentFound = true);             // false si el componente no existe en el .brd cargado

public record MazeTraceResultDto(
    string TraceId,
    string Title,
    string SymptomDescription,
    int CurrentStepNumber,
    int? BlockedAtStepNumber,
    string OverallDiagnosis,
    List<MazeStepDto> Steps,
    List<string>? Warnings = null);

public class MazeTraceRequestDto
{
    public string QueryOrSymptom { get; set; } = "no carga";
    public int? ActiveStepNumber { get; set; }
    public int? BlockedAtStepNumber { get; set; }
}

/// <summary>Problema detectado al validar un flujo curado contra la placa cargada.</summary>
public record KnowledgeIssue(string FlowId, int StepNumber, string Kind, string Detail);

/// <summary>
/// Fase 6: Circuit Step Debugger ("Modo Laberinto Paso a Paso").
/// Modela el viaje secuencial de la corriente/señales a través de puertas lógicas, fusibles, reguladores y MOSFETs
/// como un Debugger interactivo (Step Over / Play / Breakpoint) sin sustituir la simulación DC ni la cámara térmica.
/// Los flujos curados provienen de archivos JSON (<see cref="FlowKnowledgeBase"/>) y solo se aplican a las placas
/// que declaran; para cualquier otra placa se calcula una ruta topológica sobre el grafo BRD.
/// </summary>
public class CircuitMazeDebugger
{
    private readonly CircuitGraphBuilder _graph;
    private readonly FlowKnowledgeBase _knowledge;

    private record RawStep(
        string CompId, string StageTitle, string InputNet, string OutputNet, double ExpectedV,
        string PinHint, string HowItWorks, string FailDiag, string DataSource);

    public CircuitMazeDebugger(CircuitGraphBuilder graph, FlowKnowledgeBase knowledge)
    {
        _graph = graph ?? throw new ArgumentNullException(nameof(graph));
        _knowledge = knowledge ?? throw new ArgumentNullException(nameof(knowledge));
    }

    public FlowKnowledgeBase Knowledge => _knowledge;

    public MazeTraceResultDto BuildTrace(MazeTraceRequestDto request)
    {
        string rawQuery = request.QueryOrSymptom ?? "no carga";
        string q = rawQuery.Trim().ToLowerInvariant();
        var warnings = new List<string>();

        List<RawStep> rawSteps;
        string title;
        string symptomDesc;

        var flow = _knowledge.FindFlow(q);
        if (flow != null)
        {
            title = flow.Title;
            symptomDesc = flow.Symptom;
            rawSteps = flow.Steps
                .Select(s => new RawStep(s.Component, s.Stage, s.InputNet, s.OutputNet, s.ExpectedVoltage,
                    s.PinToMeasure, s.HowItWorks, s.FailDiagnosis,
                    string.IsNullOrWhiteSpace(s.VoltageSource) ? DataSources.Curated : s.VoltageSource))
                .ToList();
        }
        else
        {
            // Rastreador dinámico sobre el grafo para cualquier red o componente personalizado
            title = $"Laberinto Dinámico de Señal/Potencia: {rawQuery.ToUpperInvariant()}";
            symptomDesc = $"Ruta topológica calculada automáticamente en el grafo BRD para '{rawQuery}'.";
            rawSteps = BuildDynamicGraphSequence(rawQuery);
            warnings.Add(_knowledge.Applicable.Count == 0
                ? "No hay un flujo curado para esta placa: la ruta se calculó solo con la topología del .brd y los voltajes esperados son una estimación a partir del nombre de la red. Verifícalos contra el esquemático."
                : "La consulta no coincide con ningún flujo curado: la ruta se calculó solo con la topología del .brd y los voltajes esperados son una estimación a partir del nombre de la red. Verifícalos contra el esquemático.");
        }

        int blockedStep = request.BlockedAtStepNumber ?? 0;
        int activeStep = request.ActiveStepNumber ?? (blockedStep > 0 ? blockedStep : 1);

        var steps = new List<MazeStepDto>();
        int stepIdx = 1;

        foreach (var item in rawSteps)
        {
            var exact = _graph.GetComponentDetails(item.CompId);
            bool found = exact != null;
            var comp = exact ?? FallbackComponentSearch(item.InputNet, item.OutputNet);

            if (!found)
            {
                warnings.Add(comp != null
                    ? $"Paso {stepIdx}: el componente {item.CompId} no existe en la placa cargada; se muestra la posición aproximada de {comp.ComponentId} en la red '{item.OutputNet}'."
                    : $"Paso {stepIdx}: el componente {item.CompId} no existe en la placa cargada y no se pudo ubicar por red; su posición en pantalla es solo orientativa.");
            }

            int x = comp?.X ?? (1200 + stepIdx * 450);
            int y = comp?.Y ?? 2200;
            string quad = comp?.Quadrant ?? "Q3";
            string side = comp?.Side ?? "Bottom";
            string cat = exact?.Category ?? comp?.Category ?? "Component";

            string status;
            if (blockedStep > 0)
            {
                if (stepIdx < blockedStep) status = "PASSED";
                else if (stepIdx == blockedStep) status = "BLOCKED_FAULT";
                else status = "PENDING";
            }
            else
            {
                if (stepIdx < activeStep) status = "PASSED";
                else if (stepIdx == activeStep) status = "ACTIVE_STEP";
                else status = "PENDING";
            }

            steps.Add(new MazeStepDto(
                StepNumber: stepIdx,
                StageTitle: item.StageTitle,
                ComponentId: item.CompId,
                Category: cat,
                Quadrant: quad,
                Side: side,
                X: x,
                Y: y,
                InputNet: item.InputNet,
                OutputNet: item.OutputNet,
                ExpectedVoltage: item.ExpectedV,
                PinToMeasure: item.PinHint,
                HowItWorks: item.HowItWorks,
                FailDiagnosis: item.FailDiag,
                Status: status,
                DataSource: item.DataSource,
                ComponentFound: found
            ));

            stepIdx++;
        }

        if (steps.Count == 0)
        {
            return new MazeTraceResultDto(
                TraceId: Guid.NewGuid().ToString("N")[..8],
                Title: title,
                SymptomDescription: symptomDesc,
                CurrentStepNumber: 1,
                BlockedAtStepNumber: null,
                OverallDiagnosis: $"No se pudo construir una ruta para '{rawQuery}': no hay un flujo curado ni se encontraron redes o componentes coincidentes en la placa cargada.",
                Steps: steps,
                Warnings: warnings);
        }

        activeStep = Math.Clamp(activeStep, 1, Math.Max(1, steps.Count));
        var currentStepObj = steps.FirstOrDefault(s => s.StepNumber == (blockedStep > 0 ? blockedStep : activeStep)) ?? steps.First();

        string overallDiag = blockedStep > 0
            ? $"🛑 MURO EN EL LABERINTO (Paso {blockedStep} - {currentStepObj.ComponentId}): La corriente llegó con éxito hasta el Paso {Math.Max(1, blockedStep - 1)}, pero se interrumpe en {currentStepObj.ComponentId} ({currentStepObj.OutputNet}). Causa probable: {currentStepObj.FailDiagnosis}"
            : $"🧭 DEBUGGER EN PASO {activeStep}/{steps.Count} ({currentStepObj.ComponentId} - {currentStepObj.Quadrant}): Mide {currentStepObj.ExpectedVoltage:F2}V en {currentStepObj.PinToMeasure} ({currentStepObj.OutputNet}). Si está presente, avanza al siguiente paso; si marca 0V, presiona 'Marcar Falla Aquí'.";

        return new MazeTraceResultDto(
            TraceId: Guid.NewGuid().ToString("N")[..8],
            Title: title,
            SymptomDescription: symptomDesc,
            CurrentStepNumber: activeStep,
            BlockedAtStepNumber: blockedStep > 0 ? blockedStep : null,
            OverallDiagnosis: overallDiag,
            Steps: steps,
            Warnings: warnings.Count > 0 ? warnings : null
        );
    }

    /// <summary>
    /// Comprueba que cada componente y red citados por los flujos curados aplicables existan en la placa cargada.
    /// Un flujo curado que cita algo inexistente es un error de datos que no debe llegar al técnico.
    /// </summary>
    public List<KnowledgeIssue> ValidateKnowledge()
    {
        var issues = new List<KnowledgeIssue>();
        var netNames = new HashSet<string>(_graph.Topology.Nets.Select(n => n.Name), StringComparer.OrdinalIgnoreCase);

        foreach (var knowledge in _knowledge.Applicable)
        {
            foreach (var flow in knowledge.Flows)
            {
                int n = 1;
                foreach (var step in flow.Steps)
                {
                    var comp = _graph.GetComponentDetails(step.Component);
                    if (comp == null)
                        issues.Add(new KnowledgeIssue(flow.Id, n, "MissingComponent", step.Component));
                    if (!netNames.Contains(step.InputNet))
                        issues.Add(new KnowledgeIssue(flow.Id, n, "MissingNet", step.InputNet));
                    if (!netNames.Contains(step.OutputNet))
                        issues.Add(new KnowledgeIssue(flow.Id, n, "MissingNet", step.OutputNet));

                    // El componente debe estar físicamente conectado a la red de entrada o a la de salida del paso.
                    if (comp != null &&
                        netNames.Contains(step.InputNet) && netNames.Contains(step.OutputNet) &&
                        !comp.ConnectedNets.Contains(step.InputNet, StringComparer.OrdinalIgnoreCase) &&
                        !comp.ConnectedNets.Contains(step.OutputNet, StringComparer.OrdinalIgnoreCase))
                    {
                        issues.Add(new KnowledgeIssue(flow.Id, n, "ComponentNotOnNets",
                            $"{step.Component} no toca '{step.InputNet}' ni '{step.OutputNet}' (sus redes: {string.Join(", ", comp.ConnectedNets)})"));
                    }
                    n++;
                }
            }
        }

        return issues;
    }

    private ComponentNodeSummary? FallbackComponentSearch(string inNet, string outNet)
    {
        var onOut = _graph.GetComponentsOnNet(outNet).FirstOrDefault();
        if (onOut != null) return onOut;
        return _graph.GetComponentsOnNet(inNet).FirstOrDefault();
    }

    private List<RawStep> BuildDynamicGraphSequence(string query)
    {
        var list = new List<RawStep>();
        var matchedNets = _graph.SearchNetNames(query, 5);
        string startNet = matchedNets.FirstOrDefault() ?? "PPBUS_G3H";
        double nomV = CircuitSimulator.InferNominalVoltage(startNet);

        var hops = _graph.GetUpstreamPath(null, startNet, maxDepth: 3).Take(6).ToList();
        if (hops.Count == 0)
        {
            var comps = _graph.GetComponentsOnNet(startNet).Take(5).ToList();
            int idx = 1;
            foreach (var c in comps)
            {
                list.Add(new RawStep(c.ComponentId, $"Nodo {idx} en {startNet}", startNet, startNet, nomV, $"Pines de {c.ComponentId}",
                    $"Componente {c.Category} conectado a {startNet} en cuadrante {c.Quadrant}.",
                    $"Verificar continuidad e impedancia en {c.ComponentId}.",
                    DataSources.Inferred));
                idx++;
            }
            return list;
        }

        int step = 1;
        foreach (var h in hops)
        {
            double v = CircuitSimulator.InferNominalVoltage(h.ToNet);
            list.Add(new RawStep(h.ThroughComponentId, $"{step}. Puente {h.ComponentCategory} ({h.ThroughComponentId})",
                h.FromNet, h.ToNet, v, $"Medir en {h.ThroughComponentId}",
                $"Conecta la red {h.FromNet} hacia {h.ToNet} en el cuadrante {h.Quadrant}.",
                $"Si hay tensión en {h.FromNet} pero no en {h.ToNet}, {h.ThroughComponentId} está abierto o {h.ToNet} está en corto.",
                DataSources.Inferred));
            step++;
        }

        return list;
    }
}
