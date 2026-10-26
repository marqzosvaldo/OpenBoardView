namespace OpenBoardView.AI.Services;

using OpenBoardView.AI.Models;

public interface IWorkspaceService
{
    Task<WorkspaceDto> CreateWorkspaceAsync(string name, string? description, List<FileUploadRequest> files);
    Task<List<WorkspaceSummaryDto>> GetAllWorkspacesAsync();
    Task<WorkspaceDetailDto?> GetWorkspaceByIdAsync(string workspaceId);
    Task DeleteWorkspaceAsync(string workspaceId);
    Task<WorkspaceFileDto> AddFileToWorkspaceAsync(string workspaceId, FileUploadRequest fileRequest);
    Task DeleteFileAsync(string workspaceId, string fileId);
    Task<bool> SetActiveWorkspaceAsync(string workspaceId);
    Task<WorkspaceDetailDto?> GetActiveWorkspaceAsync();
}
