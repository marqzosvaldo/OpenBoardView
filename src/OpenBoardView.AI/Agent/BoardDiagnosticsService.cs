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
        try
        {
            var response = await _chatClient.GetResponseAsync(messages, options, cancellationToken);
            answerText = string.Join("\n\n", response.Messages
                .Where(m => m.Role == ChatRole.Assistant && !string.IsNullOrWhiteSpace(m.Text))
                .Select(m => m.Text.Trim()));
        }
        catch (Exception ex)
        {
            if (ex.Message.Contains("400") || ex.Message.Contains("API_KEY_INVALID") || ex.Message.Contains("YOUR_GEMINI_API_KEY"))
            {
                answerText = "⚠️ [Aviso del Motor IA]: La API Key de Gemini no está configurada o es inválida.\n\n" +
                             "Para activar el asistente inteligente por chat:\n" +
                             "1. Obtén tu clave gratuita en https://aistudio.google.com/app/apikey\n" +
                             "2. Configúrala en `src/OpenBoardView.AI/appsettings.json` o con la variable `export GEMINI_API_KEY=\"tu_clave\"`.\n\n" +
                             "ℹ️ *Nota: Las herramientas deterministas (Modo Laberinto / Debugger, Grafo de Componentes y Simulador Eléctrico) funcionan al 100% de manera local sin necesidad de ninguna API Key.*";
            }
            else
            {
                answerText = $"[Aviso del Motor IA]: Error al invocar el proveedor de IA ({ex.Message}).";
            }
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
                await _tools.StartCircuitMazeDebuggerAsync(request.Prompt);
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
            "6. Cuando el usuario plantee un problema de secuencia (como 'mi laptop no carga', 'no tengo luz en magsafe', 'sin backlight') o pida rastrear paso a paso cómo viaja la línea como un debugger/laberinto, SIEMPRE invoca 'StartCircuitMazeDebuggerAsync' para activar el Step Debugger interactivo en la pantalla.\n" +
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
