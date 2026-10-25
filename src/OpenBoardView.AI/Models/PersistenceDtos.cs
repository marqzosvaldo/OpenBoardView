namespace OpenBoardView.AI.Models;

public record WorkspaceSummaryDto(
    string WorkspaceId,
    string Name,
    string? Description,
    int FileCount,
    int SyncStatus,
    string UpdatedAt,
    string CreatedAt);

public record WorkspaceDetailDto(
    string WorkspaceId,
    string Name,
    string? Description,
    int SyncStatus,
    string UpdatedAt,
    string CreatedAt,
    List<WorkspaceFileDto> Files,
    List<BoardSummaryDto> Boards);

public record WorkspaceDto(
    string WorkspaceId,
    string Name,
    string? Description,
    int SyncStatus,
    string UpdatedAt,
    string CreatedAt,
    List<WorkspaceFileDto> Files);

public record WorkspaceFileDto(
    string FileId,
    string WorkspaceId,
    string FileName,
    string FilePath,
    string FileType,
    string FileHash,
    long FileSizeByte,
    int SyncStatus = 0,
    string? UpdatedAt = null);

public class FileUploadRequest
{
    public string FileName { get; set; } = string.Empty;
    public byte[] ContentBytes { get; set; } = Array.Empty<byte>();
    public long Length { get; set; }
    public string? ExplicitFileType { get; set; }
}

public record BoardSummaryDto(
    string BoardId,
    string WorkspaceId,
    string ModelName,
    string BrdHash,
    string PdfHash,
    int ComponentCount,
    int SyncStatus,
    string UpdatedAt);

public record BoardCacheDto(
    string BoardId,
    string WorkspaceId,
    string ModelName,
    string BrdHash,
    string PdfHash,
    string? CachedIndexJson,
    int SyncStatus,
    string UpdatedAt);

public record ComponentCacheDto(
    string ComponentId,
    string BoardId,
    string Side,
    string Quadrant,
    int? PdfPage,
    int PinCount,
    int SyncStatus = 0);

public record RepairRecordDto
{
    public string? RepairId { get; init; }
    public string WorkspaceId { get; init; } = string.Empty;
    public string BoardId { get; init; } = string.Empty;
    public string SymptomKey { get; init; } = string.Empty;
    public string FaultyComponent { get; init; } = string.Empty;
    public string FaultType { get; init; } = "SHORT_TO_GND";
    public double? MeasuredImpedance { get; init; }
    public double? MeasuredVoltage { get; init; }
    public string? ResolvedAt { get; init; }
    public string? Notes { get; init; }
    public int SyncStatus { get; init; } = 0;
}

public record ComponentFailureStat
{
    public string ComponentId { get; init; } = string.Empty;
    public int FailureCount { get; init; }
    public double ProbabilityPercentage { get; init; }
}

public record DiagnosticSessionDto
{
    public string? SessionId { get; init; }
    public string WorkspaceId { get; init; } = string.Empty;
    public string BoardId { get; init; } = string.Empty;
    public string? TechnicianName { get; init; }
    public int CurrentStepIndex { get; init; } = 0;
    public string Status { get; init; } = "IN_PROGRESS";
    public string? StepMeasurementsJson { get; init; }
    public int SyncStatus { get; init; } = 0;
    public string? UpdatedAt { get; init; }
}

public record StepMeasurementDto(
    int StepNumber,
    string ComponentId,
    double? MeasuredVoltage,
    double? MeasuredImpedance,
    string Status, // "PASSED", "FAILED", "SKIPPED"
    string? Note);
