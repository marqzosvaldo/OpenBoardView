using System.Text.RegularExpressions;
using OpenBoardView.AI.Algorithms;
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
    string DataSource = DataSources.Curated, // "curated" | "schematic_pdf" | "bridged" | "inferred"
    bool ComponentFound = true,              // false si el componente no existe en el .brd cargado
    string MeasurementMode = MeasurementModes.Voltage,
    string? ExpectedReading = null,
    string? Branch = null,
    double? FailureProbability = null,
    int? HistoricalFailureCount = null,
    int OmittedParallelCount = 0,
    string? Notes = null,
    List<string>? ParallelComponentIds = null);

public record MazeTraceResultDto(
    string TraceId,
    string Title,
    string SymptomDescription,
    int CurrentStepNumber,
    int? BlockedAtStepNumber,
    string OverallDiagnosis,
    List<MazeStepDto> Steps,
    List<string>? Warnings = null,
    string TargetNet = "PPBUS_G3H",
    List<ComponentFailureStat>? HistoricalFailureStats = null,
    List<MazeStepDto>? RawSteps = null,
    List<MazeStepDto>? FilteredSteps = null,
    bool IsFilterEnabled = true);

public class MazeTraceRequestDto
{
    public string QueryOrSymptom { get; set; } = "no carga";
    public int? ActiveStepNumber { get; set; }
    public int? BlockedAtStepNumber { get; set; }
    public bool FilterParallelCapacitors { get; set; } = true;
}

/// <summary>Problema detectado al validar un flujo curado contra la placa cargada.</summary>
public record KnowledgeIssue(string FlowId, int StepNumber, string Kind, string Detail);

/// <summary>
/// Fase 6: Circuit Step Debugger ("Modo Laberinto Paso a Paso").
/// Modela el viaje secuencial de la corriente/señales a través de puertas lógicas, fusibles, reguladores y MOSFETs
/// como un Debugger interactivo (Step Over / Play / Breakpoint) sin sustituir la simulación DC ni la cámara térmica.
/// Los flujos curados provienen de archivos JSON (<see cref="FlowKnowledgeBase"/>) y solo se aplican a las placas
/// que declaran; para cualquier otra consulta se extrae la red objetivo del prompt, se filtra por dominio funcional
/// y se calculan los voltajes respetando puentes de baja impedancia.
/// </summary>
public class CircuitMazeDebugger
{
    private static readonly Regex TokenRegex = new(
        @"\b([A-Za-z][A-Za-z0-9_<>]{2,})\b",
        RegexOptions.Compiled);

    private static readonly string[] PeripheralDomainMarkers =
    {
        "T29", "TBT", "FW", "FIREWIRE", "LCDBKLT", "BKL", "ODD", "ALS", "CAMERA", "KBDLED", "SPKR", "AUD_", "AUDIO"
    };

    private static readonly string[] SenseFeedbackMarkers =
    {
        "_ISNS1_", "_ISNS2_", "_ISNS3_", "_ISNS_", "ISNS_", "CHGR_CSI_", "CHGR_CSO_", "_CSIP", "_CSIN", "_CSOP", "_CSON"
    };

    private static readonly HashSet<string> GenericSymptomKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "carga", "bateria", "batería", "enciende", "magsafe", "verde", "backlight", "pantalla", "brillo", "corto"
    };

    private readonly CircuitGraphBuilder _graph;
    private readonly FlowKnowledgeBase _knowledge;
    private readonly NetVoltageResolver _voltageResolver;
    private readonly StepperParallelFilterV3 _parallelFilter = new();

    private record RawStep(
        string CompId,
        string StageTitle,
        string InputNet,
        string OutputNet,
        double ExpectedV,
        string PinHint,
        string HowItWorks,
        string FailDiag,
        string DataSource,
        string MeasurementMode = MeasurementModes.Voltage,
        string? ExpectedReading = null,
        string? Branch = null);

    public CircuitMazeDebugger(CircuitGraphBuilder graph, FlowKnowledgeBase knowledge)
    {
        _graph = graph ?? throw new ArgumentNullException(nameof(graph));
        _knowledge = knowledge ?? throw new ArgumentNullException(nameof(knowledge));
        _voltageResolver = new NetVoltageResolver(_graph, _knowledge);
    }

    public FlowKnowledgeBase Knowledge => _knowledge;

    public MazeTraceResultDto BuildTrace(
        MazeTraceRequestDto request, 
        List<ComponentFailureStat>? failureStats = null,
        DiagnosticPresetDto? preset = null)
    {
        string rawQuery = request.QueryOrSymptom ?? "no carga";
        string q = rawQuery.Trim().ToLowerInvariant();
        var warnings = new List<string>();

        List<RawStep> rawSteps;
        string title;
        string symptomDesc;
        string resolvedTargetNet;

        var flow = _knowledge.FindFlow(q);
        if (flow != null)
        {
            title = flow.Title;
            symptomDesc = flow.Symptom;
            resolvedTargetNet = !string.IsNullOrWhiteSpace(flow.TargetNet)
                ? flow.TargetNet
                : (ExtractTargetNetFromQuery(rawQuery) ?? flow.Steps.FirstOrDefault()?.OutputNet ?? "PPBUS_G3H");
            rawSteps = flow.Steps
                .Select(s => new RawStep(
                    s.Component,
                    s.Stage,
                    s.InputNet,
                    s.OutputNet,
                    s.ExpectedVoltage,
                    s.PinToMeasure,
                    s.HowItWorks,
                    s.FailDiagnosis,
                    string.IsNullOrWhiteSpace(s.VoltageSource) ? DataSources.Curated : s.VoltageSource,
                    string.IsNullOrWhiteSpace(s.MeasurementMode) ? MeasurementModes.Voltage : s.MeasurementMode,
                    s.ExpectedReading,
                    s.Branch))
                .ToList();

            if (preset != null)
            {
                warnings.Add($"⚡ Seguridad en Frío: Corto a tierra si R < {preset.ColdShortThresholdReal} Ω. Riel normal si R > {preset.ColdNormalMinReal} Ω (NO inyectar voltaje si la impedancia es normal).");
            }
        }
        else if (preset != null && !string.IsNullOrWhiteSpace(preset.PresetJson))
        {
            title = $"Laberinto Curado: {preset.SymptomKey}";
            symptomDesc = preset.SymptomDescription;
            rawSteps = new List<RawStep>();
            string firstNet = "PP3V8_AON";

            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(preset.PresetJson);
                if (doc.RootElement.TryGetProperty("stepperNodes", out var nodesArr))
                {
                    foreach (var n in nodesArr.EnumerateArray())
                    {
                        string cId = n.GetProperty("componentId").GetString()!;
                        string pNum = n.GetProperty("pin").GetString()!;
                        string sName = n.GetProperty("signalName").GetString()!;
                        string expStr = n.GetProperty("expectedNominal").GetString()!;
                        int pPage = n.GetProperty("schematicPage").GetInt32();
                        string desc = n.GetProperty("description").GetString()!;
                        double v = 3.80;
                        var m = System.Text.RegularExpressions.Regex.Match(expStr, @"(\d+(\.\d+)?)");
                        if (m.Success && double.TryParse(m.Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double parsedVal)) v = parsedVal;

                        rawSteps.Add(new RawStep(
                            CompId: cId,
                            StageTitle: $"Paso {n.GetProperty("stepNumber").GetInt32()}: {sName} ({cId})",
                            InputNet: sName,
                            OutputNet: sName,
                            ExpectedV: v,
                            PinHint: $"Pin {pNum} de {cId} [Pág. Esquemático {pPage}]",
                            HowItWorks: desc,
                            FailDiag: $"Línea {sName} en 0.00V. {desc}",
                            DataSource: DataSources.Curated,
                            MeasurementMode: MeasurementModes.Voltage,
                            ExpectedReading: expStr,
                            Branch: null
                        ));
                    }
                    firstNet = rawSteps.FirstOrDefault()?.OutputNet ?? "PP3V8_AON";
                }
            }
            catch {}

            resolvedTargetNet = firstNet;
            warnings.Add($"⚡ Seguridad en Frío: Corto a tierra si R < {preset.ColdShortThresholdReal} Ω. Riel normal si R > {preset.ColdNormalMinReal} Ω (NO inyectar voltaje si la impedancia es normal).");
        }
        else
        {
            string? targetNet = ExtractTargetNetFromQuery(rawQuery);
            string displayTarget = targetNet ?? rawQuery.Trim().ToUpperInvariant();
            resolvedTargetNet = targetNet ?? "PPBUS_G3H";
            title = $"Laberinto Dinámico de Señal/Potencia: {displayTarget}";
            symptomDesc = targetNet != null
                ? $"Ruta topológica filtrada por bloque funcional en el grafo BRD para la red '{targetNet}'."
                : $"Ruta topológica calculada automáticamente en el grafo BRD para '{rawQuery}'.";
            rawSteps = targetNet != null ? BuildDynamicGraphSequence(targetNet) : new List<RawStep>();
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

            var stat = failureStats?.FirstOrDefault(s => string.Equals(s.ComponentId, comp?.ComponentId ?? item.CompId, StringComparison.OrdinalIgnoreCase));
            double? failProb = stat?.ProbabilityPercentage;
            int? failCount = stat?.FailureCount;
            string howItWorks = item.HowItWorks;
            if (stat != null && stat.ProbabilityPercentage > 0)
            {
                howItWorks = $"⚡ [Estadística Histórica: {stat.ProbabilityPercentage:F0}% prob. de falla ({stat.FailureCount} casos)] {howItWorks}";
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
                HowItWorks: howItWorks,
                FailDiagnosis: item.FailDiag,
                Status: status,
                DataSource: item.DataSource,
                ComponentFound: found,
                MeasurementMode: item.MeasurementMode,
                ExpectedReading: item.ExpectedReading,
                Branch: item.Branch,
                FailureProbability: failProb,
                HistoricalFailureCount: failCount
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
                Warnings: warnings,
                TargetNet: resolvedTargetNet,
                HistoricalFailureStats: failureStats,
                RawSteps: steps,
                FilteredSteps: steps,
                IsFilterEnabled: request.FilterParallelCapacitors);
        }

        var (filteredSteps, expandedSteps) = _parallelFilter.ProcessMazeSteps(steps, _graph);
        bool filterOn = request.FilterParallelCapacitors;
        var activeSteps = filterOn ? filteredSteps : expandedSteps;

        activeStep = Math.Clamp(activeStep, 1, Math.Max(1, activeSteps.Count));
        var currentStepObj = activeSteps.FirstOrDefault(s => s.StepNumber == (blockedStep > 0 ? blockedStep : activeStep)) ?? activeSteps.First();

        string measurementTarget = string.Equals(currentStepObj.MeasurementMode, MeasurementModes.Resistance, StringComparison.OrdinalIgnoreCase) &&
                                   !string.IsNullOrWhiteSpace(currentStepObj.ExpectedReading)
            ? $"resistencia a GND ({currentStepObj.ExpectedReading})"
            : $"{currentStepObj.ExpectedVoltage:F2}V";

        string overallDiag = blockedStep > 0
            ? $"🛑 MURO EN EL LABERINTO (Paso {blockedStep} - {currentStepObj.ComponentId}): La secuencia llegó hasta el Paso {Math.Max(1, blockedStep - 1)}, pero presenta anomalía en {currentStepObj.ComponentId} ({currentStepObj.OutputNet}). Diagnóstico: {currentStepObj.FailDiagnosis}"
            : $"🧭 DEBUGGER EN PASO {activeStep}/{activeSteps.Count} ({currentStepObj.ComponentId} - {currentStepObj.Quadrant}): Mide {measurementTarget} en {currentStepObj.PinToMeasure} ({currentStepObj.OutputNet}). Si es correcto, avanza al siguiente paso; si falla, presiona 'Marcar Falla Aquí'.";

        return new MazeTraceResultDto(
            TraceId: Guid.NewGuid().ToString("N")[..8],
            Title: title,
            SymptomDescription: symptomDesc,
            CurrentStepNumber: activeStep,
            BlockedAtStepNumber: blockedStep > 0 ? blockedStep : null,
            OverallDiagnosis: overallDiag,
            Steps: activeSteps,
            Warnings: warnings.Count > 0 ? warnings : null,
            TargetNet: resolvedTargetNet,
            HistoricalFailureStats: failureStats,
            RawSteps: expandedSteps,
            FilteredSteps: filteredSteps,
            IsFilterEnabled: filterOn
        );
    }

    /// <summary>
    /// Extrae el nombre de la red objetivo desde una consulta en lenguaje natural o identificador directo.
    /// Evita pasar una oración entera a SearchNetNames (lo que antes provocaba caer en PPBUS_G3H por defecto).
    /// </summary>
    public string? ExtractTargetNetFromQuery(string? rawQuery)
    {
        if (string.IsNullOrWhiteSpace(rawQuery))
            return "PPBUS_G3H";

        string trimmed = rawQuery.Trim();

        // 1. Si toda la cadena ya es una red exacta de la placa
        if (_graph.NetExists(trimmed))
            return _graph.GetNetExact(trimmed)!.Name;

        // 2. Extraer tokens candidatos de la oración
        var tokens = TokenRegex.Matches(trimmed)
            .Select(m => m.Groups[1].Value.TrimStart('='))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // 2a. Coincidencia exacta con una red del .brd (priorizando rieles de alimentación PP... sobre señales secundarias)
        var exactNets = tokens
            .Select(t => _graph.GetNetExact(t))
            .Where(n => n != null && !n.IsGround && !n.IsUnconnected)
            .Select(n => n!.Name)
            .ToList();

        if (exactNets.Count > 0)
        {
            return exactNets.FirstOrDefault(n => n.StartsWith("PP", StringComparison.OrdinalIgnoreCase))
                   ?? exactNets.First();
        }

        // 2b. Coincidencia por subcadena para tokens técnicos con formato de red (ej. 5V_S3, 3V42, PPBUS)
        foreach (var tok in tokens.Where(t => t.Contains('_') || t.StartsWith("PP", StringComparison.OrdinalIgnoreCase)))
        {
            var found = _graph.SearchNetNames(tok, 5);
            if (found.Count > 0)
                return found[0];
        }

        // 2c. Si el usuario mencionó un ID de componente válido (ej. Q7221, U7200, L7260), usar su red principal
        foreach (var tok in tokens)
        {
            var comp = _graph.GetComponentDetails(tok);
            if (comp != null && comp.ConnectedNets.Count > 0)
            {
                return comp.ConnectedNets.FirstOrDefault(n => n.StartsWith("PP", StringComparison.OrdinalIgnoreCase))
                       ?? comp.ConnectedNets.FirstOrDefault(n => !n.Contains("GND", StringComparison.OrdinalIgnoreCase))
                       ?? comp.ConnectedNets[0];
            }
        }

        // 3. Búsqueda directa si es una consulta corta de una sola palabra
        if (!trimmed.Contains(' '))
        {
            var direct = _graph.SearchNetNames(trimmed, 5);
            if (direct.Count > 0)
                return direct[0];
        }

        // 4. Si es un síntoma genérico ("no carga", "sin luz verde") en una placa sin flujo curado aplicable, iniciar en PPBUS_G3H
        if (tokens.Any(t => GenericSymptomKeywords.Contains(t)))
            return "PPBUS_G3H";

        return null;
    }

    /// <summary>
    /// Comprueba que cada componente y red citados por los flujos curados aplicables existan en la placa cargada.
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

    /// <summary>
    /// Calcula el voltaje esperado para un salto topológico:
    /// - Si el componente es un puente en serie (fusible, bobina, o resistor de paso/pull-up desde un riel de potencia),
    ///   el voltaje esperado a medir es el del riel fuente (fromNet) y NO el nombre de una sub-red periférica deshabilitada.
    /// </summary>
    public double ResolveDynamicHopVoltage(string toNet, string fromNet, string? throughComponentId = null)
    {
        var fromResolved = _voltageResolver.Resolve(fromNet);

        if (!string.IsNullOrWhiteSpace(throughComponentId))
        {
            var comp = _graph.GetComponentDetails(throughComponentId);
            if (comp != null && fromResolved.Voltage is > 0 &&
                (NetVoltageResolver.IsLowImpedanceBridge(comp) || comp.Category == "Resistor"))
            {
                return fromResolved.Voltage.Value;
            }
        }

        var toResolved = _voltageResolver.Resolve(toNet);
        if (toResolved.Voltage is > 0)
            return toResolved.Voltage.Value;

        if (fromResolved.Voltage is > 0)
            return fromResolved.Voltage.Value;

        return 0.0;
    }

    private List<RawStep> BuildDynamicGraphSequence(string startNet)
    {
        var list = new List<RawStep>();
        double nomV = ResolveDynamicHopVoltage(startNet, startNet);

        // Filtrar saltos por dominio funcional para no mezclar puertos periféricos (FireWire, Thunderbolt, Audio, LCD)
        // cuando se analiza un riel de potencia principal.
        var allHops = _graph.GetUpstreamPath(null, startNet, maxDepth: 3);
        var filteredHops = FilterHopsByFunctionalDomain(startNet, allHops).Take(6).ToList();

        if (filteredHops.Count == 0)
        {
            var comps = _graph.GetComponentsOnNet(startNet).Where(c => !c.IsNoStuff).Take(5).ToList();
            int idx = 1;
            foreach (var c in comps)
            {
                list.Add(new RawStep(
                    c.ComponentId,
                    $"Nodo {idx} en {startNet}",
                    startNet,
                    startNet,
                    nomV,
                    $"Pines de {c.ComponentId}",
                    $"Componente {c.Category} conectado a {startNet} en cuadrante {c.Quadrant}.",
                    $"Verificar continuidad e impedancia en {c.ComponentId}.",
                    DataSources.Inferred));
                idx++;
            }
            return list;
        }

        int step = 1;
        foreach (var h in filteredHops)
        {
            double v = ResolveDynamicHopVoltage(h.ToNet, h.FromNet, h.ThroughComponentId);
            list.Add(new RawStep(
                h.ThroughComponentId,
                $"{step}. Puente {h.ComponentCategory} ({h.ThroughComponentId})",
                h.FromNet,
                h.ToNet,
                v,
                $"Medir en {h.ThroughComponentId}",
                $"Conecta la red {h.FromNet} hacia {h.ToNet} en el cuadrante {h.Quadrant}.",
                $"Si hay tensión en {h.FromNet} pero no en {h.ToNet}, {h.ThroughComponentId} está abierto o {h.ToNet} está en corto.",
                DataSources.Inferred));
            step++;
        }

        return list;
    }

    private List<UpstreamHopDto> FilterHopsByFunctionalDomain(string rootNet, List<UpstreamHopDto> hops)
    {
        if (hops.Count == 0) return hops;

        string upperRoot = rootNet.ToUpperInvariant();
        bool rootIsPeripheral = PeripheralDomainMarkers.Any(m => upperRoot.Contains(m, StringComparison.OrdinalIgnoreCase));
        bool rootIsPowerRail = upperRoot.StartsWith("PP", StringComparison.OrdinalIgnoreCase);

        var seenComponents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var domainFiltered = new List<UpstreamHopDto>();

        foreach (var hop in hops)
        {
            var comp = _graph.GetComponentDetails(hop.ThroughComponentId);
            if (comp is { IsNoStuff: true }) continue;

            if (!rootIsPeripheral)
            {
                // Si el riel objetivo es una fuente principal (PPBUS_G3H, PP5V_S3, PP3V3_S5, PPVCORE_S0_CPU, etc.),
                // excluir derivaciones a puertos secundarios (Thunderbolt T29, FireWire FW, Backlight, ODD, Audio).
                bool targetIsPeripheral = PeripheralDomainMarkers.Any(m =>
                    hop.ToNet.Contains(m, StringComparison.OrdinalIgnoreCase) ||
                    hop.FromNet.Contains(m, StringComparison.OrdinalIgnoreCase));

                if (targetIsPeripheral) continue;

                // Excluir lazos Kelvin de sensado de corriente diferencial (ej. CPUIMVP_ISNS1_P/N en R7510/R7520)
                // para evitar bucles repetitivos sobre resistencias shunt en lugar de avanzar a las bobinas L7510/L7520.
                if (rootIsPowerRail && SenseFeedbackMarkers.Any(m =>
                        hop.ToNet.Contains(m, StringComparison.OrdinalIgnoreCase) ||
                        hop.FromNet.Contains(m, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                // En PPBUS_G3H, excluir divisores resistivos de alta impedancia (> 10 Ω) hacia ramas secundarias
                if (upperRoot.StartsWith("PPBUS", StringComparison.OrdinalIgnoreCase) && comp is { Category: "Resistor" })
                {
                    double? ohms = NetVoltageResolver.ParseOhms(comp.SchematicValue);
                    if (ohms is > 10.0) continue;
                }
            }

            // Evitar repetir el mismo componente pasivo múltiples veces en el laberinto
            if (!seenComponents.Add(hop.ThroughComponentId))
                continue;

            domainFiltered.Add(hop);
        }

        if (domainFiltered.Count == 0)
            return hops.DistinctBy(h => h.ThroughComponentId, StringComparer.OrdinalIgnoreCase).ToList();

        // Priorizar fusibles y bobinas principales de potencia sobre resistencias de sensado cuando se analiza un riel PP...
        if (rootIsPowerRail)
        {
            return domainFiltered
                .OrderByDescending(h => h.ComponentCategory is "Fuse" or "Inductor/Coil")
                .ThenBy(h => h.Step)
                .ToList();
        }

        return domainFiltered;
    }
}
