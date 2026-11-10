using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Mega.Configuration;
using Jellyfin.Plugin.Mega.Mega;
using Jellyfin.Plugin.Mega.Streaming;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Mega.Services;

/// <summary>
/// Central service: MEGA sources built from the settings, shared stream readers, caches,
/// item lookup and account management.
/// </summary>
public sealed class MegaManager : IDisposable
{
    private readonly ConcurrentDictionary<string, MegaSource> _sources = new(StringComparer.Ordinal);
    private readonly ILogger<MegaManager> _logger;
    private readonly IServerApplicationHost _host;
    private readonly IServerConfigurationManager _serverConfig;
    private readonly IServerApplicationPaths _paths;
    private readonly object _configGate = new();
    private readonly ConcurrentDictionary<string, LoginJob> _logins = new(StringComparer.Ordinal);

    public MegaManager(
        ILogger<MegaManager> logger,
        IServerApplicationHost host,
        IServerConfigurationManager serverConfig,
        IServerApplicationPaths paths)
    {
        _logger = logger;
        MegaApiClient.Log = logger;
        _host = host;
        _serverConfig = serverConfig;
        _paths = paths;
        RootPath = Path.Combine(paths.DataPath, "mega");
        CachePath = Path.Combine(paths.CachePath, "mega");
        Directory.CreateDirectory(RootPath);
        Directory.CreateDirectory(CachePath);
        Readers = new ReaderPool(logger) { Disk = new BlockDiskCache(Path.Combine(CachePath, "blocks"), logger) };
        if (Plugin.Instance is not null)
        {
            Plugin.Instance.ConfigurationChanged += (_, _) => OnConfigurationChanged();
        }
    }

    /// <summary>Raised when the settings changed (a sync is due).</summary>
    public event EventHandler? SettingsChanged;

    /// <summary>Gets the folder that holds the virtual libraries.</summary>
    public string RootPath { get; }

    public string CachePath { get; }

    internal ReaderPool Readers { get; }

    public PluginConfiguration Configuration
    {
        get
        {
            var cfg = Plugin.Instance?.Configuration ?? new PluginConfiguration();
            if (string.IsNullOrEmpty(cfg.StreamSecret) && Plugin.Instance is not null)
            {
                lock (_configGate)
                {
                    if (string.IsNullOrEmpty(cfg.StreamSecret))
                    {
                        cfg.StreamSecret = MegaCrypto.B64Encode(RandomNumberGenerator.GetBytes(32));
                        Plugin.Instance.SaveConfiguration();
                    }
                }
            }

            return cfg;
        }
    }

    /// <summary>Base URL of the server as reached by the server itself (ffmpeg, ffprobe).</summary>
    public string StreamBaseUrl
    {
        get
        {
            var custom = Configuration.StreamBaseUrl?.Trim();
            if (!string.IsNullOrEmpty(custom))
            {
                return custom.TrimEnd('/');
            }

            var baseUrl = _serverConfig.GetNetworkConfiguration().BaseUrl?.Trim('/') ?? string.Empty;
            var url = "http://127.0.0.1:" + _host.HttpPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return baseUrl.Length > 0 ? url + "/" + baseUrl : url;
        }
    }

    public string GetLibraryPath(MegaLibraryConfig library)
    {
        var name = PathNames.Sanitize(string.IsNullOrWhiteSpace(library.LibraryName) ? library.Id : library.LibraryName);
        return Path.Combine(RootPath, "libraries", name);
    }

    internal IEnumerable<MegaSource> Sources => Configuration.Accounts.Select(a => GetSource(a.Id)).Where(s => s is not null)!;

    /// <summary>Source of an account, rebuilt when its settings changed.</summary>
    internal MegaSource? GetSource(string? id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return null;
        }

        var cfg = Configuration.Accounts.FirstOrDefault(a => a.Id == id);
        if (cfg is null)
        {
            _sources.TryRemove(id, out _);
            return null;
        }

        return _sources.AddOrUpdate(
            id,
            _ => new MegaSource(Clone(cfg), TreeCachePath),
            (_, existing) => SameCredentials(existing.Config, cfg) ? existing : new MegaSource(Clone(cfg), TreeCachePath));
    }

    /// <summary>Gets the folder of the encrypted copies of the MEGA trees (fast restarts).</summary>
    public string TreeCachePath => Path.Combine(CachePath, "trees");

    private static bool SameCredentials(MegaAccountConfig a, MegaAccountConfig b)
        => a.Kind == b.Kind && a.Session == b.Session && a.FolderLink == b.FolderLink;

    private static MegaAccountConfig Clone(MegaAccountConfig c) => new()
    {
        Id = c.Id,
        Name = c.Name,
        Kind = c.Kind,
        Email = c.Email,
        Session = c.Session,
        FolderLink = c.FolderLink,
    };

    private void OnConfigurationChanged()
    {
        foreach (var id in _sources.Keys.ToList())
        {
            if (Configuration.Accounts.All(a => a.Id != id))
            {
                _sources.TryRemove(id, out _);
            }
        }

        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>MEGA file behind a Jellyfin item (its <c>.strm</c> shortcut).</summary>
    internal bool TryResolveItem(BaseItem? item, out MegaSource source, out MegaNode node)
    {
        source = null!;
        node = null!;
        if (item is null)
        {
            return false;
        }

        var url = item.ShortcutPath;
        if (string.IsNullOrEmpty(url) && item.Path is string p && p.EndsWith(".strm", StringComparison.OrdinalIgnoreCase) && IsManagedPath(p))
        {
            url = ReadStrm(p);
        }

        return TryResolveUrl(url, out source, out node);
    }

    internal bool TryResolveUrl(string? url, out MegaSource source, out MegaNode node)
    {
        source = null!;
        node = null!;
        if (!StreamUrls.TryParse(url, out var sourceId, out var handle))
        {
            return false;
        }

        var s = GetSource(sourceId);
        var n = s?.Tree?.Get(handle);
        if (s is null || n is null || n.Key is null)
        {
            return false;
        }

        source = s;
        node = n;
        return true;
    }

    /// <summary>Same as <see cref="TryResolveItem"/>, loading the tree first if needed (just after a restart).</summary>
    internal async Task<(MegaSource Source, MegaNode Node)?> ResolveAsync(string sourceId, string handle, CancellationToken cancellationToken)
    {
        var s = GetSource(sourceId);
        if (s is null)
        {
            return null;
        }

        var tree = await s.GetTreeAsync(cancellationToken).ConfigureAwait(false);
        var n = tree.Get(handle);
        return n is { Key: not null } ? (s, n) : null;
    }

    public bool IsManagedPath(string? path)
        => !string.IsNullOrEmpty(path) && path.StartsWith(RootPath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    internal static string? ReadStrm(string path)
    {
        try
        {
            return File.ReadLines(path).Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0 && !l.StartsWith('#'));
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

    // ---- Previews (item images) ---------------------------------------------------------------

    private string PreviewPath(MegaSource source, MegaNode node, int type)
        => Path.Combine(CachePath, "previews", source.Id, BlockDiskCache.SafeName(node.Handle) + (type == 1 ? ".jpg" : ".t.jpg"));

    /// <summary>MEGA preview (or thumbnail) of a file, from the cache or from MEGA.</summary>
    internal async Task<byte[]?> GetPreviewAsync(MegaSource source, MegaNode node, int type, CancellationToken cancellationToken)
    {
        var path = PreviewPath(source, node, type);
        if (File.Exists(path))
        {
            return await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        }

        if (!node.TryGetAttribute(type, out _, out _))
        {
            return null;
        }

        var got = await source.Attributes.FetchAsync(new[] { node }, type, cancellationToken).ConfigureAwait(false);
        if (!got.TryGetValue(node.Handle, out var data))
        {
            return null;
        }

        SaveQuietly(path, data);
        return data;
    }

    /// <summary>Downloads in batches the previews not yet cached (48 per request), so that library scans find them instantly.</summary>
    internal async Task<int> PrefetchPreviewsAsync(MegaSource source, IReadOnlyList<MegaNode> nodes, CancellationToken cancellationToken)
    {
        var missing = nodes.Where(n => n.TryGetAttribute(1, out _, out _) && !File.Exists(PreviewPath(source, n, 1))).ToList();
        int count = 0;
        foreach (var chunk in missing.Chunk(400))
        {
            var got = await source.Attributes.FetchAsync(chunk, 1, cancellationToken).ConfigureAwait(false);
            foreach (var n in chunk)
            {
                if (got.TryGetValue(n.Handle, out var data))
                {
                    SaveQuietly(PreviewPath(source, n, 1), data);
                    count++;
                }
            }
        }

        return count;
    }

    private void SaveQuietly(string path, byte[] data)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, data);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(e, "Cannot cache MEGA preview {Path}", path);
        }
    }

    // ---- Accounts --------------------------------------------------------------------------------

    /// <summary>
    /// Starts a login in the background and returns its job. A login can take minutes when MEGA asks
    /// for a proof of work, longer than a reverse proxy lets an HTTP request wait.
    /// </summary>
    internal LoginJob StartLogin(string? name, string email, string password, string? mfa)
    {
        foreach (var old in _logins)
        {
            if (old.Value.Finished is { } at && DateTime.UtcNow - at > TimeSpan.FromMinutes(15))
            {
                _logins.TryRemove(old.Key, out _);
            }
        }

        var job = new LoginJob();
        job.Task = Task.Run(async () =>
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromHours(1));
            try
            {
                return await AddAccountAsync(name, email, password, mfa, cts.Token, s => job.Progress = s).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _logger.LogWarning("MEGA login of {Email} failed: {Message}", email.Trim(), e.Message);
                throw;
            }
            finally
            {
                job.Finished = DateTime.UtcNow;
            }
        });
        _logins[job.Id] = job;
        return job;
    }

    /// <summary>Gets a login started by <see cref="StartLogin"/>.</summary>
    internal LoginJob? GetLogin(string id) => _logins.TryGetValue(id, out var job) ? job : null;

    /// <summary>Logs in with e-mail and password (never stored) and saves the resulting session.</summary>
    internal async Task<MegaAccountConfig> AddAccountAsync(string? name, string email, string password, string? mfa, CancellationToken cancellationToken, Action<string>? progress = null)
    {
        var api = new MegaApiClient { Progress = progress };
        var session = await MegaAccountSession.LoginAsync(api, email, password, mfa, cancellationToken).ConfigureAwait(false);
        return SaveAccount(new MegaAccountConfig
        {
            Name = string.IsNullOrWhiteSpace(name) ? email.Trim() : name.Trim(),
            Kind = MegaAccountConfig.KindAccount,
            Email = email.Trim(),
            Session = session.ToSdkSession(),
        });
    }

    /// <summary>Adds an account from an existing session (MEGAcmd <c>mega-session</c>), checked first.</summary>
    internal async Task<MegaAccountConfig> AddSessionAsync(string? name, string session, CancellationToken cancellationToken)
    {
        var api = new MegaApiClient();
        var s = await MegaAccountSession.ResumeAsync(api, session, cancellationToken).ConfigureAwait(false);
        string email = string.Empty;
        try
        {
            var ug = await api.RequestAsync(new { a = "ug" }, cancellationToken, s.Sid).ConfigureAwait(false);
            if (ug.ValueKind == System.Text.Json.JsonValueKind.Object && ug.TryGetProperty("email", out var e))
            {
                email = e.GetString() ?? string.Empty;
            }
        }
        catch (MegaException)
        {
        }

        return SaveAccount(new MegaAccountConfig
        {
            Name = string.IsNullOrWhiteSpace(name) ? (email.Length > 0 ? email : "MEGA") : name.Trim(),
            Kind = MegaAccountConfig.KindAccount,
            Email = email,
            Session = session.Trim(),
        });
    }

    /// <summary>Adds a public folder link, checked first.</summary>
    internal async Task<MegaAccountConfig> AddFolderLinkAsync(string? name, string link, CancellationToken cancellationToken)
    {
        if (MegaSource.ParseFolderLink(link) is null)
        {
            throw new MegaException("invalid MEGA folder link (expected https://mega.nz/folder/…#…)");
        }

        var cfg = new MegaAccountConfig
        {
            Name = string.IsNullOrWhiteSpace(name) ? "Shared folder" : name.Trim(),
            Kind = MegaAccountConfig.KindFolderLink,
            FolderLink = link.Trim(),
        };
        var probe = new MegaSource(cfg, TreeCachePath);
        var tree = await probe.GetTreeAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(name) && tree.DefaultRoot is { } root)
        {
            cfg.Name = root.Name;
        }

        return SaveAccount(cfg, probe);
    }

    private MegaAccountConfig SaveAccount(MegaAccountConfig account, MegaSource? loaded = null)
    {
        var requested = account;
        var plugin = Plugin.Instance ?? throw new InvalidOperationException("plugin not loaded");
        lock (_configGate)
        {
            var cfg = plugin.Configuration;
            var existing = cfg.Accounts.FirstOrDefault(a =>
                (a.Kind == account.Kind && account.Kind == MegaAccountConfig.KindAccount && account.Email.Length > 0 && string.Equals(a.Email, account.Email, StringComparison.OrdinalIgnoreCase))
                || (account.Kind == MegaAccountConfig.KindFolderLink && a.FolderLink == account.FolderLink));
            if (existing is not null)
            {
                existing.Session = account.Session;
                existing.FolderLink = account.FolderLink;
                existing.Name = account.Name;
                account = existing;
            }
            else
            {
                cfg.Accounts.Add(account);
            }

            plugin.SaveConfiguration();
        }

        _sources.TryRemove(account.Id, out _);
        if (loaded is not null && ReferenceEquals(account, requested))
        {
            // Keep the tree just downloaded to check the link.
            _sources[account.Id] = loaded;
        }

        OnConfigurationChanged();
        return account;
    }

    internal void RemoveAccount(string id)
    {
        var plugin = Plugin.Instance ?? throw new InvalidOperationException("plugin not loaded");
        lock (_configGate)
        {
            plugin.Configuration.Accounts.RemoveAll(a => a.Id == id);
            plugin.Configuration.Libraries.RemoveAll(l => l.AccountId == id);
            plugin.SaveConfiguration();
        }

        _sources.TryRemove(id, out _);
        try
        {
            File.Delete(Path.Combine(TreeCachePath, id + ".tree"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(e, "Cannot delete the MEGA tree cache of {Id}", id);
        }

        OnConfigurationChanged();
    }

    public void Dispose()
    {
        Readers.Dispose();
    }
}

/// <summary>A login running in the background (settings page).</summary>
internal sealed class LoginJob
{
    private volatile string _progress = "Logging in…";

    public string Id { get; } = Guid.NewGuid().ToString("N");

    public Task<MegaAccountConfig> Task { get; set; } = null!;

    public string Progress
    {
        get => _progress;
        set => _progress = value;
    }

    public DateTime? Finished { get; set; }
}
