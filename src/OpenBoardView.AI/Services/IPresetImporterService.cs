namespace OpenBoardView.AI.Services;

using OpenBoardView.AI.Knowledge;
using OpenBoardView.AI.Models;

/// <summary>
/// Servicio universal de importación y gestión de plantillas curadas de diagnóstico (Presets) para OpenBoardView AI v4.
/// Permite cargar especificaciones JSON por modelo de placa y alimentar tanto el almacenamiento SQLite
/// como el motor en memoria del depurador (CircuitMazeDebugger / FlowKnowledgeBase).
/// </summary>
public interface IPresetImporterService
{
    Task<bool> ImportPresetFromJsonAsync(string jsonFilePath);
    Task<List<StepperNodeDto>> GetCuratedStepperFlowAsync(string boardId, string symptomKey);
    Task<DiagnosticPresetDto?> GetPresetAsync(string boardId, string symptomKey);
    Task<List<DiagnosticPresetDto>> GetPresetsForBoardAsync(string boardId);
    FlowDef? ConvertPresetToFlowDef(DiagnosticPresetDto preset);
}
