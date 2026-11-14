using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Mega.Configuration;
using Jellyfin.Plugin.Mega.Images;
using Jellyfin.Plugin.Mega.Services;
using Jellyfin.Plugin.Mega.Streaming;
using Jellyfin.Plugin.Mega.Tests.Support;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Mega.Tests;

/// <summary>The central service: settings, sources, item lookup, previews; and the image provider on top of it.</summary>
[Collection(FakeMegaCollection.Name)]
public sealed class ManagerTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public async Task StreamBaseUrl()
    {
        await using var host = await PluginHost.StartAsync();
        Assert.Equal("http://127.0.0.1:8096", host.Manager.StreamBaseUrl);

        host.Config.StreamBaseUrl = string.Empty;
        Assert.Equal("http://127.0.0.1:8096", host.Manager.StreamBaseUrl);
        host.Network.BaseUrl = "/jellyfin/";
        Assert.Equal("http://127.0.0.1:8096/jellyfin", host.Manager.StreamBaseUrl);

        host.Config.StreamBaseUrl = " https://media.example.com/jf/ ";
        Assert.Equal("https://media.example.com/jf", host.Manager.StreamBaseUrl);
    }

    [Fact]
    public async Task StreamSecretIsMadeOnce()
    {
        await using var host = await PluginHost.StartAsync();
        Assert.Equal(string.Empty, host.Config.StreamSecret);
        var secret = host.Manager.Configuration.StreamSecret;
        Assert.Equal(43, secret.Length);
        Assert.Equal(secret, host.Manager.Configuration.StreamSecret);
        Assert.Equal(secret, host.Config.StreamSecret);
    }

    [Fact]
    public async Task SourcesFollowTheSettings()
    {
        await using var host = await PluginHost.StartAsync();
        var source = host.Manager.GetSource(host.Account.Id);
        Assert.NotNull(source);
        Assert.Same(source, host.Manager.GetSource(host.Account.Id));
        Assert.Null(host.Manager.GetSource(null));
        Assert.Null(host.Manager.GetSource("unknown"));

        // A new name keeps the loaded source; new credentials do not.
        host.Account.Name = "Renamed";
        Assert.Same(source, host.Manager.GetSource(host.Account.Id));
        host.Account.FolderLink = host.Mega.Link + "/folder/Hnd00042";
        var rebuilt = host.Manager.GetSource(host.Account.Id);
        Assert.NotSame(source, rebuilt);

        // The source keeps its own copy of the settings.
        host.Account.FolderLink = "changed later";
        Assert.Equal(host.Mega.Link + "/folder/Hnd00042", rebuilt!.Config.FolderLink);

        host.Config.Accounts.Clear();
        Assert.Null(host.Manager.GetSource(host.Account.Id));
    }

    [Fact]
    public async Task LibraryFolders()
    {
        await using var host = await PluginHost.StartAsync();
        var libraries = Path.Combine(host.Manager.RootPath, "libraries");
        Assert.Equal(Path.Combine(libraries, "Films_ HD"), host.Manager.GetLibraryPath(new MegaLibraryConfig { LibraryName = "Films: HD" }));
        Assert.Equal(Path.Combine(libraries, "abc"), host.Manager.GetLibraryPath(new MegaLibraryConfig { Id = "abc", LibraryName = " " }));
        Assert.True(host.Manager.IsManagedPath(Path.Combine(libraries, "x", "y.strm")));
        Assert.False(host.Manager.IsManagedPath(Path.Combine(host.Root, "elsewhere", "y.strm")));
        Assert.False(host.Manager.IsManagedPath(null));
    }

    [Fact]
    public async Task ItemsAreTracedBackToTheirMegaFile()
    {
        await using var host = await PluginHost.StartAsync();
        var movie = host.Mega.AddFile(null, "Movie.mkv", TestData.Bytes(10));
        await host.Source.GetTreeAsync(Ct);
        var item = host.AddItem(movie);

        // After a restart, before the tree is loaded: nothing resolves without asking MEGA.
        host.Source.Reset();
        Assert.False(host.Manager.TryResolveItem(item, out _, out _));
        await host.Source.GetTreeAsync(Ct);
        Assert.True(host.Manager.TryResolveItem(item, out var source, out var node));
        Assert.Equal(host.Account.Id, source.Id);
        Assert.Equal(movie, node.Handle);

        // Shortcut path given by Jellyfin wins; a .strm outside the plugin folder is ignored.
        var shortcut = new MediaBrowser.Controller.Entities.Video { Path = Path.Combine(host.Root, "x.strm"), ShortcutPath = host.StreamUrl(movie) };
        Assert.True(host.Manager.TryResolveItem(shortcut, out _, out _));
        var outside = Path.Combine(host.Root, "outside.strm");
        File.WriteAllText(outside, host.StreamUrl(movie));
        Assert.False(host.Manager.TryResolveItem(new MediaBrowser.Controller.Entities.Video { Path = outside }, out _, out _));
        Assert.False(host.Manager.TryResolveItem(null, out _, out _));

        var resolved = await host.Manager.ResolveAsync(host.Account.Id, movie, Ct);
        Assert.Equal(movie, resolved!.Value.Node.Handle);
        Assert.Null(await host.Manager.ResolveAsync(host.Account.Id, "Missing1", Ct));
        Assert.Null(await host.Manager.ResolveAsync("unknown", movie, Ct));
    }

    [Fact]
    public void StrmFirstUrlLine()
    {
        var path = Path.Combine(TestData.TempDir(), "a.strm");
        try
        {
            File.WriteAllText(path, "# written by the MEGA plugin\n\n  http://127.0.0.1:8096/MegaFs/stream/a/b/c/d.mkv  \nsecond\n");
            Assert.Equal("http://127.0.0.1:8096/MegaFs/stream/a/b/c/d.mkv", MegaManager.ReadStrm(path));
            Assert.Null(MegaManager.ReadStrm(path + ".missing"));
        }
        finally
        {
            TestData.TryDelete(Path.GetDirectoryName(path)!);
        }
    }

    [Fact]
    public async Task PreviewsAreCachedOnDisk()
    {
        await using var host = await PluginHost.StartAsync();
        var movie = host.Mega.AddFile(null, "Movie.mkv", TestData.Bytes(10), preview: true);
        var bare = host.Mega.AddFile(null, "Bare.mkv", TestData.Bytes(10));
        var tree = await host.Source.GetTreeAsync(Ct);

        Assert.Equal(host.Mega.Preview(movie), await host.Manager.GetPreviewAsync(host.Source, tree.Get(movie)!, 1, Ct));
        Assert.Equal(host.Mega.Preview(movie), await host.Manager.GetPreviewAsync(host.Source, tree.Get(movie)!, 1, Ct));
        Assert.Equal(1, host.Mega.AttributeRequests);
        Assert.Null(await host.Manager.GetPreviewAsync(host.Source, tree.Get(movie)!, 0, Ct));
        Assert.Null(await host.Manager.GetPreviewAsync(host.Source, tree.Get(bare)!, 1, Ct));
        Assert.Equal(1, host.Mega.AttributeRequests);
    }

    [Fact]
    public async Task ImageProvider()
    {
        await using var host = await PluginHost.StartAsync();
        var movie = host.Mega.AddFile(null, "Movie.mkv", TestData.Bytes(10), preview: true);
        var bare = host.Mega.AddFile(null, "Bare.mkv", TestData.Bytes(10));
        await host.Source.GetTreeAsync(Ct);
        host.Config.ExtractFrameWhenNoPreview = false;
        var encoder = new Fake<IMediaEncoder>();
        var provider = new MegaImageProvider(host.Manager, encoder.Object, NullLogger<MegaImageProvider>.Instance);

        var item = host.AddItem(movie);
        Assert.True(provider.Supports(item));
        Assert.True(provider.Supports(new Audio { Path = item.Path }));
        Assert.False(provider.Supports(new MediaBrowser.Controller.Entities.Video { Path = Path.Combine(host.Root, "x.strm") }));
        Assert.False(provider.Supports(new MediaBrowser.Controller.Entities.Video { Path = Path.Combine(host.Manager.RootPath, "x.mkv") }));
        Assert.Equal(new[] { ImageType.Primary }, provider.GetSupportedImages(item));

        var image = await provider.GetImage(item, ImageType.Primary, Ct);
        Assert.True(image.HasImage);
        Assert.Equal(ImageFormat.Jpg, image.Format);
        using (var ms = new MemoryStream())
        {
            await image.Stream.CopyToAsync(ms);
            Assert.Equal(host.Mega.Preview(movie), ms.ToArray());
        }

        Assert.False((await provider.GetImage(host.AddItem(bare), ImageType.Primary, Ct)).HasImage);
        host.Config.UseMegaPreviews = false;
        Assert.False((await provider.GetImage(host.AddItem(movie, "again"), ImageType.Primary, Ct)).HasImage);
        Assert.Empty(encoder.Calls("ExtractVideoImage"));
    }
}
