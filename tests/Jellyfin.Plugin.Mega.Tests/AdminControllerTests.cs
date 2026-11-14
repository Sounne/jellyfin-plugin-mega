using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Mega.Api;
using Jellyfin.Plugin.Mega.Configuration;
using Jellyfin.Plugin.Mega.Mega;
using Jellyfin.Plugin.Mega.Tests.Support;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static Jellyfin.Plugin.Mega.Api.MegaAdminController;

namespace Jellyfin.Plugin.Mega.Tests;

/// <summary>Settings page actions: accounts, folder picker, status, manual sync.</summary>
[Collection(FakeMegaCollection.Name)]
public sealed class AdminControllerTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private static MegaAdminController Controller(PluginHost host) => new(host.Manager, host.Sync, NullLogger<MegaAdminController>.Instance);

    private static T Value<T>(ActionResult<T> result)
        where T : class
        => result.Value ?? Assert.IsAssignableFrom<T>(Assert.IsAssignableFrom<ObjectResult>(result.Result).Value);

    [Fact]
    public async Task FolderPicker()
    {
        await using var host = await PluginHost.StartAsync();
        var m = host.Mega;
        var films = m.AddFolder(null, "Films");
        m.AddFolder(null, "shows");
        m.AddFile(m.AddFolder(films, "Extras"), "Trailer.mp4", TestData.Bytes(10));
        m.AddFile(films, "A.mkv", TestData.Bytes(10));
        m.AddFile(films, "A.srt", TestData.Bytes(10));
        var admin = Controller(host);

        var root = Value(await admin.GetFolders(host.Account.Id, null, Ct));
        Assert.Equal("/", root.Path);
        Assert.Null(root.Parent);
        Assert.Equal(new[] { "Films", "shows" }, root.Folders.Select(f => f.Name));
        Assert.Equal(new[] { "/Films", "/shows" }, root.Folders.Select(f => f.Path));
        Assert.Equal(2, root.MediaFiles);

        var sub = Value(await admin.GetFolders(host.Account.Id, "/films", Ct));
        Assert.Equal("/Films", sub.Path);
        Assert.Equal("/", sub.Parent);
        Assert.Equal("/Films/Extras", Assert.Single(sub.Folders).Path);

        Assert.IsType<NotFoundResult>((await admin.GetFolders(host.Account.Id, "/Nope", Ct)).Result);
        Assert.IsType<NotFoundResult>((await admin.GetFolders("unknown", null, Ct)).Result);

        // A source never loaded while MEGA cannot be reached.
        host.Config.Accounts.Add(new MegaAccountConfig { Id = "down", Kind = MegaAccountConfig.KindFolderLink, FolderLink = host.Mega.Link });
        host.Mega.ApiDown = true;
        Assert.Equal(502, Assert.IsType<ObjectResult>((await admin.GetFolders("down", null, Ct)).Result).StatusCode);
    }

    [Fact]
    public async Task AddFolderLink()
    {
        await using var host = await PluginHost.StartAsync();
        host.Config.Accounts.Clear();
        var admin = Controller(host);

        var bad = await admin.AddLink(new LinkRequest { Link = "https://mega.nz/file/AbCd_-12#xyz" }, Ct);
        Assert.Contains("invalid MEGA folder link", (string)Assert.IsType<BadRequestObjectResult>(bad.Result).Value!, StringComparison.Ordinal);
        Assert.IsType<BadRequestObjectResult>((await admin.AddLink(new LinkRequest { Link = " " }, Ct)).Result);

        var added = Value(await admin.AddLink(new LinkRequest { Link = host.Mega.Link }, Ct));
        Assert.Equal("Shared media", added.Name);
        Assert.Equal(MegaAccountConfig.KindFolderLink, added.Kind);
        var saved = Assert.Single(host.Config.Accounts);
        Assert.Equal(added.Id, saved.Id);
        Assert.Equal(host.Mega.Link, saved.FolderLink);

        // The tree downloaded to check the link is kept; the same link again updates the account.
        Assert.NotNull(host.Manager.GetSource(added.Id)!.Tree);
        var again = Value(await admin.AddLink(new LinkRequest { Link = host.Mega.Link, Name = "Renamed" }, Ct));
        Assert.Equal(added.Id, again.Id);
        Assert.Equal("Renamed", Assert.Single(host.Config.Accounts).Name);
    }

    [Fact]
    public async Task AddSession()
    {
        await using var host = await PluginHost.StartAsync(account: true);
        host.Config.Accounts.Clear();
        var admin = Controller(host);

        var added = Value(await admin.AddSession(new SessionRequest { Session = host.Mega.Session }, Ct));
        Assert.Equal(host.Mega.Email, added.Email);
        Assert.Equal(host.Mega.Email, added.Name);
        Assert.Equal(host.Mega.Session, Assert.Single(host.Config.Accounts).Session);

        // The session never goes back to the page.
        Assert.Equal(string.Empty, added.Session);

        await using var other = await FakeMega.StartAsync(account: true);
        var closed = await admin.AddSession(new SessionRequest { Session = other.Session }, Ct);
        Assert.Contains("session expired", (string)Assert.IsType<BadRequestObjectResult>(closed.Result).Value!, StringComparison.Ordinal);
        Assert.Single(host.Config.Accounts);
    }

    [Fact]
    public async Task LoginRunsInTheBackground()
    {
        await using var host = await PluginHost.StartAsync(account: true);
        host.Config.Accounts.Clear();
        var admin = Controller(host);

        Assert.IsType<BadRequestObjectResult>(admin.Login(new LoginRequest { Email = host.Mega.Email }).Result);
        Assert.IsType<NotFoundResult>(admin.GetLogin("nope").Result);

        var started = Value(admin.Login(new LoginRequest { Email = host.Mega.Email, Password = host.Mega.Password, Name = "Mine" }));
        LoginStatus status = started;
        await Wait.Until(() => (status = Value(admin.GetLogin(started.Id))).State != "running", "the login", 60);
        Assert.Equal("done", status.State);
        Assert.Equal("Mine", status.Account!.Name);
        Assert.Equal(host.Mega.Email, status.Account.Email);
        Assert.Equal(string.Empty, status.Account.Session);

        // The password is not stored, only a session that resumes.
        var saved = Assert.Single(host.Config.Accounts);
        Assert.DoesNotContain(host.Mega.Password, System.Text.Json.JsonSerializer.Serialize(host.Config), StringComparison.Ordinal);
        var resumed = await MegaAccountSession.ResumeAsync(new MegaApiClient(), saved.Session, Ct);
        Assert.Equal(host.Mega.MasterKey, resumed.MasterKey);

        var wrong = Value(admin.Login(new LoginRequest { Email = host.Mega.Email, Password = "nope" }));
        await Wait.Until(() => (status = Value(admin.GetLogin(wrong.Id))).State != "running", "the failed login", 60);
        Assert.Equal("failed", status.State);
        Assert.Equal("wrong e-mail or password", status.Message);
    }

    [Fact]
    public async Task RemoveAccount()
    {
        await using var host = await PluginHost.StartAsync();
        host.Mega.AddFolder(null, "Films");
        host.AddLibrary("/Films", "Films");
        var other = new MegaLibraryConfig { AccountId = "someone else", MegaPath = "/" };
        host.Config.Libraries.Add(other);
        await host.Source.GetTreeAsync(Ct);
        var cache = Path.Combine(host.Manager.TreeCachePath, host.Account.Id + ".tree");
        await Wait.Until(() => File.Exists(cache), "the tree cache");

        Assert.IsType<NoContentResult>(Controller(host).RemoveAccount(host.Account.Id));
        Assert.Empty(host.Config.Accounts);
        Assert.Equal(other, Assert.Single(host.Config.Libraries));
        Assert.False(File.Exists(cache));
        Assert.Null(host.Manager.GetSource(host.Account.Id));
    }

    [Fact]
    public async Task StatusAndManualSync()
    {
        await using var host = await PluginHost.StartAsync();
        host.Mega.AddFile(host.Mega.AddFolder(null, "Films"), "Movie.mkv", TestData.Bytes(10));
        var lib = host.AddLibrary("/Films", "Films");
        var admin = Controller(host);

        var before = Value(admin.GetStatus());
        Assert.False(before.Syncing);
        Assert.Null(before.LastSync);
        Assert.Equal("http://127.0.0.1:8096", before.StreamBaseUrl);
        Assert.Equal(host.Manager.RootPath, before.LibraryRoot);
        Assert.False(Assert.Single(before.Accounts).Loaded);

        Assert.IsType<AcceptedResult>(admin.SyncNow());
        await Wait.Until(() => host.Sync.LastSync is not null && !host.Sync.IsSyncing, "the manual sync");

        var after = Value(admin.GetStatus());
        var account = Assert.Single(after.Accounts);
        Assert.True(account.Loaded);
        Assert.Equal(host.Source.Tree!.Count, account.Nodes);
        Assert.Null(account.Error);
        var library = Assert.Single(after.Libraries);
        Assert.Equal(lib.Id, library.Id);
        Assert.Equal(host.LibraryPath(lib), library.LocalPath);
        Assert.Equal(1, library.MediaFiles);
        Assert.NotNull(library.LastSync);
    }
}
