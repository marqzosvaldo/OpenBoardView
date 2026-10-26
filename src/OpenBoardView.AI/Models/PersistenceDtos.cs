namespace OpenBoardView.AI.Models;

public record WorkspaceSummaryDto(
    string WorkspaceId,
    string Name,
    string? Description,
    int FileCount,
    int SyncStatus,
    string UpdatedAt,
    string CreatedAt,
    string AnalysisStatus = "NotRequested",
    string? ActivePresetId = null,
    string? AnalysisSummary = null);

public record WorkspaceDetailDto(
    string WorkspaceId,
    string Name,
    string? Description,
    int SyncStatus,
    string UpdatedAt,
    string CreatedAt,
    List<WorkspaceFileDto> Files,
    List<BoardSummaryDto> Boards,
    string AnalysisStatus = "NotRequested",
    string? ActivePresetId = null,
    string? AnalysisSummary = null);

public record WorkspaceDto(
    string WorkspaceId,
    string Name,
    string? Description,
    int SyncStatus,
    string UpdatedAt,
    string CreatedAt,
    List<WorkspaceFileDto> Files,
    string AnalysisStatus = "NotRequested",
    string? ActivePresetId = null,
    string? AnalysisSummary = null);

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

public class StepperNodeDto
{
    public int StepNumber { get; set; }
    public string ComponentId { get; set; } = string.Empty;
    public string Pin { get; set; } = "1";
    public string SignalName { get; set; } = string.Empty;
    public string Mode { get; set; } = "VOLTAGE_DC";
    public string ExpectedNominal { get; set; } = "0.00 V";
    public string FaultNominal { get; set; } = "0.00 V";
    public int SchematicPage { get; set; } = 1;
    public string Description { get; set; } = string.Empty;
    public int OmittedParallelCount { get; set; } = 0;
    public string? Notes { get; set; }
    public List<string>? ParallelComponentIds { get; set; }

    public StepperNodeDto() { }

    public StepperNodeDto(
        int StepNumber,
        string ComponentId,
        string Pin,
        string SignalName,
        string Mode,
        string ExpectedNominal,
        string FaultNominal,
        int SchematicPage,
        string Description,
        int OmittedParallelCount = 0,
        string? Notes = null,
        List<string>? ParallelComponentIds = null)
    {
        this.StepNumber = StepNumber;
        this.ComponentId = ComponentId;
        this.Pin = Pin;
        this.SignalName = SignalName;
        this.Mode = Mode;
        this.ExpectedNominal = ExpectedNominal;
        this.FaultNominal = FaultNominal;
        this.SchematicPage = SchematicPage;
        this.Description = Description;
        this.OmittedParallelCount = OmittedParallelCount;
        this.Notes = Notes;
        this.ParallelComponentIds = ParallelComponentIds;
    }

    public StepperNodeDto Clone() => new()
    {
        StepNumber = this.StepNumber,
        ComponentId = this.ComponentId,
        Pin = this.Pin,
        SignalName = this.SignalName,
        Mode = this.Mode,
        ExpectedNominal = this.ExpectedNominal,
        FaultNominal = this.FaultNominal,
        SchematicPage = this.SchematicPage,
        Description = this.Description,
        OmittedParallelCount = this.OmittedParallelCount,
        Notes = this.Notes,
        ParallelComponentIds = this.ParallelComponentIds != null ? new List<string>(this.ParallelComponentIds) : null
    };
}

public record ColdImpedanceThresholdsDto(
    double ShortCircuitThresholdOhms,
    double NormalMinOhms,
    string? Notes);

public record KeyIntegrateDto(
    string ComponentId,
    string ChipName,
    string Function,
    int SchematicPage);

public record DiagnosticPresetDto(
    string PresetId,
    string BoardId,
    string SymptomKey,
    string SymptomDescription,
    double ColdShortThresholdReal,
    double ColdNormalMinReal,
    string PresetJson,
    string UpdatedAt);
