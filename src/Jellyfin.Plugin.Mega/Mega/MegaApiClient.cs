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
using Microsoft.Extensions.Logging;

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
    private const int MaxProofsOfWork = 12;
    private static readonly JsonSerializerOptions JsonOptions = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };
    private long _seq = Random.Shared.NextInt64(1, 1_000_000_000);

    /// <summary>Gets or sets the logger for proof-of-work events (set when the plugin starts).</summary>
    public static ILogger? Log { get; set; }

    /// <summary>Gets the base address of the API (tests use a local server).</summary>
    public string ApiBase { get; init; } = "https://g.api.mega.co.nz/";

    /// <summary>Gets or sets the session id (account).</summary>
    public string? Sid { get; set; }

    /// <summary>Gets or sets the exported folder handle (public folder link, <c>n=</c>).</summary>
    public string? FolderHandle { get; set; }

    /// <summary>Gets or sets a callback told what a long request is waiting for (shown while logging in).</summary>
    public Action<string>? Progress { get; set; }

    private string BuildUrl(string? sid)
    {
        var sb = new StringBuilder(ApiBase).Append("cs");
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

        // Retries resend the request unchanged, request id included, as the MEGA SDK and web client
        // do: the server sees the same request (idempotence) and accepts its proof of work.
        var url = BuildUrl(sid);
        var pow = new ProofOfWork(this);
        int delay = 250;
        for (int attempt = 1; ; attempt++)
        {
            byte[] text;
            try
            {
                text = await PostAsync(url, body, pow.Take(), cancellationToken).ConfigureAwait(false);
            }
            catch (HashcashRequiredException e)
            {
                await pow.SolveAsync(e.Challenge, cancellationToken).ConfigureAwait(false);
                attempt--;
                continue;
            }
            catch (MegaException e) when (e.HttpStatus is 500 or 502 or 503 && attempt < 6)
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
            bool commandResult = root.ValueKind == JsonValueKind.Array;
            if (commandResult && root.GetArrayLength() > 0)
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
                    if (commandResult)
                    {
                        // The command itself ran and failed: send it again as a new request.
                        url = BuildUrl(sid);
                    }

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
        var url = BuildUrl(null);
        var pow = new ProofOfWork(this);
        int delay = 500;
        for (int attempt = 1; ; attempt++)
        {
            HttpResponseMessage? resp = null;
            try
            {
                using var req = NewPost(url, body, pow.Take());
                resp = await MegaHttp.Api.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                if (resp.StatusCode == HttpStatusCode.OK)
                {
                    var stream = await resp.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                    return new OwnedStream(stream, resp);
                }

                int status = (int)resp.StatusCode;
                if (status == 402)
                {
                    var challenge = HashcashChallenge(resp);
                    resp.Dispose();
                    await pow.SolveAsync(challenge, cancellationToken).ConfigureAwait(false);
                    attempt--;
                    continue;
                }

                resp.Dispose();
                if (attempt >= 6 || status is not (500 or 502 or 503))
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
        var url = new StringBuilder(ApiBase).Append("sc?sn=").Append(sn);
        if (!string.IsNullOrEmpty(Sid))
        {
            url.Append("&sid=").Append(Sid);
        }

        if (!string.IsNullOrEmpty(FolderHandle))
        {
            url.Append("&n=").Append(FolderHandle);
        }

        var pow = new ProofOfWork(this);
        while (true)
        {
            byte[] text;
            try
            {
                text = await PostAsync(url.ToString(), Array.Empty<byte>(), pow.Take(), cancellationToken).ConfigureAwait(false);
            }
            catch (HashcashRequiredException e)
            {
                await pow.SolveAsync(e.Challenge, cancellationToken).ConfigureAwait(false);
                continue;
            }

            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement.Clone();
            if (root.ValueKind == JsonValueKind.Number && root.TryGetInt32(out var code) && code < 0)
            {
                throw new MegaException(code);
            }

            return root;
        }
    }

    private static HttpRequestMessage NewPost(string url, byte[] body, string? hashcash)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new ByteArrayContent(body) };
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        if (hashcash is not null)
        {
            req.Headers.TryAddWithoutValidation("X-Hashcash", hashcash);
        }

        return req;
    }

    private static string? HashcashChallenge(HttpResponseMessage resp)
        => resp.Headers.TryGetValues("X-Hashcash", out var values) ? string.Join(",", values) : null;

    private static async Task<byte[]> PostAsync(string url, byte[] body, string? hashcash, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        using var req = NewPost(url, body, hashcash);
        using var resp = await MegaHttp.Api.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        int status = (int)resp.StatusCode;
        if (status == 402)
        {
            throw new HashcashRequiredException(HashcashChallenge(resp));
        }

        if (status != 200)
        {
            throw new MegaException($"HTTP {status} from the MEGA API") { HttpStatus = status };
        }

        return await resp.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
    }

    /// <summary>HTTP 402: the API wants a proof of work before it runs the request.</summary>
    private sealed class HashcashRequiredException : MegaException
    {
        public HashcashRequiredException(string? challenge)
            : base("MEGA asked for a proof of work")
        {
            Challenge = challenge;
            HttpStatus = 402;
        }

        public string? Challenge { get; }
    }

    /// <summary>Proofs of work of one request: answers each challenge, gives up after <see cref="MaxProofsOfWork"/>.</summary>
    private sealed class ProofOfWork
    {
        private readonly MegaApiClient _client;
        private int _challenges;
        private int _timeouts;
        private string? _answer;

        public ProofOfWork(MegaApiClient client)
        {
            _client = client;
        }

        /// <summary>The <c>X-Hashcash</c> value for the next send, used once.</summary>
        public string? Take()
        {
            var a = _answer;
            _answer = null;
            return a;
        }

        public async Task SolveAsync(string? challenge, CancellationToken cancellationToken)
        {
            _answer = null;
            if (++_challenges > MaxProofsOfWork)
            {
                throw new MegaException($"MEGA asked for a proof of work {MaxProofsOfWork} times and accepted none of the answers. Wait a few minutes and try again, or add the account with a MEGAcmd session.") { HttpStatus = 402 };
            }

            if (!MegaCrypto.TryParseHashcash(challenge, out var token, out var easiness))
            {
                throw new MegaException("MEGA asked for a proof of work in an unknown format: " + (challenge ?? "no X-Hashcash header")) { HttpStatus = 402 };
            }

            // Same time budget as the MEGA SDK: a challenge stays valid about 5 minutes, so after
            // 100 seconds ask for a new one (it can be easier). From the third, take the time needed.
            var budget = _timeouts < 2 ? TimeSpan.FromSeconds(100) : TimeSpan.FromMinutes(30);
            _client.Progress?.Invoke($"MEGA asked for a proof of work (anti-abuse check, difficulty {easiness}). Computing it, this can take a few minutes…");
            Log?.LogInformation("MEGA asked for a proof of work (easiness {Easiness}, challenge {Count})", easiness, _challenges);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var prefix = await Task.Run(() => MegaCrypto.SolveHashcash(token, easiness, budget, cancellationToken), cancellationToken).ConfigureAwait(false);
            if (prefix is null)
            {
                _timeouts++;
                Log?.LogWarning("MEGA proof of work not found within {Seconds} s, asking for a new challenge", (int)budget.TotalSeconds);
                return;
            }

            Log?.LogInformation("MEGA proof of work found in {Seconds:F1} s", watch.Elapsed.TotalSeconds);
            _client.Progress?.Invoke("Proof of work done, logging in…");
            _answer = "1:" + token + ":" + prefix;
        }
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
