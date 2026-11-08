using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Mega.Mega;

/// <summary>Shared HTTP clients: one for the API, one for storage and attribute servers.</summary>
internal static class MegaHttp
{
    public const string UserAgent = "Jellyfin-Plugin-MEGA/1.0";

    // API answers (the node tree of a large account is 100+ MB of JSON) compress about 4:1.
    public static readonly HttpClient Api = Create(32, DecompressionMethods.All);

    // File data is encrypted, compression would only cost CPU.
    public static readonly HttpClient Storage = Create(256, DecompressionMethods.None);

    private static HttpClient Create(int maxPerServer, DecompressionMethods decompression)
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
            MaxConnectionsPerServer = maxPerServer,
            ConnectTimeout = TimeSpan.FromSeconds(20),
            AutomaticDecompression = decompression,
            EnableMultipleHttp2Connections = true,
            ConnectCallback = HappyEyeballs.ConnectAsync,
        };
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return client;
    }

    /// <summary>TLS handshake refused by a storage server: retry in plain HTTP (data is end-to-end encrypted anyway).</summary>
    public static bool IsTlsRefusal(Exception e)
    {
        for (var x = e; x is not null; x = x.InnerException)
        {
            if (x is AuthenticationException)
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>Transport of the MEGA API (<c>https://g.api.mega.co.nz/cs</c>).</summary>
internal sealed class MegaApiClient
{
    private const string ApiUrl = "https://g.api.mega.co.nz/cs";
    private static readonly JsonSerializerOptions JsonOptions = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };
    private long _seq = Random.Shared.NextInt64(1, 1_000_000_000);
    private string? _hashcash;

    /// <summary>Gets or sets the session id (account).</summary>
    public string? Sid { get; set; }

    /// <summary>Gets or sets the exported folder handle (public folder link, <c>n=</c>).</summary>
    public string? FolderHandle { get; set; }

    private string BuildUrl(string? sid)
    {
        var sb = new StringBuilder(ApiUrl);
        sb.Append("?id=").Append(Interlocked.Increment(ref _seq));
        sid ??= Sid;
        if (!string.IsNullOrEmpty(sid))
        {
            sb.Append("&sid=").Append(sid);
        }

        if (!string.IsNullOrEmpty(FolderHandle))
        {
            sb.Append("&n=").Append(FolderHandle);
        }

        return sb.ToString();
    }

    /// <summary>Sends one command and returns its result.</summary>
    public async Task<JsonElement> RequestAsync(object command, CancellationToken cancellationToken, string? sid = null)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(new[] { command }, JsonOptions);
        int delay = 250;
        for (int attempt = 1; ; attempt++)
        {
            byte[] text;
            try
            {
                text = await PostAsync(BuildUrl(sid), body, cancellationToken).ConfigureAwait(false);
            }
            catch (MegaException e) when (e.HttpStatus is 500 or 502 or 503 or 402 && attempt < 6)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                delay = Math.Min(delay * 2, 8000);
                continue;
            }
            catch (HttpRequestException) when (attempt < 5)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                delay = Math.Min(delay * 2, 8000);
                continue;
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested && attempt < 5)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                delay = Math.Min(delay * 2, 8000);
                continue;
            }

            JsonElement root;
            try
            {
                using var doc = JsonDocument.Parse(text);
                root = doc.RootElement.Clone();
            }
            catch (JsonException e)
            {
                throw new MegaException("unexpected MEGA API response", e);
            }

            JsonElement result;
            if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0)
            {
                result = root[0];
            }
            else if (root.ValueKind == JsonValueKind.Number)
            {
                result = root;
            }
            else
            {
                throw new MegaException("unexpected MEGA API response");
            }

            if (result.ValueKind == JsonValueKind.Number && result.TryGetInt32(out var code) && code < 0)
            {
                if ((code == -3 || code == -4 || code == -6) && attempt < 8)
                {
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                    delay = Math.Min(delay * 2, 8000);
                    continue;
                }

                throw new MegaException(code);
            }

            return result;
        }
    }

    /// <summary>
    /// Sends a command whose (large) response is read as a stream: the node tree.
    /// The caller disposes the returned stream.
    /// </summary>
    public async Task<Stream> RequestStreamAsync(object command, CancellationToken cancellationToken)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(new[] { command }, JsonOptions);
        int delay = 500;
        for (int attempt = 1; ; attempt++)
        {
            HttpResponseMessage? resp = null;
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, BuildUrl(null))
                {
                    Content = new ByteArrayContent(body),
                };
                req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                resp = await MegaHttp.Api.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                if (resp.StatusCode == HttpStatusCode.OK)
                {
                    var stream = await resp.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                    return new OwnedStream(stream, resp);
                }

                int status = (int)resp.StatusCode;
                resp.Dispose();
                if (attempt >= 6 || status is not (500 or 502 or 503 or 402))
                {
                    throw new MegaException($"HTTP {status} from the MEGA API") { HttpStatus = status };
                }
            }
            catch (HttpRequestException) when (attempt < 6)
            {
                resp?.Dispose();
            }

            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            delay = Math.Min(delay * 2, 8000);
        }
    }

    /// <summary>
    /// Action packets since <paramref name="sn"/>: <c>{"a": [...], "sn": "..."}</c>,
    /// or <c>{"w": "...", ...}</c> when nothing changed.
    /// </summary>
    public async Task<JsonElement> PollAsync(string sn, CancellationToken cancellationToken)
    {
        var url = new StringBuilder("https://g.api.mega.co.nz/sc?sn=").Append(sn);
        if (!string.IsNullOrEmpty(Sid))
        {
            url.Append("&sid=").Append(Sid);
        }

        if (!string.IsNullOrEmpty(FolderHandle))
        {
            url.Append("&n=").Append(FolderHandle);
        }

        var text = await PostAsync(url.ToString(), Array.Empty<byte>(), cancellationToken).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement.Clone();
        if (root.ValueKind == JsonValueKind.Number && root.TryGetInt32(out var code) && code < 0)
        {
            throw new MegaException(code);
        }

        return root;
    }

    private async Task<byte[]> PostAsync(string url, byte[] body, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new ByteArrayContent(body) };
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        var hc = Interlocked.Exchange(ref _hashcash, null);
        if (hc is not null)
        {
            req.Headers.TryAddWithoutValidation("X-Hashcash", hc);
        }

        using var resp = await MegaHttp.Api.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        int status = (int)resp.StatusCode;
        if (status == 402)
        {
            if (resp.Headers.TryGetValues("X-Hashcash", out var values))
            {
                foreach (var v in values)
                {
                    var answer = await Task.Run(() => MegaCrypto.SolveHashcash(v), cancellationToken).ConfigureAwait(false);
                    if (answer is not null)
                    {
                        _hashcash = answer;
                        break;
                    }
                }
            }

            throw new MegaException("MEGA requested a proof of work") { HttpStatus = 402 };
        }

        if (status != 200)
        {
            throw new MegaException($"HTTP {status} from the MEGA API") { HttpStatus = status };
        }

        return await resp.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
    }

    /// <summary>Random request id (<c>i</c>).</summary>
    public static string RequestId()
    {
        const string Alpha = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        var chars = new char[10];
        for (int i = 0; i < chars.Length; i++)
        {
            chars[i] = Alpha[Random.Shared.Next(Alpha.Length)];
        }

        return new string(chars);
    }

    /// <summary>Response stream that also disposes its HTTP response.</summary>
    private sealed class OwnedStream : Stream
    {
        private readonly Stream _inner;
        private readonly IDisposable _owner;

        public OwnedStream(Stream inner, IDisposable owner)
        {
            _inner = inner;
            _owner = owner;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => _inner.ReadAsync(buffer, cancellationToken);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => _inner.ReadAsync(buffer, offset, count, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
                _owner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
