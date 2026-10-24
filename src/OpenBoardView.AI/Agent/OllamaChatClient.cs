using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace OpenBoardView.AI.Agent;

/// <summary>
/// Adaptador IChatClient universal para Ollama local (/api/chat) o endpoints compatibles con OpenAI (/v1/chat/completions).
/// Permite alternar entre API Key en la nube y modelos locales sin tocar las herramientas ni el grafo.
/// </summary>
public class OllamaChatClient : IChatClient
{
    private readonly HttpClient _httpClient;
    private readonly string _endpoint;
    private readonly string _model;
    private readonly string? _apiKey;
    private readonly ChatClientMetadata _metadata;

    public OllamaChatClient(HttpClient httpClient, string endpoint = "http://localhost:11434/api/chat", string model = "qwen2.5-coder:7b", string? apiKey = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _endpoint = endpoint;
        _model = model;
        _apiKey = apiKey;
        _metadata = new ChatClientMetadata("OllamaOrOpenAICompatible", new Uri(_endpoint), _model);
    }

    public ChatClientMetadata Metadata => _metadata;

    public object? GetService(Type serviceType, object? serviceKey = null)
        => serviceType == typeof(ChatClientMetadata) ? _metadata : null;

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var payload = BuildOllamaPayload(chatMessages, options, stream: false);
        using var req = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
        };

        if (!string.IsNullOrWhiteSpace(_apiKey))
        {
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _apiKey);
        }

        using var resp = await _httpClient.SendAsync(req, cancellationToken);
        resp.EnsureSuccessStatusCode();

        var json = await resp.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(json);
        return ParseOllamaResponse(doc.RootElement);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var resp = await GetResponseAsync(chatMessages, options, cancellationToken);
        foreach (var msg in resp.Messages)
        {
            yield return new ChatResponseUpdate(msg.Role, msg.Contents);
        }
    }

    private JsonObject BuildOllamaPayload(IEnumerable<ChatMessage> messages, ChatOptions? options, bool stream)
    {
        var root = new JsonObject
        {
            ["model"] = _model,
            ["stream"] = stream
        };

        var msgsArray = new JsonArray();
        foreach (var msg in messages)
        {
            string role = msg.Role == ChatRole.System ? "system"
                        : msg.Role == ChatRole.User ? "user"
                        : msg.Role == ChatRole.Tool ? "tool"
                        : "assistant";

            msgsArray.Add(new JsonObject
            {
                ["role"] = role,
                ["content"] = msg.Text ?? string.Empty
            });
        }
        root["messages"] = msgsArray;

        if (options?.Tools != null && options.Tools.Count > 0)
        {
            var toolsArr = new JsonArray();
            foreach (var tool in options.Tools.OfType<AIFunction>())
            {
                var fn = new JsonObject
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description ?? tool.Name
                };
                if (tool.JsonSchema.ValueKind == JsonValueKind.Object)
                {
                    fn["parameters"] = JsonNode.Parse(tool.JsonSchema.GetRawText());
                }
                toolsArr.Add(new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = fn
                });
            }
            root["tools"] = toolsArr;
        }

        return root;
    }

    private static ChatResponse ParseOllamaResponse(JsonElement root)
    {
        var msg = new ChatMessage { Role = ChatRole.Assistant };
        if (root.TryGetProperty("message", out var messageElem))
        {
            if (messageElem.TryGetProperty("content", out var contentElem))
            {
                var text = contentElem.GetString();
                if (!string.IsNullOrEmpty(text))
                    msg.Contents.Add(new TextContent(text));
            }

            if (messageElem.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
            {
                foreach (var tc in toolCalls.EnumerateArray())
                {
                    if (tc.TryGetProperty("function", out var fn))
                    {
                        var name = fn.GetProperty("name").GetString() ?? string.Empty;
                        var args = new Dictionary<string, object?>();
                        if (fn.TryGetProperty("arguments", out var argsElem) && argsElem.ValueKind == JsonValueKind.Object)
                        {
                            foreach (var p in argsElem.EnumerateObject())
                            {
                                args[p.Name] = p.Value.ToString();
                            }
                        }
                        msg.Contents.Add(new FunctionCallContent(name, name, args));
                    }
                }
            }
        }
        return new ChatResponse(new[] { msg });
    }

    public void Dispose() { }
}
