using System;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.Mega.Mega;

namespace Jellyfin.Plugin.Mega.Streaming;

/// <summary>
/// Stream URLs written in the <c>.strm</c> files:
/// <c>{base}/MegaFs/stream/{source}/{handle}/{signature}/{file name}</c>.
/// They carry no key: the endpoint finds the file key in the decrypted tree. The signature
/// (HMAC of the source and handle) stops anyone from requesting arbitrary handles.
/// </summary>
internal static class StreamUrls
{
    public const string Marker = "/MegaFs/stream/";

    public static string Sign(string secret, string sourceId, string handle)
    {
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(sourceId + "/" + handle));
        return MegaCrypto.B64Encode(mac.AsSpan(0, 15));
    }

    public static bool Verify(string secret, string sourceId, string handle, string signature)
    {
        var expected = Encoding.ASCII.GetBytes(Sign(secret, sourceId, handle));
        var given = Encoding.ASCII.GetBytes(signature ?? string.Empty);
        return CryptographicOperations.FixedTimeEquals(expected, given);
    }

    public static string Build(string baseUrl, string secret, string sourceId, MegaNode node)
    {
        var name = node.Name.Replace('/', '_').Replace('\\', '_');
        return baseUrl.TrimEnd('/') + Marker + sourceId + "/" + node.Handle + "/" + Sign(secret, sourceId, node.Handle) + "/" + Uri.EscapeDataString(name);
    }

    /// <summary>Extracts the source and handle from a stream URL (shortcut path of an item).</summary>
    public static bool TryParse(string? url, out string sourceId, out string handle)
    {
        sourceId = string.Empty;
        handle = string.Empty;
        if (string.IsNullOrEmpty(url))
        {
            return false;
        }

        int i = url.IndexOf(Marker, StringComparison.OrdinalIgnoreCase);
        if (i < 0)
        {
            return false;
        }

        var parts = url[(i + Marker.Length)..].Split('/');
        if (parts.Length < 3 || parts[0].Length == 0 || parts[1].Length == 0)
        {
            return false;
        }

        sourceId = parts[0];
        handle = parts[1];
        return true;
    }
}
