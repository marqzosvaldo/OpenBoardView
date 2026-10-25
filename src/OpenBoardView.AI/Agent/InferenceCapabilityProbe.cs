using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;

namespace OpenBoardView.AI.Agent;

public record InferenceCapabilityDto(
    string ActiveTier,       // "Tier1_OllamaLocal", "Tier2_GeminiCloud", "Tier3_DeterministicLocal"
    string Provider,         // "Ollama", "Gemini", "Deterministic"
    string Model,            // "gemma2:2b", "gemini-3.1-flash-lite", "C# Topología BRD"
    string DisplayTitle,     // "IA Local (Ollama - gemma2:2b)", "IA Cloud (Gemini Cascada)", "Modo Autónomo Local (C#)"
    string BadgeColor,       // "#22c55e", "#3b82f6", "#eab308"
    bool IsLocal,
    bool IsAvailable,
    string Description);

/// <summary>
/// Sondeador y detector dinámico de capacidades de hardware e inferencia de IA.
/// Determina en tiempo de ejecución si la máquina soporta inferencia neural local (Ollama en Metal/CUDA),
/// si debe operar en la nube con respaldo multi-modelo (Google Gemini), o si debe operar en Modo Autónomo
/// Determinista sin conexión a internet ni consumo de recursos.
/// </summary>
public class InferenceCapabilityProbe
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _config;
    private InferenceCapabilityDto? _cachedStatus;
    private DateTime _lastProbeTime = DateTime.MinValue;

    public InferenceCapabilityProbe(HttpClient httpClient, IConfiguration config)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    public async Task<InferenceCapabilityDto> ProbeCapabilitiesAsync(bool forceRefresh = false)
    {
        if (!forceRefresh && _cachedStatus != null && (DateTime.UtcNow - _lastProbeTime).TotalSeconds < 30)
        {
            return _cachedStatus;
        }

        string configuredProvider = _config["AiProvider:Provider"] ?? "Auto";
        string ollamaEndpoint = _config["AiProvider:OllamaOrOpenAI:Endpoint"] ?? "http://localhost:11434";
        string preferredLocalModel = _config["AiProvider:OllamaOrOpenAI:Model"] ?? "gemma2:2b";
        string geminiKey = _config["AiProvider:Gemini:ApiKey"] ?? string.Empty;
        if (string.IsNullOrWhiteSpace(geminiKey) || geminiKey.StartsWith("TU_API_KEY") || geminiKey.StartsWith("YOUR_"))
        {
            geminiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY") ?? geminiKey;
        }

        // 1. TIER 1: Sondeo de Ollama local (si Provider es Auto u Ollama)
        if (!configuredProvider.Equals("Gemini", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var probeUrl = ollamaEndpoint.TrimEnd('/');
                if (probeUrl.EndsWith("/api/chat", StringComparison.OrdinalIgnoreCase))
                    probeUrl = probeUrl.Substring(0, probeUrl.Length - 9);

                using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
                var probeResp = await _httpClient.GetAsync($"{probeUrl}/api/tags", cts.Token);
                if (probeResp.IsSuccessStatusCode)
                {
                    var json = await probeResp.Content.ReadAsStringAsync(cts.Token);
                    using var doc = JsonDocument.Parse(json);
                    var models = new List<string>();
                    if (doc.RootElement.TryGetProperty("models", out var modelsArr))
                    {
                        foreach (var m in modelsArr.EnumerateArray())
                        {
                            if (m.TryGetProperty("name", out var nameProp))
                                models.Add(nameProp.GetString() ?? string.Empty);
                        }
                    }

                    string selectedModel = models.FirstOrDefault(m => m.Contains(preferredLocalModel, StringComparison.OrdinalIgnoreCase))
                                           ?? models.FirstOrDefault(m => m.Contains("gemma", StringComparison.OrdinalIgnoreCase) || m.Contains("llama", StringComparison.OrdinalIgnoreCase) || m.Contains("qwen", StringComparison.OrdinalIgnoreCase))
                                           ?? models.FirstOrDefault()
                                           ?? preferredLocalModel;

                    _cachedStatus = new InferenceCapabilityDto(
                        ActiveTier: "Tier1_OllamaLocal",
                        Provider: "Ollama",
                        Model: selectedModel,
                        DisplayTitle: $"IA Local (Ollama - {selectedModel})",
                        BadgeColor: "#22c55e",
                        IsLocal: true,
                        IsAvailable: true,
                        Description: "Inferencia neural ejecutada localmente vía Metal/CUDA. 0 tokens, privacidad total y sin requerir conexión.");
                    _lastProbeTime = DateTime.UtcNow;
                    return _cachedStatus;
                }
            }
            catch
            {
                // Ollama no está activo en esta máquina
            }
        }

        // 2. TIER 2: Verificación de Google Gemini en la Nube
        if (!string.IsNullOrWhiteSpace(geminiKey) && !geminiKey.StartsWith("TU_API_KEY"))
        {
            _cachedStatus = new InferenceCapabilityDto(
                ActiveTier: "Tier2_GeminiCloud",
                Provider: "Gemini",
                Model: "gemini-3.1-flash-lite (Cascada)",
                DisplayTitle: "IA Cloud (Gemini Cascada)",
                BadgeColor: "#3b82f6",
                IsLocal: false,
                IsAvailable: true,
                Description: "Inferencia en la nube con Google Gemini. Respaldo automático en cascada (3.1 -> 2.5 -> 2.0).");
            _lastProbeTime = DateTime.UtcNow;
            return _cachedStatus;
        }

        // 3. TIER 3: Modo Autónomo Local Determinista (C# / BRD)
        _cachedStatus = new InferenceCapabilityDto(
            ActiveTier: "Tier3_DeterministicLocal",
            Provider: "Deterministic",
            Model: "Topología C# .BRD",
            DisplayTitle: "Modo Autónomo Local (C# / BRD)",
            BadgeColor: "#eab308",
            IsLocal: true,
            IsAvailable: true,
            Description: "Modo local determinista activo. El Stepper y el BoardView operan directamente con la topología física (.brd) sin internet.");
        _lastProbeTime = DateTime.UtcNow;
        return _cachedStatus;
    }

    public InferenceCapabilityDto GetCurrentStatus()
    {
        return _cachedStatus ?? new InferenceCapabilityDto(
            ActiveTier: "Tier2_GeminiCloud",
            Provider: "Gemini",
            Model: "gemini-3.1-flash-lite (Cascada)",
            DisplayTitle: "IA Cloud (Gemini Cascada)",
            BadgeColor: "#3b82f6",
            IsLocal: false,
            IsAvailable: true,
            Description: "Inferencia en la nube con Google Gemini.");
    }
}
