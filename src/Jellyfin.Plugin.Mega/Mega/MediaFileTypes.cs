using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.Mega.Mega;

/// <summary>Which MEGA files take part in a Jellyfin library, and how.</summary>
internal static partial class MediaFileTypes
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".m4v", ".avi", ".mov", ".wmv", ".asf", ".webm", ".mpg", ".mpeg", ".mpe", ".m2v", ".ts", ".m2ts",
        ".mts", ".vob", ".flv", ".f4v", ".3gp", ".3g2", ".ogv", ".divx", ".xvid", ".rm", ".rmvb", ".dvr-ms", ".wtv", ".mxf",
        ".nsv", ".iso", ".img",
    };

    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".flac", ".m4a", ".m4b", ".aac", ".ogg", ".oga", ".opus", ".wav", ".wma", ".alac", ".aiff", ".aif", ".aifc",
        ".ape", ".wv", ".mka", ".dsf", ".dff", ".mpc", ".tta", ".mp2", ".ac3", ".dts", ".amr", ".webma",
    };

    private static readonly HashSet<string> SubtitleExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".srt", ".ass", ".ssa", ".vtt", ".sub", ".idx", ".smi", ".sami", ".sup", ".ttml", ".lrc", ".elrc", ".txt",
    };

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp", ".tbn",
    };

    /// <summary>Largest side file (subtitle, artwork, nfo) copied as a real file into the virtual library.</summary>
    public const long MaxSideFileSize = 40L * 1024 * 1024;

    public static bool IsVideo(string name) => VideoExtensions.Contains(Path.GetExtension(name));

    public static bool IsAudio(string name) => AudioExtensions.Contains(Path.GetExtension(name));

    public static bool IsMedia(string name) => IsVideo(name) || IsAudio(name);

    public static bool IsSubtitle(string name) => SubtitleExtensions.Contains(Path.GetExtension(name)) && !IsNotSubtitleText(name);

    public static bool IsNfo(string name) => string.Equals(Path.GetExtension(name), ".nfo", StringComparison.OrdinalIgnoreCase);

    public static bool IsImage(string name) => ImageExtensions.Contains(Path.GetExtension(name));

    /// <summary>Artwork recognised by Jellyfin (poster.jpg, fanart.jpg, Movie-poster.jpg, season01-poster.jpg…).</summary>
    public static bool IsArtwork(string name) => IsImage(name) && ArtworkPattern().IsMatch(Path.GetFileNameWithoutExtension(name));

    /// <summary>Side file copied as a real file next to the <c>.strm</c> files.</summary>
    public static bool IsSideFile(string name, long size) => size <= MaxSideFileSize && (IsSubtitle(name) || IsNfo(name) || IsImage(name));

    /// <summary>Files kept in memory when the tree is loaded.</summary>
    public static bool Keep(string name, long size) => IsMedia(name) || (size <= MaxSideFileSize && (IsSubtitle(name) || IsNfo(name) || IsArtwork(name)));

    public static string GetMimeType(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".mkv" => "video/x-matroska",
        ".mp4" or ".m4v" => "video/mp4",
        ".avi" or ".divx" or ".xvid" => "video/x-msvideo",
        ".mov" => "video/quicktime",
        ".wmv" => "video/x-ms-wmv",
        ".asf" => "video/x-ms-asf",
        ".webm" => "video/webm",
        ".mpg" or ".mpeg" or ".mpe" or ".m2v" or ".vob" => "video/mpeg",
        ".ts" or ".m2ts" or ".mts" => "video/mp2t",
        ".flv" or ".f4v" => "video/x-flv",
        ".3gp" => "video/3gpp",
        ".3g2" => "video/3gpp2",
        ".ogv" => "video/ogg",
        ".mp3" => "audio/mpeg",
        ".flac" => "audio/flac",
        ".m4a" or ".m4b" or ".alac" => "audio/mp4",
        ".aac" => "audio/aac",
        ".ogg" or ".oga" => "audio/ogg",
        ".opus" => "audio/opus",
        ".wav" => "audio/wav",
        ".wma" => "audio/x-ms-wma",
        ".mka" => "audio/x-matroska",
        ".aiff" or ".aif" or ".aifc" => "audio/aiff",
        _ => "application/octet-stream",
    };

    private static bool IsNotSubtitleText(string name)
        => string.Equals(Path.GetExtension(name), ".txt", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(
        @"^(poster|folder|cover|default|movie|show|fanart|backdrop\d*|background\d*|art|banner|logo|clearlogo|clearart|thumb|landscape|disc|discart|cdart|albumart\w*|front|season(\d+|-specials|-all)?(-(poster|banner|fanart|landscape|thumb))?)$|-(poster|fanart|thumb|banner|landscape|logo|clearlogo|clearart|disc|discart|backdrop\d*|cover)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ArtworkPattern();
}
