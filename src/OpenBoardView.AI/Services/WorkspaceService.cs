using System.Security.Cryptography;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenBoardView.AI.Models;
using OpenBoardView.AI.Persistence;

namespace OpenBoardView.AI.Services;

public class WorkspaceService : IWorkspaceService
{
    private readonly IPersistenceService _persistence;
    private readonly string _workspacesBaseDir;
    private readonly ILogger<WorkspaceService> _logger;
    private static string? _activeWorkspaceId;
    private static readonly object _lock = new();

    public WorkspaceService(
        IPersistenceService persistence,
        IHostEnvironment env,
        ILogger<WorkspaceService> logger)
    {
        _persistence = persistence;
        _logger = logger;
        _workspacesBaseDir = Path.Combine(env.ContentRootPath, "Data", "Workspaces");
        if (!Directory.Exists(_workspacesBaseDir))
        {
            Directory.CreateDirectory(_workspacesBaseDir);
        }
    }

    public async Task<WorkspaceDto> CreateWorkspaceAsync(string name, string? description, List<FileUploadRequest> files)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("El nombre del espacio de trabajo es requerido.", nameof(name));

        var wsId = Guid.NewGuid().ToString("D");
        await _persistence.InsertWorkspaceAsync(name.Trim(), description?.Trim(), wsId);

        var wsDir = Path.Combine(_workspacesBaseDir, wsId);
        if (!Directory.Exists(wsDir))
        {
            Directory.CreateDirectory(wsDir);
        }

        var savedFiles = new List<WorkspaceFileDto>();

        foreach (var req in files)
        {
            var savedFile = await SaveAndRegisterFileAsync(wsId, wsDir, req);
            savedFiles.Add(savedFile);
        }

        lock (_lock)
        {
            _activeWorkspaceId ??= wsId;
        }

        _logger.LogInformation("[WorkspaceService] Espacio '{Name}' ({Id}) creado con {Count} archivos.", name, wsId, savedFiles.Count);

        return new WorkspaceDto(
            WorkspaceId: wsId,
            Name: name.Trim(),
            Description: description?.Trim(),
            SyncStatus: 0,
            UpdatedAt: DateTime.UtcNow.ToString("o"),
            CreatedAt: DateTime.UtcNow.ToString("o"),
            Files: savedFiles
        );
    }

    public async Task<List<WorkspaceSummaryDto>> GetAllWorkspacesAsync()
    {
        return await _persistence.GetAllWorkspacesAsync();
    }

    public async Task<WorkspaceDetailDto?> GetWorkspaceByIdAsync(string workspaceId)
    {
        return await _persistence.GetWorkspaceByIdAsync(workspaceId);
    }

    public async Task DeleteWorkspaceAsync(string workspaceId)
    {
        await _persistence.DeleteWorkspaceAsync(workspaceId);

        var wsDir = Path.Combine(_workspacesBaseDir, workspaceId);
        try
        {
            if (Directory.Exists(wsDir))
            {
                Directory.Delete(wsDir, recursive: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[WorkspaceService] No se pudo eliminar directorio físico de espacio {Id}.", workspaceId);
        }

        lock (_lock)
        {
            if (_activeWorkspaceId == workspaceId)
            {
                _activeWorkspaceId = null;
            }
        }

        _logger.LogInformation("[WorkspaceService] Espacio {Id} eliminado.", workspaceId);
    }

    public async Task<WorkspaceFileDto> AddFileToWorkspaceAsync(string workspaceId, FileUploadRequest fileRequest)
    {
        var ws = await _persistence.GetWorkspaceByIdAsync(workspaceId);
        if (ws == null)
            throw new KeyNotFoundException($"No se encontró el espacio de trabajo con ID '{workspaceId}'.");

        var wsDir = Path.Combine(_workspacesBaseDir, workspaceId);
        if (!Directory.Exists(wsDir))
        {
            Directory.CreateDirectory(wsDir);
        }

        return await SaveAndRegisterFileAsync(workspaceId, wsDir, fileRequest);
    }

    public async Task DeleteFileAsync(string workspaceId, string fileId)
    {
        var files = await _persistence.GetWorkspaceFilesAsync(workspaceId);
        var target = files.FirstOrDefault(f => f.FileId == fileId);
        if (target != null)
        {
            await _persistence.DeleteWorkspaceFileAsync(fileId);

            var fullPath = Path.Combine(_workspacesBaseDir, workspaceId, Path.GetFileName(target.FilePath));
            try
            {
                if (File.Exists(fullPath))
                {
                    File.Delete(fullPath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[WorkspaceService] No se pudo borrar archivo físico {Path}.", fullPath);
            }
        }
    }

    public async Task<bool> SetActiveWorkspaceAsync(string workspaceId)
    {
        var ws = await _persistence.GetWorkspaceByIdAsync(workspaceId);
        if (ws == null) return false;

        lock (_lock)
        {
            _activeWorkspaceId = workspaceId;
        }

        await _persistence.SetSettingAsync("ActiveWorkspaceId", workspaceId);
        return true;
    }

    public async Task<WorkspaceDetailDto?> GetActiveWorkspaceAsync()
    {
        string? currentId;
        lock (_lock)
        {
            currentId = _activeWorkspaceId;
        }

        if (string.IsNullOrEmpty(currentId))
        {
            currentId = await _persistence.GetSettingAsync("ActiveWorkspaceId");
            if (!string.IsNullOrEmpty(currentId))
            {
                var savedWs = await _persistence.GetWorkspaceByIdAsync(currentId);
                if (savedWs != null)
                {
                    lock (_lock) { _activeWorkspaceId = currentId; }
                    return savedWs;
                }
            }

            var list = await _persistence.GetAllWorkspacesAsync();
            var candidate = list.FirstOrDefault(w => w.FileCount > 0) ?? list.FirstOrDefault();
            if (candidate != null)
            {
                currentId = candidate.WorkspaceId;
                lock (_lock) { _activeWorkspaceId = currentId; }
                await _persistence.SetSettingAsync("ActiveWorkspaceId", currentId);
            }
        }

        return string.IsNullOrEmpty(currentId) ? null : await _persistence.GetWorkspaceByIdAsync(currentId);
    }

    private async Task<WorkspaceFileDto> SaveAndRegisterFileAsync(string wsId, string wsDir, FileUploadRequest req)
    {
        var safeFileName = Path.GetFileName(req.FileName);
        if (string.IsNullOrWhiteSpace(safeFileName))
        {
            safeFileName = $"file_{Guid.NewGuid():N}.bin";
        }

        var destinationPath = Path.Combine(wsDir, safeFileName);
        await File.WriteAllBytesAsync(destinationPath, req.ContentBytes);

        var hash = Convert.ToHexString(SHA256.HashData(req.ContentBytes)).ToLowerInvariant();
        var fileRole = !string.IsNullOrWhiteSpace(req.ExplicitFileType) 
            ? req.ExplicitFileType 
            : FileRoleClassifier.Classify(safeFileName);

        var fileDto = new WorkspaceFileDto(
            FileId: Guid.NewGuid().ToString("D"),
            WorkspaceId: wsId,
            FileName: safeFileName,
            FilePath: Path.Combine("Data", "Workspaces", wsId, safeFileName),
            FileType: fileRole,
            FileHash: hash,
            FileSizeByte: req.ContentBytes.Length,
            SyncStatus: 0,
            UpdatedAt: DateTime.UtcNow.ToString("o")
        );

        await _persistence.AddWorkspaceFileAsync(fileDto);
        return fileDto;
    }
}
