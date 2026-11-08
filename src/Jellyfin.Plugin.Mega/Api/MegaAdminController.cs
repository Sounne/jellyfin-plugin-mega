using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Mega.Configuration;
using Jellyfin.Plugin.Mega.Library;
using Jellyfin.Plugin.Mega.Mega;
using Jellyfin.Plugin.Mega.Services;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Mega.Api;

/// <summary>Settings page actions: accounts, folder picker, status, manual sync. Administrators only.</summary>
[ApiController]
[Route("MegaFs")]
[Authorize(Policy = Policies.RequiresElevation)]
public class MegaAdminController : ControllerBase
{
    private readonly MegaManager _manager;
    private readonly LibrarySynchronizer _sync;
    private readonly ILogger<MegaAdminController> _logger;

    public MegaAdminController(MegaManager manager, LibrarySynchronizer sync, ILogger<MegaAdminController> logger)
    {
        _manager = manager;
        _sync = sync;
        _logger = logger;
    }

    [HttpGet("status")]
    public ActionResult<StatusResponse> GetStatus()
    {
        var cfg = _manager.Configuration;
        return new StatusResponse
        {
            Syncing = _sync.IsSyncing,
            LastSync = _sync.LastSync,
            StreamBaseUrl = _manager.StreamBaseUrl,
            LibraryRoot = _manager.RootPath,
            OpenStreams = _manager.Readers.OpenReaders,
            Accounts = cfg.Accounts.Select(a =>
            {
                var s = _manager.GetSource(a.Id);
                return new AccountStatus
                {
                    Id = a.Id,
                    Name = a.Name,
                    Kind = a.Kind,
                    Email = a.Email,
                    Loaded = s?.Tree is not null,
                    Nodes = s?.Tree?.Count ?? 0,
                    LoadedAt = s?.TreeLoadedAt,
                    Error = s?.LastError,
                };
            }).ToList(),
            Libraries = cfg.Libraries.Select(l =>
            {
                var st = _sync.GetStatus(l.Id);
                return new LibraryStatus
                {
                    Id = l.Id,
                    LocalPath = _manager.GetLibraryPath(l),
                    MediaFiles = st?.MediaFiles ?? 0,
                    SideFiles = st?.SideFiles ?? 0,
                    Folders = st?.Folders ?? 0,
                    LastSync = st?.LastSync,
                    Error = st?.Error,
                };
            }).ToList(),
        };
    }

    [HttpPost("accounts/login")]
    public async Task<ActionResult<MegaAccountConfig>> Login([FromBody] LoginRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrEmpty(request.Password))
        {
            return BadRequest("e-mail and password required");
        }

        return await Run(() => _manager.AddAccountAsync(request.Name, request.Email, request.Password, request.Mfa, cancellationToken)).ConfigureAwait(false);
    }

    [HttpPost("accounts/session")]
    public async Task<ActionResult<MegaAccountConfig>> AddSession([FromBody] SessionRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Session))
        {
            return BadRequest("session required");
        }

        return await Run(() => _manager.AddSessionAsync(request.Name, request.Session, cancellationToken)).ConfigureAwait(false);
    }

    [HttpPost("accounts/link")]
    public async Task<ActionResult<MegaAccountConfig>> AddLink([FromBody] LinkRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Link))
        {
            return BadRequest("link required");
        }

        return await Run(() => _manager.AddFolderLinkAsync(request.Name, request.Link, cancellationToken)).ConfigureAwait(false);
    }

    [HttpDelete("accounts/{id}")]
    public ActionResult RemoveAccount([FromRoute] string id)
    {
        _manager.RemoveAccount(id);
        return NoContent();
    }

    /// <summary>Sub-folders of a MEGA folder (folder picker of the settings page).</summary>
    [HttpGet("accounts/{id}/folders")]
    public async Task<ActionResult<FolderListing>> GetFolders([FromRoute] string id, [FromQuery] string? path, CancellationToken cancellationToken)
    {
        var source = _manager.GetSource(id);
        if (source is null)
        {
            return NotFound();
        }

        try
        {
            var tree = await source.GetTreeAsync(cancellationToken).ConfigureAwait(false);
            var listing = new FolderListing();
            if (string.IsNullOrEmpty(path) && !source.IsFolderLink && tree.InShares.Count > 0)
            {
                listing.Path = string.Empty;
                listing.Folders.Add(new FolderEntry { Name = "Cloud Drive", Path = "/" });
                foreach (var h in tree.InShares)
                {
                    var n = tree.Get(h);
                    if (n is not null)
                    {
                        listing.Folders.Add(new FolderEntry { Name = n.Name + " (shared with me)", Path = "in:" + n.Name });
                    }
                }

                return listing;
            }

            var node = tree.Resolve(string.IsNullOrEmpty(path) ? "/" : path);
            if (node is null)
            {
                return NotFound();
            }

            listing.Path = tree.GetPath(node);
            listing.Parent = node.Parent is not null && tree.Get(node.Parent) is { } parent && node.Handle != tree.CloudRoot && node.Handle != tree.LinkRoot && !tree.InShares.Contains(node.Handle)
                ? tree.GetPath(parent)
                : (source.IsFolderLink || node.Handle == tree.CloudRoot ? null : string.Empty);
            foreach (var c in tree.GetChildren(node.Handle).Where(c => !c.IsFile).OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
            {
                listing.Folders.Add(new FolderEntry { Name = c.Name, Path = tree.GetPath(c) });
            }

            listing.MediaFiles = tree.Descendants(node.Handle).Count(n => n.IsFile && MediaFileTypes.IsMedia(n.Name));
            return listing;
        }
        catch (Exception e) when (e is MegaException or HttpRequestException)
        {
            return StatusCode(StatusCodes.Status502BadGateway, e.Message);
        }
    }

    /// <summary>Downloads the MEGA trees again and synchronises every library now.</summary>
    [HttpPost("sync")]
    public ActionResult SyncNow()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await _sync.SyncAllAsync(true, null, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "MEGA synchronisation failed");
            }
        });
        return Accepted();
    }

    private async Task<ActionResult<MegaAccountConfig>> Run(Func<Task<MegaAccountConfig>> action)
    {
        try
        {
            var account = await action().ConfigureAwait(false);
            return new MegaAccountConfig { Id = account.Id, Name = account.Name, Kind = account.Kind, Email = account.Email };
        }
        catch (MegaException e)
        {
            _logger.LogWarning("MEGA: {Message}", e.Message);
            return BadRequest(e.Message);
        }
        catch (HttpRequestException e)
        {
            return StatusCode(StatusCodes.Status502BadGateway, "MEGA unreachable: " + e.Message);
        }
    }

#pragma warning disable CA1002, CA2227
    public sealed class LoginRequest
    {
        public string? Name { get; set; }

        public string Email { get; set; } = string.Empty;

        public string Password { get; set; } = string.Empty;

        public string? Mfa { get; set; }
    }

    public sealed class SessionRequest
    {
        public string? Name { get; set; }

        public string Session { get; set; } = string.Empty;
    }

    public sealed class LinkRequest
    {
        public string? Name { get; set; }

        public string Link { get; set; } = string.Empty;
    }

    public sealed class StatusResponse
    {
        public bool Syncing { get; set; }

        public DateTime? LastSync { get; set; }

        public string StreamBaseUrl { get; set; } = string.Empty;

        public string LibraryRoot { get; set; } = string.Empty;

        public int OpenStreams { get; set; }

        public List<AccountStatus> Accounts { get; set; } = new();

        public List<LibraryStatus> Libraries { get; set; } = new();
    }

    public sealed class AccountStatus
    {
        public string Id { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public string Kind { get; set; } = string.Empty;

        public string Email { get; set; } = string.Empty;

        public bool Loaded { get; set; }

        public int Nodes { get; set; }

        public DateTime? LoadedAt { get; set; }

        public string? Error { get; set; }
    }

    public sealed class LibraryStatus
    {
        public string Id { get; set; } = string.Empty;

        public string LocalPath { get; set; } = string.Empty;

        public int MediaFiles { get; set; }

        public int SideFiles { get; set; }

        public int Folders { get; set; }

        public DateTime? LastSync { get; set; }

        public string? Error { get; set; }
    }

    public sealed class FolderListing
    {
        public string Path { get; set; } = "/";

        public string? Parent { get; set; }

        public int MediaFiles { get; set; }

        public List<FolderEntry> Folders { get; set; } = new();
    }

    public sealed class FolderEntry
    {
        public string Name { get; set; } = string.Empty;

        public string Path { get; set; } = string.Empty;
    }
#pragma warning restore CA1002, CA2227
}
