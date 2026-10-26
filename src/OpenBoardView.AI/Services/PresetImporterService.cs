using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenBoardView.AI.Knowledge;
using OpenBoardView.AI.Models;
using OpenBoardView.AI.Persistence;

namespace OpenBoardView.AI.Services;

/// <summary>
/// Implementación universal del servicio de importación de presets de diagnóstico para OpenBoardView AI v4.
/// Lee archivos JSON de inicialización por modelo (ej. Apple Silicon M3 820-02757) y los persiste en SQLite
/// integrándolos directamente con el depurador y el simulador sin sobreescribir hashes ni workspaces existentes.
/// </summary>
public class PresetImporterService : IPresetImporterService
{
    private readonly IPersistenceService _persistence;
    private readonly IHostEnvironment _env;
    private readonly ILogger<PresetImporterService> _logger;

    public PresetImporterService(
        IPersistenceService persistence,
        IHostEnvironment env,
        ILogger<PresetImporterService> logger)
    {
        _persistence = persistence;
        _env = env;
        _logger = logger;
    }

    public async Task<bool> ImportPresetFromJsonAsync(string jsonFilePath)
    {
        string fullPath = Path.IsPathRooted(jsonFilePath)
            ? jsonFilePath
            : Path.GetFullPath(Path.Combine(_env.ContentRootPath, jsonFilePath));

        if (!File.Exists(fullPath))
        {
            _logger.LogWarning("[PresetImporter] El archivo de plantilla no existe en: {Path}", fullPath);
            return false;
        }

        try
        {
            string jsonContent = await File.ReadAllTextAsync(fullPath);
            using var doc = JsonDocument.Parse(jsonContent);
            var root = doc.RootElement;

            string boardId = root.GetProperty("boardId").GetString()!;
            string modelName = root.TryGetProperty("modelName", out var m) ? m.GetString() ?? boardId : boardId;
            string platformCode = root.TryGetProperty("platformCode", out var p) ? p.GetString() ?? string.Empty : string.Empty;

            if (!root.TryGetProperty("diagnosticPresets", out var presetsArray))
            {
                _logger.LogWarning("[PresetImporter] El JSON no contiene 'diagnosticPresets'.");
                return false;
            }

            int count = 0;
            foreach (var presetEl in presetsArray.EnumerateArray())
            {
                string symptomKey = presetEl.GetProperty("symptomKey").GetString()!;
                string symptomDesc = presetEl.TryGetProperty("symptomDescription", out var d) ? d.GetString() ?? string.Empty : string.Empty;

                double shortThreshold = 50.0;
                double normalMin = 1000.0;
                if (presetEl.TryGetProperty("coldImpedanceThresholds", out var thresh))
                {
                    if (thresh.TryGetProperty("shortCircuitThresholdOhms", out var st)) shortThreshold = st.GetDouble();
                    if (thresh.TryGetProperty("normalMinOhms", out var nm)) normalMin = nm.GetDouble();
                }

                string presetId = $"{boardId}_{symptomKey}";
                string rawPresetJson = presetEl.GetRawText();

                var presetDto = new DiagnosticPresetDto(
                    PresetId: presetId,
                    BoardId: boardId,
                    SymptomKey: symptomKey,
                    SymptomDescription: symptomDesc,
                    ColdShortThresholdReal: shortThreshold,
                    ColdNormalMinReal: normalMin,
                    PresetJson: rawPresetJson,
                    UpdatedAt: DateTime.UtcNow.ToString("o")
                );

                await _persistence.SaveDiagnosticPresetAsync(presetDto);
                count++;

                // Actualizar números de página esquemática en Components de SQLite
                if (presetEl.TryGetProperty("stepperNodes", out var nodesEl))
                {
                    foreach (var node in nodesEl.EnumerateArray())
                    {
                        if (node.TryGetProperty("componentId", out var cIdProp) &&
                            node.TryGetProperty("schematicPage", out var pageProp))
                        {
                            string compId = cIdProp.GetString()!;
                            int page = pageProp.GetInt32();
                            await _persistence.UpdateComponentPdfPageAsync(boardId, compId, page);
                        }
                    }
                }
            }

            _logger.LogInformation("[PresetImporter] ✅ Plantilla '{Model}' ({BoardId}) importada exitosamente ({Count} presets en SQLite).",
                modelName, boardId, count);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[PresetImporter] Error al procesar archivo JSON de plantilla {Path}: {Message}", fullPath, ex.Message);
            return false;
        }
    }

    public async Task<List<StepperNodeDto>> GetCuratedStepperFlowAsync(string boardId, string symptomKey)
    {
        var preset = await _persistence.GetDiagnosticPresetAsync(boardId, symptomKey);
        if (preset == null || string.IsNullOrWhiteSpace(preset.PresetJson))
        {
            return new List<StepperNodeDto>();
        }

        try
        {
            using var doc = JsonDocument.Parse(preset.PresetJson);
            if (!doc.RootElement.TryGetProperty("stepperNodes", out var nodesArray))
            {
                return new List<StepperNodeDto>();
            }

            var result = new List<StepperNodeDto>();
            foreach (var node in nodesArray.EnumerateArray())
            {
                result.Add(new StepperNodeDto(
                    StepNumber: node.GetProperty("stepNumber").GetInt32(),
                    ComponentId: node.GetProperty("componentId").GetString()!,
                    Pin: node.GetProperty("pin").GetString()!,
                    SignalName: node.GetProperty("signalName").GetString()!,
                    Mode: node.GetProperty("mode").GetString()!,
                    ExpectedNominal: node.GetProperty("expectedNominal").GetString()!,
                    FaultNominal: node.GetProperty("faultNominal").GetString()!,
                    SchematicPage: node.GetProperty("schematicPage").GetInt32(),
                    Description: node.GetProperty("description").GetString()!
                ));
            }
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[PresetImporter] Error al deserializar stepperNodes para {Board}_{Symptom}.", boardId, symptomKey);
            return new List<StepperNodeDto>();
        }
    }

    public async Task<DiagnosticPresetDto?> GetPresetAsync(string boardId, string symptomKey)
    {
        return await _persistence.GetDiagnosticPresetAsync(boardId, symptomKey);
    }

    public async Task<List<DiagnosticPresetDto>> GetPresetsForBoardAsync(string boardId)
    {
        return await _persistence.GetDiagnosticPresetsByBoardAsync(boardId);
    }

    public FlowDef? ConvertPresetToFlowDef(DiagnosticPresetDto preset)
    {
        if (preset == null || string.IsNullOrWhiteSpace(preset.PresetJson)) return null;

        try
        {
            using var doc = JsonDocument.Parse(preset.PresetJson);
            var root = doc.RootElement;
            if (!root.TryGetProperty("stepperNodes", out var nodesArray)) return null;

            var steps = new List<FlowStepDef>();
            string? firstSignal = null;

            foreach (var node in nodesArray.EnumerateArray())
            {
                int stepNum = node.GetProperty("stepNumber").GetInt32();
                string compId = node.GetProperty("componentId").GetString()!;
                string pin = node.GetProperty("pin").GetString()!;
                string signal = node.GetProperty("signalName").GetString()!;
                string mode = node.GetProperty("mode").GetString()!;
                string expectedStr = node.GetProperty("expectedNominal").GetString()!;
                int pNum = node.GetProperty("schematicPage").GetInt32();
                string desc = node.GetProperty("description").GetString()!;

                firstSignal ??= signal;

                double expectedV = 3.80;
                var matchVolt = System.Text.RegularExpressions.Regex.Match(expectedStr, @"(\d+(\.\d+)?)");
                if (matchVolt.Success && double.TryParse(matchVolt.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double parsedV))
                {
                    expectedV = parsedV;
                }

                steps.Add(new FlowStepDef(
                    Component: compId,
                    Stage: $"{stepNum}. Medición en {compId} ({signal})",
                    InputNet: signal,
                    OutputNet: signal,
                    ExpectedVoltage: expectedV,
                    VoltageSource: DataSources.Curated,
                    PinToMeasure: $"Pin {pin} de {compId} ({mode}) [Pág. Esquemático {pNum}]",
                    HowItWorks: desc,
                    FailDiagnosis: $"Línea {signal} ausente (0.00 V). Verificar alimentación y conmutación.",
                    MeasurementMode: mode.Contains("RESISTANCE", StringComparison.OrdinalIgnoreCase) ? MeasurementModes.Resistance : MeasurementModes.Voltage,
                    ExpectedReading: expectedStr
                ));
            }

            var keywords = new List<string>
            {
                preset.SymptomKey.ToLowerInvariant(),
                "pp3v8_aon",
                "3v8",
                "5v",
                "30ma",
                "m3",
                "a2918",
                "no enciende",
                "lr503",
                "l5800"
            };

            return new FlowDef(
                Id: preset.SymptomKey,
                Title: $"Diagnóstico Curado: {preset.SymptomKey}",
                Symptom: preset.SymptomDescription,
                Keywords: keywords,
                Steps: steps,
                TargetNet: firstSignal ?? "PP3V8_AON"
            );
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[PresetImporter] No se pudo convertir Preset '{Id}' a FlowDef.", preset.PresetId);
            return null;
        }
    }
}
