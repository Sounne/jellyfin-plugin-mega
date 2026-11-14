using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.Mega.Mega;
using Jellyfin.Plugin.Mega.Streaming;
using Jellyfin.Plugin.Mega.Subtitles;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Plugin.Mega.Tests;

public sealed class PathNameTests
{
    [Theory]
    [InlineData("Film: Part 1?", "Film_ Part 1_")]
    [InlineData("a\tb\u0001c", "a_b_c")]
    [InlineData("<>\"|*", "_____")]
    [InlineData("  spaced  ", "spaced")]
    [InlineData("dots...", "dots")]
    [InlineData("..", "_")]
    [InlineData("", "_")]
    [InlineData("com1.mkv", "_com1.mkv")]
    [InlineData("LPT9", "_LPT9")]
    [InlineData("CONSOLE.mkv", "CONSOLE.mkv")]
    [InlineData("Été à Paris (2024)", "Été à Paris (2024)")]
    public void Sanitize(string name, string expected) => Assert.Equal(expected, PathNames.Sanitize(name));

    [Fact]
    public void LongNamesKeepTheirExtension()
    {
        var name = new string('é', 300) + ".mkv";
        var s = PathNames.Sanitize(name);
        Assert.EndsWith(".mkv", s, StringComparison.Ordinal);
        Assert.True(Encoding.UTF8.GetByteCount(s) <= 200);
        Assert.StartsWith("éééé", s, StringComparison.Ordinal);
    }

    [Fact]
    public void UniqueNumbersDuplicates()
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Assert.Equal(
            new[] { "Film.strm", "film (2).strm", "FILM (3).strm", "Other.strm", "noext", "noext (2)" },
            new[] { "Film.strm", "film.strm", "FILM.strm", "Other.strm", "noext", "noext" }.Select(n => PathNames.Unique(n, used)));
    }
}

public sealed class MediaFileTypeTests
{
    [Theory]
    [InlineData("a.mkv", true, false)]
    [InlineData("a.M2TS", true, false)]
    [InlineData("a.iso", true, false)]
    [InlineData("a.flac", false, true)]
    [InlineData("a.m4b", false, true)]
    [InlineData("a.mka", false, true)]
    [InlineData("a.srt", false, false)]
    [InlineData("mkv", false, false)]
    [InlineData("a.mkv.part", false, false)]
    public void VideoAndAudio(string name, bool video, bool audio)
    {
        Assert.Equal(video, MediaFileTypes.IsVideo(name));
        Assert.Equal(audio, MediaFileTypes.IsAudio(name));
        Assert.Equal(video || audio, MediaFileTypes.IsMedia(name));
    }

    [Theory]
    [InlineData("a.fr.srt", true)]
    [InlineData("a.ASS", true)]
    [InlineData("a.sup", true)]
    [InlineData("a.idx", true)]
    [InlineData("a.txt", false)]
    [InlineData("a.nfo", false)]
    public void Subtitles(string name, bool subtitle) => Assert.Equal(subtitle, MediaFileTypes.IsSubtitle(name));

    [Theory]
    [InlineData("poster.jpg", true)]
    [InlineData("folder.png", true)]
    [InlineData("backdrop2.jpg", true)]
    [InlineData("season-specials-poster.jpg", true)]
    [InlineData("season02-banner.jpg", true)]
    [InlineData("Movie (2020)-clearlogo.png", true)]
    [InlineData("albumart_small.jpg", true)]
    [InlineData("poster.txt", false)]
    [InlineData("my poster.jpg", false)]
    [InlineData("IMG_2041.JPG", false)]
    public void Artwork(string name, bool artwork) => Assert.Equal(artwork, MediaFileTypes.IsArtwork(name));

    [Fact]
    public void SideFilesHaveASizeLimit()
    {
        const long Max = MediaFileTypes.MaxSideFileSize;
        Assert.True(MediaFileTypes.IsSideFile("a.srt", Max));
        Assert.False(MediaFileTypes.IsSideFile("a.srt", Max + 1));
        Assert.True(MediaFileTypes.IsSideFile("movie.nfo", 10));
        Assert.True(MediaFileTypes.IsSideFile("IMG_1.jpg", 10));
        Assert.False(MediaFileTypes.IsSideFile("a.mkv", 10));

        // Media files of any size; side files only when small and useful to Jellyfin.
        Assert.True(MediaFileTypes.Keep("a.mkv", 100L << 30));
        Assert.True(MediaFileTypes.Keep("poster.jpg", Max));
        Assert.False(MediaFileTypes.Keep("poster.jpg", Max + 1));
        Assert.True(MediaFileTypes.Keep("tvshow.nfo", 10));
        Assert.False(MediaFileTypes.Keep("IMG_1.jpg", 10));
        Assert.False(MediaFileTypes.Keep("archive.zip", 10));
    }

    [Theory]
    [InlineData("a.mkv", "video/x-matroska")]
    [InlineData("a.MP4", "video/mp4")]
    [InlineData("a.m4v", "video/mp4")]
    [InlineData("a.ts", "video/mp2t")]
    [InlineData("a.webm", "video/webm")]
    [InlineData("a.mp3", "audio/mpeg")]
    [InlineData("a.flac", "audio/flac")]
    [InlineData("a.m4b", "audio/mp4")]
    [InlineData("a.opus", "audio/opus")]
    [InlineData("a.iso", "application/octet-stream")]
    [InlineData("noext", "application/octet-stream")]
    public void MimeTypes(string name, string mime) => Assert.Equal(mime, MediaFileTypes.GetMimeType(name));
}

public sealed class StreamUrlTests
{
    [Fact]
    public void SignaturesAreShortAndBound()
    {
        var sig = StreamUrls.Sign("secret", "src", "HANDLE01");
        Assert.Equal(20, sig.Length);
        Assert.Equal(sig, StreamUrls.Sign("secret", "src", "HANDLE01"));
        Assert.NotEqual(sig, StreamUrls.Sign("secret", "src2", "HANDLE01"));
        Assert.False(StreamUrls.Verify("secret", "src", "HANDLE01", null!));
        Assert.False(StreamUrls.Verify("secret", "src", "HANDLE01", sig[..^1]));
        Assert.False(StreamUrls.Verify("secret", "sr", "cHANDLE01", sig));
    }

    [Fact]
    public void FileNameIsEscapedAndCannotAddPathSegments()
    {
        var node = new MegaNode { Handle = "HANDLE01", Name = "a/b\\c ?#%.mkv" };
        var url = StreamUrls.Build("http://host:8096", "s", "src", node);
        Assert.EndsWith("/a_b_c%20%3F%23%25.mkv", url, StringComparison.Ordinal);
        Assert.Equal(new[] { "http:", string.Empty, "host:8096", "MegaFs", "stream", "src", "HANDLE01", StreamUrls.Sign("s", "src", "HANDLE01"), "a_b_c%20%3F%23%25.mkv" }, url.Split('/'));
        Assert.True(StreamUrls.TryParse(url, out var source, out var handle));
        Assert.Equal(("src", "HANDLE01"), (source, handle));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("http://host/Videos/1/stream")]
    [InlineData("http://host/MegaFs/stream/src/HANDLE01")]
    [InlineData("http://host/MegaFs/stream//HANDLE01/sig/a.mkv")]
    [InlineData("http://host/MegaFs/stream/src//sig/a.mkv")]
    public void NotAStreamUrl(string? url) => Assert.False(StreamUrls.TryParse(url, out _, out _));

    [Fact]
    public void MarkerIsCaseInsensitive()
    {
        Assert.True(StreamUrls.TryParse("http://host/megafs/STREAM/src/HANDLE01/sig/a.mkv", out var s, out var h));
        Assert.Equal(("src", "HANDLE01"), (s, h));
    }
}

public sealed class RangeHeaderTests
{
    [Theory]
    [InlineData("BYTES=0-9", 0, 9)]
    [InlineData(" bytes= 5 - 9 ", 5, 9)]
    [InlineData("bytes=-5000", 0, 999)]
    [InlineData("bytes=999-", 999, 999)]
    [InlineData("bytes=0-0", 0, 0)]
    public void Accepted(string header, long start, long end)
    {
        Assert.True(MegaRangeResult.TryParseRange(header, 1000, out var s, out var e));
        Assert.Equal((start, end), (s, e));
    }

    [Theory]
    [InlineData("bytes=-0")]
    [InlineData("bytes=-")]
    [InlineData("bytes=5")]
    [InlineData("bytes=+5-9")]
    [InlineData("bytes=-5-9")]
    [InlineData("bytes=1e3-")]
    [InlineData("")]
    public void Refused(string header) => Assert.False(MegaRangeResult.TryParseRange(header, 1000, out _, out _));
}

public sealed class ErrorTests
{
    [Theory]
    [InlineData(-15, true, false)]
    [InlineData(-17, false, true)]
    [InlineData(-9, false, false)]
    public void Flags(int code, bool badSession, bool quota)
    {
        var e = new MegaException(code);
        Assert.Equal(code, e.Code);
        Assert.Equal(badSession, e.IsBadSession);
        Assert.Equal(quota, e.IsQuotaExceeded);
    }

    [Fact]
    public void StorageQuota()
    {
        Assert.True(new MegaException("x") { HttpStatus = 509 }.IsQuotaExceeded);
        Assert.False(new MegaException("x") { HttpStatus = 503 }.IsQuotaExceeded);
    }

    [Fact]
    public void EveryKnownCodeIsDescribed()
    {
        foreach (var code in new[] { -1, -2, -3, -4, -5, -6, -8, -9, -11, -13, -14, -15, -16, -17, -18, -19, -26 })
        {
            var text = MegaException.Describe(code);
            Assert.EndsWith($"({code})", text, StringComparison.Ordinal);
            Assert.NotEqual($"MEGA error ({code})", text);
        }

        Assert.Equal("MEGA error (-99)", MegaException.Describe(-99));
    }
}

public sealed class NodeTests
{
    [Fact]
    public void WithCopiesAndChanges()
    {
        var key = FileKey.Unpack(RandomNumberGenerator.GetBytes(32));
        var n = new MegaNode { Handle = "H", Parent = "P", Type = MegaNodeType.File, Name = "a.mkv", Size = 5, Timestamp = 10, ModifiedTime = 20, Key = key, FileAttributes = "1:0*x" };
        var renamed = n.With(name: "b.mkv");
        Assert.Equal("b.mkv", renamed.Name);
        Assert.Equal("a.mkv", n.Name);
        Assert.Equal((n.Handle, n.Parent, n.Type, n.Size, n.Timestamp, n.ModifiedTime, n.FileAttributes), (renamed.Handle, renamed.Parent, renamed.Type, renamed.Size, renamed.Timestamp, renamed.ModifiedTime, renamed.FileAttributes));
        Assert.Same(key, renamed.Key);
        Assert.Equal(30, n.With(modifiedTime: 30).ModifiedTime);
        Assert.Equal("2:1*y", n.With(fileAttributes: "2:1*y").FileAttributes);
    }

    [Fact]
    public void ModifiedFallsBackToTheUploadTime()
    {
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(20).UtcDateTime, new MegaNode { Handle = "H", Timestamp = 10, ModifiedTime = 20 }.Modified);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(10).UtcDateTime, new MegaNode { Handle = "H", Timestamp = 10, ModifiedTime = 0 }.Modified);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(10).UtcDateTime, new MegaNode { Handle = "H", Timestamp = 10 }.Modified);
    }

    [Theory]
    [InlineData("924:0*AAAAAAAAAAA", 1, false)]
    [InlineData("bad/924:1*", 1, false)]
    [InlineData("x:1*AAAAAAAAAAA", 1, true)]
    [InlineData("*:1/924:1*BBBBBBBBBBB", 1, true)]
    [InlineData(null, 0, false)]
    public void Attributes(string? fa, int type, bool found)
        => Assert.Equal(found, new MegaNode { Handle = "H", FileAttributes = fa }.TryGetAttribute(type, out _, out _));

    [Fact]
    public void FingerprintTime()
    {
        var fp = new byte[16 + 1 + 8];
        fp[16] = 5;
        BitConverter.GetBytes(0x01_2345_6789L).CopyTo(fp, 17);
        Assert.Equal(0x01_2345_6789L, MegaNode.MtimeFromFingerprint(MegaCrypto.B64Encode(fp)));

        fp[16] = 0;
        Assert.Null(MegaNode.MtimeFromFingerprint(MegaCrypto.B64Encode(fp)));
        fp[16] = 9;
        Assert.Null(MegaNode.MtimeFromFingerprint(MegaCrypto.B64Encode(fp)));
        Assert.Null(MegaNode.MtimeFromFingerprint(null));
    }
}

public sealed class CryptoHelperTests
{
    [Fact]
    public void ShareKeysFromTheKeysAttribute()
    {
        var master = RandomNumberGenerator.GetBytes(16);
        var shares = new Dictionary<string, byte[]>
        {
            ["AbCdEfGh"] = RandomNumberGenerator.GetBytes(16),
            ["Zz_-0123"] = RandomNumberGenerator.GetBytes(16),
        };

        // Record 48: 6-byte handle, 16-byte key, 1 byte of flags, per share.
        var records = shares.SelectMany(s => MegaCrypto.B64Decode(s.Key)!.Concat(s.Value).Append((byte)0)).ToArray();
        var other = new byte[] { 7, 0, 0, 2, 1, 2 };
        var plain = other.Concat(new byte[] { 48, 0, 0, (byte)records.Length }).Concat(records).ToArray();
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using (var gcm = new AesGcm(HKDF.DeriveKey(HashAlgorithmName.SHA256, master, 16, Array.Empty<byte>(), new byte[] { 1 }), 16))
        {
            gcm.Encrypt(nonce, plain, cipher, tag);
        }

        var attribute = new byte[] { 20, 0 }.Concat(nonce).Concat(cipher).Concat(tag).ToArray();
        var parsed = MegaCrypto.ParseKeysAttribute(master, attribute);
        Assert.Equal(shares.Keys.OrderBy(k => k), parsed.Keys.OrderBy(k => k));
        Assert.All(shares, s => Assert.Equal(s.Value, parsed[s.Key]));

        // Another account's master key, another version or a damaged attribute: nothing.
        Assert.Empty(MegaCrypto.ParseKeysAttribute(RandomNumberGenerator.GetBytes(16), attribute));
        Assert.Empty(MegaCrypto.ParseKeysAttribute(master, new byte[] { 21 }.Concat(attribute.Skip(1)).ToArray()));
        attribute[^1] ^= 1;
        Assert.Empty(MegaCrypto.ParseKeysAttribute(master, attribute));
    }

    [Fact]
    public void AttributesNeedTheMegaPrefix()
    {
        var key = RandomNumberGenerator.GetBytes(16);
        string Encrypt(string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            var padded = new byte[(bytes.Length + 15) / 16 * 16];
            bytes.CopyTo(padded, 0);
            MegaCrypto.CbcEncrypt(key, padded);
            return MegaCrypto.B64Encode(padded);
        }

        Assert.Equal("x", MegaCrypto.DecryptAttributes(key, Encrypt("MEGA{\"n\":\"x\"}"))!.Value.GetProperty("n").GetString());
        Assert.Null(MegaCrypto.DecryptAttributes(key, Encrypt("NOPE{\"n\":\"x\"}")));
        Assert.Null(MegaCrypto.DecryptAttributes(key, Encrypt("MEGA{broken json")));
        Assert.Null(MegaCrypto.DecryptAttributes(key, "AAAA"));
        Assert.Null(MegaCrypto.DecryptAttributes(key, null));
    }

    [Fact]
    public void PreviewsMustBeImages()
    {
        var key = RandomNumberGenerator.GetBytes(16);
        byte[] Encrypt(byte[] plain)
        {
            var data = (byte[])plain.Clone();
            MegaCrypto.CbcEncrypt(key, data);
            return data;
        }

        var jpeg = new byte[64];
        jpeg[0] = 0xFF;
        jpeg[1] = 0xD8;
        var png = new byte[64];
        new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G' }.CopyTo(png, 0);
        var webp = new byte[64];
        Encoding.ASCII.GetBytes("RIFF0000WEBP").CopyTo(webp, 0);
        var gif = new byte[64];
        Encoding.ASCII.GetBytes("GIF89a").CopyTo(gif, 0);

        foreach (var image in new[] { jpeg, png, webp, gif })
        {
            Assert.Equal(image, FileAttributeClient.Decrypt(Encrypt(image), key));
        }

        Assert.Null(FileAttributeClient.Decrypt(Encrypt(new byte[64]), key));
        Assert.Null(FileAttributeClient.Decrypt(Encrypt(jpeg), RandomNumberGenerator.GetBytes(16)));
        Assert.Null(FileAttributeClient.Decrypt(new byte[10], key));

        // Trailing bytes that are not a whole block are ignored.
        Assert.Equal(jpeg, FileAttributeClient.Decrypt(Encrypt(jpeg).Concat(new byte[5]).ToArray(), key));
    }

    [Fact]
    public void AddressFamiliesAlternate()
    {
        var v4a = IPAddress.Parse("192.0.2.1");
        var v4b = IPAddress.Parse("192.0.2.2");
        var v6a = IPAddress.Parse("2001:db8::1");
        var all = new[] { v4a, v4b, v6a };
        Assert.Equal(new[] { v6a, v4a, v4b }, HappyEyeballs.Order(all, AddressFamily.InterNetworkV6));
        Assert.Equal(new[] { v4a, v6a, v4b }, HappyEyeballs.Order(all, AddressFamily.InterNetwork));
        Assert.Empty(HappyEyeballs.Order(Array.Empty<IPAddress>(), AddressFamily.InterNetwork));
    }
}

public sealed class EbmlTests
{
    [Fact]
    public void Ids()
    {
        int pos = 0;
        Assert.True(Ebml.TryReadId(new byte[] { 0x1A, 0x45, 0xDF, 0xA3, 0x00 }, ref pos, out var id));
        Assert.Equal(0x1A45DFA3u, id);
        Assert.Equal(4, pos);

        pos = 0;
        Assert.True(Ebml.TryReadId(new byte[] { 0xA3 }, ref pos, out id));
        Assert.Equal(0xA3u, id);

        pos = 0;
        Assert.False(Ebml.TryReadId(new byte[] { 0x0F }, ref pos, out _));
        Assert.False(Ebml.TryReadId(new byte[] { 0x1A, 0x45 }, ref pos, out _));
        Assert.Equal(0, pos);
    }

    [Theory]
    [InlineData(new byte[] { 0x81 }, 1L, 1)]
    [InlineData(new byte[] { 0x40, 0x02 }, 2L, 2)]
    [InlineData(new byte[] { 0x10, 0x00, 0x01, 0x00 }, 256L, 4)]
    [InlineData(new byte[] { 0xFF }, Ebml.UnknownSize, 1)]
    [InlineData(new byte[] { 0x01, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF }, Ebml.UnknownSize, 8)]
    [InlineData(new byte[] { 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x05 }, 5L, 8)]
    public void Sizes(byte[] data, long value, int length)
    {
        int pos = 0;
        Assert.True(Ebml.TryReadVint(data, ref pos, out var v));
        Assert.Equal(value, v);
        Assert.Equal(length, pos);
    }

    [Fact]
    public void BadSizes()
    {
        int pos = 0;
        Assert.False(Ebml.TryReadVint(new byte[] { 0x00, 0x01 }, ref pos, out _));
        Assert.False(Ebml.TryReadVint(new byte[] { 0x40 }, ref pos, out _));
        Assert.False(Ebml.TryReadVint(Array.Empty<byte>(), ref pos, out _));
        pos = -1;
        Assert.False(Ebml.TryReadVint(new byte[] { 0x81 }, ref pos, out _));
    }

    [Fact]
    public void ChildrenStopAtATruncatedElement()
    {
        // TrackNumber (D7) = 1, CodecID (86) = "S_TEXT/UTF8", then an element claiming 16 bytes with 2 left.
        var data = new byte[] { 0xD7, 0x81, 0x01, 0x86, 0x8B }
            .Concat(Encoding.ASCII.GetBytes("S_TEXT/UTF8"))
            .Concat(new byte[] { 0xE0, 0x90, 0x01, 0x02 })
            .ToArray();
        var children = Ebml.Children(data, 0, data.Length).ToList();
        Assert.Equal(new[] { 0xD7u, 0x86u }, children.Select(c => c.Id));
        Assert.Equal(1UL, Ebml.UInt(data, children[0]));
        Assert.Equal(1L, Ebml.Long(data, children[0]));
        Assert.Equal("S_TEXT/UTF8", Ebml.Ascii(data, children[1]));
        Assert.Equal(16, children[1].End);
    }
}

public sealed class SubtitleWriterTests
{
    private static MatroskaTrack Track(string codec, byte[]? codecPrivate = null) => new() { CodecId = codec, Type = 0x11, CodecPrivate = codecPrivate };

    [Fact]
    public void AssWithoutHeaderGetsAMinimalOne()
    {
        var events = new[]
        {
            new SubtitleEvent(62_500, 1_500, Encoding.UTF8.GetBytes("1,0,Default,,0,0,0,,Second\r\nline"), 1),
            new SubtitleEvent(1_000, 2_000, Encoding.UTF8.GetBytes("0,0,Default,,0,0,0,,First"), 0),
            new SubtitleEvent(5_000, 1_000, Encoding.UTF8.GetBytes("no commas"), 2),
        };
        var text = Encoding.UTF8.GetString(SubtitleFiles.Ass(Track("S_TEXT/ASS"), events, 1_000_000));
        Assert.Equal(
            "[Script Info]\nScriptType: v4.00+\n\n[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n"
            + "Dialogue: 0,0:00:01.00,0:00:03.00,Default,,0,0,0,,First\n"
            + "Dialogue: 0,0:01:02.50,0:01:04.00,Default,,0,0,0,,Second\\Nline\n",
            text);
    }

    [Fact]
    public void AssKeepsTheScriptHeader()
    {
        var header = Encoding.UTF8.GetBytes("﻿[Script Info]\r\nTitle: x\r\n\r\n[V4+ Styles]\r\nStyle: Default\r\n\r\n[Events]\r\n");
        var text = Encoding.UTF8.GetString(SubtitleFiles.Ass(Track("S_TEXT/ASS", header), Array.Empty<SubtitleEvent>(), 1_000_000));
        Assert.StartsWith("[Script Info]\nTitle: x\n", text, StringComparison.Ordinal);
        Assert.EndsWith("[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void SrtTimesAndEmptyLines()
    {
        var events = new[]
        {
            new SubtitleEvent(3_723_456, 1_000, Encoding.UTF8.GetBytes("Hour\0\0"), 0),
            new SubtitleEvent(3_730_000, null, Encoding.UTF8.GetBytes("  \r\n "), 1),
        };
        Assert.Equal("1\n01:02:03,456 --> 01:02:04,456\nHour\n\n", Encoding.UTF8.GetString(SubtitleFiles.Srt(events, 1_000_000)));
        Assert.Equal("\n", Encoding.UTF8.GetString(SubtitleFiles.Srt(Array.Empty<SubtitleEvent>(), 1_000_000)));
    }

    [Fact]
    public void PgsSegmentsGetTheirHeader()
    {
        // Two segments in one block: presentation (0x16, 2 bytes) and end (0x80, 0 bytes).
        var payload = new byte[] { 0x16, 0x00, 0x02, 0xAA, 0xBB, 0x80, 0x00, 0x00 };
        var sup = SubtitleFiles.Sup(new[] { new SubtitleEvent(1_000, null, payload, 0) }, 1_000_000);

        // 1 s at 90 kHz = 90 000 = 0x00015F90, as presentation and decoding times.
        var header = new byte[] { (byte)'P', (byte)'G', 0x00, 0x01, 0x5F, 0x90, 0x00, 0x01, 0x5F, 0x90 };
        Assert.Equal(header.Concat(payload[..5]).Concat(header).Concat(payload[5..]).ToArray(), sup);
        Assert.Throws<NotSupportedException>(() => SubtitleFiles.Write(Track("S_VOBSUB"), Array.Empty<SubtitleEvent>(), 1_000_000));
    }

    [Theory]
    [InlineData("subrip", false, true)]
    [InlineData("ass", false, true)]
    [InlineData("PGSSUB", false, true)]
    [InlineData("DVBSUB", false, false)]
    [InlineData("DVDSUB", false, true)]
    [InlineData("subrip", true, false)]
    public void WhichEmbeddedSubtitlesAreRead(string codec, bool external, bool extractable)
    {
        var stream = new MediaStream { Type = MediaStreamType.Subtitle, Codec = codec, IsExternal = external };
        Assert.Equal(extractable, SubtitlePrefill.IsExtractable(stream));
        Assert.False(SubtitlePrefill.IsExtractable(new MediaStream { Type = MediaStreamType.Audio, Codec = "subrip" }));
    }
}
