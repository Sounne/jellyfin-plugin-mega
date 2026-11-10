using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Mega.Mega;

namespace Jellyfin.Plugin.Mega.Streaming;

/// <summary>
/// Byte ranges of a MEGA file (still encrypted) from its storage server: one download URL shared by
/// every connection, renewed when it expires, plain HTTP when TLS is refused.
/// </summary>
internal sealed class MegaDownloadLink
{
    private readonly MegaSource _source;
    private readonly string _handle;
    private string? _url;
    private volatile bool _plainHttp;

    public MegaDownloadLink(MegaSource source, string handle)
    {
        _source = source;
        _handle = handle;
    }

    /// <summary>Opens bytes [<paramref name="from"/>, <paramref name="to"/>) (response headers read, body streamed).</summary>
    public async Task<HttpResponseMessage> OpenRangeAsync(long from, long to, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            var url = Volatile.Read(ref _url);
            if (url is null)
            {
                url = await _source.GetDownloadUrlAsync(_handle, false, _plainHttp, ct).ConfigureAwait(false);
                Volatile.Write(ref _url, url);
            }

            HttpResponseMessage resp;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                using var req = new HttpRequestMessage(HttpMethod.Get, $"{url}/{from}-{to - 1}");
                resp = await MegaHttp.Storage.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            }
            catch (HttpRequestException e) when (MegaHttp.IsTlsRefusal(e) && !_plainHttp)
            {
                _plainHttp = true;
                Volatile.Write(ref _url, null);
                continue;
            }

            if (resp.IsSuccessStatusCode)
            {
                return resp;
            }

            int status = (int)resp.StatusCode;
            resp.Dispose();
            if (status == 509)
            {
                throw new MegaException("MEGA transfer quota exceeded, try again later") { HttpStatus = 509 };
            }

            if (status is 403 or 404 or 410 && attempt < 2)
            {
                // Expired URL: one fresh request for all connections.
                Interlocked.CompareExchange(ref _url, null, url);
                _source.ForgetDownloadUrl(_handle);
                continue;
            }

            throw new MegaException($"HTTP {status} from the MEGA storage server") { HttpStatus = status };
        }
    }
}
