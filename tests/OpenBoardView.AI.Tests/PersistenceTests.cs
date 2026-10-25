using OpenBoardView.AI.Models;
using OpenBoardView.AI.Persistence;

namespace OpenBoardView.AI.Tests;

public class PersistenceTests : IDisposable
{
    private readonly string _testDbPath;
    private readonly SqlitePersistenceService _persistence;

    public PersistenceTests()
    {
        _testDbPath = Path.Combine(Path.GetTempPath(), $"obv_test_{Guid.NewGuid():N}.db");
        _persistence = new SqlitePersistenceService(_testDbPath);
        _persistence.InitializeAsync().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        try
        {
            if (File.Exists(_testDbPath)) File.Delete(_testDbPath);
            var shm = _testDbPath + "-shm";
            var wal = _testDbPath + "-wal";
            if (File.Exists(shm)) File.Delete(shm);
            if (File.Exists(wal)) File.Delete(wal);
        }
        catch
        {
            // Limpieza pasiva
        }
    }

    [Fact]
    public async Task Workspace_CrudLifecycle_WorksCorrectly()
    {
        // 1. Crear workspace
        string wsId = await _persistence.InsertWorkspaceAsync("MacBook Pro A1278", "820-3115 logic board case");
        Assert.False(string.IsNullOrWhiteSpace(wsId));

        // 2. Obtener lista
        var all = await _persistence.GetAllWorkspacesAsync();
        Assert.Contains(all, w => w.WorkspaceId == wsId && w.Name == "MacBook Pro A1278");

        // 3. Agregar archivos
        var file1 = new WorkspaceFileDto(
            FileId: Guid.NewGuid().ToString("D"),
            WorkspaceId: wsId,
            FileName: "820-3115-B.brd",
            FilePath: $"Data/Workspaces/{wsId}/820-3115-B.brd",
            FileType: "PHYSICAL_CAD",
            FileHash: "sha256_mock_brd",
            FileSizeByte: 102400
        );
        var file2 = new WorkspaceFileDto(
            FileId: Guid.NewGuid().ToString("D"),
            WorkspaceId: wsId,
            FileName: "820-3115_Schematics.pdf",
            FilePath: $"Data/Workspaces/{wsId}/820-3115_Schematics.pdf",
            FileType: "SCHEMATIC_PRIMARY",
            FileHash: "sha256_mock_pdf",
            FileSizeByte: 512000
        );

        await _persistence.AddWorkspaceFileAsync(file1);
        await _persistence.AddWorkspaceFileAsync(file2);

        var files = await _persistence.GetWorkspaceFilesAsync(wsId);
        Assert.Equal(2, files.Count);

        // 4. Detalle
        var detail = await _persistence.GetWorkspaceByIdAsync(wsId);
        Assert.NotNull(detail);
        Assert.Equal(2, detail.Files.Count);

        // 5. Borrado
        await _persistence.DeleteWorkspaceAsync(wsId);
        var afterDelete = await _persistence.GetWorkspaceByIdAsync(wsId);
        Assert.Null(afterDelete);

        var filesAfterDelete = await _persistence.GetWorkspaceFilesAsync(wsId);
        Assert.Empty(filesAfterDelete);
    }

    [Fact]
    public async Task BoardCache_InstantStartup_StoresAndRetrieves()
    {
        string wsId = await _persistence.InsertWorkspaceAsync("iPhone 5 Test", "Test board cache");
        string boardId = "820-3141-B";
        string brdHash = "hash_brd_123";
        string pdfHash = "hash_pdf_456";

        var comps = new List<ComponentCacheDto>
        {
            new("U7000", boardId, "TOP", "Q2", 15, 28),
            new("C7513", boardId, "BOTTOM", "Q1", 22, 2),
            new("L7510", boardId, "BOTTOM", "Q1", 22, 2)
        };

        await _persistence.SaveBoardCacheAsync(boardId, wsId, "iPhone 5 Logic Board", brdHash, pdfHash, comps, "{\"mock\": true}");

        var cached = await _persistence.GetBoardCacheByHashesAsync(brdHash, pdfHash);
        Assert.NotNull(cached);
        Assert.Equal(boardId, cached.BoardId);
        Assert.Equal("{\"mock\": true}", cached.CachedIndexJson);

        var cachedComps = await _persistence.GetCachedComponentsAsync(boardId);
        Assert.Equal(3, cachedComps.Count);
        Assert.Contains(cachedComps, c => c.ComponentId == "U7000" && c.Quadrant == "Q2" && c.PdfPage == 15);
    }

    [Fact]
    public async Task RepairHistory_ProbabilityCalculation_OrdersByFailureRate()
    {
        string wsId = await _persistence.InsertWorkspaceAsync("Statistics WS", null);
        string boardId = "820-3115-B";
        string symptom = "NO_POWER_200MA";

        // Registrar 3 fallas en C7513 y 1 falla en U7000 para el mismo síntoma
        await _persistence.RecordRepairAsync(new RepairRecordDto
        {
            WorkspaceId = wsId,
            BoardId = boardId,
            SymptomKey = symptom,
            FaultyComponent = "C7513",
            FaultType = "SHORT_TO_GND",
            MeasuredImpedance = 0.3,
            MeasuredVoltage = 0.0,
            Notes = "Capacitor en corto a tierra en PPVCORE_S0_CPU"
        });
        await _persistence.RecordRepairAsync(new RepairRecordDto
        {
            WorkspaceId = wsId,
            BoardId = boardId,
            SymptomKey = symptom,
            FaultyComponent = "C7513",
            FaultType = "SHORT_TO_GND",
            MeasuredImpedance = 0.2
        });
        await _persistence.RecordRepairAsync(new RepairRecordDto
        {
            WorkspaceId = wsId,
            BoardId = boardId,
            SymptomKey = symptom,
            FaultyComponent = "C7513",
            FaultType = "SHORT_TO_GND",
            MeasuredImpedance = 0.4
        });
        await _persistence.RecordRepairAsync(new RepairRecordDto
        {
            WorkspaceId = wsId,
            BoardId = boardId,
            SymptomKey = symptom,
            FaultyComponent = "U7000",
            FaultType = "IC_FAILURE",
            MeasuredVoltage = 0.0
        });

        var stats = await _persistence.GetTopFailingComponentsAsync(boardId, symptom);
        Assert.Equal(2, stats.Count);

        // C7513 debe ser el #1 con 3 fallas y 75%
        Assert.Equal("C7513", stats[0].ComponentId);
        Assert.Equal(3, stats[0].FailureCount);
        Assert.Equal(75.0, stats[0].ProbabilityPercentage, precision: 1);

        // U7000 debe ser el #2 con 1 falla y 25%
        Assert.Equal("U7000", stats[1].ComponentId);
        Assert.Equal(1, stats[1].FailureCount);
        Assert.Equal(25.0, stats[1].ProbabilityPercentage, precision: 1);
    }

    [Fact]
    public async Task DiagnosticSessions_PauseAndResume_PreservesState()
    {
        string wsId = await _persistence.InsertWorkspaceAsync("Session WS", null);
        string boardId = "820-3115-B";

        var session = new DiagnosticSessionDto
        {
            WorkspaceId = wsId,
            BoardId = boardId,
            TechnicianName = "Alex",
            CurrentStepIndex = 3,
            Status = "IN_PROGRESS",
            StepMeasurementsJson = "[{\"stepNumber\":1,\"measuredVoltage\":12.56}]"
        };

        string sessionId = await _persistence.SaveDiagnosticSessionAsync(session);
        Assert.False(string.IsNullOrWhiteSpace(sessionId));

        var active = await _persistence.GetActiveSessionAsync(wsId, boardId);
        Assert.NotNull(active);
        Assert.Equal(sessionId, active.SessionId);
        Assert.Equal(3, active.CurrentStepIndex);
        Assert.Equal("Alex", active.TechnicianName);

        // Actualizar avance
        await _persistence.UpdateSessionProgressAsync(sessionId, 4, "IN_PROGRESS", "[{\"stepNumber\":2,\"measuredVoltage\":0.0}]");

        var updated = await _persistence.GetActiveSessionAsync(wsId, boardId);
        Assert.NotNull(updated);
        Assert.Equal(4, updated.CurrentStepIndex);
    }

    [Fact]
    public async Task UnsyncedRepairs_CanBeRetrievedAndMarkedAsSynced()
    {
        string wsId = await _persistence.InsertWorkspaceAsync("Telemetry WS", null);
        string boardId = "820-3115-B";

        var r1 = new RepairRecordDto
        {
            RepairId = Guid.NewGuid().ToString("D"),
            WorkspaceId = wsId,
            BoardId = boardId,
            SymptomKey = "PPBUS_G3H_MISSING",
            FaultyComponent = "Q7055",
            FaultType = "SHORT_TO_GND"
        };
        await _persistence.RecordRepairAsync(r1);

        var unsynced = await _persistence.GetUnsyncedRepairsAsync();
        Assert.Contains(unsynced, r => r.RepairId == r1.RepairId);

        await _persistence.MarkRepairsAsSyncedAsync(new List<string> { r1.RepairId! });

        var afterSync = await _persistence.GetUnsyncedRepairsAsync();
        Assert.DoesNotContain(afterSync, r => r.RepairId == r1.RepairId);
    }
}
