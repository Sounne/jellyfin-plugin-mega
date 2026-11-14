using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Mega.Configuration;
using Jellyfin.Plugin.Mega.Library;
using Jellyfin.Plugin.Mega.Mega;
using Jellyfin.Plugin.Mega.Services;
using Jellyfin.Plugin.Mega.Streaming;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Mega.Tests.Support;

/// <summary>
/// Tests that share process-wide state (<see cref="Plugin.Instance"/>, the default MEGA API address):
/// one at a time, after the parallel tests.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class FakeMegaCollection
{
    public const string Name = "Fake MEGA";
}

/// <summary>
/// The plugin as Jellyfin loads it (plugin instance, settings, <see cref="MegaManager"/>, <see cref="LibrarySynchronizer"/>)
/// in a temporary folder, with its MEGA source pointing at a <see cref="FakeMega"/>.
/// </summary>
internal sealed class PluginHost : IAsyncDisposable
{
    private readonly string _previousApi;

    private PluginHost(FakeMega mega, string root)
    {
        Mega = mega;
        Root = root;
        _previousApi = MegaApiClient.DefaultApiBase;
        MegaApiClient.DefaultApiBase = mega.Url;

        Config = new PluginConfiguration { StreamBaseUrl = "http://127.0.0.1:8096/" };
        var appPaths = new Fake<IApplicationPaths> { Strings = name => Path.Combine(root, name) };
        var xml = new Fake<IXmlSerializer>().On("DeserializeFromFile", _ => Config);
        Plugin = new Plugin(appPaths.Object, xml.Object);

        var serverPaths = new Fake<IServerApplicationPaths> { Strings = name => Path.Combine(root, name) };
        var host = new Fake<IServerApplicationHost>().On("get_HttpPort", _ => 8096);
        var serverConfig = new Fake<IServerConfigurationManager>().On("GetConfiguration", _ => Network);
        Manager = new MegaManager(NullLogger<MegaManager>.Instance, host.Object, serverConfig.Object, serverPaths.Object, new ConfigurationBuilder().Build());

        Library = new Fake<ILibraryManager>()
            .On("GetVirtualFolders", _ => VirtualFolders.ToList())
            .On("AddVirtualFolder", args =>
            {
                var options = (LibraryOptions)args[2]!;
                VirtualFolders.Add(new VirtualFolderInfo { Name = (string)args[0]!, Locations = options.PathInfos.Select(p => p.Path).ToArray() });
                return Task.CompletedTask;
            })
            .On("GetItemById", args => args[0] is Guid id && Items.TryGetValue(id, out var item) ? item : null);
        Sync = new LibrarySynchronizer(Manager, Library.Object, NullLogger<LibrarySynchronizer>.Instance);

        Account = mega.IsAccount
            ? new MegaAccountConfig { Id = "acc1", Name = "Account", Kind = MegaAccountConfig.KindAccount, Email = mega.Email, Session = mega.Session }
            : new MegaAccountConfig { Id = "link1", Name = "Link", Kind = MegaAccountConfig.KindFolderLink, FolderLink = mega.Link };
        Config.Accounts.Add(Account);
    }

    public FakeMega Mega { get; }

    public string Root { get; }

    public Plugin Plugin { get; }

    public PluginConfiguration Config { get; }

    public MegaManager Manager { get; }

    public LibrarySynchronizer Sync { get; }

    public Fake<ILibraryManager> Library { get; }

    public List<VirtualFolderInfo> VirtualFolders { get; } = new();

    public Dictionary<Guid, BaseItem> Items { get; } = new();

    public MediaBrowser.Common.Net.NetworkConfiguration Network { get; } = new();

    public MegaAccountConfig Account { get; }

    internal MegaSource Source => Manager.GetSource(Account.Id)!;

    public static async Task<PluginHost> StartAsync(bool account = false)
    {
        var mega = await FakeMega.StartAsync(account);
        var root = Path.Combine(Path.GetTempPath(), "mega-plugin-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return new PluginHost(mega, root);
    }

    public MegaLibraryConfig AddLibrary(string megaPath, string name, string type = "movies")
    {
        var lib = new MegaLibraryConfig { AccountId = Account.Id, MegaPath = megaPath, LibraryName = name, ContentType = type };
        Config.Libraries.Add(lib);
        return lib;
    }

    public string LibraryPath(MegaLibraryConfig lib) => Manager.GetLibraryPath(lib);

    /// <summary>Stream URL of a file, as written in its <c>.strm</c>.</summary>
    public string StreamUrl(string handle)
    {
        var node = Source.Tree!.Get(handle)!;
        return StreamUrls.Build(Manager.StreamBaseUrl, Manager.Configuration.StreamSecret, Account.Id, node);
    }

    /// <summary>A Jellyfin video item whose file is a <c>.strm</c> of the plugin, as a library scan creates it.</summary>
    public Video AddItem(string handle, string? name = null)
    {
        var dir = Path.Combine(Manager.RootPath, "libraries", "Items");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, (name ?? handle) + ".strm");
        File.WriteAllText(path, StreamUrl(handle) + "\n");
        var item = new Video { Id = Guid.NewGuid(), Name = name ?? handle, Path = path };
        Items[item.Id] = item;
        return item;
    }

    public async ValueTask DisposeAsync()
    {
        Manager.Dispose();
        await Mega.DisposeAsync();
        MegaApiClient.DefaultApiBase = _previousApi;

        // The tree cache is written in the background: give it a moment, never fail on cleanup.
        for (int i = 0; i < 10; i++)
        {
            try
            {
                Directory.Delete(Root, true);
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(100);
            }
        }
    }
}

internal static class Wait
{
    /// <summary>Waits until <paramref name="condition"/> holds (background work), failing after <paramref name="seconds"/>.</summary>
    public static async Task Until(Func<bool> condition, string what, int seconds = 10)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        while (!condition())
        {
            if (cts.IsCancellationRequested)
            {
                Assert.Fail("timed out waiting for " + what);
            }

            await Task.Delay(20);
        }
    }
}
