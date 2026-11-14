using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Mega.Configuration;
using Jellyfin.Plugin.Mega.Library;
using Jellyfin.Plugin.Mega.Mega;
using Jellyfin.Plugin.Mega.Streaming;
using Jellyfin.Plugin.Mega.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Mega.Tests;

/// <summary>A MEGA source (public folder link or account session) against a local MEGA: tree, changes, downloads, previews.</summary>
[Collection(FakeMegaCollection.Name)]
public sealed class SourceTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public async Task FolderLinkTreeIsDecrypted()
    {
        await using var mega = await FakeMega.StartAsync();
        using var api = mega.AsDefaultApi();
        var films = mega.AddFolder(null, "Films");
        var movie = mega.AddFile(films, "Movie (2020).mkv", TestData.Bytes(5000), mtime: 1_700_000_000);
        mega.AddFile(films, "notes.docx", TestData.Bytes(10));

        var source = mega.NewSource();
        Assert.True(source.IsFolderLink);
        var tree = await source.GetTreeAsync(Ct);

        Assert.Equal(FakeMega.FolderHandle, tree.LinkRoot);
        Assert.Equal("Shared media", tree.DefaultRoot!.Name);
        Assert.Equal(films, tree.Resolve("/Films")!.Handle);
        var node = Assert.Single(tree.GetChildren(films));
        Assert.Equal(movie, node.Handle);
        Assert.Equal("Movie (2020).mkv", node.Name);
        Assert.Equal(5000, node.Size);
        Assert.Equal(new DateTime(2023, 11, 14, 22, 13, 20, DateTimeKind.Utc), node.Modified);
        Assert.Equal(mega.KeyOf(movie).AesKey, node.Key!.AesKey);

        // Folder link requests name the folder; the whole tree comes in one request.
        var (query, command) = Assert.Single(mega.Commands, c => c.Command.GetProperty("a").GetString() == "f");
        Assert.Contains("n=" + FakeMega.FolderHandle, query, StringComparison.Ordinal);
        Assert.Equal(1, command.GetProperty("ca").GetInt32());
        Assert.Same(tree, await source.GetTreeAsync(Ct));
        Assert.Equal(1, mega.Count("f"));
    }

    [Fact]
    public async Task SubfolderLinkStartsThere()
    {
        await using var mega = await FakeMega.StartAsync();
        using var api = mega.AsDefaultApi();
        var films = mega.AddFolder(null, "Films");
        var source = new MegaSource(new MegaAccountConfig { Id = "x", Kind = MegaAccountConfig.KindFolderLink, FolderLink = mega.Link + "/folder/" + films });
        var tree = await source.GetTreeAsync(Ct);
        Assert.Equal(films, tree.LinkRoot);
        Assert.Equal("Films", tree.Resolve("/")!.Name);
    }

    [Fact]
    public async Task InvalidLinkIsRefusedWithoutAnyRequest()
    {
        await using var mega = await FakeMega.StartAsync();
        using var api = mega.AsDefaultApi();
        var source = new MegaSource(new MegaAccountConfig { Id = "x", Kind = MegaAccountConfig.KindFolderLink, FolderLink = "https://mega.nz/file/AbCd_-12#nope" });
        var e = await Assert.ThrowsAsync<MegaException>(() => source.GetTreeAsync(Ct));
        Assert.Contains("invalid MEGA folder link", e.Message, StringComparison.Ordinal);
        Assert.Empty(mega.ApiQueries);
    }

    [Fact]
    public async Task WrongLinkKeyDecryptsNothing()
    {
        await using var mega = await FakeMega.StartAsync();
        using var api = mega.AsDefaultApi();
        mega.AddFile(null, "Movie.mkv", TestData.Bytes(100));
        var wrong = $"https://mega.nz/folder/{FakeMega.FolderHandle}#{MegaCrypto.B64Encode(new byte[16])}";
        var tree = await new MegaSource(new MegaAccountConfig { Id = "x", Kind = MegaAccountConfig.KindFolderLink, FolderLink = wrong }).GetTreeAsync(Ct);
        Assert.Equal("[undecryptable]", tree.DefaultRoot!.Name);
        Assert.DoesNotContain(tree.Nodes, n => n.IsFile);
    }

    [Fact]
    public async Task AccountSessionIsResumed()
    {
        await using var mega = await FakeMega.StartAsync(account: true);
        using var api = mega.AsDefaultApi();
        var films = mega.AddFolder(null, "Films");
        var movie = mega.AddFile(films, "Movie.mkv", TestData.Bytes(2000));

        var source = mega.NewSource();
        var tree = await source.GetTreeAsync(Ct);
        Assert.Equal(FakeMega.User, source.UserHandle);
        Assert.Equal(FakeMega.RootHandle, tree.CloudRoot);
        Assert.Equal(FakeMega.RubbishHandle, tree.RubbishRoot);
        Assert.Equal(movie, tree.GetChildren(tree.Resolve("/films")!.Handle).Single().Handle);

        // Every request after the login carries the session.
        Assert.All(mega.Commands, c => Assert.Contains("sid=" + mega.Sid, c.Query, StringComparison.Ordinal));
        Assert.Equal(new[] { "us", "uga", "f" }, mega.Commands.Select(c => c.Command.GetProperty("a").GetString()));
    }

    [Fact]
    public async Task ClosedSessionIsReported()
    {
        await using var mega = await FakeMega.StartAsync(account: true);
        using var api = mega.AsDefaultApi();
        await using var other = await FakeMega.StartAsync(account: true);
        var source = new MegaSource(new MegaAccountConfig { Id = "x", Kind = MegaAccountConfig.KindAccount, Session = other.Session });
        var e = await Assert.ThrowsAsync<MegaException>(() => source.GetTreeAsync(Ct));
        Assert.True(e.IsBadSession);

        var empty = new MegaSource(new MegaAccountConfig { Id = "y", Kind = MegaAccountConfig.KindAccount, Session = " " });
        e = await Assert.ThrowsAsync<MegaException>(() => empty.GetTreeAsync(Ct));
        Assert.Contains("log in again", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CachedTreeMakesRestartsInstant()
    {
        await using var mega = await FakeMega.StartAsync();
        using var api = mega.AsDefaultApi();
        var films = mega.AddFolder(null, "Films");
        mega.AddFile(films, "Movie.mkv", TestData.Bytes(100), preview: true);
        var dir = TestData.TempDir();
        try
        {
            var first = mega.NewSource(dir);
            Assert.False(first.LoadCachedTree());
            var tree = await first.GetTreeAsync(Ct);
            var file = Path.Combine(dir, "link1.tree");
            await Wait.Until(() => File.Exists(file), "the tree cache");

            // Restart: the tree comes from the disk, MEGA is not asked.
            var second = mega.NewSource(dir);
            Assert.True(second.LoadCachedTree());
            var cached = await second.GetTreeAsync(Ct);
            Assert.Equal(1, mega.Count("f"));
            Assert.Equal(tree.Count, cached.Count);
            Assert.Equal(tree.Sn, cached.Sn);
            Assert.Equal(tree.LinkRoot, cached.LinkRoot);
            Assert.Equal(tree.GetPath(tree.Resolve("/Films")!), cached.GetPath(cached.Resolve("/Films")!));
            Assert.NotNull(second.TreeLoadedAt);

            // Another link (another key) never reads this cache.
            var otherKey = $"https://mega.nz/folder/{FakeMega.FolderHandle}#{MegaCrypto.B64Encode(new byte[16])}";
            var other = new MegaSource(new MegaAccountConfig { Id = "link1", Kind = MegaAccountConfig.KindFolderLink, FolderLink = otherKey }, dir);
            Assert.False(other.LoadCachedTree());

            second.DeleteCache();
            Assert.False(File.Exists(file));
        }
        finally
        {
            TestData.TryDelete(dir);
        }
    }

    [Fact]
    public async Task ChangesAreAppliedWithoutReloading()
    {
        await using var mega = await FakeMega.StartAsync();
        using var api = mega.AsDefaultApi();
        var films = mega.AddFolder(null, "Films");
        var old = mega.AddFolder(films, "Old");
        var a = mega.AddFile(films, "A.mkv", TestData.Bytes(100));
        mega.AddFile(old, "B.mkv", TestData.Bytes(100));
        var source = mega.NewSource();
        var before = await source.GetTreeAsync(Ct);

        Assert.Equal(MegaSource.TreeUpdate.None, await source.UpdateAsync(Ct));

        var c = mega.AddFile(films, "C.mp4", TestData.Bytes(100), live: true);
        mega.Rename(a, "A (2001).mkv", live: true);
        mega.Delete(old, live: true);
        Assert.Equal(MegaSource.TreeUpdate.Changed, await source.UpdateAsync(Ct));

        var tree = source.Tree!;
        Assert.NotSame(before, tree);
        Assert.Equal(new[] { "A (2001).mkv", "C.mp4" }, tree.GetChildren(films).Select(n => n.Name).OrderBy(n => n, StringComparer.Ordinal));
        Assert.Null(tree.Resolve("/Films/Old"));
        Assert.Equal(c, tree.GetChildren(films).Single(n => n.Name == "C.mp4").Handle);
        Assert.Equal(mega.Sn, tree.Sn);
        Assert.Equal(1, mega.Count("f"));

        // The tree in use before the update is left as it was.
        Assert.NotNull(before.Resolve("/Films/Old"));
        Assert.Equal(MegaSource.TreeUpdate.None, await source.UpdateAsync(Ct));
    }

    [Fact]
    public async Task ShareChangesAndUnknownSequenceReload()
    {
        await using var mega = await FakeMega.StartAsync();
        using var api = mega.AsDefaultApi();
        var films = mega.AddFolder(null, "Films");
        var source = mega.NewSource();
        await source.GetTreeAsync(Ct);

        mega.Publish(new { a = "s2", n = films });
        Assert.Equal(MegaSource.TreeUpdate.Reloaded, await source.UpdateAsync(Ct));
        Assert.Equal(2, mega.Count("f"));

        mega.PollError = -9;
        Assert.Equal(MegaSource.TreeUpdate.Reloaded, await source.UpdateAsync(Ct));
        Assert.Equal(3, mega.Count("f"));

        // No tree yet: an update loads it.
        var fresh = mega.NewSource();
        mega.PollError = null;
        Assert.Equal(MegaSource.TreeUpdate.Reloaded, await fresh.UpdateAsync(Ct));
        Assert.NotNull(fresh.Tree);
    }

    [Fact]
    public async Task DownloadUrlsAreCached()
    {
        await using var mega = await FakeMega.StartAsync();
        using var api = mega.AsDefaultApi();
        var movie = mega.AddFile(null, "Movie.mkv", TestData.Bytes(100));
        var source = mega.NewSource();

        var url = await source.GetDownloadUrlAsync(movie, false, false, Ct);
        Assert.Equal(mega.Url + "dl/" + movie, url);
        Assert.Equal(url, await source.GetDownloadUrlAsync(movie, false, false, Ct));
        Assert.Equal(1, mega.Count("g"));

        await source.GetDownloadUrlAsync(movie, true, false, Ct);
        source.ForgetDownloadUrl(movie);
        await source.GetDownloadUrlAsync(movie, false, false, Ct);
        await source.GetDownloadUrlAsync(movie, false, true, Ct);
        var g = mega.Commands.Where(c => c.Command.GetProperty("a").GetString() == "g").Select(c => c.Command).ToList();
        Assert.Equal(4, g.Count);
        Assert.Equal(new[] { 2, 2, 2, 0 }, g.Select(c => c.GetProperty("ssl").GetInt32()));

        var e = await Assert.ThrowsAsync<MegaException>(() => source.GetDownloadUrlAsync("Missing1", false, false, Ct));
        Assert.Equal(-9, e.Code);
    }

    [Fact]
    public async Task RangeReadsAreDecryptedAndExact()
    {
        await using var mega = await FakeMega.StartAsync();
        using var api = mega.AsDefaultApi();
        var content = TestData.Bytes(300_000);
        var h = mega.AddFile(null, "Movie.mkv", content);
        var source = mega.NewSource();
        var node = (await source.GetTreeAsync(Ct)).Get(h)!;
        var reader = new MegaRangeReader(source, node);

        foreach (var (at, count) in new[] { (0L, 16), (12_345L, 1000), (299_990L, 10), (65_536L, 70_000) })
        {
            Assert.Equal(content.AsSpan((int)at, count).ToArray(), await reader.ReadAsync(at, count, Ct));
        }

        Assert.Equal(content[^5..], await reader.ReadAsync(content.Length - 5, 100, Ct));
        Assert.Empty(await reader.ReadAsync(content.Length, 10, Ct));

        // Nothing read beyond what was asked.
        Assert.Equal(16 + 1000 + 10 + 70_000 + 5, mega.StorageBytes);

        // Buffered: one read of the minimum size serves the reads inside it.
        var buffered = new BufferedRangeReader(reader, 4096);
        long requests = mega.StorageRequests;
        Assert.Equal(content.AsSpan(100, 10).ToArray(), await buffered.ReadAsync(100, 10, Ct));
        Assert.Equal(content.AsSpan(2000, 100).ToArray(), await buffered.ReadAsync(2000, 100, Ct));
        Assert.Equal(requests + 1, mega.StorageRequests);
    }

    [Fact]
    public async Task ExpiredDownloadUrlIsRenewed()
    {
        await using var mega = await FakeMega.StartAsync();
        using var api = mega.AsDefaultApi();
        var content = TestData.Bytes(10_000);
        var h = mega.AddFile(null, "Movie.mkv", content);
        var source = mega.NewSource();
        var node = (await source.GetTreeAsync(Ct)).Get(h)!;

        mega.StorageFaults.Enqueue(403);
        mega.StorageFaults.Enqueue(410);
        Assert.Equal(content.AsSpan(10, 100).ToArray(), await new MegaRangeReader(source, node).ReadAsync(10, 100, Ct));
        Assert.Equal(3, mega.Count("g"));

        // Still refused after two fresh URLs: an error.
        for (int i = 0; i < 3; i++)
        {
            mega.StorageFaults.Enqueue(404);
        }

        var e = await Assert.ThrowsAsync<MegaException>(() => new MegaRangeReader(source, node).ReadAsync(0, 10, Ct));
        Assert.Equal(404, e.HttpStatus);
    }

    [Fact]
    public async Task ExhaustedQuotaIsReported()
    {
        await using var mega = await FakeMega.StartAsync();
        using var api = mega.AsDefaultApi();
        var h = mega.AddFile(null, "Movie.mkv", TestData.Bytes(10_000));
        var source = mega.NewSource();
        var node = (await source.GetTreeAsync(Ct)).Get(h)!;

        mega.StorageFaults.Enqueue(509);
        var e = await Assert.ThrowsAsync<MegaException>(() => new MegaRangeReader(source, node).ReadAsync(0, 10, Ct));
        Assert.True(e.IsQuotaExceeded);
        Assert.Equal(1, mega.StorageRequests);
    }

    [Fact]
    public async Task SmallFilesAreDownloadedWhole()
    {
        await using var mega = await FakeMega.StartAsync();
        using var api = mega.AsDefaultApi();
        var text = System.Text.Encoding.UTF8.GetBytes("1\n00:00:01,000 --> 00:00:02,000\nÉté\n");
        var srt = mega.AddFile(null, "Movie.fr.srt", text);
        var empty = mega.AddFile(null, "Empty.srt", Array.Empty<byte>());
        var source = mega.NewSource();
        var tree = await source.GetTreeAsync(Ct);

        Assert.Equal(text, await LibrarySynchronizer.DownloadSmallFileAsync(source, tree.Get(srt)!, Ct));
        Assert.Empty(await LibrarySynchronizer.DownloadSmallFileAsync(source, tree.Get(empty)!, Ct));
        Assert.Equal(1, mega.StorageRequests);

        mega.StorageFaults.Enqueue(403);
        Assert.Equal(text, await LibrarySynchronizer.DownloadSmallFileAsync(source, tree.Get(srt)!, Ct));
    }

    [Fact]
    public async Task FileReaderKeepsTheStartAndEndOnDisk()
    {
        await using var mega = await FakeMega.StartAsync();
        using var api = mega.AsDefaultApi();

        // Four blocks: the first two and the last two are kept on disk, so all of it.
        var content = TestData.Bytes((4 * MegaFileReader.BlockSize) - 100);
        var h = mega.AddFile(null, "Movie.mkv", content);
        var source = mega.NewSource();
        var node = (await source.GetTreeAsync(Ct)).Get(h)!;
        var dir = TestData.TempDir();
        try
        {
            var disk = new BlockDiskCache(dir, NullLogger.Instance);
            using (var reader = new MegaFileReader(source, node, disk, NullLogger.Instance, 48, 4))
            {
                Assert.Equal(content, await ReadAll(reader, content.Length));
            }

            for (long idx = 0; idx < 4; idx++)
            {
                Assert.True(disk.Contains(source.Id, h, idx), $"block {idx} not on disk");
            }

            // Encrypted on disk, exactly as MEGA stores it.
            var block0 = disk.TryLoad(source.Id, h, 0, MegaFileReader.BlockSize)!;
            Assert.NotEqual(content.AsSpan(0, 64).ToArray(), block0.AsSpan(0, 64).ToArray());

            int requests = mega.StorageRequests;
            using (var again = new MegaFileReader(source, node, disk, NullLogger.Instance, 48, 4))
            {
                var head = new byte[65536];
                Assert.Equal(head.Length, await again.ReadAsync(0, head, Ct));
                Assert.Equal(content.AsSpan(0, head.Length).ToArray(), head);
                var tail = new byte[100];
                Assert.Equal(tail.Length, await again.ReadAsync(content.Length - 100, tail, Ct));
                Assert.Equal(content[^100..], tail);
            }

            Assert.Equal(requests, mega.StorageRequests);
        }
        finally
        {
            TestData.TryDelete(dir);
        }
    }

    [Fact]
    public async Task FileReaderStopsOnExhaustedQuota()
    {
        await using var mega = await FakeMega.StartAsync();
        using var api = mega.AsDefaultApi();
        var h = mega.AddFile(null, "Movie.mkv", TestData.Bytes(3 * MegaFileReader.BlockSize));
        var source = mega.NewSource();
        var node = (await source.GetTreeAsync(Ct)).Get(h)!;
        for (int i = 0; i < 20; i++)
        {
            mega.StorageFaults.Enqueue(509);
        }

        using var reader = new MegaFileReader(source, node, null, NullLogger.Instance, 48, 4);
        var e = await Assert.ThrowsAsync<MegaException>(async () => await reader.ReadAsync(0, new byte[100], Ct));
        Assert.True(e.IsQuotaExceeded);
        Assert.True(reader.IsFailed);
    }

    [Fact]
    public async Task PreviewsComeInOneBatch()
    {
        await using var mega = await FakeMega.StartAsync();
        using var api = mega.AsDefaultApi();
        var handles = Enumerable.Range(0, 3).Select(i => mega.AddFile(null, $"Movie {i}.mkv", TestData.Bytes(100, i), preview: true)).ToList();
        var none = mega.AddFile(null, "No preview.mkv", TestData.Bytes(100));
        var source = mega.NewSource();
        var tree = await source.GetTreeAsync(Ct);
        var nodes = handles.Append(none).Select(h => tree.Get(h)!).ToList();

        var previews = await source.Attributes.FetchAsync(nodes, 1, Ct);
        Assert.Equal(handles.OrderBy(h => h), previews.Keys.OrderBy(h => h));
        Assert.All(handles, h => Assert.Equal(mega.Preview(h), previews[h]));
        Assert.Equal(1, mega.AttributeRequests);
        Assert.Equal(1, mega.Count("ufa"));

        // Thumbnails (type 0) were never uploaded.
        Assert.Empty(await source.Attributes.FetchAsync(nodes, 0, Ct));
        await source.Attributes.FetchAsync(nodes, 1, Ct);
        Assert.Equal(1, mega.Count("ufa"));
    }

    private static async Task<byte[]> ReadAll(MegaFileReader reader, int length)
    {
        var data = new byte[length];
        int done = 0;
        while (done < length)
        {
            int n = await reader.ReadAsync(done, data.AsMemory(done, Math.Min(256 * 1024, length - done)), Ct);
            Assert.True(n > 0);
            done += n;
        }

        return data;
    }
}
