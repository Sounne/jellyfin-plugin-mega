using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Mega.Configuration;
using Jellyfin.Plugin.Mega.Mega;
using Jellyfin.Plugin.Mega.Streaming;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Jellyfin.Plugin.Mega.Tests;

/// <summary>
/// Playback against a local storage server that answers slowly and limits each connection, like a MEGA
/// server far away: each request waits <see cref="Latency"/> before the first byte, then
/// <see cref="PerConnection"/> bytes per second.
/// </summary>
public sealed class PlaybackSimTests : IAsyncLifetime
{
    private const long FileSize = 8L << 30;
    private readonly ITestOutputHelper _out;
    private readonly byte[] _pattern = new byte[1 << 20];
    private readonly byte[] _fileKey = RandomNumberGenerator.GetBytes(32);
    private WebApplication? _app;
    private int _requests;

    public PlaybackSimTests(ITestOutputHelper output)
    {
        _out = output;
        RandomNumberGenerator.Fill(_pattern);
    }

    private static TimeSpan Latency => TimeSpan.FromMilliseconds(Env("SIM_LATENCY_MS", 300));

    private static double PerConnection => Env("SIM_CONN_MBPS", 3) * 1048576;

    private static double Env(string name, double fallback)
        => double.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        _app = builder.Build();
        _app.MapGet("/dl/{range}", async (string range, HttpContext ctx) =>
        {
            var parts = range.Split('-');
            long from = long.Parse(parts[0], CultureInfo.InvariantCulture);
            long to = long.Parse(parts[1], CultureInfo.InvariantCulture);
            Interlocked.Increment(ref _requests);
            await Task.Delay(Latency, ctx.RequestAborted);
            ctx.Response.ContentLength = to - from + 1;
            const int Chunk = 32 * 1024;
            var sw = Stopwatch.StartNew();
            long sent = 0;
            for (long p = from; p <= to;)
            {
                int inPattern = (int)(p % _pattern.Length);
                int n = (int)Math.Min(Math.Min(Chunk, to - p + 1), _pattern.Length - inPattern);
                await ctx.Response.Body.WriteAsync(_pattern.AsMemory(inPattern, n), ctx.RequestAborted);
                p += n;
                sent += n;
                var due = TimeSpan.FromSeconds(sent / PerConnection) - sw.Elapsed;
                if (due > TimeSpan.Zero)
                {
                    await Task.Delay(due, ctx.RequestAborted);
                }
            }
        });
        await _app.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    private MegaFileReader NewReader()
    {
        var source = new MegaSource(new MegaAccountConfig { Id = "t", Session = "x" });
        var node = new MegaNode { Handle = "HANDLE01", Name = "movie.mkv", Size = FileSize, Type = MegaNodeType.File, Key = FileKey.Unpack(_fileKey) };
        var urls = (ConcurrentDictionary<string, (string Url, DateTime At)>)typeof(MegaSource)
            .GetField("_downloadUrls", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(source)!;
        urls[node.Handle] = (_app!.Urls.First() + "/dl", DateTime.UtcNow);
        return new MegaFileReader(source, node, null, NullLogger.Instance, 48, 4);
    }

    /// <summary>A reader that never waits for the player (ffmpeg remux or transcode, client filling its buffer).</summary>
    [Fact]
    public async Task BurstReadUsesTheConnections()
    {
        using var reader = NewReader();
        var chunk = new byte[256 * 1024];
        long pos = 64 << 20;
        const int Total = 96 << 20;
        var sw = Stopwatch.StartNew();
        for (long end = pos + Total; pos < end;)
        {
            int n = await reader.ReadAsync(pos, chunk, CancellationToken.None);
            Assert.True(n > 0);
            pos += n;
        }

        double mbps = Total / 1048576.0 / sw.Elapsed.TotalSeconds;
        double ideal = 4 * PerConnection / 1048576;
        _out.WriteLine($"burst 96 MiB: {mbps:F1} MB/s of {ideal:F0} possible, {_requests} requests");
        Assert.True(mbps > 0.6 * ideal, $"only {mbps:F1} MB/s of {ideal:F0} MB/s with 4 connections");
    }

    /// <summary>
    /// ExoPlayer-like client (Android TV, Moonfin): starts after 2.5 s of media, resumes 5 s after a stall,
    /// keeps up to 50 s buffered. A bitrate of half what 4 connections give must play without stalls, and
    /// the client buffer must fill up.
    /// </summary>
    [Fact]
    public async Task SteadyPlaybackDoesNotStall()
    {
        double rate = Env("SIM_RATE_MBPS", 6) * 1048576;
        int seconds = (int)Env("SIM_SECONDS", 25);
        using var reader = NewReader();
        var chunk = new byte[256 * 1024];
        var sw = Stopwatch.StartNew();
        double Now() => sw.Elapsed.TotalSeconds;
        long at = 32 << 20;
        long read = 0;
        double played = 0;
        double last = 0;
        bool playing = false;
        double startup = -1;
        double stallStart = 0;
        double stallTotal = 0;
        int stalls = 0;
        while (Now() < seconds)
        {
            double now = Now();
            if (playing)
            {
                double avail = (read / rate) - played;
                double dt = now - last;
                if (dt < avail)
                {
                    played += dt;
                }
                else
                {
                    played += avail;
                    playing = false;
                    stalls++;
                    stallStart = now - (dt - avail);
                }
            }

            last = now;
            double buffered = (read / rate) - played;
            if (!playing && buffered >= (startup < 0 ? 2.5 : 5))
            {
                if (startup < 0)
                {
                    startup = now;
                }
                else
                {
                    stallTotal += now - stallStart;
                }

                playing = true;
            }

            if (buffered >= 50)
            {
                await Task.Delay(100);
                continue;
            }

            int n = await reader.ReadAsync(at + read, chunk, CancellationToken.None);
            Assert.True(n > 0);
            read += n;
        }

        if (!playing && startup >= 0)
        {
            stallTotal += Now() - stallStart;
        }

        _out.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{rate / 1048576} MB/s, server {Latency.TotalMilliseconds} ms + {PerConnection / 1048576} MB/s per connection: start {startup:F1} s, {stalls} stalls ({stallTotal:F1} s), buffer at end {(read / rate) - played:F0} s, {_requests} requests ({read / 1048576.0 / Math.Max(1, _requests):F1} MiB each)"));
        Assert.True(startup >= 0 && startup < 4, $"playback started after {startup:F1} s");
        Assert.Equal(0, stalls);

        // At half the possible speed, the client buffer must grow: one block per request left it at 2-3 s,
        // so any slow answer from the server paused playback.
        double end = (read / rate) - played;
        Assert.True(end >= 10, $"client buffer only {end:F0} s after {seconds} s");
    }
}
