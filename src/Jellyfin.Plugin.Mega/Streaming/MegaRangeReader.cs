using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Mega.Mega;
using Jellyfin.Plugin.Mega.Subtitles;

namespace Jellyfin.Plugin.Mega.Streaming;

/// <summary>
/// Small reads anywhere in a MEGA file, one request each, decrypted (AES-CTR works at any offset).
/// Unlike <see cref="MegaFileReader"/>, nothing is read beyond what is asked: for scattered reads
/// (subtitle blocks) that would cost 1 MiB each.
/// </summary>
internal sealed class MegaRangeReader : IRangeReader
{
    private readonly MegaNode _node;
    private readonly MegaDownloadLink _link;

    public MegaRangeReader(MegaSource source, MegaNode node)
    {
        _node = node;
        _link = new MegaDownloadLink(source, node.Handle);
    }

    public long Length => _node.Size;

    public async Task<byte[]> ReadAsync(long offset, int count, CancellationToken cancellationToken)
    {
        count = (int)Math.Clamp(Length - offset, 0, count);
        if (count == 0)
        {
            return Array.Empty<byte>();
        }

        for (int attempt = 0; ; attempt++)
        {
            try
            {
                using var resp = await _link.OpenRangeAsync(offset, offset + count, cancellationToken).ConfigureAwait(false);
                await using var body = await resp.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                var data = new byte[count];
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    timeout.CancelAfter(TimeSpan.FromSeconds(60));
                    await body.ReadExactlyAsync(data, timeout.Token).ConfigureAwait(false);
                }

                using var aes = MegaCrypto.CreateAes(_node.Key!.AesKey);
                MegaCrypto.CtrTransform(aes, _node.Key.Nonce, offset, data);
                return data;
            }
            catch (Exception e) when (attempt < 3 && !cancellationToken.IsCancellationRequested
                && e is HttpRequestException or IOException or OperationCanceledException)
            {
                await Task.Delay(250 << attempt, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}

/// <summary>Keeps what was read and reads at least <c>minimum</c> bytes at a time (file header, index).</summary>
internal sealed class BufferedRangeReader : IRangeReader
{
    private readonly IRangeReader _inner;
    private readonly int _minimum;
    private readonly List<(long Offset, byte[] Data)> _buffers = new();

    public BufferedRangeReader(IRangeReader inner, int minimum = 64 * 1024)
    {
        _inner = inner;
        _minimum = minimum;
    }

    public long Length => _inner.Length;

    public async Task<byte[]> ReadAsync(long offset, int count, CancellationToken cancellationToken)
    {
        count = (int)Math.Clamp(Length - offset, 0, count);
        foreach (var (start, data) in _buffers)
        {
            if (offset >= start && offset + count <= start + data.Length)
            {
                return data.AsSpan((int)(offset - start), count).ToArray();
            }
        }

        var read = await _inner.ReadAsync(offset, Math.Max(count, _minimum), cancellationToken).ConfigureAwait(false);
        _buffers.Add((offset, read));
        return read.Length > count ? read.AsSpan(0, count).ToArray() : read;
    }
}
