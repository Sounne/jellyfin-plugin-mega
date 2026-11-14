using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Mega.Api;
using Jellyfin.Plugin.Mega.Mega;
using Jellyfin.Plugin.Mega.Streaming;
using Jellyfin.Plugin.Mega.Tests.Support;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Mega.Tests;

/// <summary>The stream endpoint of the <c>.strm</c> files, served by ASP.NET Core like inside Jellyfin.</summary>
[Collection(FakeMegaCollection.Name)]
public sealed class StreamEndpointTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    /// <summary>Jellyfin's web host reduced to the stream controller.</summary>
    private static async Task<WebApplication> ServeAsync(PluginHost host)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(host.Manager);
        builder.Services.AddControllers().ConfigureApplicationPartManager(parts =>
        {
            parts.ApplicationParts.Clear();
            parts.ApplicationParts.Add(new AssemblyPart(typeof(MegaStreamController).Assembly));
            foreach (var provider in parts.FeatureProviders.OfType<ControllerFeatureProvider>().ToList())
            {
                parts.FeatureProviders.Remove(provider);
            }

            parts.FeatureProviders.Add(new OnlyStreamController());
        });
        var app = builder.Build();
        app.MapControllers();
        await app.StartAsync();
        host.Config.StreamBaseUrl = app.Urls.First();
        return app;
    }

    private static async Task<(PluginHost Host, WebApplication App, HttpClient Http, string Handle, byte[] Content)> StartAsync(int size = (3 << 20) + 12345)
    {
        var host = await PluginHost.StartAsync();
        var content = TestData.Bytes(size);
        var handle = host.Mega.AddFile(host.Mega.AddFolder(null, "Films"), "Film #1 (2020) é.mkv", content);
        var app = await ServeAsync(host);
        await host.Source.GetTreeAsync(Ct);
        return (host, app, new HttpClient(), handle, content);
    }

    private static async Task StopAsync(PluginHost host, WebApplication app, HttpClient http)
    {
        http.Dispose();
        await app.StopAsync();
        await app.DisposeAsync();
        await host.DisposeAsync();
    }

    [Fact]
    public async Task WholeFileDecrypted()
    {
        var (host, app, http, handle, content) = await StartAsync();
        try
        {
            using var resp = await http.GetAsync(host.StreamUrl(handle));
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Equal(content, await resp.Content.ReadAsByteArrayAsync());
            Assert.Equal(content.Length, resp.Content.Headers.ContentLength);
            Assert.Equal("video/x-matroska", resp.Content.Headers.ContentType!.MediaType);
            Assert.Equal("bytes", resp.Headers.AcceptRanges.Single());
            Assert.Equal($"\"{handle}-{content.Length}\"", resp.Headers.ETag!.Tag);
            Assert.Equal(1, host.Manager.Readers.OpenReaders);
        }
        finally
        {
            await StopAsync(host, app, http);
        }
    }

    [Theory]
    [InlineData(1000, 1999)]
    [InlineData(0, 0)]
    [InlineData(2_000_000, null)]
    [InlineData(null, 500)]
    public async Task ByteRanges(int? from, int? to)
    {
        var (host, app, http, handle, content) = await StartAsync();
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, host.StreamUrl(handle));
            req.Headers.Range = new RangeHeaderValue(from, to);
            using var resp = await http.SendAsync(req);
            long start = from ?? content.Length - to!.Value;
            long end = from is null || to is null ? content.Length - 1 : to.Value;
            Assert.Equal(HttpStatusCode.PartialContent, resp.StatusCode);
            Assert.Equal($"bytes {start}-{end}/{content.Length}", resp.Content.Headers.ContentRange!.ToString());
            Assert.Equal(content.AsSpan((int)start, (int)(end - start + 1)).ToArray(), await resp.Content.ReadAsByteArrayAsync());
        }
        finally
        {
            await StopAsync(host, app, http);
        }
    }

    [Fact]
    public async Task RangeOutsideTheFile()
    {
        var (host, app, http, handle, content) = await StartAsync();
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, host.StreamUrl(handle));
            req.Headers.Range = new RangeHeaderValue(content.Length, null);
            using var resp = await http.SendAsync(req);
            Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, resp.StatusCode);
            Assert.Equal($"bytes */{content.Length}", resp.Content.Headers.ContentRange!.ToString());
        }
        finally
        {
            await StopAsync(host, app, http);
        }
    }

    [Fact]
    public async Task HeadReadsNothingFromMega()
    {
        var (host, app, http, handle, content) = await StartAsync();
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Head, host.StreamUrl(handle));
            using var resp = await http.SendAsync(req);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Equal(content.Length, resp.Content.Headers.ContentLength);
            Assert.Equal(0, host.Mega.StorageRequests);
        }
        finally
        {
            await StopAsync(host, app, http);
        }
    }

    [Fact]
    public async Task UrlsMustBeSigned()
    {
        var (host, app, http, handle, _) = await StartAsync();
        try
        {
            var url = host.StreamUrl(handle);
            var parts = url.Split('/');
            parts[^2] = "AAAAAAAAAAAAAAAAAAAA";
            using (var forged = await http.GetAsync(string.Join('/', parts)))
            {
                Assert.Equal(HttpStatusCode.Forbidden, forged.StatusCode);
            }

            // Another handle under a valid signature: refused too.
            using (var swapped = await http.GetAsync(url.Replace(handle, "Hnd99999", StringComparison.Ordinal)))
            {
                Assert.Equal(HttpStatusCode.Forbidden, swapped.StatusCode);
            }

            // A new secret invalidates every URL written before.
            host.Config.StreamSecret = "another secret";
            using (var old = await http.GetAsync(url))
            {
                Assert.Equal(HttpStatusCode.Forbidden, old.StatusCode);
            }

            Assert.Equal(0, host.Mega.StorageRequests);
        }
        finally
        {
            await StopAsync(host, app, http);
        }
    }

    [Fact]
    public async Task FileGoneFromMega()
    {
        var (host, app, http, _, _) = await StartAsync();
        try
        {
            var ghost = new MegaNode { Handle = "Hnd99999", Name = "Gone.mkv" };
            var url = StreamUrls.Build(host.Manager.StreamBaseUrl, host.Config.StreamSecret, host.Account.Id, ghost);
            using var resp = await http.GetAsync(url);
            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        }
        finally
        {
            await StopAsync(host, app, http);
        }
    }

    [Fact]
    public async Task ExhaustedQuotaIsServiceUnavailable()
    {
        var (host, app, http, handle, _) = await StartAsync();
        try
        {
            for (int i = 0; i < 20; i++)
            {
                host.Mega.StorageFaults.Enqueue(509);
            }

            using var resp = await http.GetAsync(host.StreamUrl(handle));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
            Assert.Contains("quota", await resp.Content.ReadAsStringAsync(), StringComparison.Ordinal);

            // The failed reader is not reused once MEGA serves again.
            host.Mega.StorageFaults.Clear();
            using var again = await http.GetAsync(host.StreamUrl(handle));
            Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        }
        finally
        {
            await StopAsync(host, app, http);
        }
    }

    [Fact]
    public async Task EmptyFile()
    {
        var (host, app, http, handle, _) = await StartAsync(0);
        try
        {
            using var resp = await http.GetAsync(host.StreamUrl(handle));
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Empty(await resp.Content.ReadAsByteArrayAsync());
        }
        finally
        {
            await StopAsync(host, app, http);
        }
    }

    [Theory]
    [InlineData("203.0.113.7", true, 403)]
    [InlineData("2001:db8::1", true, 403)]
    [InlineData("127.0.0.1", true, 200)]
    [InlineData("::1", true, 200)]
    [InlineData("::ffff:127.0.0.1", true, 200)]
    [InlineData("203.0.113.7", false, 200)]
    public async Task OnlyTheServerItselfByDefault(string remote, bool restrict, int expected)
    {
        await using var host = await PluginHost.StartAsync();
        var handle = host.Mega.AddFile(null, "Movie.mkv", TestData.Bytes(100));
        host.Config.RestrictStreamToLocalhost = restrict;
        var controller = new MegaStreamController(host.Manager, NullLogger<MegaStreamController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
        controller.HttpContext.Connection.RemoteIpAddress = IPAddress.Parse(remote);
        var sig = StreamUrls.Sign(host.Manager.Configuration.StreamSecret, host.Account.Id, handle);

        var result = await controller.GetStream(host.Account.Id, handle, sig, "Movie.mkv", Ct);
        if (expected == 403)
        {
            Assert.Equal(403, Assert.IsType<ObjectResult>(result).StatusCode);
        }
        else
        {
            Assert.IsType<MegaRangeResult>(result);
        }
    }

    private sealed class OnlyStreamController : ControllerFeatureProvider
    {
        protected override bool IsController(TypeInfo typeInfo) => typeInfo.AsType() == typeof(MegaStreamController);
    }
}
