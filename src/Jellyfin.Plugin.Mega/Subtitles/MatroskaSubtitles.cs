using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Mega.Subtitles;

/// <summary>Random access to the bytes of a file.</summary>
internal interface IRangeReader
{
    long Length { get; }

    /// <summary>Reads <paramref name="count"/> bytes at <paramref name="offset"/> (fewer only at the end of the file).</summary>
    Task<byte[]> ReadAsync(long offset, int count, CancellationToken cancellationToken);
}

/// <summary>Subtitle format of a Matroska track, as Jellyfin stores extracted subtitles.</summary>
internal enum SubtitleFormat
{
    Srt,
    Ass,
    Sup,
}

/// <summary>
/// Reads the subtitle tracks of a Matroska file without reading the file: header, track list and index
/// (Cues) first, then only the subtitle blocks the index points to.
/// <para>
/// Muxers (mkvmerge, ffmpeg) index every subtitle block, with its position inside its cluster. For a movie
/// that is a few thousand small reads (a few MB) instead of the whole file, which is what a subtitle
/// extraction with ffmpeg costs. Files whose index does not list the subtitle blocks are refused
/// (<see cref="NotSupportedException"/>).
/// </para>
/// </summary>
internal static class MatroskaSubtitles
{
    public const uint EbmlHeaderId = 0x1A45DFA3;
    public const uint DocTypeId = 0x4282;
    public const uint SegmentId = 0x18538067;
    public const uint SeekHeadId = 0x114D9B74;
    public const uint SeekId = 0x4DBB;
    public const uint SeekIdId = 0x53AB;
    public const uint SeekPositionId = 0x53AC;
    public const uint InfoId = 0x1549A966;
    public const uint TimestampScaleId = 0x2AD7B1;
    public const uint TracksId = 0x1654AE6B;
    public const uint TrackEntryId = 0xAE;
    public const uint TrackNumberId = 0xD7;
    public const uint TrackTypeId = 0x83;
    public const uint CodecIdId = 0x86;
    public const uint CodecPrivateId = 0x63A2;
    public const uint ContentEncodingsId = 0x6D80;
    public const uint ContentEncodingId = 0x6240;
    public const uint ContentEncodingOrderId = 0x5031;
    public const uint ContentEncodingScopeId = 0x5032;
    public const uint ContentEncodingTypeId = 0x5033;
    public const uint ContentCompressionId = 0x5034;
    public const uint ContentCompAlgoId = 0x4254;
    public const uint ContentCompSettingsId = 0x4255;
    public const uint CuesId = 0x1C53BB6B;
    public const uint CuePointId = 0xBB;
    public const uint CueTimeId = 0xB3;
    public const uint CueTrackPositionsId = 0xB7;
    public const uint CueTrackId = 0xF7;
    public const uint CueClusterPositionId = 0xF1;
    public const uint CueRelativePositionId = 0xF0;
    public const uint CueDurationId = 0xB2;
    public const uint ClusterId = 0x1F43B675;
    public const uint BlockGroupId = 0xA0;
    public const uint BlockId = 0xA1;
    public const uint BlockDurationId = 0x9B;
    public const uint SimpleBlockId = 0xA3;
    public const int SubtitleTrackType = 0x11;

    private const int HeadBytes = 64 * 1024;
    private const int MaxBlockBytes = 4 << 20;

    // A cue gives the position of a block relative to the start of its cluster's data: after the cluster ID
    // (4 bytes) and size (1-8 bytes). Some muxers count from the cluster ID itself.
    private static readonly int[] HeaderLengths = { 5, 6, 7, 8, 9, 10, 11, 12, 0, 1, 2, 3, 4 };
    private const int Slack = 12;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    // ---- Index --------------------------------------------------------------------------------

    /// <summary>Header, tracks and cues of a Matroska file; null when it is not Matroska or has no index.</summary>
    public static async Task<MatroskaIndex?> ReadIndexAsync(IRangeReader file, CancellationToken cancellationToken)
    {
        var head = await file.ReadAsync(0, (int)Math.Min(HeadBytes, file.Length), cancellationToken).ConfigureAwait(false);
        int p = 0;
        if (!Ebml.TryReadHeader(head, ref p, out var id, out var size) || id != EbmlHeaderId || size < 0 || size > head.Length - p)
        {
            return null;
        }

        string docType = "matroska";
        foreach (var e in Ebml.Children(head, p, p + (int)size))
        {
            if (e.Id == DocTypeId)
            {
                docType = Ebml.Ascii(head, e);
            }
        }

        if (docType is not ("matroska" or "webm"))
        {
            return null;
        }

        p += (int)size;
        if (!Ebml.TryReadHeader(head, ref p, out id, out _) || id != SegmentId)
        {
            return null;
        }

        long segment = p;

        // Top-level elements in the head of the file, then the seek heads' entries.
        var at = new Dictionary<uint, long>();
        var seekHeads = new Queue<long>();
        int q = p;
        while (true)
        {
            int start = q;
            if (!Ebml.TryReadHeader(head, ref q, out id, out size) || id == ClusterId)
            {
                break;
            }

            at.TryAdd(id, start);
            if (id == SeekHeadId)
            {
                seekHeads.Enqueue(start);
            }

            if (size < 0 || size > head.Length - q)
            {
                break;
            }

            q += (int)size;
        }

        var done = new HashSet<long>();
        while (seekHeads.Count > 0 && done.Count < 4)
        {
            var pos = seekHeads.Dequeue();
            if (!done.Add(pos))
            {
                continue;
            }

            var body = await ReadElementAsync(file, pos, SeekHeadId, 1 << 20, cancellationToken).ConfigureAwait(false);
            if (body is null)
            {
                continue;
            }

            foreach (var seek in Ebml.Children(body, 0, body.Length))
            {
                if (seek.Id != SeekId)
                {
                    continue;
                }

                uint target = 0;
                long? where = null;
                foreach (var f in Ebml.Children(body, seek.Start, seek.End))
                {
                    if (f.Id == SeekIdId)
                    {
                        target = (uint)Ebml.UInt(body, f);
                    }
                    else if (f.Id == SeekPositionId)
                    {
                        where = segment + Ebml.Long(body, f);
                    }
                }

                if (target != 0 && where is long w)
                {
                    at.TryAdd(target, w);
                    if (target == SeekHeadId)
                    {
                        seekHeads.Enqueue(w);
                    }
                }
            }
        }

        if (!at.TryGetValue(TracksId, out var tracksAt) || !at.TryGetValue(CuesId, out var cuesAt))
        {
            return null;
        }

        long scale = 1_000_000;
        if (at.TryGetValue(InfoId, out var infoAt)
            && await ReadElementAsync(file, infoAt, InfoId, 1 << 20, cancellationToken).ConfigureAwait(false) is byte[] info)
        {
            foreach (var e in Ebml.Children(info, 0, info.Length))
            {
                if (e.Id == TimestampScaleId && Ebml.Long(info, e) > 0)
                {
                    scale = Ebml.Long(info, e);
                }
            }
        }

        var tracksBody = await ReadElementAsync(file, tracksAt, TracksId, 32 << 20, cancellationToken).ConfigureAwait(false);
        var cuesBody = await ReadElementAsync(file, cuesAt, CuesId, 64 << 20, cancellationToken).ConfigureAwait(false);
        if (tracksBody is null || cuesBody is null)
        {
            return null;
        }

        return new MatroskaIndex(segment, scale, ParseTracks(tracksBody), ParseCues(cuesBody));
    }

    private static async Task<byte[]?> ReadElementAsync(IRangeReader file, long pos, uint expected, int maxSize, CancellationToken cancellationToken)
    {
        if (pos < 0 || pos >= file.Length)
        {
            return null;
        }

        var header = await file.ReadAsync(pos, (int)Math.Min(12, file.Length - pos), cancellationToken).ConfigureAwait(false);
        int p = 0;
        if (!Ebml.TryReadHeader(header, ref p, out var id, out var size) || id != expected || size < 0 || size > maxSize || pos + p + size > file.Length)
        {
            return null;
        }

        var body = await file.ReadAsync(pos + p, (int)size, cancellationToken).ConfigureAwait(false);
        return body.Length == size ? body : null;
    }

    internal static List<MatroskaTrack> ParseTracks(byte[] data)
    {
        var tracks = new List<MatroskaTrack>();
        foreach (var entry in Ebml.Children(data, 0, data.Length))
        {
            if (entry.Id != TrackEntryId)
            {
                continue;
            }

            var track = new MatroskaTrack { Order = tracks.Count };
            foreach (var f in Ebml.Children(data, entry.Start, entry.End))
            {
                switch (f.Id)
                {
                    case TrackNumberId: track.Number = Ebml.UInt(data, f); break;
                    case TrackTypeId: track.Type = (int)Ebml.UInt(data, f); break;
                    case CodecIdId: track.CodecId = Ebml.Ascii(data, f); break;
                    case CodecPrivateId: track.CodecPrivate = Ebml.Bytes(data, f); break;
                    case ContentEncodingsId: ParseEncodings(data, f, track.Encodings); break;
                }
            }

            tracks.Add(track);
        }

        return tracks;
    }

    private static void ParseEncodings(byte[] data, Ebml.Element encodings, List<ContentEncoding> list)
    {
        foreach (var enc in Ebml.Children(data, encodings.Start, encodings.End))
        {
            if (enc.Id != ContentEncodingId)
            {
                continue;
            }

            ulong order = 0, scope = 1, type = 0, algorithm = 0;
            byte[]? settings = null;
            foreach (var f in Ebml.Children(data, enc.Start, enc.End))
            {
                switch (f.Id)
                {
                    case ContentEncodingOrderId: order = Ebml.UInt(data, f); break;
                    case ContentEncodingScopeId: scope = Ebml.UInt(data, f); break;
                    case ContentEncodingTypeId: type = Ebml.UInt(data, f); break;
                    case ContentCompressionId:
                        foreach (var c in Ebml.Children(data, f.Start, f.End))
                        {
                            if (c.Id == ContentCompAlgoId)
                            {
                                algorithm = Ebml.UInt(data, c);
                            }
                            else if (c.Id == ContentCompSettingsId)
                            {
                                settings = Ebml.Bytes(data, c);
                            }
                        }

                        break;
                }
            }

            list.Add(new ContentEncoding(order, scope, type, algorithm, settings));
        }
    }

    internal static List<CueEntry> ParseCues(byte[] data)
    {
        var cues = new List<CueEntry>();
        foreach (var point in Ebml.Children(data, 0, data.Length))
        {
            if (point.Id != CuePointId)
            {
                continue;
            }

            long time = -1;
            foreach (var f in Ebml.Children(data, point.Start, point.End))
            {
                if (f.Id == CueTimeId)
                {
                    time = Ebml.Long(data, f);
                }
            }

            if (time < 0)
            {
                continue;
            }

            foreach (var f in Ebml.Children(data, point.Start, point.End))
            {
                if (f.Id != CueTrackPositionsId)
                {
                    continue;
                }

                ulong track = 0;
                long cluster = -1;
                long? relative = null, duration = null;
                foreach (var g in Ebml.Children(data, f.Start, f.End))
                {
                    switch (g.Id)
                    {
                        case CueTrackId: track = Ebml.UInt(data, g); break;
                        case CueClusterPositionId: cluster = Ebml.Long(data, g); break;
                        case CueRelativePositionId: relative = Ebml.Long(data, g); break;
                        case CueDurationId: duration = Ebml.Long(data, g); break;
                    }
                }

                if (track != 0 && cluster >= 0)
                {
                    cues.Add(new CueEntry(time, track, cluster, relative, duration));
                }
            }
        }

        return cues;
    }

    // ---- Blocks -------------------------------------------------------------------------------

    /// <summary>
    /// Reads the blocks of <paramref name="tracks"/> through the cues: nearby blocks share one read,
    /// <paramref name="parallel"/> reads at a time.
    /// </summary>
    /// <exception cref="NotSupportedException">The cues do not locate every block of these tracks, or a track is encoded in an unsupported way.</exception>
    /// <exception cref="InvalidDataException">A cue does not point to a block of its track.</exception>
    public static async Task<SubtitleBlocks> ReadBlocksAsync(
        IRangeReader file,
        MatroskaIndex index,
        IReadOnlyCollection<MatroskaTrack> tracks,
        int parallel,
        CancellationToken cancellationToken)
    {
        var byNumber = new Dictionary<ulong, MatroskaTrack>();
        foreach (var t in tracks)
        {
            if (t.Format is null || !CanDecode(t))
            {
                throw new NotSupportedException($"subtitle track {t.Number} ({t.CodecId}) cannot be read");
            }

            byNumber[t.Number] = t;
        }

        var targets = new Dictionary<long, Target>();
        var cued = new HashSet<ulong>();
        foreach (var cue in index.Cues)
        {
            if (!byNumber.TryGetValue(cue.Track, out var track))
            {
                continue;
            }

            if (cue.Relative is not long relative)
            {
                throw new NotSupportedException($"the cues of subtitle track {track.Number} have no block positions");
            }

            long at = index.SegmentStart + cue.Cluster + relative;
            if (at >= 0 && at < file.Length)
            {
                targets.TryAdd(at, new Target(at, track, cue.Time, cue.Duration));
                cued.Add(track.Number);
            }
        }

        foreach (var t in byNumber.Values)
        {
            if (!cued.Contains(t.Number))
            {
                throw new NotSupportedException($"subtitle track {t.Number} is not in the cues");
            }
        }

        // Nearby blocks (other subtitle tracks at the same moment) in one read.
        const int Gap = 16 * 1024;
        const int MaxRead = 1 << 20;
        var reads = new List<ReadRange>();
        foreach (var t in targets.Values.OrderBy(t => t.At))
        {
            long end = Math.Min(file.Length, t.At + Slack + (t.Track.Format == SubtitleFormat.Sup ? 32 * 1024 : 4 * 1024));
            var last = reads.Count > 0 ? reads[^1] : null;
            if (last is not null && t.At <= last.End + Gap && Math.Max(last.End, end) - last.Start <= MaxRead)
            {
                last.End = Math.Max(last.End, end);
                last.Targets.Add(t);
            }
            else
            {
                reads.Add(new ReadRange(t.At, end, t));
            }
        }

        var events = new List<(Target Target, SubtitleEvent Event)>();
        var partial = new List<(Target Target, long Start, byte[] Head, long End)>();
        long bytes = 0;
        int requests = 0;
        var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, parallel), CancellationToken = cancellationToken };
        await Parallel.ForEachAsync(reads, options, async (r, ct) =>
        {
            var buf = await file.ReadAsync(r.Start, (int)(r.End - r.Start), ct).ConfigureAwait(false);
            Interlocked.Add(ref bytes, buf.Length);
            Interlocked.Increment(ref requests);
            foreach (var t in r.Targets)
            {
                var found = Locate(buf, (int)(t.At - r.Start), r.Start, t);
                lock (events)
                {
                    if (found.Event is not null)
                    {
                        events.Add((t, found.Event));
                    }
                    else if (found.NeedEnd > 0)
                    {
                        partial.Add((t, found.NeedStart, buf.AsSpan((int)(found.NeedStart - r.Start)).ToArray(), found.NeedEnd));
                    }
                    else
                    {
                        throw new InvalidDataException($"no block of subtitle track {t.Track.Number} at {t.At}");
                    }
                }
            }
        }).ConfigureAwait(false);

        // Blocks larger than the first read (pictures of PGS subtitles): the rest of them.
        await Parallel.ForEachAsync(partial, options, async (p, ct) =>
        {
            long from = p.Start + p.Head.Length;
            var tail = await file.ReadAsync(from, (int)(p.End - from), ct).ConfigureAwait(false);
            Interlocked.Add(ref bytes, tail.Length);
            Interlocked.Increment(ref requests);
            byte[] buf = [.. p.Head, .. tail];
            var ev = TryBlockAt(buf, 0, p.Start, p.Target).Event
                ?? throw new InvalidDataException($"no block of subtitle track {p.Target.Track.Number} at {p.Start}");
            lock (events)
            {
                events.Add((p.Target, ev));
            }
        }).ConfigureAwait(false);

        var result = new Dictionary<ulong, List<SubtitleEvent>>();
        foreach (var t in byNumber.Values)
        {
            result[t.Number] = new List<SubtitleEvent>();
        }

        foreach (var (target, ev) in events.OrderBy(e => e.Event.Time).ThenBy(e => e.Event.Position))
        {
            result[target.Track.Number].Add(ev);
        }

        return new SubtitleBlocks(result, requests, bytes);
    }

    private static Located Locate(byte[] buf, int offset, long bufStart, Target t)
    {
        Located partial = default;
        foreach (var h in HeaderLengths)
        {
            var found = TryBlockAt(buf, offset + h, bufStart, t);
            if (found.Event is not null)
            {
                return found;
            }

            if (found.NeedEnd > 0 && partial.NeedEnd == 0)
            {
                partial = found;
            }
        }

        return partial;
    }

    /// <summary>The block of <paramref name="t"/> at <paramref name="x"/>, checked down to its content.</summary>
    private static Located TryBlockAt(byte[] buf, int x, long bufStart, Target t)
    {
        int p = x;
        if (x >= buf.Length || !Ebml.TryReadHeader(buf, ref p, out var id, out var size) || size < 4 || size > MaxBlockBytes)
        {
            return default;
        }

        long end = p + size;
        var whole = new Located(null, bufStart + x, bufStart + end);
        if (id == SimpleBlockId)
        {
            if (!CheckBlockHeader(buf, p, t.Track.Number, out int frame))
            {
                return default;
            }

            return end > buf.Length ? whole : Decode(t, buf, frame, (int)end, t.CueDuration);
        }

        if (id != BlockGroupId)
        {
            return default;
        }

        int limit = (int)Math.Min(end, buf.Length);
        int q = p;
        int frameStart = -1, frameEnd = -1;
        long? duration = null;
        bool first = true;
        while (q < limit)
        {
            int c = q;
            if (!Ebml.TryReadHeader(buf.AsSpan(0, limit), ref c, out var cid, out var csize) || csize < 0)
            {
                if (first)
                {
                    return default;
                }

                break;
            }

            first = false;
            switch (cid)
            {
                case BlockId:
                    if (!CheckBlockHeader(buf, c, t.Track.Number, out frameStart))
                    {
                        return default;
                    }

                    frameEnd = (int)Math.Min(c + csize, int.MaxValue);
                    break;
                case BlockDurationId when c + csize <= limit:
                    duration = (long)Math.Min(Ebml.UInt(buf, new Ebml.Element(cid, c, (int)csize)), long.MaxValue);
                    break;
                case BlockDurationId:
                case 0xFB: // ReferenceBlock
                case 0x75A1: // BlockAdditions
                case 0xA2: // BlockVirtual
                case 0xA4: // CodecState
                case 0x75A2: // DiscardPadding
                case 0xC8: // Slices
                case 0xEC: // Void
                    break;
                default:
                    return default;
            }

            q = (int)Math.Min(c + csize, int.MaxValue);
        }

        if (frameStart < 0)
        {
            return default;
        }

        if (end > buf.Length)
        {
            return whole;
        }

        return frameEnd <= end ? Decode(t, buf, frameStart, frameEnd, duration ?? t.CueDuration) : default;
    }

    private static bool CheckBlockHeader(byte[] buf, int pos, ulong track, out int frame)
    {
        frame = 0;
        int p = pos;
        if (!Ebml.TryReadVint(buf, ref p, out var number) || number != (long)track || p + 3 > buf.Length)
        {
            return false;
        }

        // Laced blocks hold several frames: never used for subtitles.
        if ((buf[p + 2] & 0x06) != 0)
        {
            return false;
        }

        frame = p + 3;
        return true;
    }

    private static Located Decode(Target t, byte[] buf, int start, int end, long? duration)
    {
        if (end <= start)
        {
            return default;
        }

        byte[] payload;
        try
        {
            payload = DecodeContent(buf.AsSpan(start, end - start).ToArray(), t.Track.Encodings, 1);
        }
        catch (InvalidDataException)
        {
            return default;
        }

        return IsValid(t.Track.Format!.Value, payload)
            ? new Located(new SubtitleEvent(t.Time, duration, payload, t.At), 0, 0)
            : default;
    }

    private static bool IsValid(SubtitleFormat format, byte[] payload)
    {
        if (format == SubtitleFormat.Sup)
        {
            int p = 0, segments = 0;
            while (payload.Length - p > 2)
            {
                byte type = payload[p];
                int len = ((payload[p + 1] << 8) | payload[p + 2]) + 3;
                if (type is not (0x14 or 0x15 or 0x16 or 0x17 or 0x80) || len > payload.Length - p)
                {
                    return false;
                }

                p += len;
                segments++;
            }

            return segments > 0;
        }

        try
        {
            var text = StrictUtf8.GetString(payload).TrimEnd('\0');
            if (text.Contains('\0', StringComparison.Ordinal))
            {
                return false;
            }

            return format != SubtitleFormat.Ass || text.Count(ch => ch == ',') >= 8;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    // ---- Content encodings (compression) --------------------------------------------------------

    /// <summary>zlib and header stripping are supported; encryption and the other compressions are not.</summary>
    public static bool CanDecode(MatroskaTrack track) => track.Encodings.All(e => e.Type == 0 && e.Algorithm is 0 or 3);

    /// <summary>Undoes the encodings of <paramref name="scope"/> (1: blocks, 2: codec private data), last applied first.</summary>
    public static byte[] DecodeContent(byte[] data, IReadOnlyList<ContentEncoding> encodings, ulong scope)
    {
        foreach (var e in encodings.Where(e => (e.Scope & scope) != 0).OrderByDescending(e => e.Order))
        {
            data = e.Algorithm switch
            {
                0 => Inflate(data),
                3 => e.Settings is { Length: > 0 } s ? [.. s, .. data] : data,
                _ => throw new NotSupportedException($"content compression {e.Algorithm}"),
            };
        }

        return data;
    }

    private static byte[] Inflate(byte[] data)
    {
        try
        {
            using var input = new MemoryStream(data);
            using var z = new ZLibStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            var chunk = new byte[16 * 1024];
            int n;
            while ((n = z.Read(chunk, 0, chunk.Length)) > 0)
            {
                output.Write(chunk, 0, n);
                if (output.Length > MaxBlockBytes * 4L)
                {
                    throw new InvalidDataException("block too large");
                }
            }

            return output.ToArray();
        }
        catch (Exception e) when (e is not InvalidDataException)
        {
            throw new InvalidDataException("bad zlib data", e);
        }
    }

    private sealed record Target(long At, MatroskaTrack Track, long Time, long? CueDuration);

    private sealed class ReadRange
    {
        public ReadRange(long start, long end, Target first)
        {
            Start = start;
            End = end;
            Targets.Add(first);
        }

        public long Start { get; }

        public long End { get; set; }

        public List<Target> Targets { get; } = new();
    }

    /// <summary>A decoded block, or the bytes still needed to read it whole.</summary>
    private readonly record struct Located(SubtitleEvent? Event, long NeedStart, long NeedEnd);
}

/// <summary>What the subtitle extraction needs from a Matroska file.</summary>
internal sealed record MatroskaIndex(long SegmentStart, long TimestampScale, List<MatroskaTrack> Tracks, List<CueEntry> Cues);

internal sealed class MatroskaTrack
{
    /// <summary>Gets or sets the position in the track list (ffprobe numbers streams in this order).</summary>
    public int Order { get; set; }

    public ulong Number { get; set; }

    public int Type { get; set; }

    public string CodecId { get; set; } = string.Empty;

    public byte[]? CodecPrivate { get; set; }

    public List<ContentEncoding> Encodings { get; } = new();

    public bool IsSubtitle => Type == MatroskaSubtitles.SubtitleTrackType;

    public SubtitleFormat? Format => CodecId switch
    {
        "S_TEXT/UTF8" or "S_TEXT/ASCII" or "S_TEXT/WEBVTT" => SubtitleFormat.Srt,
        "S_TEXT/ASS" or "S_TEXT/SSA" or "S_ASS" or "S_SSA" => SubtitleFormat.Ass,
        "S_HDMV/PGS" => SubtitleFormat.Sup,
        _ => null,
    };
}

internal readonly record struct ContentEncoding(ulong Order, ulong Scope, ulong Type, ulong Algorithm, byte[]? Settings);

/// <summary>A cue: time and position of a block (relative position and duration when the muxer wrote them).</summary>
internal readonly record struct CueEntry(long Time, ulong Track, long Cluster, long? Relative, long? Duration);

/// <summary>A subtitle block: time and duration in segment ticks, decoded content, file position.</summary>
internal sealed record SubtitleEvent(long Time, long? Duration, byte[] Payload, long Position);

internal sealed record SubtitleBlocks(Dictionary<ulong, List<SubtitleEvent>> Events, int Requests, long Bytes);
