using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Mega.Configuration;

namespace Jellyfin.Plugin.Mega.Mega;

/// <summary>
/// A MEGA account (session) or a public folder link: keys, node tree, download URLs, thumbnails.
/// </summary>
internal sealed partial class MegaSource
{
    private readonly MegaApiClient _api = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, (string Url, DateTime At)> _downloadUrls = new(StringComparer.Ordinal);
    private INodeKeyring? _keyring;
    private Dictionary<string, byte[]> _extraShareKeys = new(StringComparer.Ordinal);
    private MegaTree? _tree;
    private byte[]? _folderKey;
    private string? _linkSubfolder;
    private readonly string? _cacheFile;
    private readonly object _cacheGate = new();

    public MegaSource(MegaAccountConfig config, string? cacheDirectory = null)
    {
        Config = config;
        Attributes = new FileAttributeClient(_api);
        _cacheFile = cacheDirectory is null ? null : Path.Combine(cacheDirectory, config.Id + ".tree");
    }

    /// <summary>Result of <see cref="UpdateAsync"/>.</summary>
    public enum TreeUpdate
    {
        /// <summary>Nothing changed on MEGA.</summary>
        None,

        /// <summary>Changes were applied to the tree.</summary>
        Changed,

        /// <summary>The whole tree was downloaded again.</summary>
        Reloaded,
    }

    public MegaAccountConfig Config { get; }

    public string Id => Config.Id;

    public FileAttributeClient Attributes { get; }

    public bool IsFolderLink => string.Equals(Config.Kind, MegaAccountConfig.KindFolderLink, StringComparison.OrdinalIgnoreCase);

    public MegaTree? Tree => _tree;

    public DateTime? TreeLoadedAt { get; private set; }

    public string? LastError { get; set; }

    /// <summary>Gets the account e-mail / user handle once logged in.</summary>
    public string? UserHandle { get; private set; }

    /// <summary>Returns the loaded tree, loading it first if needed.</summary>
    public async Task<MegaTree> GetTreeAsync(CancellationToken cancellationToken)
    {
        var t = _tree;
        if (t is not null)
        {
            return t;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return _tree ?? LoadCachedTreeLocked() ?? await LoadTreeLockedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Reads the tree saved on disk, if any (instant start; <see cref="UpdateAsync"/> brings it up to date).</summary>
    public bool LoadCachedTree()
    {
        if (_tree is not null)
        {
            return true;
        }

        if (!_gate.Wait(0))
        {
            return false;
        }

        try
        {
            return (_tree ?? LoadCachedTreeLocked()) is not null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private MegaTree? LoadCachedTreeLocked()
    {
        if (_cacheFile is null || CacheCredential is not string credential)
        {
            return null;
        }

        try
        {
            var tree = TreeCache.Load(_cacheFile, credential);
            if (tree is null || tree.Sn is null || tree.DefaultRoot is null)
            {
                return null;
            }

            _tree = tree;
            TreeLoadedAt = File.GetLastWriteTimeUtc(_cacheFile);
            return tree;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or EndOfStreamException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private string? CacheCredential => IsFolderLink
        ? (string.IsNullOrEmpty(Config.FolderLink) ? null : "link:" + Config.FolderLink)
        : (string.IsNullOrEmpty(Config.Session) ? null : "session:" + Config.Session);

    private void SaveCache(MegaTree tree)
    {
        if (_cacheFile is null || CacheCredential is not string credential)
        {
            return;
        }

        lock (_cacheGate)
        {
            try
            {
                TreeCache.Save(tree, _cacheFile, credential);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // The cache only speeds up the next start.
            }
        }
    }

    /// <summary>Deletes the tree saved on disk (account removed).</summary>
    public void DeleteCache()
    {
        if (_cacheFile is null)
        {
            return;
        }

        lock (_cacheGate)
        {
            try
            {
                File.Delete(_cacheFile);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>Downloads the whole tree again.</summary>
    public async Task<MegaTree> RefreshTreeAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await LoadTreeLockedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EnsureLoggedInAsync(CancellationToken cancellationToken)
    {
        if (_keyring is not null)
        {
            return;
        }

        if (IsFolderLink)
        {
            var (handle, key, sub) = ParseFolderLink(Config.FolderLink)
                ?? throw new MegaException("invalid MEGA folder link (expected https://mega.nz/folder/…#…)");
            _api.FolderHandle = handle;
            _folderKey = key;
            _linkSubfolder = sub;
            _keyring = new FolderLinkKeyring(key);
            UserHandle = null;
            return;
        }

        if (string.IsNullOrWhiteSpace(Config.Session))
        {
            throw new MegaException("account not connected: log in again from the plugin settings");
        }

        var session = await MegaAccountSession.ResumeAsync(_api, Config.Session, cancellationToken).ConfigureAwait(false);
        _api.Sid = session.Sid;
        UserHandle = session.User;
        _keyring = new AccountKeyring(session.User, session.MasterKey, session.Rsa);
        try
        {
            var r = await _api.RequestAsync(new { a = "uga", u = session.User, ua = "^!keys", v = 1 }, cancellationToken).ConfigureAwait(false);
            var av = r.ValueKind == JsonValueKind.Object && r.TryGetProperty("av", out var a) ? MegaCrypto.B64Decode(a.GetString()) : null;
            if (av is not null)
            {
                _extraShareKeys = MegaCrypto.ParseKeysAttribute(session.MasterKey, av);
            }
        }
        catch (MegaException)
        {
            // No ^!keys attribute (older accounts): share keys come with the tree.
        }
    }

    private async Task<MegaTree> LoadTreeLockedAsync(CancellationToken cancellationToken)
    {
        await EnsureLoggedInAsync(cancellationToken).ConfigureAwait(false);
        object cmd = IsFolderLink ? new { a = "f", c = 1, ca = 1, r = 1 } : new { a = "f", c = 1, r = 1 };
        MegaTree tree;
        int wait = 2;
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                await using var stream = await _api.RequestStreamAsync(cmd, cancellationToken).ConfigureAwait(false);
                tree = await MegaTree.ParseAsync(stream, _keyring!, _extraShareKeys, MediaFileTypes.Keep, cancellationToken).ConfigureAwait(false);
                break;
            }
            catch (MegaException e) when (e.Code is -3 or -4 or -6 && attempt < 6)
            {
                await Task.Delay(TimeSpan.FromSeconds(wait), cancellationToken).ConfigureAwait(false);
                wait = Math.Min(wait * 2, 30);
            }
            catch (MegaException e) when (e.IsBadSession && !IsFolderLink)
            {
                _keyring = null;
                throw;
            }
        }

        if (IsFolderLink)
        {
            tree.LinkRoot = _linkSubfolder is not null && tree.Get(_linkSubfolder) is not null ? _linkSubfolder : _api.FolderHandle;
        }

        _tree = tree;
        TreeLoadedAt = DateTime.UtcNow;
        LastError = null;
        _ = Task.Run(() => SaveCache(tree), CancellationToken.None);
        return tree;
    }

    /// <summary>
    /// Brings the tree up to date with the changes made on MEGA since it was loaded (action packets): new, moved,
    /// renamed and deleted files are applied in place; the whole tree is downloaded again only when shares changed
    /// or MEGA no longer has the packets (tree too old).
    /// </summary>
    public async Task<TreeUpdate> UpdateAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = _tree ?? LoadCachedTreeLocked();
            if (current?.Sn is null)
            {
                await LoadTreeLockedAsync(cancellationToken).ConfigureAwait(false);
                return TreeUpdate.Reloaded;
            }

            await EnsureLoggedInAsync(cancellationToken).ConfigureAwait(false);
            MegaTree? next = null;
            var sn = current.Sn;
            for (int page = 0; page < 50; page++)
            {
                JsonElement r;
                try
                {
                    r = await _api.PollAsync(sn, cancellationToken).ConfigureAwait(false);
                }
                catch (MegaException e) when (!e.IsBadSession)
                {
                    // Sequence number unknown to MEGA (tree saved long ago): start again from scratch.
                    await LoadTreeLockedAsync(cancellationToken).ConfigureAwait(false);
                    return TreeUpdate.Reloaded;
                }

                if (r.ValueKind != JsonValueKind.Object)
                {
                    break;
                }

                if (r.TryGetProperty("a", out var packets) && packets.ValueKind == JsonValueKind.Array && packets.GetArrayLength() > 0)
                {
                    next ??= current.Clone();
                    if (!next.ApplyPackets(packets, _keyring!, MediaFileTypes.Keep))
                    {
                        await LoadTreeLockedAsync(cancellationToken).ConfigureAwait(false);
                        return TreeUpdate.Reloaded;
                    }
                }

                var nextSn = r.TryGetProperty("sn", out var snProp) ? snProp.GetString() : null;
                if (nextSn is not null)
                {
                    sn = nextSn;
                }

                // "w": no more packets for now.
                if (r.TryGetProperty("w", out _) || nextSn is null)
                {
                    break;
                }
            }

            if (next is null || next.Changes == 0)
            {
                current.Sn = sn;
                return TreeUpdate.None;
            }

            next.Sn = sn;
            _tree = next;
            TreeLoadedAt = DateTime.UtcNow;
            LastError = null;
            _ = Task.Run(() => SaveCache(next), CancellationToken.None);
            return TreeUpdate.Changed;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Temporary download URL of a file (cached for a while).</summary>
    public async Task<string> GetDownloadUrlAsync(string handle, bool forceRefresh, bool plainHttp, CancellationToken cancellationToken)
    {
        if (!forceRefresh && !plainHttp && _downloadUrls.TryGetValue(handle, out var cached) && DateTime.UtcNow - cached.At < TimeSpan.FromMinutes(30))
        {
            return cached.Url;
        }

        await EnsureLoggedInAsync(cancellationToken).ConfigureAwait(false);
        var r = await _api.RequestAsync(new { a = "g", g = 1, n = handle, ssl = plainHttp ? 0 : 2 }, cancellationToken).ConfigureAwait(false);
        if (r.ValueKind == JsonValueKind.Object && r.TryGetProperty("e", out var e) && e.TryGetInt32(out var code) && code < 0)
        {
            throw new MegaException(code);
        }

        string? url = null;
        if (r.ValueKind == JsonValueKind.Object && r.TryGetProperty("g", out var g))
        {
            url = g.ValueKind switch
            {
                JsonValueKind.String => g.GetString(),
                JsonValueKind.Array when g.GetArrayLength() > 0 => g[0].GetString(),
                _ => null,
            };
        }

        if (string.IsNullOrEmpty(url))
        {
            throw new MegaException("file temporarily unavailable on MEGA");
        }

        _downloadUrls[handle] = (url, DateTime.UtcNow);
        return url;
    }

    public void ForgetDownloadUrl(string handle) => _downloadUrls.TryRemove(handle, out _);

    /// <summary>Drops the login state and tree (settings changed).</summary>
    public void Reset()
    {
        _keyring = null;
        _tree = null;
        _downloadUrls.Clear();
    }

    /// <summary>
    /// Parses <c>https://mega.nz/folder/HANDLE#KEY</c> (optionally <c>/folder/SUB</c>) and the legacy
    /// <c>https://mega.nz/#F!HANDLE!KEY</c> form.
    /// </summary>
    public static (string Handle, byte[] Key, string? Subfolder)? ParseFolderLink(string? link)
    {
        if (string.IsNullOrWhiteSpace(link))
        {
            return null;
        }

        link = link.Trim();
        var m = NewFolderLink().Match(link);
        if (!m.Success)
        {
            m = OldFolderLink().Match(link);
        }

        if (!m.Success)
        {
            return null;
        }

        var key = MegaCrypto.B64Decode(m.Groups["key"].Value);
        if (key is null || key.Length != 16)
        {
            return null;
        }

        var sub = m.Groups["sub"].Success && m.Groups["sub"].Value.Length > 0 ? m.Groups["sub"].Value : null;
        return (m.Groups["h"].Value, key, sub);
    }

    [GeneratedRegex(@"/folder/(?<h>[A-Za-z0-9_-]{8})#(?<key>[A-Za-z0-9_-]{22})(?:/folder/(?<sub>[A-Za-z0-9_-]{8}))?")]
    private static partial Regex NewFolderLink();

    [GeneratedRegex(@"#F!(?<h>[A-Za-z0-9_-]{8})!(?<key>[A-Za-z0-9_-]{22})(?:!(?<sub>[A-Za-z0-9_-]{8}))?")]
    private static partial Regex OldFolderLink();
}
