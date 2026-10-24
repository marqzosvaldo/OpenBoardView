using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using OpenBoardView.AI.Graph;

namespace OpenBoardView.AI.Server;

public record ObvUiCommandPayload(
    string Action,
    string? Component = null,
    string? Net = null,
    string? Quadrant = null,
    int? X = null,
    int? Y = null,
    string? Message = null,
    SimulationResultDto? Simulation = null,
    MazeTraceResultDto? MazeTrace = null);

/// <summary>
/// Fase 5: Servidor WebSocket Bidireccional hacia OpenBoardView.
/// Permite que las herramientas de IA iluminen componentes, redes y cuadrantes en tiempo real.
/// </summary>
public class ObvWebSocketServer
{
    private readonly List<WebSocket> _clients = new();
    private readonly object _lock = new();
    public List<ObvUiCommandPayload> CommandHistory { get; } = new();

    public async Task HandleClientAsync(WebSocket webSocket, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            _clients.Add(webSocket);
        }

        var buffer = new byte[4096];
        try
        {
            while (webSocket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Cerrado por el cliente", cancellationToken);
                    break;
                }
            }
        }
        catch
        {
            // Desconexión abrupta del cliente OBV
        }
        finally
        {
            lock (_lock)
            {
                _clients.Remove(webSocket);
            }
        }
    }

    public async Task BroadcastCommandAsync(ObvUiCommandPayload payload, CancellationToken cancellationToken = default)
    {
        CommandHistory.Add(payload);

        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
        var bytes = Encoding.UTF8.GetBytes(json);

        List<WebSocket> activeSockets;
        lock (_lock)
        {
            activeSockets = _clients.Where(c => c.State == WebSocketState.Open).ToList();
        }

        foreach (var socket in activeSockets)
        {
            try
            {
                await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cancellationToken);
            }
            catch
            {
                // Ignorar clientes desconectados
            }
        }
    }
}
