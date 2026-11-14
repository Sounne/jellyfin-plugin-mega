using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Mega.Mega;
using Xunit;

namespace Jellyfin.Plugin.Mega.Tests;

/// <summary>Tree download parsing, keys arriving late, paths, subtrees.</summary>
public sealed class TreeTests
{
    private const string User = "UserHandle1";
    private readonly byte[] _master = RandomNumberGenerator.GetBytes(16);
    private readonly byte[] _shareKey = RandomNumberGenerator.GetBytes(16);
    private readonly byte[] _okShareKey = RandomNumberGenerator.GetBytes(16);

    private AccountKeyring Keyring => new(User, _master, null);

    private static string Ecb(byte[] key, byte[] data)
    {
        var c = (byte[])data.Clone();
        MegaCrypto.EcbEncrypt(key, c);
        return MegaCrypto.B64Encode(c);
    }

    private static string Attrs(byte[] key, string name)
    {
        var json = Encoding.UTF8.GetBytes("MEGA{\"n\":" + JsonSerializer.Serialize(name) + "}");
        var padded = new byte[(json.Length + 15) / 16 * 16];
        json.CopyTo(padded, 0);
        MegaCrypto.CbcEncrypt(key, padded);
        return MegaCrypto.B64Encode(padded);
    }

    /// <summary>Folder whose key is encrypted with <paramref name="with"/>, under the key owner <paramref name="owner"/>.</summary>
    private object Folder(string h, string? p, string name, string? owner = null, byte[]? with = null, string? su = null, string? sk = null)
    {
        var key = RandomNumberGenerator.GetBytes(16);
        return new { h, p = p ?? string.Empty, t = 1, a = Attrs(key, name), k = (owner ?? User) + ":" + Ecb(with ?? _master, key), su, sk };
    }

    private object File(string h, string p, string name, long s = 1000, string? owner = null, byte[]? with = null)
    {
        var key = RandomNumberGenerator.GetBytes(32);
        return new { h, p, t = 0, a = Attrs(FileKey.Unpack(key)!.AesKey, name), k = (owner ?? User) + ":" + Ecb(with ?? _master, key), s, ts = 1_700_000_000 };
    }

    private static object[] Roots => new object[]
    {
        new { h = "ROOT0001", p = string.Empty, t = 2 },
        new { h = "INBX0001", p = string.Empty, t = 3 },
        new { h = "RUBB0001", p = string.Empty, t = 4 },
    };

    private Task<MegaTree> Parse(object[] nodes, object[]? ok = null, Func<string, long, bool>? keep = null, int chunk = 0)
        => Parse(JsonSerializer.Serialize(new object[] { new { f = nodes, ok = ok ?? Array.Empty<object>(), sn = "SN000042" } }), keep, chunk);

    private Task<MegaTree> Parse(string json, Func<string, long, bool>? keep = null, int chunk = 0)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        Stream stream = chunk > 0 ? new TrickleStream(bytes, chunk) : new MemoryStream(bytes);
        return MegaTree.ParseAsync(stream, Keyring, new Dictionary<string, byte[]>(), keep ?? ((_, _) => true), CancellationToken.None);
    }

    private object[] Library() => Roots.Concat(new[]
    {
        Folder("FOLD0001", "ROOT0001", "Films"),
        Folder("FOLD0002", "ROOT0001", "films"),
        Folder("FOLD0003", "FOLD0001", "Sub"),
        File("FILE0001", "FOLD0001", "sub"),
        File("FILE0002", "FOLD0003", "Movie.mkv"),
        Folder("FOLD0004", "FOLD0003", "Deeper"),
        File("FILE0003", "FOLD0004", "Deep.mkv"),
        File("FILE0004", "RUBB0001", "Deleted.mkv"),
    }).ToArray();

    [Fact]
    public async Task SpecialFoldersAndSequence()
    {
        var tree = await Parse(Roots);
        Assert.Equal("ROOT0001", tree.CloudRoot);
        Assert.Equal("RUBB0001", tree.RubbishRoot);
        Assert.Equal("SN000042", tree.Sn);
        Assert.Equal(
            new[] { ("ROOT0001", MegaNodeType.Root, "Cloud Drive"), ("INBX0001", MegaNodeType.Inbox, "Vault"), ("RUBB0001", MegaNodeType.Rubbish, "Rubbish Bin") },
            tree.Nodes.Select(n => (n.Handle, n.Type, n.Name)));
        Assert.All(tree.Nodes, n => Assert.Null(n.Parent));
        Assert.Same(tree.Get("ROOT0001"), tree.DefaultRoot);
        Assert.Null(tree.Get(null));
    }

    [Fact]
    public async Task NodesThatCannotBeUsedAreLeftOut()
    {
        var otherKey = RandomNumberGenerator.GetBytes(16);
        var tree = await Parse(
            Roots.Concat(new[]
            {
                File("FILE0001", "ROOT0001", "Good.mkv"),
                File("FILE0002", "ROOT0001", "Negative.mkv", s: -1),
                File("FILE0003", "ROOT0001", "Foreign.mkv", owner: "Stranger1", with: otherKey),
                File("FILE0004", "ROOT0001", "notes.txt"),
                new { h = "FILE0005", p = "ROOT0001", t = 0, a = Attrs(otherKey, "x.mkv"), k = User + ":" + Ecb(_master, RandomNumberGenerator.GetBytes(32)), s = 10L, ts = 0 },
                new { h = "FOLD0001", p = "ROOT0001", t = 1, a = "AAAA", k = User + ":" + Ecb(_master, RandomNumberGenerator.GetBytes(32)), s = 0L, ts = 0 },
                new { h = string.Empty, p = "ROOT0001", t = 1, a = "AAAA", k = string.Empty, s = 0L, ts = 0 },
            }).ToArray(),
            keep: (name, _) => name != "notes.txt");

        var names = tree.GetChildren("ROOT0001").ToDictionary(n => n.Handle, n => n.Name);
        Assert.Equal("Good.mkv", names["FILE0001"]);
        Assert.Equal("[undecryptable]", names["FILE0005"]);
        Assert.Equal(2, names.Count);

        var good = tree.Get("FILE0001")!;
        Assert.True(good.IsFile);
        Assert.Equal(1000, good.Size);
        Assert.NotNull(good.Key);
        Assert.Null(good.FolderKey);
        Assert.Equal("ROOT0001", good.Parent);
    }

    [Fact]
    public async Task SharedFolderKeysCanComeAfterTheirContent()
    {
        var tree = await Parse(
            Roots.Concat(new[]
            {
                // Content listed before its share root (key in the root's sk) and before the ok section.
                Folder("SHIN0002", "SHAR0001", "Season 1", owner: "SHAR0001", with: _shareKey),
                File("SHIN0003", "SHIN0002", "E01.mkv", owner: "SHAR0001", with: _shareKey),
                Folder("SHAR0001", "Friend01", "Friend's show", owner: "SHAR0001", with: _shareKey, su: "Friend01", sk: Ecb(_master, _shareKey)),
                File("OKSH0002", "OKSH0001", "Other.mkv", owner: "OKSH0001", with: _okShareKey),
                Folder("OKSH0001", "Friend02", "Other share", owner: "OKSH0001", with: _okShareKey, su: "Friend02"),
                File("LOST0001", "LOST0000", "Lost.mkv", owner: "LOST0000", with: RandomNumberGenerator.GetBytes(16)),
            }).ToArray(),
            ok: new object[] { new { h = "OKSH0001", k = Ecb(_master, _okShareKey) }, new { h = "BAD00001", k = "AAAA" } });

        Assert.Equal(new[] { "SHAR0001", "OKSH0001" }, tree.InShares);
        Assert.Null(tree.Get("SHAR0001")!.Parent);
        Assert.Equal("Friend's show", tree.Get("SHAR0001")!.Name);
        Assert.Equal("E01.mkv", tree.Get("SHIN0003")!.Name);
        Assert.Equal("Other.mkv", Assert.Single(tree.GetChildren("OKSH0001")).Name);
        Assert.Null(tree.Get("LOST0001"));
        Assert.Equal(_shareKey, tree.ShareKeys["SHAR0001"]);
        Assert.Equal(_okShareKey, tree.ShareKeys["OKSH0001"]);
        Assert.False(tree.ShareKeys.ContainsKey("BAD00001"));

        Assert.Equal("in:Friend's show/Season 1", tree.GetPath(tree.Get("SHIN0002")!));
        Assert.Equal("SHIN0002", tree.Resolve("in:Friend's show/Season 1")!.Handle);
        Assert.Equal("SHIN0002", tree.Resolve("IN:friend's SHOW/season 1/")!.Handle);
        Assert.Equal("OKSH0001", tree.Resolve("in:Other share")!.Handle);
        Assert.Null(tree.Resolve("in:Nobody/Season 1"));
    }

    [Fact]
    public async Task KnownShareKeysAreUsedFromTheStart()
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new object[]
        {
            new { f = Roots.Append(File("SHIN0001", "SHAR0001", "E01.mkv", owner: "SHAR0001", with: _shareKey)).ToArray(), sn = "SN1" },
        }));
        var tree = await MegaTree.ParseAsync(
            new MemoryStream(bytes),
            Keyring,
            new Dictionary<string, byte[]> { ["SHAR0001"] = _shareKey },
            (_, _) => true,
            CancellationToken.None);
        Assert.Equal("E01.mkv", tree.Get("SHIN0001")!.Name);
    }

    [Fact]
    public async Task PathsPreferTheExactCase()
    {
        var tree = await Parse(Library());
        Assert.Equal("FOLD0001", tree.Resolve("/Films")!.Handle);
        Assert.Equal("FOLD0002", tree.Resolve("/films")!.Handle);
        Assert.Equal("FOLD0001", tree.Resolve("/FILMS")!.Handle);

        // Only folders are path segments, whatever the case.
        Assert.Equal("FOLD0003", tree.Resolve("/Films/sub")!.Handle);
        Assert.Equal("FOLD0004", tree.Resolve(@" \Films\Sub\Deeper\ ")!.Handle);
        Assert.Equal("ROOT0001", tree.Resolve(null)!.Handle);
        Assert.Equal("ROOT0001", tree.Resolve("//")!.Handle);
        Assert.Null(tree.Resolve("/Films/Sub/Movie.mkv"));
        Assert.Null(tree.Resolve("/Nope/Sub"));

        Assert.Equal("/", tree.GetPath(tree.Get("ROOT0001")!));
        Assert.Equal("/Films/Sub/Deeper", tree.GetPath(tree.Get("FOLD0004")!));
        Assert.Equal("/Films/Sub/Movie.mkv", tree.GetPath(tree.Get("FILE0002")!));
    }

    [Fact]
    public async Task FolderLinkRoot()
    {
        var tree = await Parse(Library());
        tree.LinkRoot = "FOLD0003";
        Assert.Equal("FOLD0003", tree.DefaultRoot!.Handle);
        Assert.Equal("FOLD0004", tree.Resolve("/Deeper")!.Handle);
        Assert.Equal("/Deeper", tree.GetPath(tree.Get("FOLD0004")!));
        Assert.Equal("/", tree.GetPath(tree.Get("FOLD0003")!));
    }

    [Fact]
    public async Task SubtreesAreWalkedAndRemovedWhole()
    {
        var tree = await Parse(Library());
        Assert.Equal(
            new[] { "FILE0002", "FILE0003", "FOLD0004" },
            tree.Descendants("FOLD0003").Select(n => n.Handle).OrderBy(h => h));
        Assert.Empty(tree.Descendants("FILE0002"));
        Assert.Empty(tree.Descendants("Missing1"));

        var copy = tree.Clone();
        long changes = copy.Changes;
        copy.Remove("FOLD0003");
        Assert.Equal(changes + 1, copy.Changes);
        Assert.Null(copy.Get("FOLD0004"));
        Assert.Null(copy.Get("FILE0003"));
        Assert.Equal(new[] { "FILE0001" }, copy.GetChildren("FOLD0001").Select(n => n.Handle));
        Assert.Equal(tree.Count - 4, copy.Count);
        copy.Remove("Missing1");
        Assert.Equal(changes + 1, copy.Changes);

        // The original is untouched.
        Assert.NotNull(tree.Get("FILE0003"));
        Assert.Equal(new[] { "FOLD0003", "FILE0001" }, tree.GetChildren("FOLD0001").Select(n => n.Handle));
        Assert.Equal(tree.Sn, copy.Sn);
        Assert.Equal(tree.CloudRoot, copy.CloudRoot);
    }

    [Fact]
    public async Task RemovingAShareForgetsIt()
    {
        var tree = await Parse(Roots.Append(Folder("SHAR0001", "Friend01", "Show", owner: "SHAR0001", with: _shareKey, su: "Friend01", sk: Ecb(_master, _shareKey))).ToArray());
        var copy = tree.Clone();
        copy.ShareKeys["NEW00001"] = new byte[16];
        copy.Remove("SHAR0001");
        Assert.Empty(copy.InShares);
        Assert.Equal(new[] { "SHAR0001" }, tree.InShares);
        Assert.False(tree.ShareKeys.ContainsKey("NEW00001"));
    }

    [Fact]
    public async Task MovingANodeDetachesIt()
    {
        var tree = await Parse(Library());
        var movie = tree.Get("FILE0002")!;
        tree.Add(movie.With(), false);
        Assert.Single(tree.GetChildren("FOLD0003"), n => n.Handle == "FILE0002");

        var moved = new MegaNode { Handle = movie.Handle, Parent = "FOLD0002", Type = movie.Type, Name = movie.Name, Size = movie.Size, Key = movie.Key };
        tree.Add(moved, false);
        Assert.DoesNotContain(tree.GetChildren("FOLD0003"), n => n.Handle == "FILE0002");
        Assert.Equal("FILE0002", Assert.Single(tree.GetChildren("FOLD0002")).Handle);

        // Replace only changes nodes already there.
        tree.Replace(new MegaNode { Handle = "Missing1", Name = "x" });
        Assert.Null(tree.Get("Missing1"));
    }

    [Fact]
    public async Task ThumbnailAndKeyPackets()
    {
        var tree = await Parse(Library());
        Assert.True(tree.ApplyPackets(JsonSerializer.SerializeToElement(new object[]
        {
            new { a = "fa", n = "FILE0002", fa = "924:1*AbCdEfGhIjK" },
            new { a = "fa", n = "Missing1", fa = "924:1*AbCdEfGhIjK" },
            new { a = "ua", ua = new[] { "firstname" } },
            new { a = "unknown" },
            new { x = 1 },
        }), Keyring, (_, _) => true));
        Assert.Equal("924:1*AbCdEfGhIjK", tree.Get("FILE0002")!.FileAttributes);

        // New keys for shares: the tree must be loaded again.
        Assert.False(tree.ApplyPackets(JsonSerializer.SerializeToElement(new object[] { new { a = "ua", ua = new[] { "^!keys" } } }), Keyring, (_, _) => true));
    }

    [Fact]
    public async Task SlowConnectionsGiveTheSameTree()
    {
        // A name bigger than the read buffer, and a stream returning a few bytes at a time.
        var nodes = Library().Append(File("FILE0099", "FOLD0001", new string('x', 300_000) + ".mkv")).ToArray();
        var json = JsonSerializer.Serialize(new object[] { new { skipped = new { deep = new[] { 1, 2, 3 } }, f = nodes, sn = "SN9" } });
        var whole = await Parse(json);
        var trickled = await Parse(json, chunk: 7);
        Assert.Equal(whole.Count, trickled.Count);
        Assert.Equal(whole.Nodes.Select(n => (n.Handle, n.Name)), trickled.Nodes.Select(n => (n.Handle, n.Name)));
        Assert.Equal(300_004, trickled.Get("FILE0099")!.Name.Length);
        Assert.Equal("SN9", trickled.Sn);
    }

    [Theory]
    [InlineData("[-9]", -9)]
    [InlineData("-15", -15)]
    [InlineData("[{\"e\":1},-3]", -3)]
    public async Task ErrorsInsteadOfATree(string json, int code)
        => Assert.Equal(code, (await Assert.ThrowsAsync<MegaException>(() => Parse(json))).Code);

    [Fact]
    public async Task TruncatedTreeIsAnError()
    {
        var json = JsonSerializer.Serialize(new object[] { new { f = Library(), sn = "SN1" } });
        var e = await Assert.ThrowsAsync<MegaException>(() => Parse(json[..(json.Length / 2)]));
        Assert.Contains("incomplete", e.Message, StringComparison.Ordinal);
    }

    /// <summary>Stream returning at most a few bytes per read.</summary>
    private sealed class TrickleStream : MemoryStream
    {
        private readonly int _chunk;

        public TrickleStream(byte[] data, int chunk)
            : base(data)
        {
            _chunk = chunk;
        }

        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(count, _chunk));

        public override int Read(Span<byte> buffer) => base.Read(buffer[..Math.Min(buffer.Length, _chunk)]);

        public override System.Threading.Tasks.ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => base.ReadAsync(buffer[..Math.Min(buffer.Length, _chunk)], cancellationToken);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => base.ReadAsync(buffer, offset, Math.Min(count, _chunk), cancellationToken);
    }
}
