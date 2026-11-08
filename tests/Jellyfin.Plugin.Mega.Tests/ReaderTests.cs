using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Net;
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

/// <summary>Reader behaviour against a local storage server that limits the speed of each connection, like MEGA.</summary>
public sealed class ReaderTests : IAsyncLifetime
{
    private const int FileSize = 96 << 20;
    private const int BytesPerSecondPerConnection = 4 << 20;
    private readonly ITestOutputHelper _out;
    private readonly byte[] _plain = new byte[FileSize];
    private readonly byte[] _cipher;
    private readonly byte[] _fileKey = RandomNumberGenerator.GetBytes(32);
    private WebApplication? _app;
    private long _served;
    private int _requests;

    public ReaderTests(ITestOutputHelper output)
    {
        _out = output;
        new Random(42).NextBytes(_plain);
        var key = FileKey.Unpack(_fileKey)!;
        _cipher = (byte[])_plain.Clone();
        MegaCrypto.CtrTransform(key.AesKey, key.Nonce, 0, _cipher);
    }

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        _app = builder.Build();
        _app.MapGet("/dl/{range}", async (string range, HttpContext ctx) =>
        {
            var parts = range.Split('-');
            long from = long.Parse(parts[0]);
            long to = long.Parse(parts[1]);
            Interlocked.Increment(ref _requests);
            await Task.Delay(40);
            ctx.Response.ContentLength = to - from + 1;
            const int Chunk = 64 * 1024;
            var sw = Stopwatch.StartNew();
            long sent = 0;
            for (long p = from; p <= to; p += Chunk)
            {
                int n = (int)Math.Min(Chunk, to - p + 1);
                await ctx.Response.Body.WriteAsync(_cipher.AsMemory((int)p, n), ctx.RequestAborted);
                sent += n;
                Interlocked.Add(ref _served, n);
                var due = TimeSpan.FromSeconds((double)sent / BytesPerSecondPerConnection) - sw.Elapsed;
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

    private MegaFileReader NewReader(string name = "movie.mkv")
    {
        var source = new MegaSource(new MegaAccountConfig { Id = "t", Session = "x" });
        var node = new MegaNode { Handle = "HANDLE01", Name = name, Size = FileSize, Type = MegaNodeType.File, Key = FileKey.Unpack(_fileKey) };
        var urls = (ConcurrentDictionary<string, (string Url, DateTime At)>)typeof(MegaSource)
            .GetField("_downloadUrls", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(source)!;
        urls[node.Handle] = (_app!.Urls.First() + "/dl", DateTime.UtcNow);
        return new MegaFileReader(source, node, null, NullLogger.Instance, 48, 4);
    }

    private static async Task ReadExactly(MegaFileReader reader, long offset, byte[] buffer)
    {
        int done = 0;
        while (done < buffer.Length)
        {
            int n = await reader.ReadAsync(offset + done, buffer.AsMemory(done), CancellationToken.None);
            Assert.True(n > 0);
            done += n;
        }
    }

    [Fact]
    public async Task ProbeDownloadsLittle()
    {
        using var reader = NewReader();
        var head = new byte[2 << 20];
        await ReadExactly(reader, 0, head);
        var tail = new byte[64 * 1024];
        await ReadExactly(reader, FileSize - tail.Length, tail);
        Assert.Equal(_plain.AsSpan(0, head.Length).ToArray(), head);
        Assert.Equal(_plain.AsSpan(FileSize - tail.Length).ToArray(), tail);
        await Task.Delay(1500);
        _out.WriteLine($"probe: {Interlocked.Read(ref _served) / 1048576.0:F1} MiB served in {_requests} requests");
        Assert.True(Interlocked.Read(ref _served) <= 8L << 20, "a probe should not trigger a large read-ahead");
    }

    [Fact]
    public async Task SeekUsesSeveralConnections()
    {
        using var reader = NewReader();
        var buf = new byte[4 << 20];
        long at = (FileSize / 2) + 12345;
        var sw = Stopwatch.StartNew();
        await ReadExactly(reader, at, buf);
        _out.WriteLine($"seek 4 MiB: {sw.ElapsedMilliseconds} ms ({BytesPerSecondPerConnection >> 20} MB/s per connection)");
        Assert.Equal(_plain.AsSpan((int)at, buf.Length).ToArray(), buf);

        // One connection alone needs a full second for 4 MiB.
        Assert.True(sw.ElapsedMilliseconds < 800, $"seek took {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task SequentialPlaybackRampsUp()
    {
        using var reader = NewReader();
        var chunk = new byte[256 * 1024];
        var sw = Stopwatch.StartNew();
        long pos = 8 << 20;
        const int Total = 40 << 20;
        for (long end = pos + Total; pos < end; pos += chunk.Length)
        {
            await ReadExactly(reader, pos, chunk);
            Assert.Equal(_plain[(int)pos], chunk[0]);
        }

        double mbps = Total / 1048576.0 / sw.Elapsed.TotalSeconds;
        _out.WriteLine($"sequential 40 MiB: {sw.ElapsedMilliseconds} ms = {mbps:F1} MB/s, {_requests} requests");
        Assert.True(mbps > 2.5 * (BytesPerSecondPerConnection >> 20), $"only {mbps:F1} MB/s with 4 connections");
    }

    [Fact]
    public async Task RandomReadsAreExact()
    {
        using var reader = NewReader();
        var rnd = new Random(7);
        var tasks = Enumerable.Range(0, 24).Select(async _ =>
        {
            int len;
            long at;
            lock (rnd)
            {
                len = rnd.Next(1, 3 << 20);
                at = rnd.NextInt64(0, FileSize - len);
            }

            var buf = new byte[len];
            await ReadExactly(reader, at, buf);
            Assert.True(_plain.AsSpan((int)at, len).SequenceEqual(buf));
        });
        await Task.WhenAll(tasks);
    }
}
