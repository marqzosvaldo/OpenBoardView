using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace OpenBoardView.AI.Agent;

/// <summary>
/// Adaptador IChatClient para Google Gemini (v1beta) en .NET 10 con soporte nativo para Function Calling y Streaming SSE.
/// Incluye cascada resiliente multi-modelo, timeout preventivo de 15s por intento, preservación estricta de thoughtSignature
/// y afinidad de modelo exitoso para llamadas con herramientas de dos turnos.
/// </summary>
public class GeminiChatClient : IChatClient
{
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;
    private readonly string _model;
    private readonly ChatClientMetadata _metadata;
    private string? _lastWorkingModel;
    private readonly Dictionary<string, string> _thoughtSignaturesByTool = new();

    public GeminiChatClient(HttpClient httpClient, string apiKey, string model = "gemini-3.5-flash")
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
        _model = string.IsNullOrWhiteSpace(model) ? "gemini-3.5-flash" : model;
        _metadata = new ChatClientMetadata("GoogleGemini", new Uri("https://generativelanguage.googleapis.com"), _model);
    }

    public ChatClientMetadata Metadata => _metadata;

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        if (serviceType == typeof(ChatClientMetadata)) return _metadata;
        return null;
    }

    private List<string> ObtenerModelosCandidatos()
    {
        var candidateModels = new List<string>();
        // Si ya un modelo tuvo éxito en esta sesión/turno previo, priorizarlo para mantener coherencia de thoughtSignature
        if (!string.IsNullOrWhiteSpace(_lastWorkingModel))
        {
            candidateModels.Add(_lastWorkingModel);
        }
        if (!candidateModels.Contains(_model, StringComparer.OrdinalIgnoreCase))
        {
            candidateModels.Add(_model);
        }
        // Modelos probados empíricamente en orden de latencia (< 1s a 3s)
        foreach (var fallback in new[] { "gemini-3.5-flash", "gemini-flash-lite-latest", "gemini-3.5-flash-lite", "gemini-flash-latest", "gemini-3.8-flash" })
        {
            if (!candidateModels.Contains(fallback, StringComparer.OrdinalIgnoreCase))
                candidateModels.Add(fallback);
        }
        return candidateModels;
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var requestPayload = ConstruirPayloadGemini(chatMessages, options);
        var jsonPayload = requestPayload.ToJsonString();

        var candidateModels = ObtenerModelosCandidatos();
        HttpResponseMessage? lastResponse = null;
        string lastErrorBody = string.Empty;

        for (int attempt = 0; attempt < candidateModels.Count; attempt++)
        {
            var currentModel = candidateModels[attempt];
            var endpoint = $"https://generativelanguage.googleapis.com/v1beta/models/{currentModel}:generateContent?key={_apiKey}";

            try
            {
                using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                attemptCts.CancelAfter(TimeSpan.FromSeconds(15));

                using var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
                lastResponse = await _httpClient.PostAsync(endpoint, content, attemptCts.Token);

                if (lastResponse.IsSuccessStatusCode)
                {
                    _lastWorkingModel = currentModel;
                    var responseJson = await lastResponse.Content.ReadAsStringAsync(cancellationToken);
                    using var doc = JsonDocument.Parse(responseJson);
                    return ParsearRespuestaGemini(doc.RootElement);
                }

                lastErrorBody = await lastResponse.Content.ReadAsStringAsync(cancellationToken);
                int statusCode = (int)lastResponse.StatusCode;
                Console.WriteLine($"[GeminiChatClient Warning] Modelo '{currentModel}' devolvió {lastResponse.StatusCode} ({statusCode}): {lastErrorBody}");

                // Si es sobrecarga (503), rate-limit (429), error de schema (400, 404) o 5xx, conmutar al siguiente modelo
                if ((statusCode is 503 or 429 or 500 or 502 or 504 or 404 or 400) && attempt < candidateModels.Count - 1)
                {
                    await Task.Delay(250 * (attempt + 1), cancellationToken);
                    continue;
                }

                lastResponse.EnsureSuccessStatusCode();
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                Console.WriteLine($"[GeminiChatClient Warning] Modelo '{currentModel}' excedió el timeout de 15s. Conmutando al siguiente modelo en cascada...");
                if (attempt < candidateModels.Count - 1) continue;
                throw new TimeoutException($"Todos los modelos de Gemini excedieron el tiempo de espera.");
            }
            catch (HttpRequestException ex) when (attempt < candidateModels.Count - 1)
            {
                Console.WriteLine($"[GeminiChatClient Warning] Modelo '{currentModel}' fallo de red ({ex.Message}). Conmutando...");
                continue;
            }
        }

        lastResponse?.EnsureSuccessStatusCode();
        throw new HttpRequestException($"Error al invocar Gemini API: {lastErrorBody}");
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var candidateModels = ObtenerModelosCandidatos();
        var requestPayload = ConstruirPayloadGemini(chatMessages, options);
        var jsonPayload = requestPayload.ToJsonString();

        HttpResponseMessage? response = null;
        for (int attempt = 0; attempt < candidateModels.Count; attempt++)
        {
            var currentModel = candidateModels[attempt];
            var endpoint = $"https://generativelanguage.googleapis.com/v1beta/models/{currentModel}:streamGenerateContent?key={_apiKey}&alt=sse";

            try
            {
                using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                attemptCts.CancelAfter(TimeSpan.FromSeconds(15));

                var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
                var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = content };
                response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, attemptCts.Token);

                if (response.IsSuccessStatusCode)
                {
                    _lastWorkingModel = currentModel;
                    break;
                }

                int statusCode = (int)response.StatusCode;
                if ((statusCode is 503 or 429 or 500 or 502 or 504 or 404 or 400) && attempt < candidateModels.Count - 1)
                {
                    await Task.Delay(250 * (attempt + 1), cancellationToken);
                    continue;
                }

                response.EnsureSuccessStatusCode();
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                Console.WriteLine($"[GeminiChatClient Warning] Streaming en '{currentModel}' excedió timeout de 15s. Conmutando...");
                if (attempt < candidateModels.Count - 1) continue;
                throw;
            }
            catch (HttpRequestException) when (attempt < candidateModels.Count - 1)
            {
                continue;
            }
        }

        response!.EnsureSuccessStatusCode();
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line == null) break;
            if (string.IsNullOrWhiteSpace(line)) continue;

            if (line.StartsWith("data: "))
            {
                var data = line.Substring(6).Trim();
                if (data == "[DONE]") break;

                ChatResponseUpdate? update = null;
                try
                {
                    using var doc = JsonDocument.Parse(data);
                    update = ParsearChunkStreamingGemini(doc.RootElement);
                }
                catch
                {
                    // Ignorar líneas malformadas
                }

                if (update != null)
                {
                    yield return update;
                }
            }
        }
    }

    private JsonObject ConstruirPayloadGemini(IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        var root = new JsonObject();
        var contentsArray = new JsonArray();
        string? systemInstruction = null;

        var messageList = messages.ToList();

        foreach (var msg in messageList)
        {
            if (msg.Role == ChatRole.System)
            {
                systemInstruction = msg.Text;
                continue;
            }

            var role = msg.Role == ChatRole.User ? "user" : "model";
            var partsArray = new JsonArray();

            foreach (var contentItem in msg.Contents)
            {
                if (contentItem is TextContent textContent)
                {
                    if (!string.IsNullOrEmpty(textContent.Text))
                    {
                        partsArray.Add(new JsonObject { ["text"] = textContent.Text });
                    }
                }
                else if (contentItem is FunctionCallContent callContent)
                {
                    var funcCall = new JsonObject
                    {
                        ["name"] = callContent.Name
                    };

                    if (callContent.Arguments != null && callContent.Arguments.Count > 0)
                    {
                        var argsObj = new JsonObject();
                        foreach (var kvp in callContent.Arguments)
                        {
                            argsObj[kvp.Key] = JsonValue.Create(kvp.Value?.ToString());
                        }
                        funcCall["args"] = argsObj;
                    }

                    var partObj = new JsonObject
                    {
                        ["functionCall"] = funcCall
                    };

                    // Inyectar thoughtSignature original de Google si está disponible (requerido por Gemini 3)
                    string? ts = null;
                    if (callContent.AdditionalProperties != null &&
                        (callContent.AdditionalProperties.TryGetValue("thoughtSignature", out var tsVal) ||
                         callContent.AdditionalProperties.TryGetValue("thought_signature", out tsVal)) &&
                        tsVal != null)
                    {
                        ts = tsVal.ToString();
                    }
                    else if (_thoughtSignaturesByTool.TryGetValue(callContent.Name, out var cachedTs))
                    {
                        ts = cachedTs;
                    }

                    if (!string.IsNullOrEmpty(ts))
                    {
                        partObj["thoughtSignature"] = ts;
                    }

                    partsArray.Add(partObj);
                }
                else if (contentItem is FunctionResultContent resContent)
                {
                    role = "user";

                    // Resolver el nombre exacto de la herramienta original, evitando enviar call_id abstracto
                    string functionName = messageList
                        .SelectMany(m => m.Contents.OfType<FunctionCallContent>())
                        .FirstOrDefault(fc => fc.CallId == resContent.CallId)?.Name
                        ?? (resContent.CallId?.StartsWith("call_") == true
                            ? messageList.SelectMany(m => m.Contents.OfType<FunctionCallContent>()).LastOrDefault()?.Name
                            : resContent.CallId)
                        ?? "UnknownFunction";

                    string rawJson = "null";
                    if (resContent.Result != null)
                    {
                        if (resContent.Result is JsonElement je)
                        {
                            rawJson = je.GetRawText();
                        }
                        else if (resContent.Result is JsonNode jn)
                        {
                            rawJson = jn.ToJsonString();
                        }
                        else
                        {
                            try
                            {
                                rawJson = JsonSerializer.Serialize(resContent.Result);
                            }
                            catch
                            {
                                rawJson = JsonSerializer.Serialize(resContent.Result.ToString());
                            }
                        }
                    }

                    JsonNode? parsedNode = null;
                    try
                    {
                        parsedNode = JsonNode.Parse(rawJson);
                    }
                    catch
                    {
                        parsedNode = JsonValue.Create(rawJson);
                    }

                    JsonObject responseStruct;
                    if (parsedNode is JsonObject jsonObj)
                    {
                        responseStruct = jsonObj;
                    }
                    else
                    {
                        responseStruct = new JsonObject
                        {
                            ["data"] = parsedNode ?? JsonValue.Create("OK")
                        };
                    }

                    var funcResp = new JsonObject
                    {
                        ["name"] = functionName,
                        ["response"] = responseStruct
                    };
                    partsArray.Add(new JsonObject { ["functionResponse"] = funcResp });
                }
            }

            if (partsArray.Count > 0)
            {
                contentsArray.Add(new JsonObject
                {
                    ["role"] = role,
                    ["parts"] = partsArray
                });
            }
        }

        root["contents"] = contentsArray;

        if (!string.IsNullOrWhiteSpace(systemInstruction))
        {
            root["systemInstruction"] = new JsonObject
            {
                ["parts"] = new JsonArray { new JsonObject { ["text"] = systemInstruction } }
            };
        }

        if (options?.Tools != null && options.Tools.Count > 0)
        {
            var functionDeclarations = new JsonArray();

            foreach (var tool in options.Tools)
            {
                if (tool is AIFunction aiFunc)
                {
                    var decl = new JsonObject
                    {
                        ["name"] = aiFunc.Name,
                        ["description"] = aiFunc.Description ?? aiFunc.Name
                    };

                    if (aiFunc.JsonSchema.ValueKind == JsonValueKind.Object)
                    {
                        var rawNode = JsonNode.Parse(aiFunc.JsonSchema.GetRawText());
                        decl["parameters"] = NormalizarEsquemaParaGemini(rawNode);
                    }

                    functionDeclarations.Add(decl);
                }
            }

            if (functionDeclarations.Count > 0)
            {
                root["tools"] = new JsonArray
                {
                    new JsonObject { ["functionDeclarations"] = functionDeclarations }
                };
            }
        }

        var generationConfig = new JsonObject();
        if (options?.Temperature.HasValue == true)
        {
            generationConfig["temperature"] = options.Temperature.Value;
        }
        if (generationConfig.Count > 0)
        {
            root["generationConfig"] = generationConfig;
        }

        return root;
    }

    private static JsonNode? NormalizarEsquemaParaGemini(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            obj.Remove("$schema");
            obj.Remove("additionalProperties");

            if (obj.TryGetPropertyValue("default", out var defNode) && defNode == null)
            {
                obj.Remove("default");
            }

            if (obj.TryGetPropertyValue("type", out var typeNode) && typeNode is JsonArray arr)
            {
                var firstNonNull = arr.Select(x => x?.ToString())
                    .FirstOrDefault(x => x != "null" && !string.IsNullOrEmpty(x)) ?? "string";
                obj["type"] = JsonValue.Create(firstNonNull);
            }

            foreach (var kvp in obj.ToList())
            {
                if (kvp.Value is JsonObject or JsonArray)
                {
                    NormalizarEsquemaParaGemini(kvp.Value);
                }
            }
        }
        else if (node is JsonArray array)
        {
            for (int i = 0; i < array.Count; i++)
            {
                NormalizarEsquemaParaGemini(array[i]);
            }
        }

        return node;
    }

    private ChatResponse ParsearRespuestaGemini(JsonElement root)
    {
        var messages = new List<ChatMessage>();

        if (root.TryGetProperty("candidates", out var candidates) && candidates.GetArrayLength() > 0)
        {
            var firstCandidate = candidates[0];
            if (firstCandidate.TryGetProperty("content", out var contentElem) &&
                contentElem.TryGetProperty("parts", out var partsElem))
            {
                var responseMessage = new ChatMessage { Role = ChatRole.Assistant };

                foreach (var part in partsElem.EnumerateArray())
                {
                    if (part.TryGetProperty("text", out var textElem))
                    {
                        responseMessage.Contents.Add(new TextContent(textElem.GetString() ?? string.Empty));
                    }
                    else if (part.TryGetProperty("functionCall", out var funcCallElem))
                    {
                        var name = funcCallElem.GetProperty("name").GetString() ?? string.Empty;
                        var argsDict = new Dictionary<string, object?>();

                        if (funcCallElem.TryGetProperty("args", out var argsElem))
                        {
                            foreach (var prop in argsElem.EnumerateObject())
                            {
                                argsDict[prop.Name] = prop.Value.ValueKind switch
                                {
                                    JsonValueKind.String => prop.Value.GetString(),
                                    JsonValueKind.Number => prop.Value.TryGetInt32(out var iv) ? iv : prop.Value.GetDecimal(),
                                    JsonValueKind.True => true,
                                    JsonValueKind.False => false,
                                    _ => prop.Value.GetRawText()
                                };
                            }
                        }

                        var callId = funcCallElem.TryGetProperty("id", out var idElem) ? idElem.GetString() ?? name : name;
                        var callContent = new FunctionCallContent(callId, name, argsDict);

                        string? tsString = null;
                        if (part.TryGetProperty("thoughtSignature", out var tsElem))
                        {
                            tsString = tsElem.GetString();
                        }
                        else if (part.TryGetProperty("thought_signature", out var tsElem2))
                        {
                            tsString = tsElem2.GetString();
                        }

                        if (!string.IsNullOrEmpty(tsString))
                        {
                            callContent.AdditionalProperties ??= new AdditionalPropertiesDictionary();
                            callContent.AdditionalProperties["thoughtSignature"] = tsString;
                            _thoughtSignaturesByTool[name] = tsString;
                        }

                        responseMessage.Contents.Add(callContent);
                    }
                }

                messages.Add(responseMessage);
            }
        }

        return new ChatResponse(messages);
    }

    private ChatResponseUpdate? ParsearChunkStreamingGemini(JsonElement root)
    {
        if (root.TryGetProperty("candidates", out var candidates) && candidates.GetArrayLength() > 0)
        {
            var firstCandidate = candidates[0];
            if (firstCandidate.TryGetProperty("content", out var contentElem) &&
                contentElem.TryGetProperty("parts", out var partsElem))
            {
                var update = new ChatResponseUpdate { Role = ChatRole.Assistant };

                foreach (var part in partsElem.EnumerateArray())
                {
                    if (part.TryGetProperty("text", out var textElem))
                    {
                        update.Contents.Add(new TextContent(textElem.GetString() ?? string.Empty));
                    }
                    else if (part.TryGetProperty("functionCall", out var funcCallElem))
                    {
                        var name = funcCallElem.GetProperty("name").GetString() ?? string.Empty;
                        var argsDict = new Dictionary<string, object?>();

                        if (funcCallElem.TryGetProperty("args", out var argsElem))
                        {
                            foreach (var prop in argsElem.EnumerateObject())
                            {
                                argsDict[prop.Name] = prop.Value.GetString();
                            }
                        }

                        update.Contents.Add(new FunctionCallContent(name, name, argsDict));
                    }
                }

                return update;
            }
        }

        return null;
    }

    public void Dispose()
    {
    }
}
