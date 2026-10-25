using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using OpenBoardView.AI.Models;
using OpenBoardView.AI.Persistence;
using OpenBoardView.AI.Services;

namespace OpenBoardView.AI.Controllers;

[ApiController]
[Route("api/workspaces")]
public class WorkspacesController : ControllerBase
{
    private readonly IWorkspaceService _workspaceService;
    private readonly ActiveCircuitManager _activeCircuitManager;
    private readonly AutomatedSchematicAnalyzer _schematicAnalyzer;
    private readonly IPersistenceService _persistence;

    public WorkspacesController(
        IWorkspaceService workspaceService, 
        ActiveCircuitManager activeCircuitManager,
        AutomatedSchematicAnalyzer schematicAnalyzer,
        IPersistenceService persistence)
    {
        _workspaceService = workspaceService;
        _activeCircuitManager = activeCircuitManager;
        _schematicAnalyzer = schematicAnalyzer;
        _persistence = persistence;
    }

    [HttpGet]
    public async Task<IActionResult> GetAllWorkspaces()
    {
        var list = await _workspaceService.GetAllWorkspacesAsync();
        return Ok(list);
    }

    [HttpGet("active")]
    public async Task<IActionResult> GetActiveWorkspace()
    {
        var active = await _workspaceService.GetActiveWorkspaceAsync();
        if (active == null) return NoContent();
        return Ok(active);
    }

    [HttpPost("active/{id}")]
    public async Task<IActionResult> SetActiveWorkspace(string id)
    {
        var ok = await _activeCircuitManager.SwitchToWorkspaceAsync(id);
        if (!ok) return NotFound(new { error = $"No se pudo activar el espacio de trabajo con ID '{id}'. Asegúrate de que contenga un archivo CAD (.brd) válido." });
        return Ok(new { success = true, activeWorkspaceId = id, activeBoard = _activeCircuitManager.ActiveBoardName });
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetWorkspaceById(string id)
    {
        var ws = await _workspaceService.GetWorkspaceByIdAsync(id);
        if (ws == null) return NotFound(new { error = $"Espacio de trabajo con ID '{id}' no encontrado." });
        return Ok(ws);
    }

    [HttpPost("upload")]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> CreateWorkspace(
        [FromForm] string name, 
        [FromForm] string? description, 
        [FromForm] List<IFormFile> files,
        [FromForm] bool autoAnalyze = false)
    {
        if (string.IsNullOrWhiteSpace(name))
            return BadRequest(new { error = "El nombre del espacio de trabajo es requerido." });

        if (files == null || !files.Any())
            return BadRequest(new { error = "Debe adjuntar al menos un archivo (.brd, .pcb, .pdf)." });

        var fileRequests = new List<FileUploadRequest>();

        foreach (var file in files)
        {
            if (file.Length == 0) continue;

            using var ms = new MemoryStream();
            await file.CopyToAsync(ms);

            fileRequests.Add(new FileUploadRequest
            {
                FileName = file.FileName,
                ContentBytes = ms.ToArray(),
                Length = file.Length
            });
        }

        if (!fileRequests.Any())
            return BadRequest(new { error = "Los archivos adjuntos están vacíos." });

        var workspace = await _workspaceService.CreateWorkspaceAsync(name, description, fileRequests);
        await _activeCircuitManager.SwitchToWorkspaceAsync(workspace.WorkspaceId);

        if (autoAnalyze)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await _schematicAnalyzer.ProcessWorkspaceSchematicAsync(workspace.WorkspaceId);
                }
                catch
                {
                    // Manejado internamente en AutomatedSchematicAnalyzer con log y persistencia de fallos
                }
            });
        }

        return Ok(workspace);
    }

    [HttpPost("{id}/trigger-analysis")]
    public async Task<IActionResult> TriggerSchematicAnalysis(string id)
    {
        var ws = await _workspaceService.GetWorkspaceByIdAsync(id);
        if (ws == null) return NotFound(new { error = $"Espacio de trabajo '{id}' no encontrado." });

        var status = await _persistence.GetWorkspaceAnalysisStatusAsync(id);
        if (status.Status == "Processing" || status.Status == "Queued")
        {
            return Conflict(new { message = "El análisis ya se encuentra en ejecución en segundo plano.", status = status.Status });
        }

        await _persistence.UpdateWorkspaceAnalysisStatusAsync(id, "Queued", summary: "En cola para análisis de esquemático con IA...");

        _ = Task.Run(async () =>
        {
            try
            {
                await _schematicAnalyzer.ProcessWorkspaceSchematicAsync(id);
            }
            catch
            {
                // Manejado internamente en AutomatedSchematicAnalyzer
            }
        });

        return Ok(new { success = true, message = "Análisis autónomo de esquemático iniciado en segundo plano.", status = "Queued" });
    }

    [HttpGet("{id}/analysis-status")]
    public async Task<IActionResult> GetWorkspaceAnalysisStatus(string id)
    {
        var ws = await _workspaceService.GetWorkspaceByIdAsync(id);
        if (ws == null) return NotFound(new { error = $"Espacio de trabajo '{id}' no encontrado." });

        var status = await _persistence.GetWorkspaceAnalysisStatusAsync(id);
        return Ok(new { status = status.Status, activePresetId = status.ActivePresetId, summary = status.Summary });
    }

    [HttpPost("{id}/files")]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> AddFileToWorkspace(
        string id, 
        [FromForm] IFormFile file, 
        [FromForm] string? fileType)
    {
        if (file == null || file.Length == 0)
            return BadRequest(new { error = "Debe seleccionar un archivo válido." });

        using var ms = new MemoryStream();
        await file.CopyToAsync(ms);

        var req = new FileUploadRequest
        {
            FileName = file.FileName,
            ContentBytes = ms.ToArray(),
            Length = file.Length,
            ExplicitFileType = fileType
        };

        try
        {
            var saved = await _workspaceService.AddFileToWorkspaceAsync(id, req);
            return Ok(saved);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteWorkspace(string id)
    {
        await _workspaceService.DeleteWorkspaceAsync(id);
        return NoContent();
    }

    [HttpDelete("{id}/files/{fileId}")]
    public async Task<IActionResult> DeleteFile(string id, string fileId)
    {
        await _workspaceService.DeleteFileAsync(id, fileId);
        return NoContent();
    }
}
