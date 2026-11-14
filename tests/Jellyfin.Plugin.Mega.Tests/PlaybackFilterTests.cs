using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Mega.Api;
using Jellyfin.Plugin.Mega.Streaming;
using Jellyfin.Plugin.Mega.Subtitles;
using Jellyfin.Plugin.Mega.Tests.Support;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.IO;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Mega.Tests;

/// <summary>How MEGA items look to Jellyfin's clients: local files, served directly, short ffmpeg analysis.</summary>
[Collection(FakeMegaCollection.Name)]
public sealed class PlaybackFilterTests
{
    private static MegaPlaybackFilter Filter(PluginHost host)
    {
        var subtitles = new SubtitlePrefill(host.Manager, new Fake<IPathManager>().Object, NullLogger<SubtitlePrefill>.Instance);
        return new MegaPlaybackFilter(host.Manager, host.Library.Object, subtitles, NullLogger<MegaPlaybackFilter>.Instance);
    }

    private static async Task<(PluginHost Host, string Handle, byte[] Content, BaseItem Item)> StartAsync()
    {
        var host = await PluginHost.StartAsync();
        var content = TestData.Bytes(200_000);
        var handle = host.Mega.AddFile(null, "Movie.mkv", content);
        await host.Source.GetTreeAsync(CancellationToken.None);
        return (host, handle, content, host.AddItem(handle, "Movie"));
    }

    private static ActionContext Action(string controller, string action, HttpContext? http = null)
        => new(http ?? new DefaultHttpContext(), new RouteData(), new ControllerActionDescriptor { ControllerName = controller, ActionName = action });

    private static MediaSourceInfo MegaSource(BaseItem item, string url) => new()
    {
        Id = item.Id.ToString("N"),
        Path = url,
        Protocol = MediaProtocol.Http,
        IsRemote = true,
        Size = 160,
        MediaStreams = new List<MediaStream>(),
    };

    private static async Task Results(MegaPlaybackFilter filter, object value)
    {
        var context = new ResultExecutingContext(Action("MediaInfo", "GetPostedPlaybackInfo"), new List<IFilterMetadata>(), new ObjectResult(value), new object());
        bool next = false;
        await filter.OnResultExecutionAsync(context, () =>
        {
            next = true;
            return Task.FromResult(new ResultExecutedContext(context, context.Filters, context.Result, context.Controller));
        });
        Assert.True(next);
    }

    [Fact]
    public async Task PlaybackInfoShowsALocalFile()
    {
        var (host, handle, content, item) = await StartAsync();
        await using var _ = host;
        var mega = MegaSource(item, host.StreamUrl(handle));
        var local = new MediaSourceInfo { Id = Guid.NewGuid().ToString("N"), Path = "/media/film.mkv", Protocol = MediaProtocol.File, Size = 42 };
        var http = new MediaSourceInfo { Id = Guid.NewGuid().ToString("N"), Path = "https://example.com/film.mkv", Protocol = MediaProtocol.Http, IsRemote = true };

        await Results(Filter(host), new PlaybackInfoResponse { MediaSources = new[] { mega, local, http } });

        Assert.Equal(item.Path, mega.Path);
        Assert.Equal(MediaProtocol.File, mega.Protocol);
        Assert.False(mega.IsRemote);
        Assert.Equal(content.Length, mega.Size);

        Assert.Equal("/media/film.mkv", local.Path);
        Assert.Equal(42, local.Size);
        Assert.Equal("https://example.com/film.mkv", http.Path);
        Assert.True(http.IsRemote);
    }

    [Fact]
    public async Task ItemDetailsToo()
    {
        var (host, handle, content, item) = await StartAsync();
        await using var _ = host;
        var dto = new BaseItemDto { MediaSources = new[] { MegaSource(item, host.StreamUrl(handle)) } };
        await Results(Filter(host), dto);
        Assert.Equal(MediaProtocol.File, dto.MediaSources[0].Protocol);

        var listed = new BaseItemDto { MediaSources = new[] { MegaSource(item, host.StreamUrl(handle)) } };
        await Results(Filter(host), new QueryResult<BaseItemDto>(new[] { listed, new BaseItemDto() }));
        Assert.Equal(content.Length, listed.MediaSources[0].Size);

        // Unknown item: still not shown as remote.
        var orphan = new MediaSourceInfo { Id = Guid.NewGuid().ToString("N"), Path = host.StreamUrl(handle), Protocol = MediaProtocol.Http, IsRemote = true };
        await Results(Filter(host), new BaseItemDto { MediaSources = new[] { orphan } });
        Assert.Equal("MEGA", orphan.Path);
        Assert.False(orphan.IsRemote);
    }

    [Fact]
    public async Task StaticStreamIsServedDirectly()
    {
        var (host, _, content, item) = await StartAsync();
        await using var __ = host;
        var http = new DefaultHttpContext();
        http.Response.Body = new MemoryStream();
        var action = Action("Videos", "GetVideoStream", http);
        var context = new ActionExecutingContext(action, new List<IFilterMetadata>(), new Dictionary<string, object?> { ["itemId"] = item.Id, ["static"] = true }, new object());
        bool next = false;

        await Filter(host).OnActionExecutionAsync(context, () =>
        {
            next = true;
            return Task.FromResult(new ActionExecutedContext(context, context.Filters, context.Controller));
        });

        Assert.False(next);
        var result = Assert.IsType<MegaRangeResult>(context.Result);
        await result.ExecuteResultAsync(action);
        Assert.Equal(200, http.Response.StatusCode);
        Assert.Equal(content, ((MemoryStream)http.Response.Body).ToArray());
    }

    [Theory]
    [InlineData("MediaInfo", "GetPostedPlaybackInfo", true, true)]
    [InlineData("Videos", "GetVideoStream", true, true)]
    [InlineData("DynamicHls", "GetMasterHlsVideoPlaylist", true, true)]
    [InlineData("Items", "GetItem", true, false)]
    [InlineData("MediaInfo", "GetPostedPlaybackInfo", false, false)]
    public async Task ShortAnalysisForMegaItemsOnly(string controller, string action, bool mega, bool shortened)
    {
        var (host, _, _, item) = await StartAsync();
        await using var __ = host;
        if (!mega)
        {
            item.Path = Path.Combine(host.Root, "elsewhere", "Movie.strm");
        }

        var context = new ActionExecutingContext(Action(controller, action), new List<IFilterMetadata>(), new Dictionary<string, object?> { ["itemId"] = item.Id }, new object());
        bool? active = null;
        await Filter(host).OnActionExecutionAsync(context, () =>
        {
            active = FfmpegAnalysis.IsActive;
            return Task.FromResult(new ActionExecutedContext(context, context.Filters, context.Controller));
        });

        Assert.Equal(shortened, active);
        Assert.False(FfmpegAnalysis.IsActive);
        Assert.Null(context.Result);
    }

    [Fact]
    public async Task UniversalAudioNeverRedirects()
    {
        var (host, _, _, item) = await StartAsync();
        await using var __ = host;
        var args = new Dictionary<string, object?> { ["itemId"] = item.Id, ["enableRedirection"] = true };
        var context = new ActionExecutingContext(Action("UniversalAudio", "GetUniversalAudioStream"), new List<IFilterMetadata>(), args, new object());
        await Filter(host).OnActionExecutionAsync(context, () => Task.FromResult(new ActionExecutedContext(context, context.Filters, context.Controller)));
        Assert.False((bool)args["enableRedirection"]!);
    }

    [Fact]
    public async Task ItemFoundThroughTheMediaSourceId()
    {
        var (host, _, content, item) = await StartAsync();
        await using var __ = host;
        var http = new DefaultHttpContext();
        http.Response.Body = new MemoryStream();
        var args = new Dictionary<string, object?> { ["itemId"] = Guid.NewGuid(), ["mediaSourceId"] = item.Id.ToString("N"), ["static"] = true };
        var context = new ActionExecutingContext(Action("Audio", "GetAudioStream", http), new List<IFilterMetadata>(), args, new object());
        await Filter(host).OnActionExecutionAsync(context, () => Task.FromResult(new ActionExecutedContext(context, context.Filters, context.Controller)));
        Assert.IsType<MegaRangeResult>(context.Result);
    }
}
