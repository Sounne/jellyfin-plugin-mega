using System;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.Mega.Configuration;
using Jellyfin.Plugin.Mega.Mega;
using Jellyfin.Plugin.Mega.Streaming;
using Jellyfin.Plugin.Mega.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Mega.Tests;

/// <summary>Disk cache of the first and last blocks of files.</summary>
public sealed class BlockDiskCacheTests : IDisposable
{
    private readonly string _dir = TestData.TempDir();

    public void Dispose() => TestData.TryDelete(_dir);

    [Fact]
    public void BlocksRoundTrip()
    {
        var cache = new BlockDiskCache(_dir, NullLogger.Instance);
        var data = TestData.Bytes(1000);
        Assert.False(cache.Contains("src", "AbCdEfGh", 0));
        Assert.Null(cache.TryLoad("src", "AbCdEfGh", 0, 1000));

        cache.Save("src", "AbCdEfGh", 0, data);
        Assert.True(cache.Contains("src", "AbCdEfGh", 0));
        Assert.Equal(data, cache.TryLoad("src", "AbCdEfGh", 0, 1000));

        // A block of another size (file replaced on MEGA, truncated write) is not used.
        Assert.Null(cache.TryLoad("src", "AbCdEfGh", 0, 999));
        Assert.Null(cache.TryLoad("src", "AbCdEfGh", 1, 1000));
        Assert.Null(cache.TryLoad("other", "AbCdEfGh", 0, 1000));
        Assert.Empty(Directory.EnumerateFiles(_dir, "*.tmp*", SearchOption.AllDirectories));
    }

    [Fact]
    public void HandlesDifferingOnlyByCaseStayApart()
    {
        // MEGA handles are case-sensitive; Windows and macOS file names are not.
        var cache = new BlockDiskCache(_dir, NullLogger.Instance);
        cache.Save("src", "AbCdEfGh", 0, new byte[] { 1 });
        cache.Save("src", "abcdefgh", 0, new byte[] { 2 });
        Assert.Equal(new byte[] { 1 }, cache.TryLoad("src", "AbCdEfGh", 0, 1));
        Assert.Equal(new byte[] { 2 }, cache.TryLoad("src", "abcdefgh", 0, 1));
        Assert.NotEqual(BlockDiskCache.SafeName("AbCdEfGh"), BlockDiskCache.SafeName("abcdefgh"));
        Assert.Matches("^[0-9A-F]+$", BlockDiskCache.SafeName("not base64 !"));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(7, false)]
    [InlineData(8, true)]
    [InlineData(9, true)]
    public void OnlyTheEndsAreKept(long block, bool kept)
        => Assert.Equal(kept, new BlockDiskCache(_dir, NullLogger.Instance).IsCandidate(block, 9));

    [Fact]
    public void TrimDropsTheLeastRecentlyUsed()
    {
        var cache = new BlockDiskCache(_dir, NullLogger.Instance);
        var start = DateTime.UtcNow.AddHours(-1);
        for (int i = 0; i < 4; i++)
        {
            cache.Save("src", "AbCdEfGh", i, new byte[1000]);
            File.SetLastWriteTimeUtc(Directory.EnumerateFiles(Path.Combine(_dir, "src"), "*." + i).Single(), start.AddMinutes(i));
        }

        // Reading block 0 makes it the most recent.
        Assert.NotNull(cache.TryLoad("src", "AbCdEfGh", 0, 1000));
        File.WriteAllBytes(Path.Combine(_dir, "src", "leftover.tmp7"), new byte[10]);

        cache.Trim(2500);
        Assert.True(cache.Contains("src", "AbCdEfGh", 0));
        Assert.True(cache.Contains("src", "AbCdEfGh", 3));
        Assert.False(cache.Contains("src", "AbCdEfGh", 1));
        Assert.False(cache.Contains("src", "AbCdEfGh", 2));
        Assert.False(File.Exists(Path.Combine(_dir, "src", "leftover.tmp7")));
    }
}

/// <summary>One shared reader per file, closed when replaced or when the pool stops.</summary>
public sealed class ReaderPoolTests
{
    private static readonly MegaSource Source = new(new MegaAccountConfig { Id = "src", Session = "unused" });

    private static MegaNode Node(string handle, long size) => new() { Handle = handle, Name = "Movie.mkv", Size = size, Type = MegaNodeType.File, Key = FileKey.Unpack(new byte[32]) };

    [Fact]
    public void RequestsOnOneFileShareItsReader()
    {
        using var pool = new ReaderPool(NullLogger.Instance);
        using var a = pool.Acquire(Source, Node("HANDLE01", 100), 48, 4);
        using var b = pool.Acquire(Source, Node("HANDLE01", 100), 48, 4);
        using var c = pool.Acquire(Source, Node("HANDLE02", 100), 48, 4);
        Assert.Same(a.Reader, b.Reader);
        Assert.NotSame(a.Reader, c.Reader);
        Assert.Equal(2, pool.OpenReaders);
    }

    [Fact]
    public void ReleasedReadersStayOpenForTheNextRequest()
    {
        using var pool = new ReaderPool(NullLogger.Instance);
        MegaFileReader first;
        using (var lease = pool.Acquire(Source, Node("HANDLE01", 100), 48, 4))
        {
            first = lease.Reader;
        }

        using var again = pool.Acquire(Source, Node("HANDLE01", 100), 48, 4);
        Assert.Same(first, again.Reader);
        Assert.False(first.IsFailed);
    }

    [Fact]
    public void ChangedOrFailedFileGetsANewReader()
    {
        using var pool = new ReaderPool(NullLogger.Instance);
        var lease = pool.Acquire(Source, Node("HANDLE01", 100), 48, 4);
        var old = lease.Reader;

        // Replaced on MEGA (new size) while still being read: the old reader closes once released.
        using var replaced = pool.Acquire(Source, Node("HANDLE01", 200), 48, 4);
        Assert.NotSame(old, replaced.Reader);
        Assert.False(old.IsFailed);
        lease.Dispose();
        lease.Dispose();
        Assert.True(old.IsFailed);
        Assert.False(replaced.Reader.IsFailed);
        Assert.Equal(1, pool.OpenReaders);

        replaced.Reader.Dispose();
        using var fresh = pool.Acquire(Source, Node("HANDLE01", 200), 48, 4);
        Assert.NotSame(replaced.Reader, fresh.Reader);
    }

    [Fact]
    public void StoppingThePoolClosesEveryReader()
    {
        var pool = new ReaderPool(NullLogger.Instance);
        var lease = pool.Acquire(Source, Node("HANDLE01", 100), 48, 4);
        pool.Dispose();
        Assert.True(lease.Reader.IsFailed);
        Assert.Equal(0, pool.OpenReaders);
    }
}
