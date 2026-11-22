using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Mega.Configuration;
using Jellyfin.Plugin.Mega.Mega;
using Jellyfin.Plugin.Mega.Streaming;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace Jellyfin.Plugin.Mega.Tests;

/// <summary>
/// Read-only playback benchmark against a real MEGA account: a client that buffers like ExoPlayer
/// (Android TV, Moonfin) plays a large video at a fixed bitrate, and the stalls are counted.
/// Runs only when <c>MEGA_TOML</c> is set (see <see cref="LiveTests"/>) and <c>MEGA_BENCH</c> lists the
/// bitrates to try in MB/s (for example <c>2,5</c>). <c>MEGA_BENCH_SECONDS</c> sets the length of each run (60).
/// Every run downloads about bitrate × length + 50 s of video from the account's transfer quota.
/// </summary>
public class LiveBenchTests
{
    private readonly ITestOutputHelper _out;

    public LiveBenchTests(ITestOutputHelper output)
    {
        _out = output;
    }

    [Fact]
    public async Task LivePlaybackStalls()
    {
        var path = Environment.GetEnvironmentVariable("MEGA_TOML");
        var rates = Environment.GetEnvironmentVariable("MEGA_BENCH");
        if (string.IsNullOrEmpty(path) || !File.Exists(path) || string.IsNullOrEmpty(rates))
        {
            return;
        }

        int seconds = int.TryParse(Environment.GetEnvironmentVariable("MEGA_BENCH_SECONDS"), out var s) ? s : 60;
        var ct = new CancellationTokenSource(TimeSpan.FromMinutes(30)).Token;
        var session = File.ReadLines(path).Select(l => l.Trim()).First(l => l.StartsWith("session", StringComparison.Ordinal));
        session = session[(session.IndexOf('=', StringComparison.Ordinal) + 1)..].Trim().Trim('"', '\'');
        var cacheDir = Environment.GetEnvironmentVariable("MEGA_CACHE_DIR") ?? Path.Combine(Path.GetTempPath(), "mega-live-cache");
        var source = new MegaSource(new MegaAccountConfig { Id = "live", Kind = MegaAccountConfig.KindAccount, Session = session }, cacheDir);
        source.LoadCachedTree();
        var tree = await source.GetTreeAsync(ct);
        var video = tree.Descendants(tree.DefaultRoot!.Handle)
            .Where(n => n.IsFile && n.Name.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase) && n.Size > 3L << 30)
            .OrderBy(n => n.Size)
            .First();
        _out.WriteLine($"file: {video.Size / 1048576} MB");

        await RawTimings(source, video, ct);

        var log = new OutputLogger(_out);
        var list = rates.Split(',').Select(r => double.Parse(r, CultureInfo.InvariantCulture)).ToList();
        for (int i = 0; i < list.Count; i++)
        {
            long at = video.Size * (i + 1) / (list.Count + 2);
            using var reader = new MegaFileReader(source, video, null, log, 48, 4);
            await Play(reader, at, list[i], seconds, ct);
        }
    }

    /// <summary>What the storage server gives: answer time of small requests, speed of one long request.</summary>
    private async Task RawTimings(MegaSource source, MegaNode video, CancellationToken ct)
    {
        var url = await source.GetDownloadUrlAsync(video.Handle, false, false, ct);
        var buf = new byte[1 << 20];
        long at = video.Size / 20;
        var first = new System.Collections.Generic.List<long>();
        var whole = new System.Collections.Generic.List<long>();
        for (int i = 0; i < 6; i++, at += buf.Length)
        {
            var sw = Stopwatch.StartNew();
            using var resp = await MegaHttp.Storage.GetAsync($"{url}/{at}-{at + buf.Length - 1}", HttpCompletionOption.ResponseHeadersRead, ct);
            await using var body = await resp.Content.ReadAsStreamAsync(ct);
            first.Add(sw.ElapsedMilliseconds);
            await body.ReadExactlyAsync(buf, ct);
            whole.Add(sw.ElapsedMilliseconds);
            await Task.Delay(400, ct);
        }

        _out.WriteLine($"1 MiB requests (one every 0.4 s): headers after {string.Join("/", first)} ms, done after {string.Join("/", whole)} ms");

        const int Long = 32 << 20;
        var big = new byte[Long];
        var sw2 = Stopwatch.StartNew();
        using (var resp = await MegaHttp.Storage.GetAsync($"{url}/{at}-{at + Long - 1}", HttpCompletionOption.ResponseHeadersRead, ct))
        {
            await using var body = await resp.Content.ReadAsStreamAsync(ct);
            await body.ReadExactlyAsync(big, ct);
        }

        _out.WriteLine($"one 32 MiB request: {sw2.ElapsedMilliseconds} ms = {Long / 1048576.0 / sw2.Elapsed.TotalSeconds:F1} MB/s");
    }

    /// <summary>
    /// ExoPlayer-like client: starts after 2.5 s of media, resumes 5 s after a stall, keeps up to 50 s
    /// buffered, reads 256 KiB at a time.
    /// </summary>
    private async Task Play(MegaFileReader reader, long at, double megabytesPerSecond, int seconds, CancellationToken ct)
    {
        double rate = megabytesPerSecond * 1048576;
        var chunk = new byte[256 * 1024];
        var sw = Stopwatch.StartNew();
        double Now() => sw.Elapsed.TotalSeconds;
        long read = 0;
        double played = 0;
        double last = 0;
        bool playing = false;
        double startup = -1;
        double stallStart = 0;
        double stallTotal = 0;
        double maxWait = 0;
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
                await Task.Delay(100, ct);
                continue;
            }

            var t0 = Now();
            int n = await reader.ReadAsync(at + read, chunk, ct);
            maxWait = Math.Max(maxWait, Now() - t0);
            Assert.True(n > 0);
            read += n;
        }

        if (!playing && startup >= 0)
        {
            stallTotal += Now() - stallStart;
        }

        _out.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{megabytesPerSecond} MB/s for {seconds} s: start {startup:F1} s, {stalls} stalls ({stallTotal:F1} s), longest read {maxWait:F1} s, buffer at end {(read / rate) - played:F0} s, {reader.Requests} requests ({read / 1048576.0 / Math.Max(1, reader.Requests):F1} MiB each), {reader.Retries} retries"));
    }

    private sealed class OutputLogger(ITestOutputHelper output) : ILogger
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            try
            {
                output.WriteLine($"  [{_clock.Elapsed.TotalSeconds:F1} s] {logLevel}: {formatter(state, exception)} {exception?.GetType().Name} {exception?.Message}");
            }
            catch (InvalidOperationException)
            {
                // Test already finished.
            }
        }
    }
}
