using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Jellyfin.Plugin.Mega.Streaming;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Mega.Services;

/// <summary>
/// Probes each new MEGA item once (duration, codecs, tracks, audio tags) as soon as Jellyfin adds it.
/// Jellyfin skips the media probe of <c>.strm</c> items during scans, and libraries created or refreshed on
/// their own do not run the post-scan tasks. The probes run here rather than in Jellyfin's refresh queue so
/// they get the short ffmpeg analysis of MEGA files (<see cref="FfmpegAnalysis"/>).
/// </summary>
public sealed class MegaProbeQueue : BackgroundService
{
    private const int Workers = 2;

    private readonly MegaManager _manager;
    private readonly ILibraryManager _libraryManager;
    private readonly IFileSystem _fileSystem;
    private readonly ILogger<MegaProbeQueue> _logger;
    private readonly Channel<Guid> _queue = Channel.CreateUnbounded<Guid>();
    private readonly ConcurrentDictionary<Guid, byte> _queued = new();

    public MegaProbeQueue(MegaManager manager, ILibraryManager libraryManager, IFileSystem fileSystem, ILogger<MegaProbeQueue> logger)
    {
        _manager = manager;
        _libraryManager = libraryManager;
        _fileSystem = fileSystem;
        _logger = logger;
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded += OnItemAdded;
        return base.StartAsync(cancellationToken);
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded -= OnItemAdded;
        return base.StopAsync(cancellationToken);
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
        => Task.WhenAll(Enumerable.Range(0, Workers).Select(_ => Task.Run(() => WorkAsync(stoppingToken), stoppingToken)));

    private static bool NeedsProbe(BaseItem item)
        => item is Video or Audio
            && !item.RunTimeTicks.HasValue
            && item.Path is string path
            && path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase);

    private void OnItemAdded(object? sender, ItemChangeEventArgs e)
    {
        var item = e.Item;
        if (NeedsProbe(item)
            && _manager.Configuration.ProbeAfterScan
            && _manager.IsManagedPath(item.Path)
            && _queued.TryAdd(item.Id, 0))
        {
            _queue.Writer.TryWrite(item.Id);
        }
    }

    private async Task WorkAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var id in _queue.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                _queued.TryRemove(id, out _);
                if (_libraryManager.GetItemById(id) is not BaseItem item || !NeedsProbe(item))
                {
                    continue;
                }

                try
                {
                    using var analysis = FfmpegAnalysis.Enter();
                    await item.RefreshMetadata(
                        new MetadataRefreshOptions(new DirectoryService(_fileSystem))
                        {
                            EnableRemoteContentProbe = true,
                            MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                            ImageRefreshMode = MetadataRefreshMode.Default,
                        },
                        stoppingToken).ConfigureAwait(false);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    _logger.LogWarning("MEGA: cannot probe {Name}: {Message}", item.Name, e.Message);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
