using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenBoardView.AI.Persistence;

namespace OpenBoardView.AI.Services;

public class BackgroundSyncWorker : BackgroundService
{
    private readonly IPersistenceService _persistenceService;
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _config;
    private readonly ILogger<BackgroundSyncWorker> _logger;
    private readonly TimeSpan _syncInterval;
    private readonly string _syncEndpoint;
    private readonly bool _enabled;

    public BackgroundSyncWorker(
        IPersistenceService persistenceService,
        HttpClient httpClient,
        IConfiguration config,
        ILogger<BackgroundSyncWorker> logger)
    {
        _persistenceService = persistenceService;
        _httpClient = httpClient;
        _config = config;
        _logger = logger;

        int intervalMinutes = config.GetValue("CloudSync:IntervalMinutes", 5);
        _syncInterval = TimeSpan.FromMinutes(Math.Max(1, intervalMinutes));
        _syncEndpoint = config["CloudSync:Endpoint"] ?? "http://api.internal/v1/telemetry/repairs";
        _enabled = config.GetValue("CloudSync:Enabled", true);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[BackgroundSyncWorker] Servicio de sincronización en segundo plano iniciado (Intervalo: {Minutes}m, Habilitado: {Enabled}).", _syncInterval.TotalMinutes, _enabled);

        // Esperar unos segundos iniciales para permitir que el servidor Kestrel termine de iniciar completamente
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            if (_enabled)
            {
                try
                {
                    await SyncUnsyncedRepairsAsync(stoppingToken);
                }
                catch (HttpRequestException ex)
                {
                    _logger.LogDebug("[BackgroundSyncWorker] Endpoint de telemetría no disponible ({Message}). Se reintentará en el próximo ciclo.", ex.Message);
                }
                catch (TaskCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[BackgroundSyncWorker] Error no crítico durante ciclo de sincronización.");
                }
            }

            try
            {
                await Task.Delay(_syncInterval, stoppingToken);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }
    }

    private async Task SyncUnsyncedRepairsAsync(CancellationToken cancellationToken)
    {
        var unsyncedRepairs = await _persistenceService.GetUnsyncedRepairsAsync();
        if (!unsyncedRepairs.Any()) return;

        _logger.LogInformation("[BackgroundSyncWorker] Sincronizando {Count} reparaciones pendientes hacia {Endpoint}...", unsyncedRepairs.Count, _syncEndpoint);

        var response = await _httpClient.PostAsJsonAsync(_syncEndpoint, unsyncedRepairs, cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            var syncedIds = unsyncedRepairs.Where(r => !string.IsNullOrEmpty(r.RepairId)).Select(r => r.RepairId!).ToList();
            await _persistenceService.MarkRepairsAsSyncedAsync(syncedIds);
            _logger.LogInformation("[BackgroundSyncWorker] {Count} reparaciones sincronizadas con éxito en la nube.", syncedIds.Count);
        }
        else
        {
            _logger.LogDebug("[BackgroundSyncWorker] Respuesta de telemetría HTTP {StatusCode}.", response.StatusCode);
        }
    }
}
