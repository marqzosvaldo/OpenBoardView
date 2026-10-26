using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenBoardView.AI.Graph;
using OpenBoardView.AI.Knowledge;
using OpenBoardView.AI.Models;
using OpenBoardView.AI.Parsers;
using OpenBoardView.AI.Persistence;
using OpenBoardView.AI.Server;

namespace OpenBoardView.AI.Services;

/// <summary>
/// Administrador dinámico de placas y circuitos en memoria para OpenBoardView AI v4.
/// Permite alternar entre Espacios de Trabajo (Workspaces) en tiempo de ejecución,
/// cargando instantáneamente la topología y esquemático correspondientes desde la caché SQLite (<0.1s)
/// y manteniendo sincronizados el Grafo, Simulador, Debugger y Base de Conocimiento.
/// </summary>
public class ActiveCircuitManager
{
    private readonly IPersistenceService _persistence;
    private readonly IWorkspaceService _workspaceService;
    private readonly IHostEnvironment _env;
    private readonly IConfiguration _config;
    private readonly ILogger<ActiveCircuitManager> _logger;
    private readonly ObvWebSocketServer? _wsServer;
    private readonly string[] _knowledgeDirs;

    private CircuitGraphBuilder _currentGraph = null!;
    private CircuitSimulator _currentSimulator = null!;
    private CircuitMazeDebugger _currentMazeDebugger = null!;
    private FlowKnowledgeBase _currentKnowledge = null!;
    private string? _activeWorkspaceId;
    private string? _activeBoardName;
    private readonly object _syncLock = new();

    public ActiveCircuitManager(
        IPersistenceService persistence,
        IWorkspaceService workspaceService,
        IHostEnvironment env,
        IConfiguration config,
        ILogger<ActiveCircuitManager> logger,
        ObvWebSocketServer? wsServer = null)
    {
        _persistence = persistence;
        _workspaceService = workspaceService;
        _env = env;
        _config = config;
        _logger = logger;
        _wsServer = wsServer;

        _knowledgeDirs = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Knowledge"),
            Path.GetFullPath(Path.Combine(env.ContentRootPath, "../../Boards/knowledge"))
        };
    }

    public CircuitGraphBuilder CurrentGraph { get { lock (_syncLock) return _currentGraph; } }
    public CircuitSimulator CurrentSimulator { get { lock (_syncLock) return _currentSimulator; } }
    public CircuitMazeDebugger CurrentMazeDebugger { get { lock (_syncLock) return _currentMazeDebugger; } }
    public FlowKnowledgeBase CurrentKnowledge { get { lock (_syncLock) return _currentKnowledge; } }
    public string? ActiveWorkspaceId { get { lock (_syncLock) return _activeWorkspaceId; } }
    public string? ActiveBoardName { get { lock (_syncLock) return _activeBoardName; } }

    public async Task InitializeDefaultAsync(string[] args)
    {
        // 1. Verificar si hay argumentos CLI explícitos
        string? brdArg = null;
        string? pdfArg = null;
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals("--brd", StringComparison.OrdinalIgnoreCase)) brdArg = args[i + 1];
            if (args[i].Equals("--pdf", StringComparison.OrdinalIgnoreCase)) pdfArg = args[i + 1];
        }

        if (!string.IsNullOrEmpty(brdArg))
        {
            string brdPath = Path.IsPathRooted(brdArg) ? brdArg : Path.GetFullPath(Path.Combine(_env.ContentRootPath, brdArg));
            string? pdfPath = !string.IsNullOrEmpty(pdfArg) 
                ? (Path.IsPathRooted(pdfArg) ? pdfArg : Path.GetFullPath(Path.Combine(_env.ContentRootPath, pdfArg))) 
                : null;

            await LoadBoardFromFilesAsync(brdPath, pdfPath, "cli_workspace", "Placa CLI");
            return;
        }

        // 2. Cargar el espacio de trabajo activo o el primero con archivos CAD válidos
        var activeWs = await _workspaceService.GetActiveWorkspaceAsync();
        if (activeWs != null && activeWs.Files.Any(f => f.FileType == "PHYSICAL_CAD" || f.FileName.EndsWith(".brd", StringComparison.OrdinalIgnoreCase)))
        {
            bool loaded = await SwitchToWorkspaceAsync(activeWs.WorkspaceId);
            if (loaded) return;
        }

        var workspaces = await _persistence.GetAllWorkspacesAsync();
        foreach (var candidateWs in workspaces.Where(w => w.FileCount > 0))
        {
            if (candidateWs.WorkspaceId == activeWs?.WorkspaceId) continue;
            bool loaded = await SwitchToWorkspaceAsync(candidateWs.WorkspaceId);
            if (loaded) return;
        }

        // 3. Fallback a Boards/ o Plan/
        string boardsDir = Path.GetFullPath(Path.Combine(_env.ContentRootPath, "../../Boards"));
        string? autoBrd = Directory.Exists(boardsDir) ? Directory.GetFiles(boardsDir, "*.brd").FirstOrDefault() : null;
        string? autoPdf = Directory.Exists(boardsDir) ? Directory.GetFiles(boardsDir, "*.pdf").FirstOrDefault() : null;

        string brdConfigPath = Environment.GetEnvironmentVariable("BOARD_BRD_PATH")
            ?? _config["BoardFiles:BrdPath"]
            ?? autoBrd
            ?? "../../Plan/Apple MacBook Pro 13 A1278 i5 2.5GHz 820-3115-B Boardview.brd";

        string pdfConfigPath = Environment.GetEnvironmentVariable("BOARD_PDF_PATH")
            ?? _config["BoardFiles:PdfPath"]
            ?? autoPdf
            ?? "../../Plan/Apple MacBook Pro A1278 13 MLB,J30 i5 2.5GHZ  051-9058  820-3115-B (03-12-2012) Schematics.pdf";

        string fallbackBrd = Path.IsPathRooted(brdConfigPath) ? brdConfigPath : Path.GetFullPath(Path.Combine(_env.ContentRootPath, brdConfigPath));
        string fallbackPdf = Path.IsPathRooted(pdfConfigPath) ? pdfConfigPath : Path.GetFullPath(Path.Combine(_env.ContentRootPath, pdfConfigPath));

        await LoadBoardFromFilesAsync(fallbackBrd, fallbackPdf, "default_ws", "Espacio Predeterminado");
    }

    public async Task<bool> SwitchToWorkspaceAsync(string workspaceId)
    {
        var ws = await _persistence.GetWorkspaceByIdAsync(workspaceId);
        if (ws == null || ws.Files.Count == 0)
        {
            _logger.LogWarning("[ActiveCircuitManager] El espacio {Id} no existe o no contiene archivos.", workspaceId);
            return false;
        }

        // Localizar archivo físico CAD (.brd, .pcb, .cad, etc.)
        var cadFile = ws.Files.FirstOrDefault(f => f.FileType == "PHYSICAL_CAD")
            ?? ws.Files.FirstOrDefault(f => f.FileName.EndsWith(".brd", StringComparison.OrdinalIgnoreCase) ||
                                            f.FileName.EndsWith(".bdv", StringComparison.OrdinalIgnoreCase) ||
                                            f.FileName.EndsWith(".cad", StringComparison.OrdinalIgnoreCase));

        if (cadFile == null)
        {
            _logger.LogWarning("[ActiveCircuitManager] El espacio {Name} no tiene un archivo CAD válido.", ws.Name);
            return false;
        }

        // Localizar esquemático PDF primario
        var pdfFile = ws.Files.FirstOrDefault(f => f.FileType == "SCHEMATIC_PRIMARY")
            ?? ws.Files.FirstOrDefault(f => f.FileType == "SCHEMATIC_ALT")
            ?? ws.Files.FirstOrDefault(f => f.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase));

        string cadFullPath = Path.IsPathRooted(cadFile.FilePath)
            ? cadFile.FilePath
            : Path.GetFullPath(Path.Combine(_env.ContentRootPath, cadFile.FilePath));

        string? pdfFullPath = pdfFile != null
            ? (Path.IsPathRooted(pdfFile.FilePath) ? pdfFile.FilePath : Path.GetFullPath(Path.Combine(_env.ContentRootPath, pdfFile.FilePath)))
            : null;

        if (!File.Exists(cadFullPath))
        {
            _logger.LogError("[ActiveCircuitManager] Archivo CAD no encontrado en disco: {Path}", cadFullPath);
            return false;
        }

        try
        {
            await LoadBoardFromFilesAsync(cadFullPath, pdfFullPath, ws.WorkspaceId, ws.Name, cadFile.FileHash, pdfFile?.FileHash);
            await _workspaceService.SetActiveWorkspaceAsync(workspaceId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ActiveCircuitManager] Error al cargar la placa del espacio {Name}: {Message}", ws.Name, ex.Message);
            return false;
        }
    }

    private async Task LoadBoardFromFilesAsync(
        string brdFullPath, 
        string? pdfFullPath, 
        string wsId, 
        string wsName, 
        string? precalculatedBrdHash = null, 
        string? precalculatedPdfHash = null)
    {
        var swTotal = System.Diagnostics.Stopwatch.StartNew();

        var brdParser = new BoardViewParser();
        _logger.LogInformation("[ActiveCircuitManager] Parseando archivo CAD: {File}...", Path.GetFileName(brdFullPath));
        BoardTopology topology = brdParser.ParseFile(brdFullPath);

        byte[] brdBytes = File.ReadAllBytes(brdFullPath);
        string brdHash = precalculatedBrdHash ?? Convert.ToHexString(SHA256.HashData(brdBytes)).ToLowerInvariant();
        string pdfHash = precalculatedPdfHash ?? (pdfFullPath != null && File.Exists(pdfFullPath)
            ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(pdfFullPath))).ToLowerInvariant()
            : "no_pdf");

        var cachedBoard = await _persistence.GetBoardCacheByHashesAsync(brdHash, pdfHash);
        SchematicIndex? schematicIndex = null;

        if (cachedBoard != null && !string.IsNullOrWhiteSpace(cachedBoard.CachedIndexJson))
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            schematicIndex = SchematicIndexCacheSerializer.Deserialize(cachedBoard.CachedIndexJson);
            sw.Stop();
            _logger.LogInformation("[ActiveCircuitManager] ⚡ ¡Inicio Instantáneo! Esquemático recuperado de SQLite en {Ms} ms (<0.1s) | Páginas: {Pages} | Contexto: {Comps} comps.",
                sw.ElapsedMilliseconds, schematicIndex?.TotalPages ?? 0, schematicIndex?.ComponentContext.Count ?? 0);
        }
        else if (pdfFullPath != null && File.Exists(pdfFullPath))
        {
            _logger.LogInformation("[ActiveCircuitManager] Indexando esquemático PDF: {File}...", Path.GetFileName(pdfFullPath));
            var pdfReader = new SchematicPdfReader();
            schematicIndex = pdfReader.ReadAndIndexPdf(pdfFullPath, topology);

            string boardId = Path.GetFileNameWithoutExtension(topology.FileName);
            string serializedJson = SchematicIndexCacheSerializer.Serialize(schematicIndex);
            var compCache = topology.Components
                .Where(c => !string.IsNullOrWhiteSpace(c.Id))
                .GroupBy(c => c.Id, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .Select(c => new ComponentCacheDto(
                    c.Id,
                    boardId,
                    c.Side.ToString(),
                    c.Quadrant,
                    c.SchematicPages.FirstOrDefault(),
                    c.Pins.Count
                )).ToList();

            await _persistence.SaveBoardCacheAsync(boardId, wsId, topology.FileName, brdHash, pdfHash, compCache, serializedJson);
            _logger.LogInformation("[ActiveCircuitManager] Placa '{BoardId}' indexada y guardada en caché SQLite.", boardId);
        }

        var graph = new CircuitGraphBuilder(topology, schematicIndex);
        var kb = FlowKnowledgeBase.LoadFromDirectories(_knowledgeDirs, topology.FileName);
        var simulator = new CircuitSimulator(graph, kb);
        var debugger = new CircuitMazeDebugger(graph, kb);

        lock (_syncLock)
        {
            _currentGraph = graph;
            _currentKnowledge = kb;
            _currentSimulator = simulator;
            _currentMazeDebugger = debugger;
            _activeWorkspaceId = wsId;
            _activeBoardName = topology.FileName;
        }

        swTotal.Stop();
        _logger.LogInformation("[ActiveCircuitManager] ✅ Espacio '{WsName}' ({Board}) cargado en memoria ({TotalMs} ms). Comps: {Comps}, Redes: {Nets}.",
            wsName, topology.FileName, swTotal.ElapsedMilliseconds, topology.Components.Count, topology.Nets.Count);

        if (_wsServer != null)
        {
            try
            {
                await _wsServer.BroadcastCommandAsync(new ObvUiCommandPayload(
                    Action: "workspace_switched",
                    Message: $"Espacio activo: {wsName} ({topology.FileName})"
                ));
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[ActiveCircuitManager] No se pudo enviar notificación WebSocket de cambio de espacio.");
            }
        }
    }
}
