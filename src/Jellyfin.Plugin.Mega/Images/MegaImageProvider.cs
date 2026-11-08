using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Mega.Mega;
using Jellyfin.Plugin.Mega.Services;
using Jellyfin.Plugin.Mega.Streaming;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Mega.Images;

/// <summary>
/// Item images for MEGA files. Jellyfin's own frame grabber skips <c>.strm</c> items; this provider uses
/// the preview MEGA keeps for most videos (decrypted, cached, prefetched by batches during the sync),
/// and otherwise grabs a frame (or the embedded cover of audio files) through the stream endpoint,
/// which only reads the bytes around the chosen position.
/// Runs after the online providers: a TMDb poster wins over a frame preview.
/// </summary>
public class MegaImageProvider : IDynamicImageProvider, IHasOrder
{
    private static readonly SemaphoreSlim FrameSlots = new(2, 2);
    private readonly MegaManager _manager;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly ILogger<MegaImageProvider> _logger;

    public MegaImageProvider(MegaManager manager, IMediaEncoder mediaEncoder, ILogger<MegaImageProvider> logger)
    {
        _manager = manager;
        _mediaEncoder = mediaEncoder;
        _logger = logger;
    }

    public string Name => "MEGA";

    public int Order => 100;

    public bool Supports(BaseItem item)
        => (item is Video || item is Audio)
            && item.Path is string p
            && p.EndsWith(".strm", StringComparison.OrdinalIgnoreCase)
            && _manager.IsManagedPath(p);

    public IEnumerable<ImageType> GetSupportedImages(BaseItem item) => new[] { ImageType.Primary };

    public async Task<DynamicImageResponse> GetImage(BaseItem item, ImageType type, CancellationToken cancellationToken)
    {
        var none = new DynamicImageResponse { HasImage = false };
        var url = !string.IsNullOrEmpty(item.ShortcutPath) ? item.ShortcutPath : MegaManager.ReadStrm(item.Path);
        if (!StreamUrls.TryParse(url, out var sourceId, out var handle))
        {
            return none;
        }

        (MegaSource Source, MegaNode Node)? found;
        try
        {
            found = await _manager.ResolveAsync(sourceId, handle, cancellationToken).ConfigureAwait(false);
        }
        catch (MegaException e)
        {
            _logger.LogDebug("MEGA image of {Name}: {Message}", item.Name, e.Message);
            return none;
        }

        if (found is null)
        {
            return none;
        }

        var (source, node) = found.Value;
        var cfg = _manager.Configuration;
        try
        {
            if (cfg.UseMegaPreviews)
            {
                var preview = await _manager.GetPreviewAsync(source, node, 1, cancellationToken).ConfigureAwait(false);
                if (preview is not null)
                {
                    return FromBytes(preview);
                }
            }

            if (cfg.ExtractFrameWhenNoPreview)
            {
                var frame = await ExtractFrameAsync(item, source, node, cancellationToken).ConfigureAwait(false);
                if (frame is not null)
                {
                    return new DynamicImageResponse { HasImage = true, Path = frame, Protocol = MediaProtocol.File, Format = ImageFormat.Jpg };
                }
            }

            if (cfg.UseMegaPreviews)
            {
                var thumb = await _manager.GetPreviewAsync(source, node, 0, cancellationToken).ConfigureAwait(false);
                if (thumb is not null)
                {
                    return FromBytes(thumb);
                }
            }
        }
        catch (Exception e) when (e is MegaException or IOException or System.Net.Http.HttpRequestException)
        {
            _logger.LogDebug("MEGA image of {Name}: {Message}", item.Name, e.Message);
        }

        return none;
    }

    private static DynamicImageResponse FromBytes(byte[] data)
    {
        var format = data.Length > 4 && data[0] == 0x89 && data[1] == (byte)'P' ? ImageFormat.Png
            : data.Length > 12 && data[0] == (byte)'R' && data[8] == (byte)'W' ? ImageFormat.Webp
            : ImageFormat.Jpg;
        return new DynamicImageResponse { HasImage = true, Format = format, Stream = new MemoryStream(data, false) };
    }

    /// <summary>Frame at 10 % of the video (or the cover of an audio file), read through the stream endpoint.</summary>
    private async Task<string?> ExtractFrameAsync(BaseItem item, MegaSource source, MegaNode node, CancellationToken cancellationToken)
    {
        var encoder = _mediaEncoder.EncoderPath;
        if (string.IsNullOrEmpty(encoder) || !File.Exists(encoder))
        {
            return null;
        }

        var dir = Path.Combine(_manager.CachePath, "frames");
        Directory.CreateDirectory(dir);
        var output = Path.Combine(dir, source.Id + "-" + BlockDiskCache.SafeName(node.Handle) + ".jpg");
        if (File.Exists(output) && new FileInfo(output).Length > 0)
        {
            return output;
        }

        var url = StreamUrls.Build(_manager.StreamBaseUrl, _manager.Configuration.StreamSecret, source.Id, node);
        bool isAudio = item is Audio;
        double seconds = 0;
        if (!isAudio)
        {
            seconds = item.RunTimeTicks is long ticks && ticks > 0
                ? Math.Min(TimeSpan.FromTicks(ticks).TotalSeconds * 0.1, 600)
                : 20;
        }

        await FrameSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var at in seconds > 0 ? new[] { seconds, 0 } : new[] { 0.0 })
            {
                if (await RunFfmpegAsync(encoder, url, at, output, cancellationToken).ConfigureAwait(false))
                {
                    return output;
                }
            }
        }
        finally
        {
            FrameSlots.Release();
        }

        return null;
    }

    private async Task<bool> RunFfmpegAsync(string encoder, string url, double at, string output, CancellationToken cancellationToken)
    {
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin" };
        if (at > 0)
        {
            args.AddRange(new[] { "-ss", at.ToString("0.###", CultureInfo.InvariantCulture) });
        }

        args.AddRange(new[] { "-i", url, "-map", "0:v:0", "-frames:v", "1", "-vf", "scale='min(1280,iw)':-2", "-q:v", "3", "-y", output });
        var psi = new ProcessStartInfo(encoder)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            _ = process.StandardOutput.ReadToEndAsync(cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(90));
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                _logger.LogDebug("MEGA frame grab failed: {Error}", await stderr.ConfigureAwait(false));
            }

            return process.ExitCode == 0 && File.Exists(output) && new FileInfo(output).Length > 0;
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(true);
            }
            catch (InvalidOperationException)
            {
            }

            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return false;
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            _logger.LogDebug(e, "MEGA: cannot start ffmpeg");
            return false;
        }
    }
}
