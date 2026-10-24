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
    List<ObvUiCommandPayload> UiCommands);

/// <summary>
/// Orquestador del Agente de Diagnóstico para OpenBoardView sobre Microsoft.Extensions.AI (IChatClient).
/// </summary>
public class BoardDiagnosticsService
{
    private readonly BoardDiagnosticsTools _tools;
    private readonly DiagnosticsAgentContext _context;
    private readonly IChatClient _chatClient;

    public BoardDiagnosticsService(
        BoardDiagnosticsTools tools,
        DiagnosticsAgentContext context,
        IChatClient chatClient)
    {
        _tools = tools;
        _context = context;
        _chatClient = chatClient;
    }

    public async Task<AgentQueryResponse> ProcessQueryAsync(
        AgentQueryRequest request,
        CancellationToken cancellationToken = default)
    {
        var conversationId = request.ConversationId ?? Guid.NewGuid().ToString("N");
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
            answerText = $"[Aviso del Motor IA]: Error al invocar IChatClient ({ex.Message}).";
        }

        return new AgentQueryResponse(
            ConversationId: conversationId,
            ResponseText: answerText,
            ExecutedTools: _context.ExecutedTools.ToList(),
            InspectedNet: _context.LastInspectedNet,
            InspectedComponent: _context.LastInspectedComponent,
            SuggestedQuadrant: _context.SuggestedQuadrant,
            Simulation: _context.LastSimulation,
            MazeTrace: _context.LastMazeTrace,
            UiCommands: _context.SentUiCommands.ToList()
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
