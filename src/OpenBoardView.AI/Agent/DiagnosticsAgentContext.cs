using OpenBoardView.AI.Graph;
using OpenBoardView.AI.Server;

namespace OpenBoardView.AI.Agent;

/// <summary>
/// Contexto Scoped para rastrear las herramientas ejecutadas por el agente IA durante el diagnóstico
/// y los comandos enviados a OpenBoardView.
/// </summary>
public class DiagnosticsAgentContext
{
    public List<string> ExecutedTools { get; } = new();
    public string? LastInspectedNet { get; set; }
    public string? LastInspectedComponent { get; set; }
    public string? SuggestedQuadrant { get; set; }
    public SimulationResultDto? LastSimulation { get; set; }
    public MazeTraceResultDto? LastMazeTrace { get; set; }
    public List<ObvUiCommandPayload> SentUiCommands { get; } = new();

    public void RegisterToolCall(string toolName, string? net = null, string? component = null, string? quadrant = null)
    {
        ExecutedTools.Add(toolName);
        if (!string.IsNullOrWhiteSpace(net)) LastInspectedNet = net;
        if (!string.IsNullOrWhiteSpace(component)) LastInspectedComponent = component;
        if (!string.IsNullOrWhiteSpace(quadrant)) SuggestedQuadrant = quadrant;
    }
}
