using Microsoft.AspNetCore.Mvc;
using OpenBoardView.AI.Models;
using OpenBoardView.AI.Persistence;

namespace OpenBoardView.AI.Controllers;

[ApiController]
[Route("api/sessions")]
public class SessionsController : ControllerBase
{
    private readonly IPersistenceService _persistence;

    public SessionsController(IPersistenceService persistence)
    {
        _persistence = persistence;
    }

    [HttpGet("active")]
    public async Task<IActionResult> GetActiveSession([FromQuery] string workspaceId, [FromQuery] string boardId)
    {
        if (string.IsNullOrWhiteSpace(workspaceId) || string.IsNullOrWhiteSpace(boardId))
            return BadRequest(new { error = "Se requiere workspaceId y boardId." });

        var session = await _persistence.GetActiveSessionAsync(workspaceId, boardId);
        if (session == null) return NoContent();
        return Ok(session);
    }

    [HttpPost]
    public async Task<IActionResult> SaveSession([FromBody] DiagnosticSessionDto session)
    {
        if (string.IsNullOrWhiteSpace(session.WorkspaceId) || string.IsNullOrWhiteSpace(session.BoardId))
            return BadRequest(new { error = "WorkspaceId y BoardId son requeridos." });

        var id = await _persistence.SaveDiagnosticSessionAsync(session);
        return Ok(new { success = true, sessionId = id });
    }

    public record SessionProgressUpdateRequest(int CurrentStepIndex, string Status, string? StepMeasurementsJson);

    [HttpPut("{id}/progress")]
    public async Task<IActionResult> UpdateProgress(string id, [FromBody] SessionProgressUpdateRequest req)
    {
        await _persistence.UpdateSessionProgressAsync(id, req.CurrentStepIndex, req.Status, req.StepMeasurementsJson);
        return Ok(new { success = true });
    }
}
