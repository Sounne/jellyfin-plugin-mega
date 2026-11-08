using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Mega.Configuration;
using Jellyfin.Plugin.Mega.Mega;
using Jellyfin.Plugin.Mega.Services;
using Jellyfin.Plugin.Mega.Streaming;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Mega.Library;

/// <summary>Result of the last synchronisation of a library.</summary>
public sealed class LibrarySyncStatus
{
    public string LibraryId { get; set; } = string.Empty;

    public DateTime? LastSync { get; set; }

    public int MediaFiles { get; set; }

    public int SideFiles { get; set; }

    public int Folders { get; set; }

    public string? Error { get; set; }
}

/// <summary>
/// Virtual file system: mirrors MEGA folders into local folders that Jellyfin scans like any library.
/// Every audio/video file becomes a tiny <c>.strm</c> shortcut to the plugin's stream endpoint (nothing
/// is downloaded); subtitles, <c>.nfo</c> files and artwork are copied for real (they are small and
/// Jellyfin reads them directly). Files removed or renamed on MEGA disappear locally too.
/// </summary>
public sealed partial class LibrarySynchronizer
{
    private readonly MegaManager _manager;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<LibrarySynchronizer> _logger;
    private readonly SemaphoreSlim _syncLock = new(1, 1);
    private readonly ConcurrentDictionary<string, LibrarySyncStatus> _status = new(StringComparer.Ordinal);

    public LibrarySynchronizer(MegaManager manager, ILibraryManager libraryManager, ILogger<LibrarySynchronizer> logger)
    {
        _manager = manager;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    public bool IsSyncing => _syncLock.CurrentCount == 0;

    public DateTime? LastSync { get; private set; }

    public LibrarySyncStatus? GetStatus(string libraryId) => _status.TryGetValue(libraryId, out var s) ? s : null;

    /// <summary>
    /// Synchronises every library. The MEGA trees are brought up to date first: with the changes since they were
    /// loaded, or downloaded again entirely when <paramref name="refreshTrees"/> is set.
    /// </summary>
    public async Task SyncAllAsync(bool refreshTrees, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        await _syncLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var cfg = _manager.Configuration;
            var libraries = cfg.Libraries.Where(l => l.Enabled).ToList();
            bool changed = false;
            int done = 0;
            foreach (var group in libraries.GroupBy(l => l.AccountId))
            {
                var source = _manager.GetSource(group.Key);
                if (source is null)
                {
                    foreach (var lib in group)
                    {
                        SetError(lib, "MEGA account not found");
                    }

                    continue;
                }

                MegaTree tree;
                try
                {
                    if (refreshTrees)
                    {
                        tree = await source.RefreshTreeAsync(cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await source.GetTreeAsync(cancellationToken).ConfigureAwait(false);
                        try
                        {
                            await source.UpdateAsync(cancellationToken).ConfigureAwait(false);
                        }
                        catch (Exception e) when (e is HttpRequestException or IOException || (e is MegaException me && !me.IsBadSession))
                        {
                            // Offline: keep serving the tree we have.
                            _logger.LogWarning("MEGA {Account}: cannot check for changes: {Message}", source.Config.Name, e.Message);
                        }

                        tree = source.Tree ?? await source.GetTreeAsync(cancellationToken).ConfigureAwait(false);
                    }

                    _logger.LogInformation("MEGA {Account}: {Count} nodes", source.Config.Name, tree.Count);
                }
                catch (Exception e) when (e is MegaException or HttpRequestException or IOException)
                {
                    source.LastError = e.Message;
                    _logger.LogError("MEGA {Account}: cannot load the files: {Message}", source.Config.Name, e.Message);
                    foreach (var lib in group)
                    {
                        SetError(lib, e.Message);
                    }

                    continue;
                }

                foreach (var lib in group)
                {
                    try
                    {
                        changed |= await SyncLibraryAsync(lib, source, tree, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception e) when (e is MegaException or IOException or UnauthorizedAccessException or HttpRequestException or InvalidOperationException or ArgumentException)
                    {
                        _logger.LogError(e, "MEGA library {Library}: synchronisation failed", lib.LibraryName);
                        SetError(lib, e.Message);
                    }

                    progress?.Report(100.0 * ++done / Math.Max(1, libraries.Count));
                }
            }

            RemoveOrphanFolders(libraries);
            LastSync = DateTime.UtcNow;
            _manager.Readers.Disk?.Trim(Math.Max(64, cfg.DiskCacheMegabytes) * 1024L * 1024L);
            if (changed)
            {
                _logger.LogInformation("MEGA libraries changed: queueing a library scan");
                _libraryManager.QueueLibraryScan();
            }
        }
        finally
        {
            _syncLock.Release();
        }
    }

    /// <summary>Polls MEGA for changes, applies them and synchronises the libraries when something changed.</summary>
    public async Task CheckForChangesAsync(CancellationToken cancellationToken)
    {
        var cfg = _manager.Configuration;
        bool any = false;
        foreach (var accountId in cfg.Libraries.Where(l => l.Enabled).Select(l => l.AccountId).Distinct())
        {
            var source = _manager.GetSource(accountId);
            if (source is null)
            {
                continue;
            }

            try
            {
                if (source.Tree is null || await source.UpdateAsync(cancellationToken).ConfigureAwait(false) != MegaSource.TreeUpdate.None)
                {
                    any = true;
                }
            }
            catch (Exception e) when (e is MegaException or HttpRequestException or IOException)
            {
                source.LastError = e.Message;
                _logger.LogWarning("MEGA {Account}: {Message}", source.Config.Name, e.Message);
            }
        }

        if (any)
        {
            await SyncAllAsync(false, null, cancellationToken).ConfigureAwait(false);
        }
    }

    internal static bool IsSingleShow(MegaLibraryConfig lib, MegaTree tree, MegaNode folder)
    {
        if (!string.Equals(lib.ContentType, "tvshows", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (var child in tree.GetChildren(folder.Handle))
        {
            if (child.IsFile ? MediaFileTypes.IsVideo(child.Name) : SeasonFolder().IsMatch(child.Name))
            {
                return true;
            }
        }

        return false;
    }

    [GeneratedRegex(@"^\s*(season|saison|series|s[ée]rie|staffel|temporada|stagione|seizoen|sezon|sæson|s)[\s._-]*\d{1,4}\b|^\s*specials?\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SeasonFolder();

    private void SetError(MegaLibraryConfig lib, string error)
    {
        var s = _status.GetOrAdd(lib.Id, id => new LibrarySyncStatus { LibraryId = id });
        s.Error = error;
        s.LastSync = DateTime.UtcNow;
    }

    private async Task<bool> SyncLibraryAsync(MegaLibraryConfig lib, MegaSource source, MegaTree tree, CancellationToken cancellationToken)
    {
        var root = tree.Resolve(lib.MegaPath) ?? throw new MegaException($"MEGA folder not found: {lib.MegaPath}");
        if (root.IsFile)
        {
            throw new MegaException($"not a folder: {lib.MegaPath}");
        }

        var localRoot = _manager.GetLibraryPath(lib);
        Directory.CreateDirectory(localRoot);
        var cfg = _manager.Configuration;
        var baseUrl = _manager.StreamBaseUrl;
        var expected = new HashSet<string>(PathNames.Comparer) { localRoot };
        var downloads = new List<(MegaNode Node, string Path)>();
        var media = new List<MegaNode>();
        var status = new LibrarySyncStatus { LibraryId = lib.Id };
        bool changed = false;

        // A shows library pointed at one show (its seasons or episodes directly inside): Jellyfin expects show
        // folders at the root of the library, so the show gets its own folder.
        var startDir = localRoot;
        if (IsSingleShow(lib, tree, root))
        {
            startDir = Path.Combine(localRoot, PathNames.Sanitize(root.Name));
            Directory.CreateDirectory(startDir);
            expected.Add(startDir);
        }

        var stack = new Stack<(string Handle, string Dir)>();
        stack.Push((root.Handle, startDir));
        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (handle, dir) = stack.Pop();
            var used = new HashSet<string>(PathNames.Comparer);
            var children = tree.GetChildren(handle)
                .OrderBy(c => c.IsFile)
                .ThenBy(c => c.Name, StringComparer.Ordinal)
                .ThenBy(c => c.Handle, StringComparer.Ordinal);
            foreach (var child in children)
            {
                if (!child.IsFile)
                {
                    var sub = Path.Combine(dir, PathNames.Unique(PathNames.Sanitize(child.Name), used));
                    if (!Directory.Exists(sub))
                    {
                        Directory.CreateDirectory(sub);
                        changed = true;
                    }

                    expected.Add(sub);
                    status.Folders++;
                    stack.Push((child.Handle, sub));
                    continue;
                }

                if (child.Key is null)
                {
                    continue;
                }

                if (MediaFileTypes.IsMedia(child.Name))
                {
                    var stem = PathNames.Sanitize(Path.GetFileNameWithoutExtension(child.Name));
                    var path = Path.Combine(dir, PathNames.Unique(stem + ".strm", used));
                    var content = StreamUrls.Build(baseUrl, cfg.StreamSecret, source.Id, child) + "\n";
                    expected.Add(path);
                    media.Add(child);
                    status.MediaFiles++;
                    if (!File.Exists(path) || await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false) != content)
                    {
                        await File.WriteAllTextAsync(path, content, cancellationToken).ConfigureAwait(false);
                        TrySetTimes(path, child);
                        changed = true;
                    }
                }
                else if (MediaFileTypes.IsSideFile(child.Name, child.Size))
                {
                    var path = Path.Combine(dir, PathNames.Unique(PathNames.Sanitize(child.Name), used));
                    expected.Add(path);
                    status.SideFiles++;
                    var info = new FileInfo(path);
                    if (!info.Exists || info.Length != child.Size || Math.Abs((info.LastWriteTimeUtc - child.Modified).TotalSeconds) > 2)
                    {
                        downloads.Add((child, path));
                    }
                }
            }
        }

        // Removed / renamed on MEGA.
        foreach (var file in Directory.EnumerateFiles(localRoot, "*", SearchOption.AllDirectories))
        {
            if (!expected.Contains(file))
            {
                TryDelete(file);
                changed = true;
            }
        }

        foreach (var dir in Directory.EnumerateDirectories(localRoot, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
        {
            if (!expected.Contains(dir))
            {
                try
                {
                    Directory.Delete(dir, true);
                    changed = true;
                }
                catch (IOException)
                {
                }
            }
        }

        if (downloads.Count > 0)
        {
            _logger.LogInformation("MEGA library {Library}: copying {Count} subtitle / artwork / nfo files", lib.LibraryName, downloads.Count);
            await Parallel.ForEachAsync(
                downloads,
                new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken },
                async (d, ct) =>
                {
                    try
                    {
                        var data = await DownloadSmallFileAsync(source, d.Node, ct).ConfigureAwait(false);
                        var tmp = d.Path + ".megatmp";
                        await File.WriteAllBytesAsync(tmp, data, ct).ConfigureAwait(false);
                        File.Move(tmp, d.Path, true);
                        TrySetTimes(d.Path, d.Node);
                    }
                    catch (Exception e) when (e is MegaException or HttpRequestException or IOException or TaskCanceledException && !ct.IsCancellationRequested)
                    {
                        _logger.LogWarning("MEGA: cannot copy {Name}: {Message}", d.Node.Name, e.Message);
                    }
                }).ConfigureAwait(false);
            changed = true;
        }

        if (cfg.UseMegaPreviews)
        {
            try
            {
                var fetched = await _manager.PrefetchPreviewsAsync(source, media, cancellationToken).ConfigureAwait(false);
                if (fetched > 0)
                {
                    _logger.LogInformation("MEGA library {Library}: {Count} previews cached", lib.LibraryName, fetched);
                }
            }
            catch (Exception e) when (e is MegaException or HttpRequestException)
            {
                _logger.LogWarning("MEGA library {Library}: previews not cached: {Message}", lib.LibraryName, e.Message);
            }
        }

        changed |= await EnsureJellyfinLibraryAsync(lib, localRoot).ConfigureAwait(false);
        status.LastSync = DateTime.UtcNow;
        _status[lib.Id] = status;
        _logger.LogInformation(
            "MEGA library {Library}: {Media} media files, {Side} side files, {Folders} folders{Changed}",
            lib.LibraryName,
            status.MediaFiles,
            status.SideFiles,
            status.Folders,
            changed ? " (changed)" : string.Empty);
        return changed;
    }

    /// <summary>Downloads and decrypts a small file entirely (subtitles, artwork, nfo).</summary>
    internal static async Task<byte[]> DownloadSmallFileAsync(MegaSource source, MegaNode node, CancellationToken cancellationToken)
    {
        if (node.Size == 0)
        {
            return Array.Empty<byte>();
        }

        bool plain = false;
        for (int attempt = 0; ; attempt++)
        {
            var url = await source.GetDownloadUrlAsync(node.Handle, attempt > 0, plain, cancellationToken).ConfigureAwait(false);
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromMinutes(2));
                using var resp = await MegaHttp.Storage.GetAsync(url + "/0-" + (node.Size - 1).ToString(System.Globalization.CultureInfo.InvariantCulture), timeout.Token).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    throw new MegaException($"HTTP {(int)resp.StatusCode} from the MEGA storage server") { HttpStatus = (int)resp.StatusCode };
                }

                var data = await resp.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
                if (data.LongLength != node.Size)
                {
                    throw new MegaException("incomplete download");
                }

                MegaCrypto.CtrTransform(node.Key!.AesKey, node.Key.Nonce, 0, data);
                return data;
            }
            catch (HttpRequestException e) when (MegaHttp.IsTlsRefusal(e) && !plain)
            {
                plain = true;
            }
            catch (MegaException e) when (e.HttpStatus is 403 or 404 or 410 && attempt < 2)
            {
                source.ForgetDownloadUrl(node.Handle);
            }
        }
    }

    private static void TrySetTimes(string path, MegaNode node)
    {
        try
        {
            File.SetLastWriteTimeUtc(path, node.Modified);
            if (OperatingSystem.IsWindows())
            {
                File.SetCreationTimeUtc(path, DateTimeOffset.FromUnixTimeSeconds(node.Timestamp).UtcDateTime);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException)
        {
        }
    }

    private void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(e, "Cannot delete {File}", file);
        }
    }

    /// <summary>Local folders of libraries that no longer exist in the settings.</summary>
    private void RemoveOrphanFolders(List<MegaLibraryConfig> libraries)
    {
        var root = Path.Combine(_manager.RootPath, "libraries");
        if (!Directory.Exists(root))
        {
            return;
        }

        var keep = new HashSet<string>(libraries.Select(_manager.GetLibraryPath), PathNames.Comparer);
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            if (!keep.Contains(dir))
            {
                try
                {
                    Directory.Delete(dir, true);
                    _logger.LogInformation("MEGA: removed the folder of a deleted library: {Dir}", dir);
                }
                catch (IOException)
                {
                }
            }
        }
    }

    /// <summary>Creates the Jellyfin library pointing at the local folder (or adds the folder to a library of that name).</summary>
    private async Task<bool> EnsureJellyfinLibraryAsync(MegaLibraryConfig lib, string localRoot)
    {
        if (!lib.CreateLibrary)
        {
            return false;
        }

        var folders = _libraryManager.GetVirtualFolders();
        if (folders.Any(f => f.Locations.Any(l => string.Equals(Path.GetFullPath(l), Path.GetFullPath(localRoot), PathNames.Comparison))))
        {
            return false;
        }

        var name = string.IsNullOrWhiteSpace(lib.LibraryName) ? "MEGA" : lib.LibraryName.Trim();
        var named = folders.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
        if (named is not null)
        {
            _libraryManager.AddMediaPath(named.Name, new MediaPathInfo(localRoot));
            _logger.LogInformation("MEGA: folder added to the existing library {Library}", named.Name);
            return true;
        }

        CollectionTypeOptions? type = Enum.TryParse<CollectionTypeOptions>(lib.ContentType, true, out var t) && t != CollectionTypeOptions.mixed ? t : null;
        var options = new LibraryOptions
        {
            PathInfos = new[] { new MediaPathInfo(localRoot) },
            EnableRealtimeMonitor = false,
            EnablePhotos = false,
            EnableChapterImageExtraction = false,
            ExtractChapterImagesDuringLibraryScan = false,
            SaveLocalMetadata = false,
        };
        await _libraryManager.AddVirtualFolder(name, type, options, false).ConfigureAwait(false);
        _logger.LogInformation("MEGA: Jellyfin library {Library} created ({Type})", name, type?.ToString() ?? "mixed");
        return true;
    }
}
