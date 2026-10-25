using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using OpenBoardView.AI.Models;

namespace OpenBoardView.AI.Persistence;

/// <summary>
/// Implementación de persistencia híbrida (Relacional WAL + Vectorial opcional con sqlite-vec) para OpenBoardView AI v4.
/// Garantiza arquitectura Local-First, inicio ultra-rápido (<0.1s) mediante caché y reordenamiento predictivo del Stepper.
/// </summary>
public class SqlitePersistenceService : IPersistenceService
{
    private readonly string _connectionString;
    private readonly string _dbPath;
    private bool _isVectorExtensionLoaded;

    static SqlitePersistenceService()
    {
        try
        {
            SQLitePCL.Batteries.Init();
        }
        catch
        {
            // Fallback si ya está inicializado
        }
    }

    public SqlitePersistenceService(string dbPath = "Data/openboardview_ai.db")
    {
        _dbPath = dbPath;
        var folder = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(folder) && !Directory.Exists(folder))
        {
            Directory.CreateDirectory(folder);
        }

        var connBuilder = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            DefaultTimeout = 5
        };
        _connectionString = connBuilder.ToString();
    }

    public bool IsVectorExtensionLoaded => _isVectorExtensionLoaded;

    private async Task<SqliteConnection> OpenConnectionAsync()
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
        await pragma.ExecuteNonQueryAsync();

        return connection;
    }

    public async Task InitializeAsync()
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        // 1. Configurar WAL y timeout
        using (var walCmd = connection.CreateCommand())
        {
            walCmd.CommandText = @"
                PRAGMA journal_mode = WAL;
                PRAGMA synchronous = NORMAL;
                PRAGMA foreign_keys = ON;
                PRAGMA busy_timeout = 5000;
            ";
            await walCmd.ExecuteNonQueryAsync();
        }

        // 2. Intentar cargar extensión sqlite-vec con fallback seguro
        try
        {
            connection.EnableExtensions(true);
            connection.LoadExtension("sqlite-vec");
            _isVectorExtensionLoaded = true;
            Console.WriteLine("[Persistencia] Extensión sqlite-vec cargada exitosamente (RAG Vectorial activo).");
        }
        catch (Exception ex)
        {
            _isVectorExtensionLoaded = false;
            Console.WriteLine($"[Persistencia] Aviso: sqlite-vec no cargado (modo relacional WAL activo): {ex.Message}");
        }

        // 3. Crear tablas relacionales principales (DDL v4)
        using (var ddlCmd = connection.CreateCommand())
        {
            ddlCmd.CommandText = @"
                -- 1. Tabla de Espacios de Trabajo (Workspaces)
                CREATE TABLE IF NOT EXISTS Workspaces (
                    WorkspaceId TEXT PRIMARY KEY,
                    Name TEXT NOT NULL,
                    Description TEXT,
                    SyncStatus INTEGER DEFAULT 0,
                    UpdatedAt TEXT NOT NULL DEFAULT (datetime('now')),
                    CreatedAt TEXT NOT NULL DEFAULT (datetime('now'))
                );

                -- 2. Tabla de Archivos Asociados a Espacios (Multi-File Mapping)
                CREATE TABLE IF NOT EXISTS WorkspaceFiles (
                    FileId TEXT PRIMARY KEY,
                    WorkspaceId TEXT NOT NULL,
                    FileName TEXT NOT NULL,
                    FilePath TEXT NOT NULL,
                    FileType TEXT CHECK(FileType IN ('PHYSICAL_CAD', 'SCHEMATIC_PRIMARY', 'SCHEMATIC_ALT', 'AUX_DOC')),
                    FileHash TEXT NOT NULL,
                    FileSizeByte INTEGER NOT NULL,
                    SyncStatus INTEGER DEFAULT 0,
                    UpdatedAt TEXT NOT NULL DEFAULT (datetime('now')),
                    FOREIGN KEY (WorkspaceId) REFERENCES Workspaces(WorkspaceId) ON DELETE CASCADE
                );

                -- 3. Tabla de Placas / Modelos Indexados (Caché de Inicio Instantáneo)
                CREATE TABLE IF NOT EXISTS Boards (
                    BoardId TEXT PRIMARY KEY,
                    WorkspaceId TEXT NOT NULL,
                    ModelName TEXT NOT NULL,
                    BrdHash TEXT NOT NULL,
                    PdfHash TEXT NOT NULL,
                    CachedIndexJson TEXT,
                    SyncStatus INTEGER DEFAULT 0,
                    UpdatedAt TEXT NOT NULL DEFAULT (datetime('now')),
                    FOREIGN KEY (WorkspaceId) REFERENCES Workspaces(WorkspaceId) ON DELETE CASCADE
                );

                -- 4. Caché de Componentes Indexados
                CREATE TABLE IF NOT EXISTS Components (
                    ComponentId TEXT NOT NULL,
                    BoardId TEXT NOT NULL,
                    Side TEXT CHECK(Side IN ('TOP', 'BOTTOM', 'BOTH')),
                    Quadrant TEXT,
                    PdfPage INTEGER,
                    PinCount INTEGER,
                    SyncStatus INTEGER DEFAULT 0,
                    UpdatedAt TEXT NOT NULL DEFAULT (datetime('now')),
                    PRIMARY KEY (BoardId, ComponentId),
                    FOREIGN KEY (BoardId) REFERENCES Boards(BoardId) ON DELETE CASCADE
                );

                -- 5. Historial de Reparaciones (Memoria Estadística)
                CREATE TABLE IF NOT EXISTS RepairHistory (
                    RepairId TEXT PRIMARY KEY,
                    WorkspaceId TEXT NOT NULL,
                    BoardId TEXT NOT NULL,
                    SymptomKey TEXT NOT NULL,
                    FaultyComponent TEXT NOT NULL,
                    FaultType TEXT CHECK(FaultType IN ('SHORT_TO_GND', 'OPEN_CIRCUIT', 'NO_ENABLE', 'IC_FAILURE', 'PCH_DAMAGE')),
                    MeasuredImpedance REAL,
                    MeasuredVoltage REAL,
                    ResolvedAt TEXT NOT NULL DEFAULT (datetime('now')),
                    Notes TEXT,
                    SyncStatus INTEGER DEFAULT 0,
                    UpdatedAt TEXT NOT NULL DEFAULT (datetime('now')),
                    FOREIGN KEY (WorkspaceId) REFERENCES Workspaces(WorkspaceId) ON DELETE CASCADE,
                    FOREIGN KEY (BoardId) REFERENCES Boards(BoardId) ON DELETE CASCADE
                );

                -- 6. Sesiones Diagnósticas Activas (Pausar/Reanudar)
                CREATE TABLE IF NOT EXISTS DiagnosticSessions (
                    SessionId TEXT PRIMARY KEY,
                    WorkspaceId TEXT NOT NULL,
                    BoardId TEXT NOT NULL,
                    TechnicianName TEXT,
                    CurrentStepIndex INTEGER DEFAULT 0,
                    Status TEXT CHECK(Status IN ('IN_PROGRESS', 'RESOLVED', 'ABANDONED')),
                    StepMeasurementsJson TEXT,
                    SyncStatus INTEGER DEFAULT 0,
                    UpdatedAt TEXT NOT NULL DEFAULT (datetime('now')),
                    FOREIGN KEY (WorkspaceId) REFERENCES Workspaces(WorkspaceId) ON DELETE CASCADE,
                    FOREIGN KEY (BoardId) REFERENCES Boards(BoardId) ON DELETE CASCADE
                );

                -- 7. Configuración y Estado Persistente de la Aplicación
                CREATE TABLE IF NOT EXISTS AppSettings (
                    Key TEXT PRIMARY KEY,
                    Value TEXT NOT NULL,
                    UpdatedAt TEXT NOT NULL DEFAULT (datetime('now'))
                );

                -- Índices para búsquedas de alto rendimiento
                CREATE INDEX IF NOT EXISTS idx_workspace_files ON WorkspaceFiles(WorkspaceId);
                CREATE INDEX IF NOT EXISTS idx_boards_hashes ON Boards(BrdHash, PdfHash);
                CREATE INDEX IF NOT EXISTS idx_repair_lookup ON RepairHistory(BoardId, SymptomKey);
                CREATE INDEX IF NOT EXISTS idx_repair_sync ON RepairHistory(SyncStatus);
            ";
            await ddlCmd.ExecuteNonQueryAsync();
        }

        // 4. Crear tabla virtual vectorial únicamente si la extensión sqlite-vec se cargó
        if (_isVectorExtensionLoaded)
        {
            try
            {
                using var vecCmd = connection.CreateCommand();
                vecCmd.CommandText = @"
                    CREATE VIRTUAL TABLE IF NOT EXISTS RepairEmbeddings USING vec0(
                        RepairId TEXT PRIMARY KEY,
                        SymptomVector float[768]
                    );
                ";
                await vecCmd.ExecuteNonQueryAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Persistencia] No se pudo crear tabla virtual vec0: {ex.Message}");
                _isVectorExtensionLoaded = false;
            }
        }
    }

    #region Workspaces

    public async Task<string> InsertWorkspaceAsync(string name, string? description, string? workspaceId = null)
    {
        var id = string.IsNullOrWhiteSpace(workspaceId) ? Guid.NewGuid().ToString("D") : workspaceId;
        using var connection = await OpenConnectionAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO Workspaces (WorkspaceId, Name, Description, SyncStatus, UpdatedAt, CreatedAt)
            VALUES ($id, $name, $description, 0, datetime('now'), datetime('now'))
            ON CONFLICT(WorkspaceId) DO UPDATE SET
                Name = $name,
                Description = $description,
                UpdatedAt = datetime('now');
        ";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$description", (object?)description ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
        return id;
    }

    public async Task<List<WorkspaceSummaryDto>> GetAllWorkspacesAsync()
    {
        using var connection = await OpenConnectionAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT w.WorkspaceId, w.Name, w.Description, 
                   COUNT(f.FileId) as FileCount, 
                   w.SyncStatus, w.UpdatedAt, w.CreatedAt
            FROM Workspaces w
            LEFT JOIN WorkspaceFiles f ON w.WorkspaceId = f.WorkspaceId
            GROUP BY w.WorkspaceId
            ORDER BY w.UpdatedAt DESC;
        ";

        var list = new List<WorkspaceSummaryDto>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(new WorkspaceSummaryDto(
                WorkspaceId: reader.GetString(0),
                Name: reader.GetString(1),
                Description: reader.IsDBNull(2) ? null : reader.GetString(2),
                FileCount: reader.GetInt32(3),
                SyncStatus: reader.GetInt32(4),
                UpdatedAt: reader.GetString(5),
                CreatedAt: reader.GetString(6)
            ));
        }
        return list;
    }

    public async Task<WorkspaceDetailDto?> GetWorkspaceByIdAsync(string workspaceId)
    {
        using var connection = await OpenConnectionAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT WorkspaceId, Name, Description, SyncStatus, UpdatedAt, CreatedAt
            FROM Workspaces
            WHERE WorkspaceId = $id;
        ";
        cmd.Parameters.AddWithValue("$id", workspaceId);

        WorkspaceDetailDto? detail = null;
        using (var reader = await cmd.ExecuteReaderAsync())
        {
            if (await reader.ReadAsync())
            {
                detail = new WorkspaceDetailDto(
                    WorkspaceId: reader.GetString(0),
                    Name: reader.GetString(1),
                    Description: reader.IsDBNull(2) ? null : reader.GetString(2),
                    SyncStatus: reader.GetInt32(3),
                    UpdatedAt: reader.GetString(4),
                    CreatedAt: reader.GetString(5),
                    Files: new List<WorkspaceFileDto>(),
                    Boards: new List<BoardSummaryDto>()
                );
            }
        }

        if (detail == null) return null;

        var files = await GetWorkspaceFilesAsync(workspaceId);
        var boards = await GetWorkspaceBoardsAsync(workspaceId);

        return detail with { Files = files, Boards = boards };
    }

    public async Task DeleteWorkspaceAsync(string workspaceId)
    {
        using var connection = await OpenConnectionAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM Workspaces WHERE WorkspaceId = $id;";
        cmd.Parameters.AddWithValue("$id", workspaceId);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task AddWorkspaceFileAsync(WorkspaceFileDto file)
    {
        using var connection = await OpenConnectionAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO WorkspaceFiles 
                (FileId, WorkspaceId, FileName, FilePath, FileType, FileHash, FileSizeByte, SyncStatus, UpdatedAt)
            VALUES 
                ($fileId, $wsId, $fileName, $filePath, $fileType, $fileHash, $fileSize, $sync, datetime('now'))
            ON CONFLICT(FileId) DO UPDATE SET
                FileName = $fileName,
                FilePath = $filePath,
                FileType = $fileType,
                FileHash = $fileHash,
                FileSizeByte = $fileSize,
                UpdatedAt = datetime('now');
        ";
        cmd.Parameters.AddWithValue("$fileId", file.FileId);
        cmd.Parameters.AddWithValue("$wsId", file.WorkspaceId);
        cmd.Parameters.AddWithValue("$fileName", file.FileName);
        cmd.Parameters.AddWithValue("$filePath", file.FilePath);
        cmd.Parameters.AddWithValue("$fileType", file.FileType);
        cmd.Parameters.AddWithValue("$fileHash", file.FileHash);
        cmd.Parameters.AddWithValue("$fileSize", file.FileSizeByte);
        cmd.Parameters.AddWithValue("$sync", file.SyncStatus);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<List<WorkspaceFileDto>> GetWorkspaceFilesAsync(string workspaceId)
    {
        using var connection = await OpenConnectionAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT FileId, WorkspaceId, FileName, FilePath, FileType, FileHash, FileSizeByte, SyncStatus, UpdatedAt
            FROM WorkspaceFiles
            WHERE WorkspaceId = $wsId
            ORDER BY FileType ASC, FileName ASC;
        ";
        cmd.Parameters.AddWithValue("$wsId", workspaceId);

        var list = new List<WorkspaceFileDto>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(new WorkspaceFileDto(
                FileId: reader.GetString(0),
                WorkspaceId: reader.GetString(1),
                FileName: reader.GetString(2),
                FilePath: reader.GetString(3),
                FileType: reader.GetString(4),
                FileHash: reader.GetString(5),
                FileSizeByte: reader.GetInt64(6),
                SyncStatus: reader.GetInt32(7),
                UpdatedAt: reader.GetString(8)
            ));
        }
        return list;
    }

    public async Task DeleteWorkspaceFileAsync(string fileId)
    {
        using var connection = await OpenConnectionAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM WorkspaceFiles WHERE FileId = $fileId;";
        cmd.Parameters.AddWithValue("$fileId", fileId);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<List<BoardSummaryDto>> GetWorkspaceBoardsAsync(string workspaceId)
    {
        using var connection = await OpenConnectionAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT b.BoardId, b.WorkspaceId, b.ModelName, b.BrdHash, b.PdfHash, 
                   COUNT(c.ComponentId) as ComponentCount, b.SyncStatus, b.UpdatedAt
            FROM Boards b
            LEFT JOIN Components c ON b.BoardId = c.BoardId
            WHERE b.WorkspaceId = $wsId
            GROUP BY b.BoardId;
        ";
        cmd.Parameters.AddWithValue("$wsId", workspaceId);

        var list = new List<BoardSummaryDto>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(new BoardSummaryDto(
                BoardId: reader.GetString(0),
                WorkspaceId: reader.GetString(1),
                ModelName: reader.GetString(2),
                BrdHash: reader.GetString(3),
                PdfHash: reader.GetString(4),
                ComponentCount: reader.GetInt32(5),
                SyncStatus: reader.GetInt32(6),
                UpdatedAt: reader.GetString(7)
            ));
        }
        return list;
    }

    #endregion

    #region Boards & Cache

    public async Task SaveBoardCacheAsync(
        string boardId,
        string workspaceId,
        string modelName,
        string brdHash,
        string pdfHash,
        List<ComponentCacheDto> components,
        string? cachedIndexJson = null)
    {
        using var connection = await OpenConnectionAsync();
        using var transaction = connection.BeginTransaction();

        // 1. Guardar placa
        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText = @"
                INSERT INTO Boards (BoardId, WorkspaceId, ModelName, BrdHash, PdfHash, CachedIndexJson, SyncStatus, UpdatedAt)
                VALUES ($boardId, $wsId, $model, $brdHash, $pdfHash, $cachedJson, 0, datetime('now'))
                ON CONFLICT(BoardId) DO UPDATE SET
                    WorkspaceId = $wsId,
                    ModelName = $model,
                    BrdHash = $brdHash,
                    PdfHash = $pdfHash,
                    CachedIndexJson = $cachedJson,
                    UpdatedAt = datetime('now');
            ";
            cmd.Parameters.AddWithValue("$boardId", boardId);
            cmd.Parameters.AddWithValue("$wsId", workspaceId);
            cmd.Parameters.AddWithValue("$model", modelName);
            cmd.Parameters.AddWithValue("$brdHash", brdHash);
            cmd.Parameters.AddWithValue("$pdfHash", pdfHash);
            cmd.Parameters.AddWithValue("$cachedJson", (object?)cachedIndexJson ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync();
        }

        // 2. Insertar componentes por lotes en la misma transacción
        using (var delCmd = connection.CreateCommand())
        {
            delCmd.Transaction = transaction;
            delCmd.CommandText = "DELETE FROM Components WHERE BoardId = $boardId;";
            delCmd.Parameters.AddWithValue("$boardId", boardId);
            await delCmd.ExecuteNonQueryAsync();
        }

        if (components.Count > 0)
        {
            foreach (var comp in components)
            {
                using var insertCompCmd = connection.CreateCommand();
                insertCompCmd.Transaction = transaction;
                insertCompCmd.CommandText = @"
                    INSERT INTO Components (ComponentId, BoardId, Side, Quadrant, PdfPage, PinCount, SyncStatus, UpdatedAt)
                    VALUES ($comp, $board, $side, $quad, $page, $pins, 0, datetime('now'))
                    ON CONFLICT(BoardId, ComponentId) DO UPDATE SET
                        Side = $side,
                        Quadrant = $quad,
                        PdfPage = $page,
                        PinCount = $pins,
                        UpdatedAt = datetime('now');
                ";
                insertCompCmd.Parameters.AddWithValue("$comp", comp.ComponentId);
                insertCompCmd.Parameters.AddWithValue("$board", boardId);
                insertCompCmd.Parameters.AddWithValue("$side", comp.Side.ToUpperInvariant());
                insertCompCmd.Parameters.AddWithValue("$quad", comp.Quadrant);
                insertCompCmd.Parameters.AddWithValue("$page", (object?)comp.PdfPage ?? DBNull.Value);
                insertCompCmd.Parameters.AddWithValue("$pins", comp.PinCount);
                await insertCompCmd.ExecuteNonQueryAsync();
            }
        }

        await transaction.CommitAsync();
    }

    public async Task<BoardCacheDto?> GetBoardCacheByHashesAsync(string brdHash, string pdfHash)
    {
        using var connection = await OpenConnectionAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT BoardId, WorkspaceId, ModelName, BrdHash, PdfHash, CachedIndexJson, SyncStatus, UpdatedAt
            FROM Boards
            WHERE BrdHash = $brdHash AND PdfHash = $pdfHash;
        ";
        cmd.Parameters.AddWithValue("$brdHash", brdHash);
        cmd.Parameters.AddWithValue("$pdfHash", pdfHash);

        using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            return new BoardCacheDto(
                BoardId: reader.GetString(0),
                WorkspaceId: reader.GetString(1),
                ModelName: reader.GetString(2),
                BrdHash: reader.GetString(3),
                PdfHash: reader.GetString(4),
                CachedIndexJson: reader.IsDBNull(5) ? null : reader.GetString(5),
                SyncStatus: reader.GetInt32(6),
                UpdatedAt: reader.GetString(7)
            );
        }
        return null;
    }

    public async Task<BoardCacheDto?> GetBoardCacheByIdAsync(string boardId)
    {
        using var connection = await OpenConnectionAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT BoardId, WorkspaceId, ModelName, BrdHash, PdfHash, CachedIndexJson, SyncStatus, UpdatedAt
            FROM Boards
            WHERE BoardId = $id;
        ";
        cmd.Parameters.AddWithValue("$id", boardId);

        using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            return new BoardCacheDto(
                BoardId: reader.GetString(0),
                WorkspaceId: reader.GetString(1),
                ModelName: reader.GetString(2),
                BrdHash: reader.GetString(3),
                PdfHash: reader.GetString(4),
                CachedIndexJson: reader.IsDBNull(5) ? null : reader.GetString(5),
                SyncStatus: reader.GetInt32(6),
                UpdatedAt: reader.GetString(7)
            );
        }
        return null;
    }

    public async Task<List<ComponentCacheDto>> GetCachedComponentsAsync(string boardId)
    {
        using var connection = await OpenConnectionAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT ComponentId, BoardId, Side, Quadrant, PdfPage, PinCount, SyncStatus
            FROM Components
            WHERE BoardId = $boardId
            ORDER BY ComponentId ASC;
        ";
        cmd.Parameters.AddWithValue("$boardId", boardId);

        var list = new List<ComponentCacheDto>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(new ComponentCacheDto(
                ComponentId: reader.GetString(0),
                BoardId: reader.GetString(1),
                Side: reader.GetString(2),
                Quadrant: reader.GetString(3),
                PdfPage: reader.IsDBNull(4) ? null : reader.GetInt32(4),
                PinCount: reader.GetInt32(5),
                SyncStatus: reader.GetInt32(6)
            ));
        }
        return list;
    }

    #endregion

    #region Repair History & Predictive Intelligence

    public async Task RecordRepairAsync(RepairRecordDto repair)
    {
        using var connection = await OpenConnectionAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO RepairHistory 
                (RepairId, WorkspaceId, BoardId, SymptomKey, FaultyComponent, FaultType, MeasuredImpedance, MeasuredVoltage, ResolvedAt, Notes, SyncStatus, UpdatedAt)
            VALUES 
                ($repairId, $workspaceId, $boardId, $symptomKey, $faultyComponent, $faultType, $measuredImpedance, $measuredVoltage, datetime('now'), $notes, 0, datetime('now'))
            ON CONFLICT(RepairId) DO UPDATE SET
                SymptomKey = $symptomKey,
                FaultyComponent = $faultyComponent,
                FaultType = $faultType,
                MeasuredImpedance = $measuredImpedance,
                MeasuredVoltage = $measuredVoltage,
                Notes = $notes,
                UpdatedAt = datetime('now');
        ";

        var uuid = string.IsNullOrWhiteSpace(repair.RepairId) ? Guid.NewGuid().ToString("D") : repair.RepairId;

        cmd.Parameters.AddWithValue("$repairId", uuid);
        cmd.Parameters.AddWithValue("$workspaceId", repair.WorkspaceId);
        cmd.Parameters.AddWithValue("$boardId", repair.BoardId);
        cmd.Parameters.AddWithValue("$symptomKey", repair.SymptomKey);
        cmd.Parameters.AddWithValue("$faultyComponent", repair.FaultyComponent);
        cmd.Parameters.AddWithValue("$faultType", repair.FaultType);
        cmd.Parameters.AddWithValue("$measuredImpedance", (object?)repair.MeasuredImpedance ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$measuredVoltage", (object?)repair.MeasuredVoltage ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$notes", (object?)repair.Notes ?? DBNull.Value);

        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<List<ComponentFailureStat>> GetTopFailingComponentsAsync(string boardId, string symptomKey)
    {
        using var connection = await OpenConnectionAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT FaultyComponent, COUNT(*) as FailCount, 
                   (COUNT(*) * 100.0 / SUM(COUNT(*)) OVER()) as ProbabilityPercentage
            FROM RepairHistory
            WHERE BoardId = $boardId AND LOWER(SymptomKey) = LOWER($symptomKey)
            GROUP BY FaultyComponent
            ORDER BY FailCount DESC;
        ";
        cmd.Parameters.AddWithValue("$boardId", boardId);
        cmd.Parameters.AddWithValue("$symptomKey", symptomKey);

        var results = new List<ComponentFailureStat>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(new ComponentFailureStat
            {
                ComponentId = reader.GetString(0),
                FailureCount = reader.GetInt32(1),
                ProbabilityPercentage = reader.GetDouble(2)
            });
        }
        return results;
    }

    public async Task<List<RepairRecordDto>> GetUnsyncedRepairsAsync()
    {
        using var connection = await OpenConnectionAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT RepairId, WorkspaceId, BoardId, SymptomKey, FaultyComponent, FaultType, MeasuredImpedance, MeasuredVoltage, ResolvedAt, Notes, SyncStatus
            FROM RepairHistory
            WHERE SyncStatus = 0;
        ";

        var results = new List<RepairRecordDto>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(new RepairRecordDto
            {
                RepairId = reader.GetString(0),
                WorkspaceId = reader.GetString(1),
                BoardId = reader.GetString(2),
                SymptomKey = reader.GetString(3),
                FaultyComponent = reader.GetString(4),
                FaultType = reader.GetString(5),
                MeasuredImpedance = reader.IsDBNull(6) ? null : reader.GetDouble(6),
                MeasuredVoltage = reader.IsDBNull(7) ? null : reader.GetDouble(7),
                ResolvedAt = reader.IsDBNull(8) ? null : reader.GetString(8),
                Notes = reader.IsDBNull(9) ? null : reader.GetString(9),
                SyncStatus = reader.GetInt32(10)
            });
        }
        return results;
    }

    public async Task MarkRepairsAsSyncedAsync(List<string> repairIds)
    {
        if (!repairIds.Any()) return;

        using var connection = await OpenConnectionAsync();
        using var transaction = connection.BeginTransaction();

        foreach (var id in repairIds)
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = "UPDATE RepairHistory SET SyncStatus = 1, UpdatedAt = datetime('now') WHERE RepairId = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            await cmd.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();
    }

    public async Task<List<RepairRecordDto>> GetRepairsByBoardAsync(string boardId)
    {
        using var connection = await OpenConnectionAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT RepairId, WorkspaceId, BoardId, SymptomKey, FaultyComponent, FaultType, MeasuredImpedance, MeasuredVoltage, ResolvedAt, Notes, SyncStatus
            FROM RepairHistory
            WHERE BoardId = $boardId
            ORDER BY ResolvedAt DESC;
        ";
        cmd.Parameters.AddWithValue("$boardId", boardId);

        var list = new List<RepairRecordDto>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(new RepairRecordDto
            {
                RepairId = reader.GetString(0),
                WorkspaceId = reader.GetString(1),
                BoardId = reader.GetString(2),
                SymptomKey = reader.GetString(3),
                FaultyComponent = reader.GetString(4),
                FaultType = reader.GetString(5),
                MeasuredImpedance = reader.IsDBNull(6) ? null : reader.GetDouble(6),
                MeasuredVoltage = reader.IsDBNull(7) ? null : reader.GetDouble(7),
                ResolvedAt = reader.IsDBNull(8) ? null : reader.GetString(8),
                Notes = reader.IsDBNull(9) ? null : reader.GetString(9),
                SyncStatus = reader.GetInt32(10)
            });
        }
        return list;
    }

    #endregion

    #region Diagnostic Sessions (Pause / Resume)

    public async Task<string> SaveDiagnosticSessionAsync(DiagnosticSessionDto session)
    {
        var id = string.IsNullOrWhiteSpace(session.SessionId) ? Guid.NewGuid().ToString("D") : session.SessionId;
        using var connection = await OpenConnectionAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO DiagnosticSessions 
                (SessionId, WorkspaceId, BoardId, TechnicianName, CurrentStepIndex, Status, StepMeasurementsJson, SyncStatus, UpdatedAt)
            VALUES 
                ($id, $wsId, $boardId, $tech, $step, $status, $json, 0, datetime('now'))
            ON CONFLICT(SessionId) DO UPDATE SET
                CurrentStepIndex = $step,
                Status = $status,
                StepMeasurementsJson = $json,
                UpdatedAt = datetime('now');
        ";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$wsId", session.WorkspaceId);
        cmd.Parameters.AddWithValue("$boardId", session.BoardId);
        cmd.Parameters.AddWithValue("$tech", (object?)session.TechnicianName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$step", session.CurrentStepIndex);
        cmd.Parameters.AddWithValue("$status", session.Status);
        cmd.Parameters.AddWithValue("$json", (object?)session.StepMeasurementsJson ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
        return id;
    }

    public async Task<DiagnosticSessionDto?> GetActiveSessionAsync(string workspaceId, string boardId)
    {
        using var connection = await OpenConnectionAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT SessionId, WorkspaceId, BoardId, TechnicianName, CurrentStepIndex, Status, StepMeasurementsJson, SyncStatus, UpdatedAt
            FROM DiagnosticSessions
            WHERE WorkspaceId = $wsId AND BoardId = $boardId AND Status = 'IN_PROGRESS'
            ORDER BY UpdatedAt DESC
            LIMIT 1;
        ";
        cmd.Parameters.AddWithValue("$wsId", workspaceId);
        cmd.Parameters.AddWithValue("$boardId", boardId);

        using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            return new DiagnosticSessionDto
            {
                SessionId = reader.GetString(0),
                WorkspaceId = reader.GetString(1),
                BoardId = reader.GetString(2),
                TechnicianName = reader.IsDBNull(3) ? null : reader.GetString(3),
                CurrentStepIndex = reader.GetInt32(4),
                Status = reader.GetString(5),
                StepMeasurementsJson = reader.IsDBNull(6) ? null : reader.GetString(6),
                SyncStatus = reader.GetInt32(7),
                UpdatedAt = reader.GetString(8)
            };
        }
        return null;
    }

    public async Task UpdateSessionProgressAsync(string sessionId, int currentStepIndex, string status, string? stepMeasurementsJson)
    {
        using var connection = await OpenConnectionAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            UPDATE DiagnosticSessions 
            SET CurrentStepIndex = $step,
                Status = $status,
                StepMeasurementsJson = COALESCE($json, StepMeasurementsJson),
                UpdatedAt = datetime('now')
            WHERE SessionId = $id;
        ";
        cmd.Parameters.AddWithValue("$id", sessionId);
        cmd.Parameters.AddWithValue("$step", currentStepIndex);
        cmd.Parameters.AddWithValue("$status", status);
        cmd.Parameters.AddWithValue("$json", (object?)stepMeasurementsJson ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    #endregion

    #region Vector Embeddings (sqlite-vec)

    public async Task SaveRepairEmbeddingAsync(string repairId, float[] vector)
    {
        if (!_isVectorExtensionLoaded) return;

        using var connection = await OpenConnectionAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO RepairEmbeddings (RepairId, SymptomVector)
            VALUES ($id, $vec)
            ON CONFLICT(RepairId) DO UPDATE SET SymptomVector = $vec;
        ";
        cmd.Parameters.AddWithValue("$id", repairId);

        var bytes = new byte[vector.Length * sizeof(float)];
        Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
        cmd.Parameters.AddWithValue("$vec", bytes);

        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<List<string>> SearchSimilarRepairsAsync(float[] queryVector, int topK = 5)
    {
        if (!_isVectorExtensionLoaded) return new List<string>();

        using var connection = await OpenConnectionAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT RepairId
            FROM RepairEmbeddings
            WHERE SymptomVector MATCH $q
            ORDER BY distance
            LIMIT $k;
        ";

        var bytes = new byte[queryVector.Length * sizeof(float)];
        Buffer.BlockCopy(queryVector, 0, bytes, 0, bytes.Length);
        cmd.Parameters.AddWithValue("$q", bytes);
        cmd.Parameters.AddWithValue("$k", topK);

        var list = new List<string>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(reader.GetString(0));
        }
        return list;
    }

    #endregion

    #region App Settings & State

    public async Task SetSettingAsync(string key, string value)
    {
        using var connection = await OpenConnectionAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO AppSettings (Key, Value, UpdatedAt)
            VALUES ($key, $val, datetime('now'))
            ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value, UpdatedAt = excluded.UpdatedAt;
        ";
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$val", value);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<string?> GetSettingAsync(string key)
    {
        using var connection = await OpenConnectionAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Value FROM AppSettings WHERE Key = $key;";
        cmd.Parameters.AddWithValue("$key", key);
        var result = await cmd.ExecuteScalarAsync();
        return result != null && result != DBNull.Value ? result.ToString() : null;
    }

    #endregion
}
