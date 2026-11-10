using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Mega.Subtitles;
using Xunit;

namespace Jellyfin.Plugin.Mega.Tests;

/// <summary>
/// Subtitle tracks read through the Matroska cues give the files Jellyfin's extraction (ffmpeg) writes.
/// Fixtures/subtitles.mkv and the expected files come from Fixtures/make_fixtures.py.
/// </summary>
public sealed class SubtitleReaderTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    [Fact]
    public async Task SameFilesAsFfmpegReadingOnlyTheSubtitleBlocks()
    {
        var file = new FileRanges(Fixture("subtitles.mkv"));
        var index = await MatroskaSubtitles.ReadIndexAsync(file, CancellationToken.None);
        Assert.NotNull(index);
        Assert.Equal(1_000_000, index!.TimestampScale);
        var subs = index.Tracks.Where(t => t.IsSubtitle).ToList();
        Assert.Equal(new[] { "S_TEXT/UTF8", "S_TEXT/ASS", "S_HDMV/PGS" }, subs.Select(t => t.CodecId));
        Assert.Equal(new[] { 1, 2, 3 }, subs.Select(t => t.Order));

        long indexBytes = file.Bytes;
        var blocks = await MatroskaSubtitles.ReadBlocksAsync(file, index, subs, 8, CancellationToken.None);

        Assert.Equal(File.ReadAllText(Fixture("expected.1.srt")), Encoding.UTF8.GetString(SubtitleFiles.Write(subs[0], blocks.Events[subs[0].Number], index.TimestampScale)));
        Assert.Equal(File.ReadAllText(Fixture("expected.2.ass")), Encoding.UTF8.GetString(SubtitleFiles.Write(subs[1], blocks.Events[subs[1].Number], index.TimestampScale)));
        Assert.Equal(File.ReadAllBytes(Fixture("expected.3.sup")), SubtitleFiles.Write(subs[2], blocks.Events[subs[2].Number], index.TimestampScale));

        // Nearby blocks share a read, no byte is read twice (this small file is mostly subtitles: on a
        // real movie the blocks are a few MB out of GBs).
        Assert.Equal(file.Bytes - indexBytes, blocks.Bytes);
        Assert.True(blocks.Bytes < file.Length, $"{blocks.Bytes} of {file.Length} bytes read");
        Assert.True(blocks.Requests < blocks.Events.Values.Sum(e => e.Count), $"{blocks.Requests} reads");
    }

    [Fact]
    public async Task OneTrackAlone()
    {
        var file = new FileRanges(Fixture("subtitles.mkv"));
        var index = (await MatroskaSubtitles.ReadIndexAsync(file, CancellationToken.None))!;
        var ass = index.Tracks.Single(t => t.CodecId == "S_TEXT/ASS");
        var blocks = await MatroskaSubtitles.ReadBlocksAsync(file, index, new[] { ass }, 4, CancellationToken.None);
        Assert.Single(blocks.Events);
        Assert.Equal(36, blocks.Events[ass.Number].Count);
    }

    [Fact]
    public async Task NotMatroska()
    {
        var file = new FileRanges(Fixture("expected.3.sup"));
        Assert.Null(await MatroskaSubtitles.ReadIndexAsync(file, CancellationToken.None));
    }

    [Fact]
    public async Task TrackWithoutCuesIsRefused()
    {
        var file = new FileRanges(Fixture("subtitles.mkv"));
        var index = (await MatroskaSubtitles.ReadIndexAsync(file, CancellationToken.None))!;
        var srt = index.Tracks.Single(t => t.CodecId == "S_TEXT/UTF8");
        var noCues = index with { Cues = index.Cues.Where(c => c.Track != srt.Number).ToList() };
        await Assert.ThrowsAsync<NotSupportedException>(() => MatroskaSubtitles.ReadBlocksAsync(file, noCues, new[] { srt }, 4, CancellationToken.None));

        var noPositions = index with { Cues = index.Cues.Select(c => c with { Relative = null }).ToList() };
        await Assert.ThrowsAsync<NotSupportedException>(() => MatroskaSubtitles.ReadBlocksAsync(file, noPositions, new[] { srt }, 4, CancellationToken.None));
    }

    [Fact]
    public async Task WrongCuePositionIsAnError()
    {
        var file = new FileRanges(Fixture("subtitles.mkv"));
        var index = (await MatroskaSubtitles.ReadIndexAsync(file, CancellationToken.None))!;
        var srt = index.Tracks.Single(t => t.CodecId == "S_TEXT/UTF8");
        var shifted = index with { Cues = index.Cues.Select(c => c.Track == srt.Number ? c with { Relative = c.Relative + 40 } : c).ToList() };
        await Assert.ThrowsAsync<InvalidDataException>(() => MatroskaSubtitles.ReadBlocksAsync(file, shifted, new[] { srt }, 4, CancellationToken.None));
    }

    [Fact]
    public void ContentCompression()
    {
        var text = Encoding.UTF8.GetBytes("Hello, compressed subtitles");
        using var packed = new MemoryStream();
        using (var z = new System.IO.Compression.ZLibStream(packed, System.IO.Compression.CompressionLevel.Optimal, true))
        {
            z.Write(text);
        }

        var zlib = new ContentEncoding(0, 1, 0, 0, null);
        Assert.Equal(text, MatroskaSubtitles.DecodeContent(packed.ToArray(), new[] { zlib }, 1));
        Assert.Equal(packed.ToArray(), MatroskaSubtitles.DecodeContent(packed.ToArray(), new[] { zlib }, 2));

        var strip = new ContentEncoding(0, 1, 0, 3, Encoding.UTF8.GetBytes("Hello"));
        Assert.Equal(text, MatroskaSubtitles.DecodeContent(text.AsSpan(5).ToArray(), new[] { strip }, 1));

        var track = new MatroskaTrack { CodecId = "S_TEXT/UTF8", Type = 0x11 };
        track.Encodings.Add(new ContentEncoding(0, 1, 1, 0, null));
        Assert.False(MatroskaSubtitles.CanDecode(track));
    }

    [Fact]
    public void SrtWithoutDurationsEndsAtTheNextLine()
    {
        var events = new[]
        {
            new SubtitleEvent(1000, null, Encoding.UTF8.GetBytes("a\r\n\r\nb"), 0),
            new SubtitleEvent(2500, null, Encoding.UTF8.GetBytes("c"), 1),
        };
        Assert.Equal(
            "1\n00:00:01,000 --> 00:00:02,500\na\nb\n\n2\n00:00:02,500 --> 00:00:07,500\nc\n\n",
            Encoding.UTF8.GetString(SubtitleFiles.Srt(events, 1_000_000)));
    }

    /// <summary>A local file read by ranges, counting what is read.</summary>
    private sealed class FileRanges : IRangeReader
    {
        private readonly byte[] _data;
        private long _bytes;

        public FileRanges(string path)
        {
            _data = File.ReadAllBytes(path);
        }

        public long Length => _data.Length;

        public long Bytes => Interlocked.Read(ref _bytes);

        public async Task<byte[]> ReadAsync(long offset, int count, CancellationToken cancellationToken)
        {
            await Task.Yield();
            count = (int)Math.Clamp(Length - offset, 0, count);
            Interlocked.Add(ref _bytes, count);
            return _data.AsSpan((int)offset, count).ToArray();
        }
    }
}
