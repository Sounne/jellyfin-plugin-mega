using System;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Mega.Mega;

public enum MegaNodeType
{
    File = 0,
    Folder = 1,
    Root = 2,
    Inbox = 3,
    Rubbish = 4,
}

/// <summary>Decrypted MEGA node.</summary>
internal sealed class MegaNode
{
    public required string Handle { get; init; }

    public string? Parent { get; init; }

    public MegaNodeType Type { get; init; }

    public string Name { get; init; } = string.Empty;

    public long Size { get; init; }

    /// <summary>Gets the creation time on MEGA (unix seconds).</summary>
    public long Timestamp { get; init; }

    /// <summary>Gets or sets the modification time of the original file (unix seconds, from the fingerprint).</summary>
    public long? ModifiedTime { get; init; }

    public FileKey? Key { get; init; }

    /// <summary>Gets the folder key (folders only: decrypts the attributes after a rename).</summary>
    public byte[]? FolderKey { get; init; }

    /// <summary>Gets the raw <c>fa</c> string (thumbnail / preview references).</summary>
    public string? FileAttributes { get; init; }

    public bool IsFile => Type == MegaNodeType.File;

    /// <summary>Copy with some fields changed (nodes are shared between tree snapshots, never mutated).</summary>
    public MegaNode With(string? name = null, long? modifiedTime = null, string? fileAttributes = null) => new()
    {
        Handle = Handle,
        Parent = Parent,
        Type = Type,
        Name = name ?? Name,
        Size = Size,
        Timestamp = Timestamp,
        ModifiedTime = modifiedTime ?? ModifiedTime,
        Key = Key,
        FolderKey = FolderKey,
        FileAttributes = fileAttributes ?? FileAttributes,
    };

    public DateTime Modified => DateTimeOffset.FromUnixTimeSeconds(ModifiedTime is > 0 ? ModifiedTime.Value : Timestamp).UtcDateTime;

    /// <summary>
    /// File attribute <paramref name="type"/> (0 = 120 px thumbnail, 1 = preview up to 1000 px):
    /// server bundle and 8-byte handle (base64). Format: <c>924:0*abcdefghijk/924:1*lmnopqrstuv</c>.
    /// </summary>
    public bool TryGetAttribute(int type, out uint bundle, out string handle)
    {
        bundle = 0;
        handle = string.Empty;
        if (string.IsNullOrEmpty(FileAttributes))
        {
            return false;
        }

        foreach (var part in FileAttributes.Split('/'))
        {
            int colon = part.IndexOf(':', StringComparison.Ordinal);
            int star = part.IndexOf('*', StringComparison.Ordinal);
            if (colon <= 0 || star <= colon)
            {
                continue;
            }

            if (!int.TryParse(part.AsSpan(colon + 1, star - colon - 1), out var t) || t != type)
            {
                continue;
            }

            _ = uint.TryParse(part.AsSpan(0, colon), out bundle);
            handle = part[(star + 1)..];
            return handle.Length > 0;
        }

        return false;
    }

    /// <summary>Modification time from the MEGA fingerprint (<c>c</c> attribute): CRC (16 bytes), count, LE time.</summary>
    public static long? MtimeFromFingerprint(string? c)
    {
        var fp = MegaCrypto.B64Decode(c);
        if (fp is null || fp.Length < 18)
        {
            return null;
        }

        int count = fp[16];
        if (count == 0 || count > 8 || 17 + count > fp.Length)
        {
            return null;
        }

        long v = 0;
        for (int i = 0; i < count; i++)
        {
            v |= (long)fp[17 + i] << (8 * i);
        }

        return v;
    }
}

/// <summary>Node as received from the API.</summary>
internal sealed class RawNode
{
    [JsonPropertyName("h")]
    public string H { get; set; } = string.Empty;

    [JsonPropertyName("p")]
    public string? P { get; set; }

    [JsonPropertyName("t")]
    public int T { get; set; }

    [JsonPropertyName("a")]
    public string? A { get; set; }

    [JsonPropertyName("k")]
    public string? K { get; set; }

    [JsonPropertyName("s")]
    public long? S { get; set; }

    [JsonPropertyName("ts")]
    public long? Ts { get; set; }

    [JsonPropertyName("fa")]
    public string? Fa { get; set; }

    [JsonPropertyName("su")]
    public string? Su { get; set; }

    [JsonPropertyName("sk")]
    public string? Sk { get; set; }
}

/// <summary>Outgoing share key (<c>ok</c>).</summary>
internal sealed class RawShareKey
{
    [JsonPropertyName("h")]
    public string H { get; set; } = string.Empty;

    [JsonPropertyName("k")]
    public string K { get; set; } = string.Empty;
}
