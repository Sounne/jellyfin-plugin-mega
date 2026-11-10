using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;

namespace Jellyfin.Plugin.Mega.Subtitles;

/// <summary>EBML (Matroska) primitives: element IDs, sizes and values.</summary>
internal static class Ebml
{
    /// <summary>Size of an element whose size is not written (live streams, some clusters).</summary>
    public const long UnknownSize = -1;

    /// <summary>Reads an element ID, marker bits kept (as IDs are written in the specification).</summary>
    public static bool TryReadId(ReadOnlySpan<byte> data, ref int pos, out uint id)
    {
        id = 0;
        if (pos < 0 || pos >= data.Length || data[pos] < 0x10)
        {
            return false;
        }

        int len = BitOperations.LeadingZeroCount((uint)data[pos]) - 23;
        if (pos + len > data.Length)
        {
            return false;
        }

        for (int i = 0; i < len; i++)
        {
            id = (id << 8) | data[pos + i];
        }

        pos += len;
        return true;
    }

    /// <summary>Reads a variable-size integer (element size, track number in a block); all bits set = <see cref="UnknownSize"/>.</summary>
    public static bool TryReadVint(ReadOnlySpan<byte> data, ref int pos, out long value)
    {
        value = 0;
        if (pos < 0 || pos >= data.Length || data[pos] == 0)
        {
            return false;
        }

        int len = BitOperations.LeadingZeroCount((uint)data[pos]) - 23;
        if (pos + len > data.Length)
        {
            return false;
        }

        ulong v = (ulong)(data[pos] & (0xFF >> len));
        bool allOnes = v == (ulong)(0xFF >> len);
        for (int i = 1; i < len; i++)
        {
            v = (v << 8) | data[pos + i];
            allOnes &= data[pos + i] == 0xFF;
        }

        pos += len;
        value = allOnes ? UnknownSize : (long)v;
        return true;
    }

    /// <summary>Reads an element header: ID and size.</summary>
    public static bool TryReadHeader(ReadOnlySpan<byte> data, ref int pos, out uint id, out long size)
    {
        size = 0;
        int p = pos;
        if (!TryReadId(data, ref p, out id) || !TryReadVint(data, ref p, out size))
        {
            return false;
        }

        pos = p;
        return true;
    }

    /// <summary>Child elements of a master element whose data is <paramref name="data"/>[start..end). Stops at the first truncated one.</summary>
    public static IEnumerable<Element> Children(byte[] data, int start, int end)
    {
        int pos = start;
        end = Math.Min(end, data.Length);
        while (pos < end)
        {
            int p = pos;
            if (!TryReadHeader(data.AsSpan(0, end), ref p, out var id, out var size) || size < 0 || size > end - p)
            {
                yield break;
            }

            yield return new Element(id, p, (int)size);
            pos = p + (int)size;
        }
    }

    public static ulong UInt(byte[] data, Element e)
    {
        ulong v = 0;
        for (int i = 0; i < Math.Min(e.Size, 8); i++)
        {
            v = (v << 8) | data[e.Start + i];
        }

        return v;
    }

    public static long Long(byte[] data, Element e) => (long)Math.Min(UInt(data, e), long.MaxValue);

    public static string Ascii(byte[] data, Element e) => Encoding.ASCII.GetString(data, e.Start, e.Size).TrimEnd('\0');

    public static byte[] Bytes(byte[] data, Element e) => data.AsSpan(e.Start, e.Size).ToArray();

    /// <summary>An element inside a buffer: its ID, where its data starts and its data size.</summary>
    public readonly record struct Element(uint Id, int Start, int Size)
    {
        public int End => Start + Size;
    }
}
