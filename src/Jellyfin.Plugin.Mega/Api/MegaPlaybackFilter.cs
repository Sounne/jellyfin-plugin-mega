using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Mega.Services;
using Jellyfin.Plugin.Mega.Streaming;
using Jellyfin.Plugin.Mega.Subtitles;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Mega.Api;

/// <summary>
/// Makes MEGA items behave like local files for every client.
/// <list type="bullet">
/// <item>Playback info: a <c>.strm</c> media source is "remote" and some clients (web browsers) would
/// fetch its URL themselves, which only the server can reach. The source is shown as a local file, so
/// clients ask the server's own stream endpoint, like for any file on disk.</item>
/// <item>Static streams (<c>/Videos/{id}/stream?static=true</c>, <c>/Audio/{id}/stream</c>) of MEGA items are
/// served directly with byte ranges (seeking), without the server calling itself.</item>
/// <item>Universal audio never redirects clients to the server-local URL.</item>
/// <item>ffprobe and ffmpeg runs for MEGA items analyse a few seconds of the file, not 200 (<see cref="FfmpegAnalysis"/>).</item>
/// <item>Embedded subtitles a client cannot take with the video are put in Jellyfin's subtitle cache from the
/// subtitle blocks alone (<see cref="SubtitlePrefill"/>): started by the playback info, awaited by the subtitle
/// and burn-in requests, which would otherwise make Jellyfin read the whole file first.</item>
/// </list>
/// </summary>
public sealed class MegaPlaybackFilter : IAsyncActionFilter, IAsyncResultFilter
{
    private static readonly HashSet<string> StaticStreamActions = new(StringComparer.Ordinal)
    {
        "GetVideoStream", "GetVideoStreamByContainer", "GetAudioStream", "GetAudioStreamByContainer",
    };

    /// <summary>Controllers that serve, probe or transcode media (the others never start ffmpeg for an item).</summary>
    private static readonly HashSet<string> MediaControllers = new(StringComparer.Ordinal)
    {
        "MediaInfo", "Videos", "Audio", "UniversalAudio", "DynamicHls", "HlsSegment", "VideoHls", "Subtitle",
    };

    /// <summary>Subtitle actions that serve a subtitle stream (Jellyfin extracts embedded ones first).</summary>
    private static readonly HashSet<string> SubtitleActions = new(StringComparer.Ordinal)
    {
        "GetSubtitle", "GetSubtitleWithTicks", "GetSubtitlePlaylist",
    };

    private static readonly TimeSpan SubtitleWait = TimeSpan.FromMinutes(3);

    private readonly MegaManager _manager;
    private readonly ILibraryManager _libraryManager;
    private readonly SubtitlePrefill _subtitles;
    private readonly ILogger<MegaPlaybackFilter> _logger;

    public MegaPlaybackFilter(MegaManager manager, ILibraryManager libraryManager, SubtitlePrefill subtitles, ILogger<MegaPlaybackFilter> logger)
    {
        _manager = manager;
        _libraryManager = libraryManager;
        _subtitles = subtitles;
        _logger = logger;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (context.ActionDescriptor is not ControllerActionDescriptor action
            || !MediaControllers.Contains(action.ControllerName)
            || GetItem(context) is not BaseItem item
            || !_manager.IsManagedPath(item.Path))
        {
            await next().ConfigureAwait(false);
            return;
        }

        if (StaticStreamActions.Contains(action.ActionName)
            && context.ActionArguments.TryGetValue("static", out var st) && st is true)
        {
            var url = !string.IsNullOrEmpty(item.ShortcutPath) ? item.ShortcutPath : MegaManager.ReadStrm(item.Path);
            if (StreamUrls.TryParse(url, out var sourceId, out var handle))
            {
                var found = await _manager.ResolveAsync(sourceId, handle, context.HttpContext.RequestAborted).ConfigureAwait(false);
                if (found is not null)
                {
                    context.Result = new MegaRangeResult(_manager, found.Value.Source, found.Value.Node, _logger);
                    return;
                }
            }
        }
        else if (action.ActionName == "GetUniversalAudioStream" && context.ActionArguments.ContainsKey("enableRedirection"))
        {
            context.ActionArguments["enableRedirection"] = false;
        }

        // Embedded subtitles this request makes Jellyfin extract: from the subtitle blocks, not the whole file.
        if (NeedsExtractedSubtitles(action, context.ActionArguments, item)
            && !await WaitForSubtitlesAsync(item, context.HttpContext.RequestAborted).ConfigureAwait(false))
        {
            context.Result = new EmptyResult();
            return;
        }

        // ffprobe (playback info probes .strm items again) and ffmpeg (transcode, seek, subtitles) started by
        // this request: short analysis of the MEGA file.
        using var analysis = FfmpegAnalysis.Enter();
        await next().ConfigureAwait(false);
    }

    public async Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        if (context.Result is ObjectResult { Value: not null } result)
        {
            try
            {
                switch (result.Value)
                {
                    case PlaybackInfoResponse info:
                        StartSubtitles(info.MediaSources);
                        Patch(info.MediaSources);
                        break;
                    case BaseItemDto dto:
                        Patch(dto.MediaSources);
                        break;
                    case QueryResult<BaseItemDto> query when query.Items is not null:
                        foreach (var dto in query.Items)
                        {
                            Patch(dto.MediaSources);
                        }

                        break;
                }
            }
            catch (Exception e) when (e is InvalidOperationException or ArgumentException or NullReferenceException)
            {
                _logger.LogDebug(e, "MEGA: playback info left unchanged");
            }
        }

        await next().ConfigureAwait(false);
    }

    private static readonly string[] MediaSourceArguments = { "mediaSourceId", "routeMediaSourceId" };

    private static readonly string[] ItemArguments = { "itemId", "routeItemId" };

    private BaseItem? GetItem(ActionExecutingContext context)
    {
        var args = context.ActionArguments;
        foreach (var name in MediaSourceArguments)
        {
            if (args.TryGetValue(name, out var ms) && ms is string s && Guid.TryParse(s, out var g) && g != Guid.Empty)
            {
                return _libraryManager.GetItemById(g);
            }
        }

        foreach (var name in ItemArguments)
        {
            if (args.TryGetValue(name, out var it) && it is Guid g && g != Guid.Empty)
            {
                return _libraryManager.GetItemById(g);
            }
        }

        return null;
    }

    private static int? IntArgument(IDictionary<string, object?> args, params string[] names)
    {
        foreach (var name in names)
        {
            if (args.TryGetValue(name, out var value) && value is int i)
            {
                return i;
            }
        }

        return null;
    }

    /// <summary>
    /// Whether Jellyfin will need the extracted copy of an embedded subtitle of <paramref name="item"/>:
    /// to serve it, or to burn text subtitles into a transcode.
    /// </summary>
    private static bool NeedsExtractedSubtitles(ControllerActionDescriptor action, IDictionary<string, object?> args, BaseItem item)
    {
        bool burnIn = false;
        int? index;
        if (action.ControllerName == "Subtitle")
        {
            index = SubtitleActions.Contains(action.ActionName) ? IntArgument(args, "index", "routeIndex") : null;
        }
        else
        {
            index = IntArgument(args, "subtitleStreamIndex");
            burnIn = args.TryGetValue("subtitleMethod", out var method) && method is SubtitleDeliveryMethod.Encode;
            if (!burnIn)
            {
                return false;
            }
        }

        if (index is not int i || i < 0)
        {
            return false;
        }

        var stream = item.GetMediaStreams().FirstOrDefault(s => s.Index == i);
        return stream is not null && SubtitlePrefill.IsExtractable(stream) && (!burnIn || stream.IsTextSubtitleStream);
    }

    /// <summary>Waits for the subtitle cache of the item (false: the client went away).</summary>
    private async Task<bool> WaitForSubtitlesAsync(BaseItem item, CancellationToken cancellationToken)
    {
        try
        {
            await _subtitles.PrefillAsync(item).WaitAsync(SubtitleWait, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("MEGA: subtitles of {Name} still not read after {Minutes} minutes, Jellyfin extracts them", item.Name, SubtitleWait.TotalMinutes);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        return true;
    }

    /// <summary>Starts filling the subtitle cache when the client may ask for an embedded subtitle as a separate file.</summary>
    private void StartSubtitles(IEnumerable<MediaSourceInfo>? sources)
    {
        foreach (var source in sources ?? Enumerable.Empty<MediaSourceInfo>())
        {
            if (source?.MediaStreams is null || !IsMega(source) || !Guid.TryParse(source.Id, out var id)
                || !source.MediaStreams.Any(s => s.Type == MediaStreamType.Subtitle && !s.IsExternal
                    && (s.DeliveryMethod is SubtitleDeliveryMethod.External or SubtitleDeliveryMethod.Hls
                        || (s.DeliveryMethod == SubtitleDeliveryMethod.Encode && s.IsTextSubtitleStream)))
                || _libraryManager.GetItemById(id) is not BaseItem item
                || !_manager.IsManagedPath(item.Path))
            {
                continue;
            }

            _ = Task.Run(() => _subtitles.PrefillAsync(item));
        }
    }

    private static bool IsMega(MediaSourceInfo? source)
        => source?.Path is not null && source.Path.IndexOf(StreamUrls.Marker, StringComparison.OrdinalIgnoreCase) >= 0;

    private void Patch(IEnumerable<MediaSourceInfo>? sources)
    {
        if (sources is null)
        {
            return;
        }

        foreach (var source in sources)
        {
            if (source is null || !IsMega(source))
            {
                continue;
            }

            string localPath = "MEGA";
            if (Guid.TryParse(source.Id, out var id) && _libraryManager.GetItemById(id) is BaseItem item && !string.IsNullOrEmpty(item.Path))
            {
                localPath = item.Path;
            }

            // Real file size (Jellyfin reports the size of the .strm file).
            if (_manager.TryResolveUrl(source.Path, out _, out var node))
            {
                source.Size = node.Size;
            }

            source.Path = localPath;
            source.Protocol = MediaProtocol.File;
            source.IsRemote = false;
        }
    }
}
