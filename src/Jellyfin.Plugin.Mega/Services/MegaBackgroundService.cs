using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Mega.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Mega.Services;

/// <summary>
/// Loads the MEGA trees at start-up (from the local cache when possible, so stream URLs work right away), then
/// checks MEGA for changes every few minutes (cheap action-packet poll, changes applied in place) and
/// synchronises right after the settings are saved.
/// </summary>
public sealed class MegaBackgroundService : BackgroundService
{
    private readonly MegaManager _manager;
    private readonly LibrarySynchronizer _sync;
    private readonly ILogger<MegaBackgroundService> _logger;
    private readonly SemaphoreSlim _settingsChanged = new(0, 1);

    public MegaBackgroundService(MegaManager manager, LibrarySynchronizer sync, ILogger<MegaBackgroundService> logger)
    {
        _manager = manager;
        _sync = sync;
        _logger = logger;
        _manager.SettingsChanged += (_, _) =>
        {
            if (_settingsChanged.CurrentCount == 0)
            {
                try
                {
                    _settingsChanged.Release();
                }
                catch (SemaphoreFullException)
                {
                }
            }
        };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Trees saved at the last run: stream URLs and playback work at once.
            await RunSafe(() => Task.Run(
                () =>
                {
                    foreach (var source in _manager.Sources)
                    {
                        if (source.LoadCachedTree())
                        {
                            _logger.LogInformation("MEGA {Account}: {Count} nodes read from the local cache", source.Config.Name, source.Tree?.Count);
                        }
                    }
                },
                stoppingToken)).ConfigureAwait(false);

            // Then catch up with MEGA (changes since the last run) and synchronise the libraries.
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken).ConfigureAwait(false);
            await RunSafe(() => _sync.SyncAllAsync(false, null, stoppingToken)).ConfigureAwait(false);
            while (!stoppingToken.IsCancellationRequested)
            {
                var interval = TimeSpan.FromMinutes(Math.Clamp(_manager.Configuration.SyncIntervalMinutes, 1, 1440));
                bool settings = await _settingsChanged.WaitAsync(interval, stoppingToken).ConfigureAwait(false);
                if (settings)
                {
                    // Let several quick saves settle.
                    await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken).ConfigureAwait(false);
                    await RunSafe(() => _sync.SyncAllAsync(false, null, stoppingToken)).ConfigureAwait(false);
                }
                else
                {
                    await RunSafe(() => _sync.CheckForChangesAsync(stoppingToken)).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task RunSafe(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogError(e, "MEGA synchronisation failed");
        }
    }
}
