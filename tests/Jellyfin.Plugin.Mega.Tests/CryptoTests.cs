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
using Jellyfin.Plugin.Mega.Streaming;
using Xunit;

namespace Jellyfin.Plugin.Mega.Tests;

public class CryptoTests
{
    [Fact]
    public void CtrSeekMatchesSequential()
    {
        var key = Enumerable.Repeat((byte)7, 16).ToArray();
        var nonce = Enumerable.Repeat((byte)3, 8).ToArray();
        var plain = Enumerable.Range(0, 300_000).Select(i => (byte)(i * 31 % 251)).ToArray();
        var whole = (byte[])plain.Clone();
        MegaCrypto.CtrTransform(key, nonce, 0, whole);
        foreach (var (from, to) in new[] { (1234, 4321), (0, 17), (65_535, 200_001), (16, 32), (299_990, 300_000) })
        {
            var part = plain[from..to];
            MegaCrypto.CtrTransform(key, nonce, from, part);
            Assert.Equal(whole[from..to], part);
        }

        // Symmetric.
        MegaCrypto.CtrTransform(key, nonce, 0, whole);
        Assert.Equal(plain, whole);
    }

    [Fact]
    public void CtrKeystreamIsAesOfNonceAndBigEndianCounter()
    {
        var key = RandomNumberGenerator.GetBytes(16);
        var nonce = RandomNumberGenerator.GetBytes(8);
        var data = new byte[48];
        MegaCrypto.CtrTransform(key, nonce, 16 * 5, data);
        using var aes = Aes.Create();
        aes.Key = key;
        for (int b = 0; b < 3; b++)
        {
            var counter = new byte[16];
            nonce.CopyTo(counter, 0);
            counter[15] = (byte)(5 + b);
            var ks = aes.EncryptEcb(counter, PaddingMode.None);
            Assert.Equal(ks, data[(b * 16)..((b + 1) * 16)]);
        }
    }

    [Fact]
    public void AttributesRoundTrip()
    {
        var key = RandomNumberGenerator.GetBytes(16);
        var json = Encoding.UTF8.GetBytes("MEGA{\"n\":\"Été 2024.mkv\",\"c\":\"x\"}");
        var padded = new byte[(json.Length + 15) / 16 * 16];
        json.CopyTo(padded, 0);
        MegaCrypto.CbcEncrypt(key, padded);
        var attrs = MegaCrypto.DecryptAttributes(key, MegaCrypto.B64Encode(padded));
        Assert.NotNull(attrs);
        Assert.Equal("Été 2024.mkv", attrs!.Value.GetProperty("n").GetString());
        Assert.Null(MegaCrypto.DecryptAttributes(RandomNumberGenerator.GetBytes(16), MegaCrypto.B64Encode(padded)));
    }

    [Fact]
    public void FileKeyUnpack()
    {
        var k = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        var fk = FileKey.Unpack(k)!;
        for (int i = 0; i < 16; i++)
        {
            Assert.Equal((byte)(i ^ (i + 16)), fk.AesKey[i]);
        }

        Assert.Equal(k[16..24], fk.Nonce);
        Assert.Equal(k[24..32], fk.Mac);
    }

    [Fact]
    public void Base64IsTolerant()
    {
        Assert.Equal(new byte[] { 1, 2, 3 }, MegaCrypto.B64Decode("AQID"));
        Assert.Equal(MegaCrypto.B64Decode("_-8"), MegaCrypto.B64Decode("/+8="));
        Assert.Null(MegaCrypto.B64Decode("A"));
        var bytes = RandomNumberGenerator.GetBytes(43);
        Assert.Equal(bytes, MegaCrypto.B64Decode(MegaCrypto.B64Encode(bytes)));
    }

    [Fact]
    public void MpiParsing()
    {
        var data = new byte[] { 0x00, 0x09, 0x01, 0xFF, 0xAA };
        int pos = 0;
        Assert.True(MegaCrypto.TryReadMpi(data, ref pos, out var v));
        Assert.Equal(new byte[] { 0x01, 0xFF }, v);
        Assert.Equal(4, pos);
        Assert.False(MegaCrypto.TryReadMpi(data, ref pos, out _));
    }

    [Fact]
    public void RsaRawDecryptionMatchesDotNet()
    {
        using var rsa = RSA.Create(2048);
        var p = rsa.ExportParameters(true);
        static byte[] Mpi(byte[] be)
        {
            int start = 0;
            while (start < be.Length - 1 && be[start] == 0)
            {
                start++;
            }

            var v = be[start..];
            int bits = (v.Length * 8) - System.Numerics.BitOperations.LeadingZeroCount((uint)v[0]) + 24;
            return new[] { (byte)(bits >> 8), (byte)bits }.Concat(v).ToArray();
        }

        var privk = Mpi(p.P!).Concat(Mpi(p.Q!)).Concat(Mpi(p.D!)).Concat(Mpi(p.InverseQ!)).ToArray();
        var mega = MegaRsa.FromPrivk(privk)!;
        var message = RandomNumberGenerator.GetBytes(64);
        message[0] = 0x42;
        var cipher = RawEncrypt(p, message);
        Assert.Equal(message, mega.DecryptMpi(Mpi(cipher)));
    }

    private static byte[] RawEncrypt(RSAParameters p, byte[] message)
    {
        var n = new System.Numerics.BigInteger(p.Modulus, true, true);
        var e = new System.Numerics.BigInteger(p.Exponent, true, true);
        var m = new System.Numerics.BigInteger(message, true, true);
        return System.Numerics.BigInteger.ModPow(m, e, n).ToByteArray(true, true);
    }

    [Fact]
    public void FingerprintMtime()
    {
        var fp = new byte[16 + 1 + 4];
        fp[16] = 4;
        BitConverter.GetBytes(1_700_000_000).CopyTo(fp, 17);
        Assert.Equal(1_700_000_000L, MegaNode.MtimeFromFingerprint(MegaCrypto.B64Encode(fp)));
        Assert.Null(MegaNode.MtimeFromFingerprint("AQID"));
    }

    [Fact]
    public void FileAttributeReference()
    {
        var n = new MegaNode { Handle = "abcdefgh", FileAttributes = "924:0*AAAAAAAAAAA/925:1*BBBBBBBBBBB" };
        Assert.True(n.TryGetAttribute(1, out var bundle, out var h));
        Assert.Equal(925u, bundle);
        Assert.Equal("BBBBBBBBBBB", h);
        Assert.False(n.TryGetAttribute(8, out _, out _));
    }

    [Theory]
    [InlineData("bytes=0-", 1000, 0, 999)]
    [InlineData("bytes=10-19", 1000, 10, 19)]
    [InlineData("bytes=990-5000", 1000, 990, 999)]
    [InlineData("bytes=-100", 1000, 900, 999)]
    [InlineData("bytes=5-9, 20-30", 1000, 5, 9)]
    public void RangeParsing(string header, long size, long start, long end)
    {
        Assert.True(MegaRangeResult.TryParseRange(header, size, out var s, out var e));
        Assert.Equal(start, s);
        Assert.Equal(end, e);
    }

    [Theory]
    [InlineData("bytes=1000-")]
    [InlineData("bytes=20-10")]
    [InlineData("items=0-1")]
    [InlineData("bytes=abc")]
    public void RangeParsingRejects(string header)
    {
        Assert.False(MegaRangeResult.TryParseRange(header, 1000, out _, out _));
    }

    [Fact]
    public void FolderLinks()
    {
        var key = MegaCrypto.B64Encode(Enumerable.Range(1, 16).Select(i => (byte)i).ToArray());
        var a = MegaSource.ParseFolderLink($"https://mega.nz/folder/AbCd_-12#{key}");
        Assert.NotNull(a);
        Assert.Equal("AbCd_-12", a!.Value.Handle);
        Assert.Equal(16, a.Value.Key.Length);
        Assert.Null(a.Value.Subfolder);
        var b = MegaSource.ParseFolderLink($"https://mega.nz/folder/AbCd_-12#{key}/folder/XyZ12345");
        Assert.Equal("XyZ12345", b!.Value.Subfolder);
        var c = MegaSource.ParseFolderLink($"https://mega.nz/#F!AbCd_-12!{key}");
        Assert.Equal("AbCd_-12", c!.Value.Handle);
        Assert.Null(MegaSource.ParseFolderLink("https://mega.nz/file/AbCd_-12#xyz"));
    }

    [Fact]
    public void StreamUrlsRoundTrip()
    {
        var node = new MegaNode { Handle = "Ab-_1234", Name = "Film #1 (2020) é.mkv" };
        var url = StreamUrls.Build("http://127.0.0.1:8096/jf/", "secret", "src1", node);
        Assert.StartsWith("http://127.0.0.1:8096/jf/MegaFs/stream/src1/Ab-_1234/", url, StringComparison.Ordinal);
        Assert.EndsWith(".mkv", url, StringComparison.Ordinal);
        Assert.True(StreamUrls.TryParse(url, out var s, out var h));
        Assert.Equal("src1", s);
        Assert.Equal("Ab-_1234", h);
        var sig = url.Split('/')[^2];
        Assert.True(StreamUrls.Verify("secret", "src1", "Ab-_1234", sig));
        Assert.False(StreamUrls.Verify("secret", "src1", "Ab-_1235", sig));
        Assert.False(StreamUrls.Verify("other", "src1", "Ab-_1234", sig));
    }

    [Fact]
    public void PathNamesAreSafeAndUnique()
    {
        Assert.Equal("a_b_c", PathNames.Sanitize("a/b:c"));
        Assert.Equal("_CON.mkv", PathNames.Sanitize("CON.mkv"));
        Assert.Equal("x", PathNames.Sanitize("x. "));
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Assert.Equal("Film.strm", PathNames.Unique("Film.strm", used));
        Assert.Equal("film (2).strm", PathNames.Unique("film.strm", used));
    }

    [Fact]
    public void ArtworkAndMediaTypes()
    {
        Assert.True(MediaFileTypes.IsArtwork("poster.jpg"));
        Assert.True(MediaFileTypes.IsArtwork("Movie (2020)-fanart.png"));
        Assert.True(MediaFileTypes.IsArtwork("season01-poster.jpg"));
        Assert.False(MediaFileTypes.IsArtwork("IMG_2041.jpg"));
        Assert.True(MediaFileTypes.Keep("Film.MKV", 10_000_000_000));
        Assert.True(MediaFileTypes.Keep("Film.fr.srt", 50_000));
        Assert.False(MediaFileTypes.Keep("IMG_2041.jpg", 3_000_000));
        Assert.False(MediaFileTypes.Keep("notes.txt", 100));
    }

    [Fact]
    public async Task TreeParsesInSmallChunksAndDecrypts()
    {
        var master = RandomNumberGenerator.GetBytes(16);
        const string User = "UserHandle1";
        string EncKey(byte[] k)
        {
            var c = (byte[])k.Clone();
            MegaCrypto.EcbEncrypt(master, c);
            return User + ":" + MegaCrypto.B64Encode(c);
        }

        string Attrs(byte[] key, string name)
        {
            var json = Encoding.UTF8.GetBytes("MEGA{\"n\":" + JsonSerializer.Serialize(name) + "}");
            var padded = new byte[(json.Length + 15) / 16 * 16];
            json.CopyTo(padded, 0);
            MegaCrypto.CbcEncrypt(key, padded);
            return MegaCrypto.B64Encode(padded);
        }

        var folderKey = RandomNumberGenerator.GetBytes(16);
        var fileKey = RandomNumberGenerator.GetBytes(32);
        var fileAes = FileKey.Unpack(fileKey)!.AesKey;
        var nodes = new[]
        {
            new { h = "ROOT0001", p = "", t = 2, a = "", k = "", s = 0L },
            new { h = "FOLD0001", p = "ROOT0001", t = 1, a = Attrs(folderKey, "Films"), k = EncKey(folderKey), s = 0L },
            new { h = "FILE0001", p = "FOLD0001", t = 0, a = Attrs(fileAes, "Movie (2020).mkv"), k = EncKey(fileKey), s = 123456789L },
            new { h = "FILE0002", p = "FOLD0001", t = 0, a = Attrs(fileAes, "notes.docx"), k = EncKey(fileKey), s = 10L },
        };
        var json = JsonSerializer.Serialize(new object[] { new { f = nodes, u = new[] { new { u = User, m = "x@y.z" } }, sn = "SN123" } });
        using var stream = new TrickleStream(Encoding.UTF8.GetBytes(json), 7);
        var tree = await MegaTree.ParseAsync(stream, new AccountKeyring(User, master, null), new Dictionary<string, byte[]>(), MediaFileTypes.Keep, CancellationToken.None);
        Assert.Equal("SN123", tree.Sn);
        Assert.Equal("ROOT0001", tree.CloudRoot);
        var films = tree.Resolve("/films");
        Assert.NotNull(films);
        Assert.Equal("Films", films!.Name);
        var movie = Assert.Single(tree.GetChildren("FOLD0001"));
        Assert.Equal("Movie (2020).mkv", movie.Name);
        Assert.Equal(123456789L, movie.Size);
        Assert.Equal(fileAes, movie.Key!.AesKey);
        Assert.Equal("/Films", tree.GetPath(films));
    }

    [Fact]
    public async Task TreeReportsApiErrorCode()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("[-15]"));
        var e = await Assert.ThrowsAsync<MegaException>(() => MegaTree.ParseAsync(stream, new FolderLinkKeyring(new byte[16]), new Dictionary<string, byte[]>(), MediaFileTypes.Keep, CancellationToken.None));
        Assert.Equal(-15, e.Code);
    }

    /// <summary>Returns a few bytes per read: exercises buffer boundaries of the streaming parser.</summary>
    private sealed class TrickleStream : MemoryStream
    {
        private readonly int _chunk;

        public TrickleStream(byte[] data, int chunk)
            : base(data)
        {
            _chunk = chunk;
        }

        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(count, _chunk));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => base.ReadAsync(buffer[..Math.Min(buffer.Length, _chunk)], cancellationToken);
    }
}
