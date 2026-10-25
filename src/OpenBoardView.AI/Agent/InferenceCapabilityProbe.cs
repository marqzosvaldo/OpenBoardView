using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;

namespace OpenBoardView.AI.Agent;

public record InferenceCapabilityDto(
    string ActiveTier,       // "Tier1_OllamaLocal", "Tier2_GeminiCloud", "Tier3_DeterministicLocal"
    string Provider,         // "Ollama", "Gemini", "Deterministic"
    string Model,            // "gemini-2.5-flash", "gemini-3.1-flash-lite", "qwen2.5-coder:7b"
    string DisplayTitle,     // "gemini-2.5-flash", "qwen2.5-coder:7b"
    string BadgeColor,       // "#22c55e" (verde), "#ef4444" (rojo), "#f59e0b" (amarillo)
    bool IsOnline,           // true = Verde online, false = Rojo offline
    bool IsAvailable,        // compatibilidad hacia atrás
    bool IsLocal,            // true si corre local en GPU/CPU
    string? ErrorMessage,    // detalle de falla si está offline
    List<string>? AvailableModels, // modelos detectados
    string Description);

/// <summary>
/// Sondeador y detector dinámico de capacidades de hardware e inferencia de IA.
/// Valida en tiempo real la conectividad con Ollama (local) y Google Gemini (nube con cascada).
/// Soporta reintentos interactivos, actualización de API Key en caliente y conmutación de estado Verde/Rojo.
/// </summary>
public class InferenceCapabilityProbe
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _config;
    private InferenceCapabilityDto? _cachedStatus;
    private DateTime _lastProbeTime = DateTime.MinValue;
    private string? _overrideGeminiKey;

    public InferenceCapabilityProbe(HttpClient httpClient, IConfiguration config)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    public void SetGeminiApiKey(string newKey)
    {
        _overrideGeminiKey = newKey?.Trim();
        _cachedStatus = null; // Invalidar caché de inmediato
        _lastProbeTime = DateTime.MinValue;
    }

    public string GetEffectiveGeminiApiKey()
    {
        if (!string.IsNullOrWhiteSpace(_overrideGeminiKey))
            return _overrideGeminiKey;

        var configKey = _config["AiProvider:Gemini:ApiKey"];
        if (string.IsNullOrWhiteSpace(configKey) || configKey.StartsWith("TU_API_KEY") || configKey.StartsWith("YOUR_"))
        {
            configKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY") ?? configKey ?? string.Empty;
        }
        return configKey;
    }

    public async Task<InferenceCapabilityDto> ProbeCapabilitiesAsync(bool forceRefresh = false)
    {
        if (!forceRefresh && _cachedStatus != null && (DateTime.UtcNow - _lastProbeTime).TotalSeconds < 30)
        {
            return _cachedStatus;
        }

        string configuredProvider = _config["AiProvider:Provider"] ?? "Auto";
        string ollamaEndpoint = _config["AiProvider:OllamaOrOpenAI:Endpoint"] ?? "http://localhost:11434";
        string preferredLocalModel = _config["AiProvider:OllamaOrOpenAI:Model"] ?? "qwen2.5-coder:7b";
        string geminiKey = GetEffectiveGeminiApiKey();

        // 1. TIER 1: Sondeo de Ollama local (si no está forzado a Gemini exclusivamente)
        if (!configuredProvider.Equals("Gemini", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var probeUrl = ollamaEndpoint.TrimEnd('/');
                if (probeUrl.EndsWith("/api/chat", StringComparison.OrdinalIgnoreCase))
                    probeUrl = probeUrl.Substring(0, probeUrl.Length - 9);

                using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
                var probeResp = await _httpClient.GetAsync($"{probeUrl}/api/tags", cts.Token);
                if (probeResp.IsSuccessStatusCode)
                {
                    var json = await probeResp.Content.ReadAsStringAsync(cts.Token);
                    using var doc = JsonDocument.Parse(json);
                    var localModels = new List<string>();
                    if (doc.RootElement.TryGetProperty("models", out var modelsArr))
                    {
                        foreach (var m in modelsArr.EnumerateArray())
                        {
                            if (m.TryGetProperty("name", out var nameProp))
                            {
                                var n = nameProp.GetString();
                                if (!string.IsNullOrWhiteSpace(n)) localModels.Add(n);
                            }
                        }
                    }

                    if (localModels.Count > 0)
                    {
                        string selectedModel = localModels.FirstOrDefault(m => m.Contains(preferredLocalModel, StringComparison.OrdinalIgnoreCase))
                                               ?? localModels.FirstOrDefault(m => m.Contains("qwen", StringComparison.OrdinalIgnoreCase) || m.Contains("llama", StringComparison.OrdinalIgnoreCase) || m.Contains("gemma", StringComparison.OrdinalIgnoreCase))
                                               ?? localModels.First();

                        _cachedStatus = new InferenceCapabilityDto(
                            ActiveTier: "Tier1_OllamaLocal",
                            Provider: "Ollama",
                            Model: selectedModel,
                            DisplayTitle: selectedModel,
                            BadgeColor: "#22c55e",
                            IsOnline: true,
                            IsAvailable: true,
                            IsLocal: true,
                            ErrorMessage: null,
                            AvailableModels: localModels,
                            Description: $"Inferencia local activa con {selectedModel} (0 tokens, 100% privado)."
                        );
                        _lastProbeTime = DateTime.UtcNow;
                        return _cachedStatus;
                    }
                }
            }
            catch
            {
                // Ollama no disponible en esta máquina
            }
        }

        // 2. TIER 2: Verificación de Google Gemini en la Nube
        var candidateGeminiModels = new List<string> { "gemini-2.5-flash", "gemini-3.1-flash-lite", "gemini-2.0-flash" };
        var configuredGeminiModel = _config["AiProvider:Gemini:Model"] ?? "gemini-2.5-flash";
        if (!candidateGeminiModels.Contains(configuredGeminiModel, StringComparer.OrdinalIgnoreCase))
        {
            candidateGeminiModels.Insert(0, configuredGeminiModel);
        }

        if (string.IsNullOrWhiteSpace(geminiKey) || geminiKey.StartsWith("TU_API_KEY") || geminiKey.StartsWith("YOUR_"))
        {
            // Sin API Key -> OFFLINE (Rojo)
            _cachedStatus = new InferenceCapabilityDto(
                ActiveTier: "Tier2_GeminiCloud",
                Provider: "Gemini",
                Model: candidateGeminiModels[0],
                DisplayTitle: $"{candidateGeminiModels[0]}",
                BadgeColor: "#ef4444",
                IsOnline: false,
                IsAvailable: false,
                IsLocal: false,
                ErrorMessage: "API Key de Gemini no configurada o vacía",
                AvailableModels: candidateGeminiModels,
                Description: "API Key de Gemini no configurada. Haz clic en el chip para reintentar o conectarte."
            );
            _lastProbeTime = DateTime.UtcNow;
            return _cachedStatus;
        }

        // Prueba activa de conectividad con la API de Google Gemini (models list)
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var probeUrl = $"https://generativelanguage.googleapis.com/v1beta/models?key={geminiKey}";
            var resp = await _httpClient.GetAsync(probeUrl, cts.Token);

            if (resp.IsSuccessStatusCode)
            {
                var content = await resp.Content.ReadAsStringAsync(cts.Token);
                using var doc = JsonDocument.Parse(content);
                var onlineModels = new List<string>();

                if (doc.RootElement.TryGetProperty("models", out var modelsArr))
                {
                    foreach (var m in modelsArr.EnumerateArray())
                    {
                        if (m.TryGetProperty("name", out var n))
                        {
                            var raw = n.GetString() ?? "";
                            if (raw.StartsWith("models/")) raw = raw.Substring(7);
                            onlineModels.Add(raw);
                        }
                    }
                }

                var activeModel = candidateGeminiModels.FirstOrDefault(c => onlineModels.Any(o => o.Equals(c, StringComparison.OrdinalIgnoreCase)))
                                  ?? candidateGeminiModels[0];

                _cachedStatus = new InferenceCapabilityDto(
                    ActiveTier: "Tier2_GeminiCloud",
                    Provider: "Gemini",
                    Model: activeModel,
                    DisplayTitle: activeModel,
                    BadgeColor: "#22c55e",
                    IsOnline: true,
                    IsAvailable: true,
                    IsLocal: false,
                    ErrorMessage: null,
                    AvailableModels: onlineModels.Count > 0 ? onlineModels : candidateGeminiModels,
                    Description: $"Conexión activa con Google Gemini ({activeModel}). Respaldo en cascada disponible."
                );
                _lastProbeTime = DateTime.UtcNow;
                return _cachedStatus;
            }
            else
            {
                var errorBody = await resp.Content.ReadAsStringAsync(cts.Token);
                string errorMsg = (int)resp.StatusCode == 400 
                    ? "API Key inválida o revocada (HTTP 400)" 
                    : (int)resp.StatusCode == 429 
                        ? "Límite de cuota excedido (HTTP 429)" 
                        : $"Error de autenticación con Google ({resp.StatusCode})";

                _cachedStatus = new InferenceCapabilityDto(
                    ActiveTier: "Tier2_GeminiCloud",
                    Provider: "Gemini",
                    Model: candidateGeminiModels[0],
                    DisplayTitle: $"{candidateGeminiModels[0]}",
                    BadgeColor: "#ef4444",
                    IsOnline: false,
                    IsAvailable: false,
                    IsLocal: false,
                    ErrorMessage: errorMsg,
                    AvailableModels: candidateGeminiModels,
                    Description: $"{errorMsg}. Haz clic para reintentar o ingresar una nueva API Key."
                );
                _lastProbeTime = DateTime.UtcNow;
                return _cachedStatus;
            }
        }
        catch (Exception ex)
        {
            _cachedStatus = new InferenceCapabilityDto(
                ActiveTier: "Tier2_GeminiCloud",
                Provider: "Gemini",
                Model: candidateGeminiModels[0],
                DisplayTitle: $"{candidateGeminiModels[0]}",
                BadgeColor: "#ef4444",
                IsOnline: false,
                IsAvailable: false,
                IsLocal: false,
                ErrorMessage: $"Fallo de red: {ex.Message}",
                AvailableModels: candidateGeminiModels,
                Description: "No se pudo contactar a Google Gemini. Haz clic en Reintentar para probar nuevamente."
            );
            _lastProbeTime = DateTime.UtcNow;
            return _cachedStatus;
        }
    }

    public InferenceCapabilityDto GetCurrentStatus()
    {
        return _cachedStatus ?? new InferenceCapabilityDto(
            ActiveTier: "Tier2_GeminiCloud",
            Provider: "Gemini",
            Model: "gemini-2.5-flash",
            DisplayTitle: "gemini-2.5-flash",
            BadgeColor: "#ef4444",
            IsOnline: false,
            IsAvailable: false,
            IsLocal: false,
            ErrorMessage: "No verificado",
            AvailableModels: new List<string> { "gemini-2.5-flash", "gemini-3.1-flash-lite", "gemini-2.0-flash" },
            Description: "Estado no verificado. Presiona reintentar.");
    }
}
