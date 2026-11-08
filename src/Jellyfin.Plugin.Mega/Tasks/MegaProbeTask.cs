using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Mega.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Mega.Tasks;

/// <summary>
/// After each library scan: probes the new MEGA items once (duration, codecs, audio tags) through the
/// stream endpoint. Jellyfin does not probe <c>.strm</c> items during scans, so without this they would
/// have no duration and music would have no tags until played. The start / end of each file stays in the
/// disk cache afterwards, which makes the first playback start faster.
/// </summary>
public class MegaProbeTask : ILibraryPostScanTask
{
    private readonly MegaManager _manager;
    private readonly ILibraryManager _libraryManager;
    private readonly IFileSystem _fileSystem;
    private readonly ILogger<MegaProbeTask> _logger;

    public MegaProbeTask(MegaManager manager, ILibraryManager libraryManager, IFileSystem fileSystem, ILogger<MegaProbeTask> logger)
    {
        _manager = manager;
        _libraryManager = libraryManager;
        _fileSystem = fileSystem;
        _logger = logger;
    }

    public async Task Run(IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (!_manager.Configuration.ProbeAfterScan || _manager.Configuration.Libraries.Count == 0)
        {
            return;
        }

        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Episode, BaseItemKind.Video, BaseItemKind.MusicVideo, BaseItemKind.Audio, BaseItemKind.AudioBook },
            IsVirtualItem = false,
            Recursive = true,
        })
            .Where(i => !i.RunTimeTicks.HasValue && _manager.IsManagedPath(i.Path) && i.Path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (items.Count == 0)
        {
            return;
        }

        _logger.LogInformation("MEGA: probing {Count} new items", items.Count);
        int done = 0;
        await Parallel.ForEachAsync(
            items,
            new ParallelOptions { MaxDegreeOfParallelism = 3, CancellationToken = cancellationToken },
            async (item, ct) =>
            {
                try
                {
                    await item.RefreshMetadata(
                        new MetadataRefreshOptions(new DirectoryService(_fileSystem))
                        {
                            EnableRemoteContentProbe = true,
                            MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                            ImageRefreshMode = MetadataRefreshMode.Default,
                        },
                        ct).ConfigureAwait(false);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    _logger.LogWarning("MEGA: cannot probe {Name}: {Message}", item.Name, e.Message);
                }

                progress.Report(100.0 * Interlocked.Increment(ref done) / items.Count);
            }).ConfigureAwait(false);
        _logger.LogInformation("MEGA: probing done");
    }
}
