using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using OpenBoardView.AI.Graph;
using OpenBoardView.AI.Server;

namespace OpenBoardView.AI.Agent;

public record AgentQueryRequest(
    string Prompt,
    string? ConversationId = null,
    List<ChatTurnDto>? History = null);

public record ChatTurnDto(string Role, string Text);

public record AgentQueryResponse(
    string ConversationId,
    string ResponseText,
    List<string> ExecutedTools,
    string? InspectedNet,
    string? InspectedComponent,
    string? SuggestedQuadrant,
    SimulationResultDto? Simulation,
    MazeTraceResultDto? MazeTrace,
    List<ObvUiCommandPayload> UiCommands,
    DiagnosticIntentDto? Intent = null);

/// <summary>
/// Orquestador del Agente de Diagnóstico para OpenBoardView sobre Microsoft.Extensions.AI (IChatClient).
/// </summary>
public class BoardDiagnosticsService
{
    private readonly BoardDiagnosticsTools _tools;
    private readonly DiagnosticsAgentContext _context;
    private readonly IChatClient _chatClient;
    private readonly CircuitMazeDebugger _mazeDebugger;
    private readonly CircuitGraphBuilder _graph;

    public BoardDiagnosticsService(
        BoardDiagnosticsTools tools,
        DiagnosticsAgentContext context,
        IChatClient chatClient,
        CircuitMazeDebugger mazeDebugger,
        CircuitGraphBuilder graph)
    {
        _tools = tools;
        _context = context;
        _chatClient = chatClient;
        _mazeDebugger = mazeDebugger;
        _graph = graph;
    }

    public async Task<AgentQueryResponse> ProcessQueryAsync(
        AgentQueryRequest request,
        CancellationToken cancellationToken = default)
    {
        var conversationId = request.ConversationId ?? Guid.NewGuid().ToString("N");

        // Módulo 1: Extraer intención estructurada (primary_net, secondary_nets, primary_components, diagnostic_mode)
        var intent = DiagnosticIntentParser.Parse(request.Prompt, _graph, _mazeDebugger.Knowledge, _mazeDebugger);
        var (messages, options) = PrepareContextAndTools(request.Prompt, request.History);

        string answerText = string.Empty;
        bool llmSuccess = false;
        try
        {
            var response = await _chatClient.GetResponseAsync(messages, options, cancellationToken);
            answerText = string.Join("\n\n", response.Messages
                .Where(m => m.Role == ChatRole.Assistant && !string.IsNullOrWhiteSpace(m.Text))
                .Select(m => m.Text.Trim()));
            llmSuccess = !string.IsNullOrWhiteSpace(answerText);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[BoardDiagnosticsService] Aviso: Proveedor IA no disponible ({ex.Message}). Activando Enrutador Determinista de Fallback.");
        }

        // Si el LLM no respondió (desconexión, 503, cuota o sin API key), ejecutar el Enrutador Determinista de Fallback
        if (!llmSuccess)
        {
            answerText = await EjecutarHerramientaFallbackPorIntencionAsync(request.Prompt, intent);
        }

        // Garantizar sincronización estricta entre el prompt (Intent.PrimaryNet) y el Step Debugger / BoardView:
        // Si el LLM no invocó StartCircuitMazeDebuggerAsync, o si lo invocó con una red distinta a la primary_net extraída del prompt,
        // inyectamos el prompt / primary_net al motor de rutas para evitar caer en fallbacks de síntomas anteriores.
        if (!string.IsNullOrWhiteSpace(request.Prompt))
        {
            var matchedFlow = _mazeDebugger.Knowledge.FindFlow(request.Prompt);
            var extractedNet = _mazeDebugger.ExtractTargetNetFromQuery(request.Prompt);

            bool traceMissingOrMismatched = _context.LastMazeTrace is null ||
                (!string.IsNullOrWhiteSpace(intent.PrimaryNet) &&
                 !string.Equals(_context.LastMazeTrace.TargetNet, intent.PrimaryNet, StringComparison.OrdinalIgnoreCase));

            if (traceMissingOrMismatched && (matchedFlow is not null || extractedNet is not null))
            {
                string targetToUse = extractedNet ?? intent.PrimaryNet ?? request.Prompt;
                await _tools.StartCircuitMazeDebuggerAsync(targetToUse);
            }
        }

        string? resolvedInspectedNet = _context.LastMazeTrace?.TargetNet
                                       ?? intent.PrimaryNet
                                       ?? _context.LastInspectedNet;

        return new AgentQueryResponse(
            ConversationId: conversationId,
            ResponseText: answerText,
            ExecutedTools: _context.ExecutedTools.ToList(),
            InspectedNet: resolvedInspectedNet,
            InspectedComponent: _context.LastInspectedComponent ?? intent.PrimaryComponents.FirstOrDefault(),
            SuggestedQuadrant: _context.SuggestedQuadrant,
            Simulation: _context.LastSimulation,
            MazeTrace: _context.LastMazeTrace,
            UiCommands: _context.SentUiCommands.ToList(),
            Intent: intent
        );
    }

    private async Task<string> EjecutarHerramientaFallbackPorIntencionAsync(string prompt, DiagnosticIntentDto intent)
    {
        string primaryNet = !string.IsNullOrWhiteSpace(intent.PrimaryNet) 
            ? intent.PrimaryNet 
            : (_mazeDebugger.ExtractTargetNetFromQuery(prompt) ?? _graph.GetBoardMasterPowerRail());

        string lower = (prompt ?? string.Empty).ToLowerInvariant();

        // 1. Si es consulta de corto circuito o inyección de voltaje
        if (lower.Contains("corto") || lower.Contains("inyect") || lower.Contains("tierra") || lower.Contains("0v") || lower.Contains("calienta"))
        {
            var quadrants = _tools.FindShortCircuitCandidatesByQuadrant(primaryNet);
            var worstQuad = quadrants.OrderByDescending(q => q.ShuntCapacitorsToGnd.Count).FirstOrDefault();
            
            if (worstQuad != null && worstQuad.ShuntCapacitorsToGnd.Count > 0)
            {
                await _tools.HighlightInBoardViewAsync(worstQuad.ShuntCapacitorsToGnd.First(), primaryNet);
            }

            return $"⚡ **[Modo Autónomo Local - Aislamiento de Cortocircuito]**\n\n" +
                   $"Se analizó la topología física (.brd) para la línea **{primaryNet}**:\n" +
                   $"- **Densidad de capacitores a GND:** Se encontraron {quadrants.Sum(q => q.CapacitorCount)} capacitores conectados ({quadrants.Sum(q => q.ShuntCapacitorsToGnd.Count)} shunts directos a tierra).\n" +
                   $"- **Cuadrante Crítico:** Cuadrante **{worstQuad?.Quadrant ?? "Q1"}** con {worstQuad?.ShuntCapacitorsToGnd.Count ?? 0} capacitores paralelos a GND.\n" +
                   $"- **Recomendación técnica:** Con la placa en frío, mide impedancia en `{worstQuad?.ShuntCapacitorsToGnd.FirstOrDefault() ?? "los capacitores"}`. Si mides < 50 Ω, inyecta 1.0V (límite 1.5A) en el cuadrante {worstQuad?.Quadrant} con cámara térmica o alcohol isopropílico.";
        }

        // 2. Si es consulta de rastreo, puertos, alimentación, carga o encendido
        var trace = await _tools.StartCircuitMazeDebuggerAsync(primaryNet);
        var activeStep = trace.Steps.FirstOrDefault(s => s.StepNumber == trace.CurrentStepNumber) ?? trace.Steps.FirstOrDefault();

        return $"⚡ **[Modo Autónomo Local - Secuencia de Diagnóstico]**\n\n" +
               $"Se activó el Step Debugger en la placa para **{primaryNet}** ({trace.Steps.Count} pasos secuenciales):\n\n" +
               $"### Paso 1: {activeStep?.StageTitle ?? primaryNet}\n" +
               $"* **Componente a medir:** `{activeStep?.ComponentId ?? "Punto de prueba"}` ({activeStep?.Category ?? "Componente"}, Cuadrante **{activeStep?.Quadrant ?? "Q1"}**, cara **{activeStep?.Side ?? "Top"}**).\n" +
               $"* **Tensión esperada:** `{activeStep?.ExpectedVoltage:F2}V` en `{activeStep?.PinToMeasure ?? "pines de entrada"}`.\n" +
               $"* **Diagnóstico si mide 0V:** {activeStep?.FailDiagnosis ?? "Línea caída por falta de habilitación o corto a tierra."}\n\n" +
               $"*(El circuito y los componentes ya están resaltados y posicionados en la pantalla de OpenBoardView).*";
    }

    public async IAsyncEnumerable<ChatResponseUpdate> ProcessStreamingQueryAsync(
        AgentQueryRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var (messages, options) = PrepareContextAndTools(request.Prompt, request.History);
        await foreach (var update in _chatClient.GetStreamingResponseAsync(messages, options, cancellationToken))
        {
            yield return update;
        }
    }

    private (List<ChatMessage> Messages, ChatOptions Options) PrepareContextAndTools(
        string userPrompt,
        List<ChatTurnDto>? history)
    {
        var systemPrompt =
            "Eres un Ingeniero Experto en Microelectrónica y Diagnóstico de Placas Madre (especializado en Apple MacBook y laptops) integrado en OpenBoardView.\n" +
            "REGLAS ESTRICTAS DE OPERACIÓN:\n" +
            "1. Siempre usa las herramientas disponibles para consultar la topología real (.brd) y el esquemático (.pdf) antes de responder. NUNCA inventes IDs de componentes ni nombres de redes.\n" +
            "2. Si el técnico reporta un corto o falla en una línea (ej. 'PPBUS_G3H', 'PP3V42_G3H', 'PP5V_S5'), invoca 'FindShortCircuitCandidatesByQuadrant' y 'TraceUpstreamPath' para aislar la falla por cuadrantes espaciales (Q1, Q2, Q3, Q4).\n" +
            "3. Recomienda al técnico en qué cuadrante y cara (Top/Bottom) inyectar voltaje o buscar temperatura según dónde haya mayor densidad de capacitores en paralelo a GND (ShuntCapacitorsToGnd), o si conviene levantar un fusible/bobina puente (ej. F7040) para aislar la línea.\n" +
            "4. Cuando identifiques un componente clave a revisar o medir, invoca 'HighlightInBoardViewAsync' para iluminarlo automáticamente en la pantalla de OpenBoardView.\n" +
            "5. Cuando el usuario pida simular una falla, inyectar tensión, ver el flujo de corriente, cámara térmica o qué pasa al levantar un fusible/componente, SIEMPRE invoca 'SimulateElectricalFaultAndThermalFlowAsync' para dibujar la animación de corriente y puntos de calor en vivo.\n" +
            "6. Cuando el usuario plantee un problema o síntoma en lenguaje natural (ej. 'los puertos usb no funcionan', 'mi laptop no carga', 'no da imagen', 'sin audio', 'se calienta') o pida rastrear paso a paso cómo viaja la línea como un debugger/laberinto, SIEMPRE invoca 'StartCircuitMazeDebuggerAsync'. Puedes pasarle tanto el síntoma en lenguaje natural como la red específica de la placa (ej. 'PPDCIN_USBC_AON', 'PPVBUS_USBC0', 'PPVOUT_S0_LCDBKLT', 'PP3V8_AON'). El motor de diagnóstico mapeará automáticamente el subsistema hacia los componentes físicos y guiará al técnico paso a paso.\n" +
            "7. Responde en español técnico, claro, estructurado y directo al grano.";

        var messages = new List<ChatMessage>
        {
            new ChatMessage(ChatRole.System, systemPrompt)
        };

        if (history != null)
        {
            foreach (var turn in history.TakeLast(6))
            {
                var role = turn.Role.Equals("user", StringComparison.OrdinalIgnoreCase)
                    ? ChatRole.User
                    : ChatRole.Assistant;
                messages.Add(new ChatMessage(role, turn.Text));
            }
        }

        messages.Add(new ChatMessage(ChatRole.User, userPrompt));

        var options = new ChatOptions
        {
            Tools = new List<AITool>
            {
                AIFunctionFactory.Create(_tools.GetBoardOverview),
                AIFunctionFactory.Create(_tools.SearchNets),
                AIFunctionFactory.Create(_tools.FindShortCircuitCandidatesByQuadrant),
                AIFunctionFactory.Create(_tools.GetComponentsOnNet),
                AIFunctionFactory.Create(_tools.GetComponentDetails),
                AIFunctionFactory.Create(_tools.TraceUpstreamPath),
                AIFunctionFactory.Create(_tools.QuerySchematicPdf),
                AIFunctionFactory.Create(_tools.HighlightInBoardViewAsync),
                AIFunctionFactory.Create(_tools.SimulateElectricalFaultAndThermalFlowAsync),
                AIFunctionFactory.Create(_tools.StartCircuitMazeDebuggerAsync)
            }
        };

        return (messages, options);
    }
}
