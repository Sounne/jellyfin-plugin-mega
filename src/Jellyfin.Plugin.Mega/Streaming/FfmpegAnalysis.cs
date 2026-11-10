using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Mega.Streaming;

/// <summary>
/// Short ffmpeg/ffprobe analysis for MEGA items.
/// <para>
/// Jellyfin starts ffprobe and ffmpeg with <c>-analyzeduration 200M</c> (its <c>FFmpeg:analyzeduration</c>
/// setting). When a track cannot be fully described from the file header (PGS subtitles in MKV, for example),
/// ffmpeg reads 200 seconds of the file before giving up: about 350 MB for a 1080p movie. For a MEGA file that
/// is 10-15 seconds and as much transfer quota, at every playback start (Jellyfin probes <c>.strm</c> items again
/// in each playback info request), at every start and seek of a transcode and at every library probe.
/// </para>
/// <para>
/// This configuration provider answers that one setting with a few seconds, and only while Jellyfin works for a
/// MEGA item (inside <see cref="Enter"/>). Every other file keeps Jellyfin's setting.
/// </para>
/// </summary>
internal sealed class FfmpegAnalysis : ConfigurationProvider, IConfigurationSource
{
    internal const string AnalyzeDurationKey = "FFmpeg:analyzeduration";

    private static readonly AsyncLocal<bool> ForMega = new();

    private readonly Func<int> _seconds;

    public FfmpegAnalysis(Func<int> seconds)
    {
        _seconds = seconds;
    }

    /// <summary>Gets a value indicating whether the current async flow works for a MEGA item.</summary>
    public static bool IsActive => ForMega.Value;

    /// <summary>Short analysis for every ffmpeg/ffprobe run started from the current async flow, until disposed.</summary>
    public static Scope Enter()
    {
        var previous = ForMega.Value;
        ForMega.Value = true;
        return new Scope(previous);
    }

    /// <summary>
    /// Adds the provider to Jellyfin's configuration, last so it is asked first. Returns false when the
    /// configuration cannot take one more provider (Jellyfin's setting then applies to MEGA items too).
    /// </summary>
    public static bool Install(IConfiguration configuration, Func<int> seconds, ILogger logger)
    {
        try
        {
            if (configuration is IConfigurationRoot installed && installed.Providers.OfType<FfmpegAnalysis>().Any())
            {
                return true;
            }

            switch (configuration)
            {
                // ConfigurationManager: sources can be added at any time.
                case IConfigurationBuilder builder:
                    builder.Add(new FfmpegAnalysis(seconds));
                    return true;

                // ConfigurationRoot (generic host): the provider list it reads on every lookup.
                case IConfigurationRoot root when root.Providers is IList<IConfigurationProvider> { IsReadOnly: false } providers:
                    providers.Add(new FfmpegAnalysis(seconds));
                    return true;
            }
        }
        catch (Exception e) when (e is NotSupportedException or InvalidOperationException)
        {
            logger.LogDebug(e, "MEGA: configuration provider not added");
        }

        logger.LogWarning(
            "MEGA: cannot shorten the ffmpeg analysis of MEGA files ({Type}); playback of files with PGS subtitles may start slowly",
            configuration.GetType().Name);
        return false;
    }

    public IConfigurationProvider Build(IConfigurationBuilder builder) => this;

    public override bool TryGet(string key, out string? value)
    {
        if (ForMega.Value && string.Equals(key, AnalyzeDurationKey, StringComparison.OrdinalIgnoreCase))
        {
            var seconds = _seconds();
            if (seconds > 0)
            {
                // Microseconds, as ffmpeg takes them.
                value = (seconds * 1_000_000L).ToString(CultureInfo.InvariantCulture);
                return true;
            }
        }

        value = null;
        return false;
    }

    /// <summary>Read-only: settings written at run time go to the other providers.</summary>
    public override void Set(string key, string? value)
    {
    }

    public readonly struct Scope : IDisposable
    {
        private readonly bool _previous;

        internal Scope(bool previous)
        {
            _previous = previous;
        }

        public void Dispose() => ForMega.Value = _previous;
    }
}
