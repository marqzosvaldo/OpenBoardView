using Microsoft.AspNetCore.Mvc;
using OpenBoardView.AI.Models;
using OpenBoardView.AI.Persistence;

namespace OpenBoardView.AI.Controllers;

[ApiController]
[Route("api/repairs")]
public class RepairsController : ControllerBase
{
    private readonly IPersistenceService _persistence;

    public RepairsController(IPersistenceService persistence)
    {
        _persistence = persistence;
    }

    [HttpGet("stats")]
    public async Task<IActionResult> GetFailureStats([FromQuery] string boardId, [FromQuery] string symptomKey)
    {
        if (string.IsNullOrWhiteSpace(boardId) || string.IsNullOrWhiteSpace(symptomKey))
            return BadRequest(new { error = "Se requiere boardId y symptomKey." });

        var stats = await _persistence.GetTopFailingComponentsAsync(boardId, symptomKey);
        return Ok(stats);
    }

    [HttpPost]
    public async Task<IActionResult> RecordRepair([FromBody] RepairRecordDto repair)
    {
        if (string.IsNullOrWhiteSpace(repair.BoardId) || string.IsNullOrWhiteSpace(repair.FaultyComponent))
            return BadRequest(new { error = "BoardId y FaultyComponent son requeridos." });

        await _persistence.RecordRepairAsync(repair);
        return Ok(new { success = true, repairId = repair.RepairId });
    }

    [HttpGet("board/{boardId}")]
    public async Task<IActionResult> GetRepairsByBoard(string boardId)
    {
        var repairs = await _persistence.GetRepairsByBoardAsync(boardId);
        return Ok(repairs);
    }
}
