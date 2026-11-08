using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Hosting;

namespace Jellyfin.Plugin.Mega.Services;

/// <summary>
/// Probes each new MEGA item once (duration, codecs, tracks, audio tags) as soon as Jellyfin adds it, through
/// Jellyfin's own refresh queue. Jellyfin skips the media probe of <c>.strm</c> items during scans, and
/// libraries created or refreshed on their own do not run the post-scan tasks.
/// </summary>
public sealed class MegaProbeQueue : IHostedService
{
    private readonly MegaManager _manager;
    private readonly ILibraryManager _libraryManager;
    private readonly IProviderManager _providerManager;
    private readonly IFileSystem _fileSystem;

    public MegaProbeQueue(MegaManager manager, ILibraryManager libraryManager, IProviderManager providerManager, IFileSystem fileSystem)
    {
        _manager = manager;
        _libraryManager = libraryManager;
        _providerManager = providerManager;
        _fileSystem = fileSystem;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded += OnItemAdded;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded -= OnItemAdded;
        return Task.CompletedTask;
    }

    private void OnItemAdded(object? sender, ItemChangeEventArgs e)
    {
        var item = e.Item;
        if (item is not (Video or Audio)
            || item.RunTimeTicks.HasValue
            || !_manager.Configuration.ProbeAfterScan
            || !_manager.IsManagedPath(item.Path)
            || !item.Path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _providerManager.QueueRefresh(
            item.Id,
            new MetadataRefreshOptions(new DirectoryService(_fileSystem))
            {
                EnableRemoteContentProbe = true,
                MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                ImageRefreshMode = MetadataRefreshMode.Default,
            },
            RefreshPriority.Low);
    }
}
