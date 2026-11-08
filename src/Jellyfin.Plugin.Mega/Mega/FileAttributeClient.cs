using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Mega.Mega;

/// <summary>
/// MEGA file attributes: thumbnail (type 0, 120 px) and preview (type 1, up to 1000 px), AES-CBC encrypted.
/// Fetched in batches: one request per attribute server for all the handles it hosts.
/// </summary>
internal sealed class FileAttributeClient
{
    private const int BatchSize = 48;
    private readonly MegaApiClient _api;
    private readonly ConcurrentDictionary<uint, (string Url, DateTime At)> _urls = new();
    private readonly ConcurrentDictionary<uint, bool> _plainHttp = new();

    public FileAttributeClient(MegaApiClient api)
    {
        _api = api;
    }

    /// <summary>Fetches attribute <paramref name="type"/> of several files. Missing / failed ones are absent from the result.</summary>
    public async Task<Dictionary<string, byte[]>> FetchAsync(IReadOnlyList<MegaNode> nodes, int type, CancellationToken cancellationToken)
    {
        var groups = new Dictionary<uint, List<(MegaNode Node, string Fah)>>();
        foreach (var n in nodes)
        {
            if (n.Key is not null && n.TryGetAttribute(type, out var bundle, out var fah))
            {
                if (!groups.TryGetValue(bundle, out var list))
                {
                    list = new List<(MegaNode, string)>();
                    groups[bundle] = list;
                }

                list.Add((n, fah));
            }
        }

        var batches = groups.SelectMany(g => g.Value.Chunk(BatchSize).Select(c => (Bundle: g.Key, Items: c))).ToList();
        var result = new ConcurrentDictionary<string, byte[]>(StringComparer.Ordinal);
        await Parallel.ForEachAsync(
            batches,
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken },
            async (batch, ct) =>
            {
                var got = await FetchBatchAsync(batch.Bundle, batch.Items, type, ct).ConfigureAwait(false);
                foreach (var kv in got)
                {
                    result[kv.Key] = kv.Value;
                }
            }).ConfigureAwait(false);
        return new Dictionary<string, byte[]>(result, StringComparer.Ordinal);
    }

    private async Task<string> GetServerUrlAsync(uint bundle, string fah, CancellationToken cancellationToken)
    {
        if (_urls.TryGetValue(bundle, out var cached) && DateTime.UtcNow - cached.At < TimeSpan.FromMinutes(10))
        {
            return cached.Url;
        }

        int ssl = _plainHttp.ContainsKey(bundle) ? 0 : 2;
        var r = await _api.RequestAsync(new { a = "ufa", fah, ssl, r = 1 }, cancellationToken).ConfigureAwait(false);
        var url = r.ValueKind == JsonValueKind.Object && r.TryGetProperty("p", out var p) ? p.GetString() : null;
        if (string.IsNullOrEmpty(url))
        {
            throw new MegaException("no file attribute server");
        }

        _urls[bundle] = (url, DateTime.UtcNow);
        return url;
    }

    private async Task<Dictionary<string, byte[]>> FetchBatchAsync(uint bundle, (MegaNode Node, string Fah)[] items, int type, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var raw = new List<byte[]>();
        var byHandle = new Dictionary<string, List<MegaNode>>(StringComparer.Ordinal);
        foreach (var (node, fah) in items)
        {
            var h = MegaCrypto.B64Decode(fah);
            if (h is null || h.Length != 8)
            {
                continue;
            }

            if (!byHandle.TryGetValue(fah, out var list))
            {
                list = new List<MegaNode>();
                byHandle[fah] = list;
                raw.Add(h);
            }

            list.Add(node);
        }

        if (raw.Count == 0)
        {
            return result;
        }

        var body = raw.SelectMany(b => b).ToArray();
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var url = await GetServerUrlAsync(bundle, items[0].Fah, cancellationToken).ConfigureAwait(false);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(60));
                using var req = new HttpRequestMessage(HttpMethod.Post, url + "/" + type) { Content = new ByteArrayContent(body) };
                using var resp = await MegaHttp.Storage.SendAsync(req, timeout.Token).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    throw new MegaException($"HTTP {(int)resp.StatusCode} from the attribute server") { HttpStatus = (int)resp.StatusCode };
                }

                var data = await resp.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
                int pos = 0;
                while (pos + 12 <= data.Length)
                {
                    var fah = MegaCrypto.B64Encode(data.AsSpan(pos, 8));
                    int len = BitConverter.ToInt32(data, pos + 8);
                    pos += 12;
                    if (len < 0 || pos + len > data.Length)
                    {
                        break;
                    }

                    if (byHandle.TryGetValue(fah, out var nodesForHandle))
                    {
                        foreach (var node in nodesForHandle)
                        {
                            var image = Decrypt(data.AsSpan(pos, len), node.Key!.AesKey);
                            if (image is not null)
                            {
                                result[node.Handle] = image;
                            }
                        }
                    }

                    pos += len;
                }

                return result;
            }
            catch (Exception e) when (e is HttpRequestException or MegaException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                _urls.TryRemove(bundle, out _);
                if (MegaHttp.IsTlsRefusal(e))
                {
                    _plainHttp[bundle] = true;
                    continue;
                }

                await Task.Delay(300 * (attempt + 1), cancellationToken).ConfigureAwait(false);
            }
        }

        return result;
    }

    /// <summary>Decrypts an attribute; data that is not an image (damaged attribute, wrong key) counts as missing.</summary>
    internal static byte[]? Decrypt(ReadOnlySpan<byte> encrypted, byte[] key)
    {
        int len = encrypted.Length / 16 * 16;
        if (len == 0)
        {
            return null;
        }

        var data = encrypted[..len].ToArray();
        MegaCrypto.CbcDecrypt(key, data);
        bool isImage = (data[0] == 0xFF && data[1] == 0xD8)
            || (data.Length > 4 && data[0] == 0x89 && data[1] == (byte)'P' && data[2] == (byte)'N' && data[3] == (byte)'G')
            || (data.Length > 4 && data[0] == (byte)'G' && data[1] == (byte)'I' && data[2] == (byte)'F')
            || (data.Length > 12 && data[0] == (byte)'R' && data[1] == (byte)'I' && data[8] == (byte)'W' && data[9] == (byte)'E');
        return isImage ? data : null;
    }
}
