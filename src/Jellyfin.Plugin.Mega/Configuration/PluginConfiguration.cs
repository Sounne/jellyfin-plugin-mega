using System;
using System.Collections.Generic;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Mega.Configuration;

/// <summary>Plugin settings.</summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Gets or sets the MEGA accounts and public folder links.</summary>
#pragma warning disable CA2227, CA1002
    public List<MegaAccountConfig> Accounts { get; set; } = new();

    /// <summary>Gets or sets the MEGA folders mirrored as Jellyfin libraries.</summary>
    public List<MegaLibraryConfig> Libraries { get; set; } = new();
#pragma warning restore CA2227, CA1002

    /// <summary>Gets or sets the minutes between two checks for changes on MEGA.</summary>
    public int SyncIntervalMinutes { get; set; } = 10;

    /// <summary>
    /// Gets or sets the base URL written in the <c>.strm</c> files, as seen from the Jellyfin server itself.
    /// Empty: <c>http://127.0.0.1:&lt;http port&gt;&lt;base url&gt;</c>.
    /// </summary>
    public string StreamBaseUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets the read-ahead per playback position, in MiB.</summary>
    public int ReadAheadMegabytes { get; set; } = 48;

    /// <summary>Gets or sets the parallel connections to MEGA per file.</summary>
    public int ConnectionsPerFile { get; set; } = 4;

    /// <summary>Gets or sets the disk cache for the start and end of files (fast probing and playback start), in MiB.</summary>
    public int DiskCacheMegabytes { get; set; } = 2048;

    /// <summary>Gets or sets a value indicating whether only the server itself may use the stream URLs.</summary>
    public bool RestrictStreamToLocalhost { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether MEGA previews are used as item images.</summary>
    public bool UseMegaPreviews { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether a frame is extracted when MEGA has no preview.</summary>
    public bool ExtractFrameWhenNoPreview { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether new items are probed (duration, codecs, audio tags) after each scan.</summary>
    public bool ProbeAfterScan { get; set; } = true;

    /// <summary>
    /// Gets or sets how much of a MEGA file ffmpeg and ffprobe analyse before playing or describing it, in
    /// seconds (Jellyfin's own setting, 200 seconds by default, when 0).
    /// </summary>
    public int FfmpegAnalyzeSeconds { get; set; } = 10;

    /// <summary>Gets or sets the secret used to sign stream URLs (generated on first start).</summary>
    public string StreamSecret { get; set; } = string.Empty;
}

/// <summary>A MEGA account or a public folder link.</summary>
public class MegaAccountConfig
{
    public const string KindAccount = "account";
    public const string KindFolderLink = "folder";

    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets <c>account</c> or <c>folder</c>.</summary>
    public string Kind { get; set; } = KindAccount;

    public string Email { get; set; } = string.Empty;

    /// <summary>Gets or sets the session (MEGA SDK format). The password itself is never stored.</summary>
    public string Session { get; set; } = string.Empty;

    /// <summary>Gets or sets the public folder link (folder kind).</summary>
    public string FolderLink { get; set; } = string.Empty;
}

/// <summary>A MEGA folder mirrored as a Jellyfin library.</summary>
public class MegaLibraryConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public bool Enabled { get; set; } = true;

    public string AccountId { get; set; } = string.Empty;

    /// <summary>Gets or sets the MEGA folder: <c>/Films</c>, or <c>in:Share/Films</c> for an incoming share.</summary>
    public string MegaPath { get; set; } = "/";

    /// <summary>Gets or sets the Jellyfin library name.</summary>
    public string LibraryName { get; set; } = string.Empty;

    /// <summary>Gets or sets the library type: movies, tvshows, music, musicvideos, homevideos, mixed.</summary>
    public string ContentType { get; set; } = "movies";

    /// <summary>Gets or sets a value indicating whether the Jellyfin library is created automatically.</summary>
    public bool CreateLibrary { get; set; } = true;
}
