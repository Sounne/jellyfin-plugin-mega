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

public class TreeUpdateTests
{
    private const string User = "UserHandle1";
    private readonly byte[] _master = RandomNumberGenerator.GetBytes(16);
    private readonly byte[] _folderKey = RandomNumberGenerator.GetBytes(16);
    private readonly byte[] _fileKey = RandomNumberGenerator.GetBytes(32);

    private AccountKeyring Keyring => new(User, _master, null);

    private string EncKey(byte[] k)
    {
        var c = (byte[])k.Clone();
        MegaCrypto.EcbEncrypt(_master, c);
        return User + ":" + MegaCrypto.B64Encode(c);
    }

    private static string Attrs(byte[] key, string name)
    {
        var json = Encoding.UTF8.GetBytes("MEGA{\"n\":" + JsonSerializer.Serialize(name) + "}");
        var padded = new byte[(json.Length + 15) / 16 * 16];
        json.CopyTo(padded, 0);
        MegaCrypto.CbcEncrypt(key, padded);
        return MegaCrypto.B64Encode(padded);
    }

    private object Folder(string h, string p, string name) => new { h, p, t = 1, a = Attrs(_folderKey, name), k = EncKey(_folderKey) };

    private object File(string h, string p, string name, long s = 1000) => new { h, p, t = 0, a = Attrs(FileKey.Unpack(_fileKey)!.AesKey, name), k = EncKey(_fileKey), s, fa = "1:1*AAAAAAAAAAA" };

    private async Task<MegaTree> BaseTree()
    {
        var nodes = new object[]
        {
            new { h = "ROOT0001", p = string.Empty, t = 2 },
            Folder("FOLD0001", "ROOT0001", "Films"),
            Folder("FOLD0002", "FOLD0001", "Old"),
            File("FILE0001", "FOLD0001", "A.mkv"),
            File("FILE0002", "FOLD0002", "B.mkv"),
        };
        var json = JsonSerializer.Serialize(new object[] { new { f = nodes, sn = "SN1" } });
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(json));
        return await MegaTree.ParseAsync(ms, Keyring, new Dictionary<string, byte[]>(), MediaFileTypes.Keep, CancellationToken.None);
    }

    private static JsonElement Packets(params object[] packets) => JsonSerializer.SerializeToElement(packets);

    [Fact]
    public async Task PacketsAddMoveRenameDelete()
    {
        var original = await BaseTree();
        var tree = original.Clone();
        Assert.True(tree.ApplyPackets(
            Packets(
                new { a = "t", t = new { f = new[] { File("FILE0003", "FOLD0001", "C.mp4"), File("FILE0004", "FOLD0001", "notes.docx") } } },
                new { a = "u", n = "FILE0001", at = Attrs(FileKey.Unpack(_fileKey)!.AesKey, "A renamed.mkv") },
                new { a = "u", n = "FOLD0001", at = Attrs(_folderKey, "Movies") },
                new { a = "d", n = "FOLD0002" },
                new { a = "fa", n = "FILE0003", fa = "2:0*BBBBBBBBBBB/2:1*CCCCCCCCCCC" },
                new { a = "c", u = new[] { new { u = "x" } } }),
            Keyring,
            MediaFileTypes.Keep));

        Assert.Equal(new[] { "A renamed.mkv", "C.mp4" }, tree.GetChildren("FOLD0001").Select(n => n.Name).OrderBy(n => n));
        Assert.Equal("Movies", tree.Get("FOLD0001")!.Name);
        Assert.Null(tree.Get("FOLD0002"));
        Assert.Null(tree.Get("FILE0002"));
        Assert.True(tree.Get("FILE0003")!.TryGetAttribute(1, out var bundle, out _));
        Assert.Equal(2u, bundle);
        Assert.NotNull(tree.Resolve("/Movies"));

        // The original snapshot is untouched (readers keep using it during the update).
        Assert.Equal("A.mkv", original.Get("FILE0001")!.Name);
        Assert.NotNull(original.Get("FILE0002"));
        Assert.Equal(2, original.GetChildren("FOLD0001").Count);
    }

    [Fact]
    public async Task MoveDetachesFromOldParent()
    {
        var tree = await BaseTree();
        Assert.True(tree.ApplyPackets(
            Packets(new { a = "d", n = "FILE0002" }, new { a = "t", t = new { f = new[] { File("FILE0002", "FOLD0001", "B.mkv") } } }),
            Keyring,
            MediaFileTypes.Keep));
        Assert.Empty(tree.GetChildren("FOLD0002"));
        Assert.Contains(tree.GetChildren("FOLD0001"), n => n.Handle == "FILE0002");

        // Same move without the delete packet.
        Assert.True(tree.ApplyPackets(Packets(new { a = "t", t = new { f = new[] { File("FILE0002", "FOLD0002", "B.mkv") } } }), Keyring, MediaFileTypes.Keep));
        Assert.Single(tree.GetChildren("FOLD0002"));
        Assert.DoesNotContain(tree.GetChildren("FOLD0001"), n => n.Handle == "FILE0002");
    }

    [Fact]
    public async Task RenameToNonMediaRemovesFile()
    {
        var tree = await BaseTree();
        Assert.True(tree.ApplyPackets(Packets(new { a = "u", n = "FILE0001", at = Attrs(FileKey.Unpack(_fileKey)!.AesKey, "A.txt") }), Keyring, MediaFileTypes.Keep));
        Assert.Null(tree.Get("FILE0001"));
    }

    [Fact]
    public async Task ShareChangesNeedFullReload()
    {
        var tree = await BaseTree();
        Assert.False(tree.ApplyPackets(Packets(new { a = "s2", n = "FOLD0001" }), Keyring, MediaFileTypes.Keep));
        Assert.False(tree.ApplyPackets(Packets(new { a = "ua", ua = new[] { "^!keys" } }), Keyring, MediaFileTypes.Keep));
        Assert.True(tree.ApplyPackets(Packets(new { a = "ua", ua = new[] { "^!lstint" } }), Keyring, MediaFileTypes.Keep));
    }

    [Fact]
    public async Task CacheRoundTrip()
    {
        var tree = await BaseTree();
        tree.ShareKeys["SHARE001"] = RandomNumberGenerator.GetBytes(16);
        var path = Path.Combine(Path.GetTempPath(), "mega-tree-" + System.Guid.NewGuid().ToString("N") + ".tree");
        try
        {
            TreeCache.Save(tree, path, "session:abc");
            Assert.Null(TreeCache.Load(path, "session:other"));
            var back = TreeCache.Load(path, "session:abc")!;
            Assert.Equal("SN1", back.Sn);
            Assert.Equal(tree.Count, back.Count);
            Assert.Equal("ROOT0001", back.CloudRoot);
            Assert.Equal(tree.ShareKeys["SHARE001"], back.ShareKeys["SHARE001"]);
            var a = back.Get("FILE0001")!;
            Assert.Equal("A.mkv", a.Name);
            Assert.Equal(1000, a.Size);
            Assert.Equal(FileKey.Unpack(_fileKey)!.AesKey, a.Key!.AesKey);
            Assert.Equal(tree.Get("FILE0001")!.Key!.Nonce, a.Key.Nonce);
            Assert.Equal("1:1*AAAAAAAAAAA", a.FileAttributes);
            Assert.Equal(_folderKey, back.Get("FOLD0001")!.FolderKey);
            Assert.Equal("/Films/Old", back.GetPath(back.Get("FOLD0002")!));

            // Damaged file: ignored.
            var bytes = System.IO.File.ReadAllBytes(path);
            bytes[^1] ^= 1;
            System.IO.File.WriteAllBytes(path, bytes);
            Assert.Null(TreeCache.Load(path, "session:abc"));
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }
}

public class SingleShowTests
{
    [Theory]
    [InlineData("Saison 1", true)]
    [InlineData("Season 02", true)]
    [InlineData("S03", true)]
    [InlineData("Specials", true)]
    [InlineData("Breaking Bad", false)]
    [InlineData("Sherlock", false)]
    public void SeasonFolderNames(string name, bool single)
    {
        var tree = new MegaTree();
        tree.Add(new MegaNode { Handle = "ROOT0001", Type = MegaNodeType.Root }, false);
        tree.Add(new MegaNode { Handle = "SHOW0001", Parent = "ROOT0001", Type = MegaNodeType.Folder, Name = "Show" }, false);
        tree.Add(new MegaNode { Handle = "SUB00001", Parent = "SHOW0001", Type = MegaNodeType.Folder, Name = name }, false);
        var lib = new Jellyfin.Plugin.Mega.Configuration.MegaLibraryConfig { ContentType = "tvshows" };
        Assert.Equal(single, Jellyfin.Plugin.Mega.Library.LibrarySynchronizer.IsSingleShow(lib, tree, tree.Get("SHOW0001")!));
        lib.ContentType = "movies";
        Assert.False(Jellyfin.Plugin.Mega.Library.LibrarySynchronizer.IsSingleShow(lib, tree, tree.Get("SHOW0001")!));
    }
}
