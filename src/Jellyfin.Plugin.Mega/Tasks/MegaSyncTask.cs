using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Mega.Library;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.Mega.Tasks;

/// <summary>Scheduled task: downloads the MEGA trees again and synchronises the virtual libraries.</summary>
public class MegaSyncTask : IScheduledTask
{
    private readonly LibrarySynchronizer _sync;

    public MegaSyncTask(LibrarySynchronizer sync)
    {
        _sync = sync;
    }

    public string Name => "Synchronize MEGA libraries";

    public string Key => "MegaFsSync";

    public string Description => "Reloads the MEGA folders and updates the libraries (new, renamed and deleted files). Changes are also picked up automatically every few minutes.";

    public string Category => "MEGA";

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        => _sync.SyncAllAsync(true, progress, cancellationToken);

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromHours(24).Ticks,
        };
    }
}
