using System;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Mega.Streaming;

/// <summary>
/// Start and end blocks of files kept on disk, still encrypted exactly as MEGA stores them
/// (nothing in clear on disk). Makes probing and playback start instant after the first time.
/// </summary>
internal sealed class BlockDiskCache
{
    private const int HeadBlocks = 2;
    private const int TailBlocks = 2;
    private readonly string _root;
    private readonly ILogger _logger;

    public BlockDiskCache(string root, ILogger logger)
    {
        _root = root;
        _logger = logger;
        Directory.CreateDirectory(root);
    }

    public bool IsCandidate(long idx, long lastBlock) => idx < HeadBlocks || idx > lastBlock - TailBlocks;

    private string PathFor(string sourceId, string handle, long idx)
        => Path.Combine(_root, sourceId, SafeName(handle) + "." + idx.ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>Handles are case-sensitive base64: hex keeps them distinct on case-insensitive file systems.</summary>
    public static string SafeName(string handle)
        => Convert.ToHexString(Jellyfin.Plugin.Mega.Mega.MegaCrypto.B64Decode(handle) ?? System.Text.Encoding.UTF8.GetBytes(handle));

    public byte[]? TryLoad(string sourceId, string handle, long idx, int expectedLength)
    {
        var path = PathFor(sourceId, handle, idx);
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length != expectedLength)
            {
                return null;
            }

            var data = File.ReadAllBytes(path);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
            return data;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Save(string sourceId, string handle, long idx, byte[] encrypted)
    {
        var path = PathFor(sourceId, handle, idx);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp" + Environment.CurrentManagedThreadId;
            File.WriteAllBytes(tmp, encrypted);
            File.Move(tmp, path, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(e, "Cannot write MEGA block cache {Path}", path);
        }
    }

    public bool Contains(string sourceId, string handle, long idx) => File.Exists(PathFor(sourceId, handle, idx));

    /// <summary>Removes the least recently used blocks above <paramref name="maxBytes"/>.</summary>
    public void Trim(long maxBytes)
    {
        try
        {
            var files = new DirectoryInfo(_root).EnumerateFiles("*", SearchOption.AllDirectories)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .ToList();
            long total = 0;
            foreach (var f in files)
            {
                total += f.Length;
                if (total > maxBytes || f.Name.Contains(".tmp", StringComparison.Ordinal))
                {
                    try
                    {
                        f.Delete();
                    }
                    catch (IOException)
                    {
                    }
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(e, "Cannot trim MEGA block cache");
        }
    }
}
