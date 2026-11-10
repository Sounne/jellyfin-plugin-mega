using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Mega.Mega;
using Jellyfin.Plugin.Mega.Services;
using Jellyfin.Plugin.Mega.Streaming;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.IO;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Mega.Subtitles;

/// <summary>
/// Fills Jellyfin's cache of extracted subtitles for MEGA items before Jellyfin needs it.
/// <para>
/// When a client cannot take embedded subtitles with the video (transcoding, subtitles shown by the
/// client, burned-in text), Jellyfin extracts them with ffmpeg into its cache, all tracks of the file in
/// one run that reads the whole file. For a MEGA file that is minutes and gigabytes of transfer quota
/// before the first subtitle shows (players give up). Here only the subtitle blocks are read, through the
/// Matroska index, and written where and as Jellyfin's extraction writes them, so Jellyfin finds them done.
/// </para>
/// </summary>
public sealed class SubtitlePrefill
{
    private static readonly TimeSpan MaxDuration = TimeSpan.FromMinutes(5);
    private readonly MegaManager _manager;
    private readonly IPathManager _paths;
    private readonly ILogger<SubtitlePrefill> _logger;
    private readonly ConcurrentDictionary<Guid, Lazy<Task<bool>>> _running = new();
    private readonly ConcurrentDictionary<string, byte> _refused = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _slots = new(2, 2);

    public SubtitlePrefill(MegaManager manager, IPathManager paths, ILogger<SubtitlePrefill> logger)
    {
        _manager = manager;
        _paths = paths;
        _logger = logger;
    }

    /// <summary>Gets the reads in flight at once for one file.</summary>
    internal int ParallelReads { get; init; } = 48;

    /// <summary>
    /// Fills the cache for the embedded subtitles of <paramref name="item"/> (or joins the run in progress).
    /// True when every one of them is in the cache; false when Jellyfin will extract them itself.
    /// </summary>
    public Task<bool> PrefillAsync(BaseItem item)
    {
        var missing = GetMissing(item, out var all);
        if (all is null)
        {
            return Task.FromResult(false);
        }

        if (missing.Count == 0)
        {
            return Task.FromResult(true);
        }

        return _running.GetOrAdd(item.Id, _ => new Lazy<Task<bool>>(() => RunAsync(item, all, missing))).Value;
    }

    /// <summary>Gets a value indicating whether <paramref name="stream"/> is an embedded subtitle Jellyfin would extract.</summary>
    internal static bool IsExtractable(MediaStream stream)
        => stream.Type == MediaStreamType.Subtitle && !stream.IsExternal && (stream.IsTextSubtitleStream || IsPgs(stream.Codec) || IsVobSub(stream.Codec));

    private static bool IsPgs(string? codec) => codec?.Contains("pgs", StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsVobSub(string? codec)
        => codec is not null && (codec.Contains("dvdsub", StringComparison.OrdinalIgnoreCase) || codec.Contains("vobsub", StringComparison.OrdinalIgnoreCase));

    /// <summary>Extension of the extracted file, exactly as Jellyfin names it.</summary>
    private static string Extension(MediaStream stream)
    {
        if (string.Equals(stream.Codec, "pgssub", StringComparison.OrdinalIgnoreCase))
        {
            return "sup";
        }

        if (string.Equals(stream.Codec, "ass", StringComparison.OrdinalIgnoreCase) || string.Equals(stream.Codec, "ssa", StringComparison.OrdinalIgnoreCase))
        {
            return stream.Codec;
        }

        return "srt";
    }

    /// <summary>
    /// Embedded subtitles of the item whose cache file is missing. <paramref name="all"/> is every embedded
    /// subtitle with its cache path (index order), or null when this item cannot be done here.
    /// </summary>
    private List<(MediaStream Stream, string Path)> GetMissing(BaseItem item, out List<(MediaStream Stream, string Path)>? all)
    {
        all = null;
        var missing = new List<(MediaStream, string)>();
        if (!_manager.TryResolveItem(item, out var source, out var node)
            || !IsMatroska(node.Name)
            || _refused.ContainsKey(Key(source, node)))
        {
            return missing;
        }

        var subtitles = item.GetMediaStreams().Where(s => s.Type == MediaStreamType.Subtitle && !s.IsExternal).OrderBy(s => s.Index).ToList();
        if (subtitles.Count == 0 || subtitles.Any(s => !IsExtractable(s) || IsVobSub(s.Codec)))
        {
            // Nothing to extract, or a track that only ffmpeg can extract (VobSub): Jellyfin reads the file anyway.
            return missing;
        }

        var list = new List<(MediaStream, string)>();
        var id = item.Id.ToString("N");
        foreach (var s in subtitles)
        {
            if (_paths.GetSubtitlePath(id, s.Index, "." + Extension(s)) is not string path)
            {
                return missing;
            }

            list.Add((s, path));
            if (!IsDone(path))
            {
                missing.Add((s, path));
            }
        }

        all = list;
        return missing;
    }

    private static bool IsMatroska(string name)
        => name.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".mk3d", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".webm", StringComparison.OrdinalIgnoreCase);

    private static bool IsDone(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists && info.Length > 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string Key(MegaSource source, MegaNode node) => $"{source.Id}/{node.Handle}/{node.Size}";

    private async Task<bool> RunAsync(BaseItem item, List<(MediaStream Stream, string Path)> all, List<(MediaStream Stream, string Path)> missing)
    {
        await Task.Yield();
        using var cts = new CancellationTokenSource(MaxDuration);
        var ct = cts.Token;
        string? refuseKey = null;
        try
        {
            await _slots.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (!_manager.TryResolveItem(item, out var source, out var node))
                {
                    return false;
                }

                refuseKey = Key(source, node);
                var watch = Stopwatch.StartNew();
                var reader = new MegaRangeReader(source, node);
                var index = await MatroskaSubtitles.ReadIndexAsync(new BufferedRangeReader(reader), ct).ConfigureAwait(false)
                    ?? throw new NotSupportedException("no Matroska index (cues)");

                // ffprobe lists the tracks in file order: the k-th subtitle track is the k-th subtitle stream.
                var tracks = index.Tracks.Where(t => t.IsSubtitle).ToList();
                if (tracks.Count != all.Count)
                {
                    throw new NotSupportedException($"{tracks.Count} subtitle tracks in the file, {all.Count} known to Jellyfin");
                }

                var work = new List<(MatroskaTrack Track, string Path)>();
                for (int i = 0; i < all.Count; i++)
                {
                    if (!Matches(tracks[i], all[i].Stream))
                    {
                        throw new NotSupportedException($"track {tracks[i].Number} ({tracks[i].CodecId}) does not match stream {all[i].Stream.Index} ({all[i].Stream.Codec})");
                    }

                    if (missing.Any(m => m.Stream.Index == all[i].Stream.Index))
                    {
                        work.Add((tracks[i], all[i].Path));
                    }
                }

                var blocks = await MatroskaSubtitles.ReadBlocksAsync(reader, index, work.Select(w => w.Track).ToList(), ParallelReads, ct).ConfigureAwait(false);
                foreach (var (track, path) in work)
                {
                    Save(path, SubtitleFiles.Write(track, blocks.Events[track.Number], index.TimestampScale));
                }

                _logger.LogInformation(
                    "MEGA: {Count} embedded subtitle track(s) of {Name} ready in {Seconds:0.0} s ({Requests} reads, {KiB} KiB) instead of a full read by ffmpeg",
                    work.Count,
                    node.Name,
                    watch.Elapsed.TotalSeconds,
                    blocks.Requests,
                    blocks.Bytes / 1024);
                return true;
            }
            finally
            {
                _slots.Release();
            }
        }
        catch (Exception e) when (e is NotSupportedException or InvalidDataException)
        {
            if (refuseKey is not null)
            {
                _refused.TryAdd(refuseKey, 0);
            }

            _logger.LogInformation("MEGA: embedded subtitles of {Name} left to Jellyfin's extraction: {Reason}", item.Name, e.Message);
            return false;
        }
        catch (Exception e) when (e is MegaException or HttpRequestException or IOException or OperationCanceledException or UnauthorizedAccessException)
        {
            _logger.LogWarning("MEGA: cannot read the embedded subtitles of {Name}: {Reason}", item.Name, e.Message);
            return false;
        }
        finally
        {
            _running.TryRemove(item.Id, out _);
        }
    }

    private static bool Matches(MatroskaTrack track, MediaStream stream) => (track.Format, Extension(stream).ToLowerInvariant()) switch
    {
        (SubtitleFormat.Srt, "srt") => stream.IsTextSubtitleStream,
        (SubtitleFormat.Ass, "ass" or "ssa") => true,
        (SubtitleFormat.Sup, "sup") => true,
        _ => false,
    };

    private void Save(string path, byte[] content)
    {
        var tmp = path + ".mega-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(tmp, content);
            if (!IsDone(path))
            {
                File.Move(tmp, path, true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Jellyfin writing the same file at the same moment: its copy is as good.
            _logger.LogDebug(e, "MEGA: subtitle cache file {Path} not written", path);
        }
        finally
        {
            try
            {
                File.Delete(tmp);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(e, "MEGA: {Path} not deleted", tmp);
            }
        }
    }
}
