using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using OpenBoardView.AI.Models;
using OpenBoardView.AI.Services;

namespace OpenBoardView.AI.Controllers;

[ApiController]
[Route("api/workspaces")]
public class WorkspacesController : ControllerBase
{
    private readonly IWorkspaceService _workspaceService;

    public WorkspacesController(IWorkspaceService workspaceService)
    {
        _workspaceService = workspaceService;
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
        var ok = await _workspaceService.SetActiveWorkspaceAsync(id);
        if (!ok) return NotFound(new { error = $"Espacio de trabajo con ID '{id}' no encontrado." });
        return Ok(new { success = true, activeWorkspaceId = id });
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
        [FromForm] List<IFormFile> files)
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
        return Ok(workspace);
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
