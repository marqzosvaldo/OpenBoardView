using System.ComponentModel;
using OpenBoardView.AI.Graph;
using OpenBoardView.AI.Server;

namespace OpenBoardView.AI.Agent;

/// <summary>
/// Fase 4: Herramientas del Agente para Microsoft.Extensions.AI (AIFunctionFactory.Create).
/// Sigue exactamente el patrón de FinancialTools.cs de Clínica Esencial.
/// </summary>
public class BoardDiagnosticsTools
{
    private readonly CircuitGraphBuilder _graph;
    private readonly CircuitSimulator _simulator;
    private readonly CircuitMazeDebugger _mazeDebugger;
    private readonly DiagnosticsAgentContext _context;
    private readonly ObvWebSocketServer _wsServer;

    public BoardDiagnosticsTools(
        CircuitGraphBuilder graph,
        CircuitSimulator simulator,
        CircuitMazeDebugger mazeDebugger,
        DiagnosticsAgentContext context,
        ObvWebSocketServer wsServer)
    {
        _graph = graph;
        _simulator = simulator;
        _mazeDebugger = mazeDebugger;
        _context = context;
        _wsServer = wsServer;
    }

    [Description("Obtiene el resumen general de la placa madre cargada: dimensiones físicas (bounding box), total de componentes, redes y distribución espacial por cuadrantes Q1, Q2, Q3 y Q4.")]
    public object GetBoardOverview()
    {
        var t = _graph.Topology;
        var byQuad = t.Components
            .GroupBy(c => c.Quadrant)
            .OrderBy(g => g.Key)
            .Select(g => new
            {
                Quadrant = g.Key,
                ComponentCount = g.Count(),
                Capacitors = g.Count(c => c.Category == "Capacitor"),
                ICs = g.Count(c => c.Category == "IC/Chip")
            })
            .ToList();

        _context.RegisterToolCall(nameof(GetBoardOverview));

        return new
        {
            t.FileName,
            t.WasEncrypted,
            TotalComponents = t.Components.Count,
            TotalNets = t.Nets.Count,
            BoundingBox = new
            {
                t.BoundingBox.MinX,
                t.BoundingBox.MaxX,
                t.BoundingBox.MinY,
                t.BoundingBox.MaxY,
                t.BoundingBox.Width,
                t.BoundingBox.Height
            },
            QuadrantBreakdown = byQuad
        };
    }

    [Description("Busca nombres de líneas o redes eléctricas (Nets) en la placa que coincidan con un término (ej. 'PPBUS', '3V42', '5V_S5', 'SMC', 'SPI').")]
    public List<string> SearchNets(
        [Description("Fragmento del nombre de la red eléctrica a buscar, ej. 'PPBUS_G3H' o '3V42'.")] string query)
    {
        var matches = _graph.SearchNetNames(query);
        _context.RegisterToolCall(nameof(SearchNets), net: matches.FirstOrDefault());
        return matches;
    }

    [Description("Analiza una red en corto o con falla y devuelve en qué cuadrantes espaciales (Q1, Q2, Q3, Q4) hay mayor densidad de capacitores conectados a tierra (principales sospechosos de cortocircuito) y componentes.")]
    public List<QuadrantDensitySummary> FindShortCircuitCandidatesByQuadrant(
        [Description("Nombre exacto o parcial de la red eléctrica afectada, ej. 'PPBUS_G3H' o 'PP3V42_G3H'.")] string netName)
    {
        var quadrants = _graph.GetHighDensityQuadrants(netName);
        var topQuad = quadrants.FirstOrDefault()?.Quadrant;
        _context.RegisterToolCall(nameof(FindShortCircuitCandidatesByQuadrant), net: netName, quadrant: topQuad);
        return quadrants;
    }

    [Description("Obtiene todos los componentes conectados a una red eléctrica (Net) junto con su categoría (Capacitor, IC, Resistor), cuadrante (Q1-Q4), cara de la placa (Top/Bottom) y contexto del esquemático PDF.")]
    public List<ComponentNodeSummary> GetComponentsOnNet(
        [Description("Nombre de la red eléctrica, ej. 'PPBUS_G3H'.")] string netName)
    {
        var list = _graph.GetComponentsOnNet(netName);
        _context.RegisterToolCall(nameof(GetComponentsOnNet), net: netName);
        return list.Take(40).ToList();
    }

    [Description("Obtiene los detalles completos de un componente específico (ej. 'U7000', 'R7040', 'F7040', 'C7040'): coordenadas X/Y, cuadrante, cara (Top/Bottom), redes en sus pines y texto extraído del esquemático PDF.")]
    public ComponentNodeSummary? GetComponentDetails(
        [Description("ID del componente en la placa, ej. 'U7000' o 'F7040'.")] string componentId)
    {
        var details = _graph.GetComponentDetails(componentId);
        _context.RegisterToolCall(nameof(GetComponentDetails), component: componentId, quadrant: details?.Quadrant);
        return details;
    }

    [Description("Rastrea el camino eléctrico hacia atrás/adelante (Upstream/Downstream) desde una red o componente a través de fusibles, bobinas y resistencias en serie para descubrir de dónde proviene un voltaje o señal.")]
    public List<UpstreamHopDto> TraceUpstreamPath(
        [Description("Nombre de la red eléctrica desde donde iniciar el rastreo, ej. 'PPBUS_G3H'.")] string netName,
        [Description("ID del componente inicial opcional, ej. 'U7000' o 'F7040'.")] string? startComponentId = null)
    {
        var path = _graph.GetUpstreamPath(startComponentId, netName);
        _context.RegisterToolCall(nameof(TraceUpstreamPath), net: netName, component: startComponentId);
        return path;
    }

    [Description("Consulta el índice semántico del esquemático PDF para un componente, red o palabra clave (ej. 'U7000', 'ISL6259', 'SMC_BC_ACOK', 'BIL').")]
    public string QuerySchematicPdf(
        [Description("ID de componente, nombre de red o término técnico a buscar en el PDF.")] string keyword)
    {
        _context.RegisterToolCall(nameof(QuerySchematicPdf), component: keyword);
        return _graph.SearchSchematicText(keyword);
    }

    [Description("Ilumina y centra la cámara en la interfaz gráfica de OpenBoardView sobre un componente o red específica mediante WebSockets.")]
    public async Task<ObvUiCommandPayload> HighlightInBoardViewAsync(
        [Description("ID del componente a iluminar en pantalla, ej. 'U7000' o 'F7040'.")] string componentId,
        [Description("Nombre opcional de la red eléctrica a resaltar.")] string? netName = null,
        [Description("Nota breve de diagnóstico para mostrar al técnico en el visor.")] string? note = null)
    {
        var comp = _graph.GetComponentDetails(componentId);
        var payload = new ObvUiCommandPayload(
            Action: "highlight",
            Component: componentId.ToUpperInvariant(),
            Net: netName ?? _context.LastInspectedNet,
            Quadrant: comp?.Quadrant,
            X: comp?.X,
            Y: comp?.Y,
            Message: note ?? $"Inspeccionar {componentId.ToUpperInvariant()} en cuadrante {comp?.Quadrant ?? "N/A"}"
        );

        await _wsServer.BroadcastCommandAsync(payload);
        _context.SentUiCommands.Add(payload);
        _context.RegisterToolCall(nameof(HighlightInBoardViewAsync), net: netName, component: componentId, quadrant: comp?.Quadrant);

        return payload;
    }

    [Description("Ejecuta una simulación eléctrica en vivo de flujo de corriente, caída de tensión por cortocircuito, levantamiento de fusibles/bobinas y cámara térmica infrarroja (IR), dibujándola automáticamente en la pantalla de OpenBoardView.")]
    public async Task<SimulationResultDto> SimulateElectricalFaultAndThermalFlowAsync(
        [Description("Nombre de la red eléctrica a simular, ej. 'PPBUS_G3H' o 'PP3V42_G3H'.")] string netName,
        [Description("ID opcional del componente en cortocircuito (ej. 'C7513', 'C7120'). Si se omite, el simulador elige el candidato principal.")] string? faultedComponentId = null,
        [Description("ID opcional de un fusible, bobina o resistencia levantada para probar aislamiento (ej. 'F7040').")] string? liftedComponentId = null,
        [Description("Voltaje de inyección en Voltios (ej. 1.2 o 12.56).")] double injectedVoltage = 1.5,
        [Description("Límite de corriente de la fuente de laboratorio en Amperios (ej. 2.5).")] double currentLimitAmps = 2.5)
    {
        var req = new SimulationRequestDto
        {
            NetName = netName,
            FaultedComponentId = faultedComponentId,
            LiftedComponentIds = string.IsNullOrWhiteSpace(liftedComponentId)
                ? new List<string>()
                : new List<string> { liftedComponentId.Trim().ToUpperInvariant() },
            InjectedVoltage = injectedVoltage,
            CurrentLimitAmps = currentLimitAmps
        };

        var simResult = _simulator.RunSimulation(req);
        _context.LastSimulation = simResult;

        var targetComp = simResult.FaultedComponentId ?? liftedComponentId ?? "F7040";
        var compDetails = _graph.GetComponentDetails(targetComp);

        var wsPayload = new ObvUiCommandPayload(
            Action: "simulate_flow",
            Component: targetComp,
            Net: simResult.RootNet,
            Quadrant: compDetails?.Quadrant,
            X: compDetails?.X,
            Y: compDetails?.Y,
            Message: simResult.DiagnosticSummary,
            Simulation: simResult
        );

        await _wsServer.BroadcastCommandAsync(wsPayload);
        _context.SentUiCommands.Add(wsPayload);
        _context.RegisterToolCall(
            nameof(SimulateElectricalFaultAndThermalFlowAsync),
            net: simResult.RootNet,
            component: targetComp,
            quadrant: compDetails?.Quadrant);

        return simResult;
    }

    [Description("Inicia el Modo Debugger Paso a Paso (Laberinto Secuencial) para seguir cómo viaja la línea eléctrica por el circuito ante síntomas como 'no carga', 'sin luz magsafe', 'sin backlight' o cualquier red, permitiendo avanzar nodo por nodo o marcar dónde se bloquea el voltaje.")]
    public async Task<MazeTraceResultDto> StartCircuitMazeDebuggerAsync(
        [Description("Síntoma o línea a rastrear paso a paso, ej. 'no carga', 'sin luz verde magsafe', 'sin backlight' o 'PPBUS_G3H'.")] string symptomOrNet,
        [Description("Número de paso opcional (1 a 9) donde el técnico reporta que se cayó el voltaje a 0V (Breakpoint/Muro).")] int? blockedAtStepNumber = null)
    {
        var trace = _mazeDebugger.BuildTrace(new MazeTraceRequestDto
        {
            QueryOrSymptom = symptomOrNet,
            ActiveStepNumber = blockedAtStepNumber ?? 1,
            BlockedAtStepNumber = blockedAtStepNumber
        });

        _context.LastMazeTrace = trace;
        var activeNode = trace.Steps.FirstOrDefault(s => s.StepNumber == trace.CurrentStepNumber) ?? trace.Steps.First();

        var wsPayload = new ObvUiCommandPayload(
            Action: "maze_debugger",
            Component: activeNode.ComponentId,
            Net: activeNode.OutputNet,
            Quadrant: activeNode.Quadrant,
            X: activeNode.X,
            Y: activeNode.Y,
            Message: trace.OverallDiagnosis,
            MazeTrace: trace
        );

        await _wsServer.BroadcastCommandAsync(wsPayload);
        _context.SentUiCommands.Add(wsPayload);
        _context.RegisterToolCall(
            nameof(StartCircuitMazeDebuggerAsync),
            net: activeNode.OutputNet,
            component: activeNode.ComponentId,
            quadrant: activeNode.Quadrant);

        return trace;
    }
}
