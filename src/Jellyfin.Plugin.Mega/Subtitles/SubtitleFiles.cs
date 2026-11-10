using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Jellyfin.Plugin.Mega.Subtitles;

/// <summary>
/// Subtitle files as ffmpeg writes them when Jellyfin extracts embedded subtitles
/// (<c>-c:s srt</c> for text, <c>-c:s copy</c> to .ass and .sup).
/// </summary>
internal static class SubtitleFiles
{
    private const string AssEventsFormat = "Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text";

    /// <summary>File content for <paramref name="track"/>, times in ticks of <paramref name="timestampScale"/> nanoseconds.</summary>
    public static byte[] Write(MatroskaTrack track, IReadOnlyList<SubtitleEvent> events, long timestampScale) => track.Format switch
    {
        SubtitleFormat.Srt => Srt(events, timestampScale),
        SubtitleFormat.Ass => Ass(track, events, timestampScale),
        SubtitleFormat.Sup => Sup(events, timestampScale),
        _ => throw new NotSupportedException(track.CodecId),
    };

    public static byte[] Srt(IReadOnlyList<SubtitleEvent> events, long timestampScale)
    {
        var sb = new StringBuilder();
        int n = 0;
        for (int i = 0; i < events.Count; i++)
        {
            var lines = Text(events[i].Payload)
                .Split('\n')
                .Select(l => l.TrimEnd())
                .Where(l => l.Length > 0)
                .ToList();
            if (lines.Count == 0)
            {
                continue;
            }

            var (start, end) = Times(events, i, timestampScale);
            sb.Append(++n).Append('\n')
                .Append(SrtTime(start)).Append(" --> ").Append(SrtTime(end)).Append('\n');
            foreach (var line in lines)
            {
                sb.Append(line).Append('\n');
            }

            sb.Append('\n');
        }

        if (sb.Length == 0)
        {
            sb.Append('\n');
        }

        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    public static byte[] Ass(MatroskaTrack track, IReadOnlyList<SubtitleEvent> events, long timestampScale)
    {
        var header = track.CodecPrivate is { Length: > 0 } cp
            ? Text(MatroskaSubtitles.DecodeContent(cp, track.Encodings, 2))
            : "[Script Info]\nScriptType: v4.00+\n";
        header = header.TrimStart('﻿').TrimEnd('\n', '\r', ' ', '\0') + "\n";
        int events0 = header.IndexOf("[Events]", StringComparison.OrdinalIgnoreCase);
        if (events0 < 0)
        {
            header += "\n[Events]\n" + AssEventsFormat + "\n";
        }
        else if (header.IndexOf("Format:", events0, StringComparison.OrdinalIgnoreCase) < 0)
        {
            header += AssEventsFormat + "\n";
        }

        // Block content: ReadOrder, Layer, Style, Name, MarginL, MarginR, MarginV, Effect, Text.
        var lines = new List<(long ReadOrder, int Index, string Line)>();
        bool ordered = true;
        for (int i = 0; i < events.Count; i++)
        {
            var text = Text(events[i].Payload).TrimEnd('\n', '\r');
            int c1 = text.IndexOf(',', StringComparison.Ordinal);
            if (c1 < 0)
            {
                continue;
            }

            ordered &= long.TryParse(text.AsSpan(0, c1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var readOrder);
            int c2 = text.IndexOf(',', c1 + 1);
            if (c2 < 0)
            {
                continue;
            }

            var (start, end) = Times(events, i, timestampScale);
            var layer = text.Substring(c1 + 1, c2 - c1 - 1);
            var rest = text[(c2 + 1)..].Replace("\r\n", "\\N", StringComparison.Ordinal).Replace("\n", "\\N", StringComparison.Ordinal);
            lines.Add((readOrder, i, $"Dialogue: {layer},{AssTime(start)},{AssTime(end)},{rest}"));
        }

        // Original script order (renderers stack lines of one layer in that order).
        var sorted = ordered ? lines.OrderBy(l => l.ReadOrder).ThenBy(l => l.Index) : lines.OrderBy(l => l.Index);
        var sb = new StringBuilder(header);
        foreach (var l in sorted)
        {
            sb.Append(l.Line).Append('\n');
        }

        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    /// <summary>PGS (.sup): each segment with "PG", presentation and decoding times (90 kHz).</summary>
    public static byte[] Sup(IReadOnlyList<SubtitleEvent> events, long timestampScale)
    {
        using var output = new MemoryStream();
        Span<byte> header = stackalloc byte[10];
        foreach (var ev in events)
        {
            uint pts = unchecked((uint)(Nanoseconds(ev.Time, timestampScale) * 9 / 100_000));
            header[0] = (byte)'P';
            header[1] = (byte)'G';
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(header[2..], pts);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(header[6..], pts);
            var data = ev.Payload;
            int p = 0;
            while (data.Length - p > 2)
            {
                int len = ((data[p + 1] << 8) | data[p + 2]) + 3;
                if (len > data.Length - p)
                {
                    break;
                }

                output.Write(header);
                output.Write(data, p, len);
                p += len;
            }
        }

        return output.ToArray();
    }

    private static string Text(byte[] payload)
        => Encoding.UTF8.GetString(payload).TrimEnd('\0').Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private static long Nanoseconds(long ticks, long timestampScale) => ticks * timestampScale;

    /// <summary>Start and end in nanoseconds; a block without duration lasts until the next one (5 s at most).</summary>
    private static (long Start, long End) Times(IReadOnlyList<SubtitleEvent> events, int i, long timestampScale)
    {
        long start = Nanoseconds(events[i].Time, timestampScale);
        if (events[i].Duration is long d && d > 0)
        {
            return (start, start + Nanoseconds(d, timestampScale));
        }

        long end = start + 5_000_000_000L;
        for (int j = i + 1; j < events.Count; j++)
        {
            long next = Nanoseconds(events[j].Time, timestampScale);
            if (next > start)
            {
                end = Math.Min(end, next);
                break;
            }
        }

        return (start, end);
    }

    private static string SrtTime(long ns)
    {
        long ms = (ns + 500_000) / 1_000_000;
        return string.Create(CultureInfo.InvariantCulture, $"{ms / 3_600_000:00}:{ms / 60_000 % 60:00}:{ms / 1000 % 60:00},{ms % 1000:000}");
    }

    private static string AssTime(long ns)
    {
        long cs = ns / 10_000_000;
        return string.Create(CultureInfo.InvariantCulture, $"{cs / 360_000}:{cs / 6000 % 60:00}:{cs / 100 % 60:00}.{cs % 100:00}");
    }
}
