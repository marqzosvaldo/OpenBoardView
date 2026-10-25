namespace OpenBoardView.AI.Persistence;

using OpenBoardView.AI.Models;

public interface IPersistenceService
{
    Task InitializeAsync();

    // Workspaces
    Task<string> InsertWorkspaceAsync(string name, string? description, string? workspaceId = null);
    Task<List<WorkspaceSummaryDto>> GetAllWorkspacesAsync();
    Task<WorkspaceDetailDto?> GetWorkspaceByIdAsync(string workspaceId);
    Task DeleteWorkspaceAsync(string workspaceId);
    Task AddWorkspaceFileAsync(WorkspaceFileDto file);
    Task<List<WorkspaceFileDto>> GetWorkspaceFilesAsync(string workspaceId);
    Task DeleteWorkspaceFileAsync(string fileId);

    // Boards & Cache (< 0.1s startup)
    Task SaveBoardCacheAsync(string boardId, string workspaceId, string modelName, string brdHash, string pdfHash, List<ComponentCacheDto> components, string? cachedIndexJson = null);
    Task<BoardCacheDto?> GetBoardCacheByHashesAsync(string brdHash, string pdfHash);
    Task<BoardCacheDto?> GetBoardCacheByIdAsync(string boardId);
    Task<List<ComponentCacheDto>> GetCachedComponentsAsync(string boardId);

    // Repair History & Statistics
    Task RecordRepairAsync(RepairRecordDto repair);
    Task<List<ComponentFailureStat>> GetTopFailingComponentsAsync(string boardId, string symptomKey);
    Task<List<RepairRecordDto>> GetUnsyncedRepairsAsync();
    Task MarkRepairsAsSyncedAsync(List<string> repairIds);
    Task<List<RepairRecordDto>> GetRepairsByBoardAsync(string boardId);

    // Diagnostic Sessions (Pause / Resume)
    Task<string> SaveDiagnosticSessionAsync(DiagnosticSessionDto session);
    Task<DiagnosticSessionDto?> GetActiveSessionAsync(string workspaceId, string boardId);
    Task UpdateSessionProgressAsync(string sessionId, int currentStepIndex, string status, string? stepMeasurementsJson);

    // App Settings & Active Workspace State
    Task SetSettingAsync(string key, string value);
    Task<string?> GetSettingAsync(string key);

    // Curated Diagnostic Presets (Universal Multi-Model)
    Task SaveDiagnosticPresetAsync(DiagnosticPresetDto preset);
    Task<DiagnosticPresetDto?> GetDiagnosticPresetAsync(string boardId, string symptomKey);
    Task<List<DiagnosticPresetDto>> GetDiagnosticPresetsByBoardAsync(string boardId);
    Task UpdateComponentPdfPageAsync(string boardId, string componentId, int pdfPage);

    // Vector Embeddings (sqlite-vec)
    bool IsVectorExtensionLoaded { get; }
    Task SaveRepairEmbeddingAsync(string repairId, float[] vector);
    Task<List<string>> SearchSimilarRepairsAsync(float[] queryVector, int topK = 5);
}
