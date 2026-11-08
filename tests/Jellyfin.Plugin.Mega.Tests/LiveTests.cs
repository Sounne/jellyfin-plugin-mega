using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Mega.Configuration;
using Jellyfin.Plugin.Mega.Mega;
using Jellyfin.Plugin.Mega.Streaming;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Jellyfin.Plugin.Mega.Tests;

/// <summary>
/// Read-only checks against a real MEGA account. Runs only when <c>MEGA_TOML</c> points to a file holding
/// <c>session = "..."</c> (MEGAcmd session) or <c>folder_link = "..."</c>; passes trivially otherwise.
/// </summary>
public class LiveTests
{
    private readonly ITestOutputHelper _out;

    public LiveTests(ITestOutputHelper output)
    {
        _out = output;
    }

    [Fact]
    public async Task LiveSourceTreeStreamingAndPreviews()
    {
        var path = Environment.GetEnvironmentVariable("MEGA_TOML");
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return;
        }

        var ct = new CancellationTokenSource(TimeSpan.FromMinutes(10)).Token;
        var session = ReadValue(path, "session");
        var link = ReadValue(path, "folder_link");
        var config = session is not null
            ? new MegaAccountConfig { Id = "live", Kind = MegaAccountConfig.KindAccount, Session = session }
            : new MegaAccountConfig { Id = "live", Kind = MegaAccountConfig.KindFolderLink, FolderLink = link! };
        var cacheDir = Environment.GetEnvironmentVariable("MEGA_CACHE_DIR") ?? Path.Combine(Path.GetTempPath(), "mega-live-cache");
        var cacheFile = Path.Combine(cacheDir, "live.tree");
        var source = new MegaSource(config, cacheDir);

        var sw = Stopwatch.StartNew();
        bool cached = source.LoadCachedTree();
        _out.WriteLine($"cache: {(cached ? "loaded" : "none")} in {sw.ElapsedMilliseconds} ms");
        sw.Restart();
        var tree = await source.GetTreeAsync(ct);
        _out.WriteLine($"tree: {tree.Count} nodes kept in {sw.ElapsedMilliseconds} ms, {tree.InShares.Count} incoming shares, {GC.GetTotalMemory(true) / 1048576} MB managed heap");
        sw.Restart();
        var update = await source.UpdateAsync(ct);
        tree = source.Tree!;
        _out.WriteLine($"update since cache: {update} in {sw.ElapsedMilliseconds} ms, {tree.Count} nodes");
        if (!cached)
        {
            for (int i = 0; i < 60 && !File.Exists(cacheFile); i++)
            {
                await Task.Delay(500, ct);
            }

            _out.WriteLine($"cache saved: {File.Exists(cacheFile)} ({(File.Exists(cacheFile) ? new FileInfo(cacheFile).Length / 1048576 : 0)} MB)");
        }

        var root = tree.DefaultRoot!;
        var media = tree.Descendants(root.Handle).Where(n => n.IsFile && MediaFileTypes.IsMedia(n.Name)).ToList();
        var videos = media.Where(n => MediaFileTypes.IsVideo(n.Name)).ToList();
        _out.WriteLine($"media files: {media.Count}, videos: {videos.Count}, with preview: {videos.Count(v => v.TryGetAttribute(1, out _, out _))}");
        Assert.NotEmpty(videos);

        var video = videos
            .Where(v => v.Size > 200L * 1024 * 1024 && (v.Name.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase) || v.Name.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)))
            .OrderBy(v => v.Size)
            .FirstOrDefault() ?? videos.OrderByDescending(v => v.Size).First();
        _out.WriteLine($"test file: {Path.GetExtension(video.Name)} {video.Size / 1048576} MB");

        using (var reader = new MegaFileReader(source, video, null, NullLogger.Instance, 48, 4))
        {
            // Time to first byte, container magic.
            sw.Restart();
            var head = new byte[65536];
            await ReadExactly(reader, 0, head, ct);
            _out.WriteLine($"first 64 KiB: {sw.ElapsedMilliseconds} ms");
            bool mkv = head[0] == 0x1A && head[1] == 0x45 && head[2] == 0xDF && head[3] == 0xA3;
            bool mp4 = head[4] == (byte)'f' && head[5] == (byte)'t' && head[6] == (byte)'y' && head[7] == (byte)'p';
            if (video.Name.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase))
            {
                Assert.True(mkv, "decrypted .mkv does not start with the EBML magic");
            }
            else if (video.Name.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
            {
                Assert.True(mp4, "decrypted .mp4 has no ftyp box");
            }

            // Seek far away, compare with a plain ranged download decrypted independently.
            long at = (video.Size * 6 / 10) + 12345;
            var seek = new byte[1 << 20];
            sw.Restart();
            await ReadExactly(reader, at, seek, ct);
            _out.WriteLine($"seek to 60 %, 1 MiB: {sw.ElapsedMilliseconds} ms");
            var url = await source.GetDownloadUrlAsync(video.Handle, false, false, ct);
            using (var http = new HttpClient())
            {
                var raw = await http.GetByteArrayAsync($"{url}/{at}-{at + seek.Length - 1}", ct);
                MegaCrypto.CtrTransform(video.Key!.AesKey, video.Key.Nonce, at, raw);
                Assert.Equal(raw, seek);
            }

            // Sequential throughput after a seek (playback).
            long pos = video.Size * 3 / 10;
            const int Total = 48 << 20;
            var chunk = new byte[256 * 1024];
            sw.Restart();
            for (long done = 0; done < Total && pos + chunk.Length <= video.Size; done += chunk.Length, pos += chunk.Length)
            {
                await ReadExactly(reader, pos, chunk, ct);
            }

            _out.WriteLine($"sequential 48 MiB: {sw.ElapsedMilliseconds} ms = {Total / 1048576.0 / sw.Elapsed.TotalSeconds:F1} MB/s");
        }

        // Previews.
        var withPreview = videos.Where(v => v.TryGetAttribute(1, out _, out _)).Take(24).ToList();
        if (withPreview.Count > 0)
        {
            sw.Restart();
            var previews = await source.Attributes.FetchAsync(withPreview, 1, ct);
            _out.WriteLine($"previews: {previews.Count}/{withPreview.Count} in {sw.ElapsedMilliseconds} ms, avg {(previews.Count == 0 ? 0 : previews.Values.Sum(p => p.Length) / previews.Count / 1024)} KiB");
            Assert.NotEmpty(previews);
            Assert.All(previews.Values, p => Assert.True((p[0] == 0xFF && p[1] == 0xD8) || (p[0] == (byte)'R' && p[8] == (byte)'W') || p[0] == 0x89, "preview is not an image"));
        }

        // Change detection.
        sw.Restart();
        var changed = await source.UpdateAsync(ct);
        _out.WriteLine($"change poll: {changed} in {sw.ElapsedMilliseconds} ms");
    }

    private static async Task ReadExactly(MegaFileReader reader, long offset, byte[] buffer, CancellationToken ct)
    {
        int done = 0;
        while (done < buffer.Length)
        {
            int n = await reader.ReadAsync(offset + done, buffer.AsMemory(done), ct);
            Assert.True(n > 0, "unexpected end of stream");
            done += n;
        }
    }

    private static string? ReadValue(string path, string key)
    {
        foreach (var line in File.ReadLines(path))
        {
            var t = line.Trim();
            if (t.StartsWith('#'))
            {
                continue;
            }

            int eq = t.IndexOf('=', StringComparison.Ordinal);
            if (eq > 0 && t[..eq].Trim() == key)
            {
                return t[(eq + 1)..].Trim().Trim('"', '\'');
            }
        }

        return null;
    }
}
