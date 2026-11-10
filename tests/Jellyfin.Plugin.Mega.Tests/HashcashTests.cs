using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Mega.Mega;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Jellyfin.Plugin.Mega.Tests;

/// <summary>Proof of work ("hashcash") that the MEGA API can ask for with HTTP 402, mostly at login.</summary>
public sealed class HashcashTests : IAsyncLifetime
{
    // Token of the MEGA SDK unit tests (tests/unit/hashcash_test.cpp).
    private const string SdkToken = "K4QHo4I6XmnLNNsFqutTwObWZMClxf7ov--5OHLdGXSMHRwN8bLvrUTlpnhXVdtO";

    private readonly ConcurrentQueue<(string Query, string? Hashcash)> _requests = new();
    private WebApplication? _app;
    private string _challenge = string.Empty;
    private int _easiness = 255;
    private bool _alwaysRefuse;
    private string _challengeFormat = "1:{0}:1731410499:{1}";

    /// <summary>Straight from the protocol, independent of the plugin code: SHA-256 of prefix || token x 262144.</summary>
    private static bool IsValid(string token, int easiness, string prefixB64)
    {
        var prefix = MegaCrypto.B64Decode(prefixB64);
        var tokenBytes = MegaCrypto.B64Decode(token)!;
        if (prefix is not { Length: 4 })
        {
            return false;
        }

        var message = new byte[4 + (262144 * 48)];
        prefix.CopyTo(message, 0);
        for (int i = 0; i < 262144; i++)
        {
            tokenBytes.CopyTo(message, 4 + (i * 48));
        }

        var hash = SHA256.HashData(message);
        uint threshold = (uint)((((easiness & 63) << 1) + 1) << (((easiness >> 6) * 7) + 3));
        return BinaryPrimitives.ReadUInt32BigEndian(hash) <= threshold;
    }

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        _app = builder.Build();
        _app.MapPost("/cs", async (HttpContext ctx) =>
        {
            string? answer = ctx.Request.Headers["X-Hashcash"];
            _requests.Enqueue((ctx.Request.QueryString.Value ?? string.Empty, answer));
            var parts = answer?.Split(':');
            bool ok = !_alwaysRefuse
                && parts is { Length: 3 }
                && parts[0] == "1"
                && parts[1] == _challenge
                && IsValid(_challenge, _easiness, parts[2]);
            if (!ok)
            {
                _challenge = MegaCrypto.B64Encode(RandomNumberGenerator.GetBytes(48));
                ctx.Response.StatusCode = 402;
                ctx.Response.Headers["X-Hashcash"] = string.Format(System.Globalization.CultureInfo.InvariantCulture, _challengeFormat, _easiness, _challenge);
                return;
            }

            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync("[{\"ok\":1}]");
        });
        await _app.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    private MegaApiClient NewClient(List<string>? progress = null)
        => new() { ApiBase = _app!.Urls.First() + "/", Progress = progress is null ? null : progress.Add };

    [Fact]
    public void ParsesTheSdkExampleHeader()
    {
        Assert.True(MegaCrypto.TryParseHashcash("1:100:1731410499:RUvIePV2PNO8ofg8xp1aT5ugBcKSEzwKoLBw9o4E6F_fmn44eC3oMpv388UtFl2K", out var token, out var easiness));
        Assert.Equal(100, easiness);
        Assert.Equal("RUvIePV2PNO8ofg8xp1aT5ugBcKSEzwKoLBw9o4E6F_fmn44eC3oMpv388UtFl2K", token);

        Assert.False(MegaCrypto.TryParseHashcash(null, out _, out _));
        Assert.False(MegaCrypto.TryParseHashcash("2:100:1731410499:" + SdkToken, out _, out _));
        Assert.False(MegaCrypto.TryParseHashcash("1:300:1731410499:" + SdkToken, out _, out _));
        Assert.False(MegaCrypto.TryParseHashcash("1:100:1731410499:AAAA", out _, out _));
    }

    [Theory]
    [InlineData(100, 73u << 10)]
    [InlineData(180, 105u << 17)]
    [InlineData(192, 1u << 24)]
    [InlineData(255, 127u << 24)]
    public void Thresholds(int easiness, uint expected) => Assert.Equal(expected, MegaCrypto.HashcashThreshold(easiness));

    [Theory]
    [InlineData(192)]
    [InlineData(255)]
    public void SolutionIsValid(int easiness)
    {
        var watch = Stopwatch.StartNew();
        var prefix = MegaCrypto.SolveHashcash(SdkToken, easiness, TimeSpan.FromMinutes(2), CancellationToken.None);
        Assert.NotNull(prefix);
        Assert.True(IsValid(SdkToken, easiness, prefix!), $"prefix {prefix} does not meet easiness {easiness}");
        Assert.True(watch.Elapsed < TimeSpan.FromMinutes(1), $"took {watch.Elapsed}");
    }

    [Fact]
    public void BudgetAndCancellation()
    {
        // Easiness 0: about one chance in 500 million per try, never found in time.
        var watch = Stopwatch.StartNew();
        Assert.Null(MegaCrypto.SolveHashcash(SdkToken, 0, TimeSpan.FromMilliseconds(300), CancellationToken.None));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"budget overrun: {watch.Elapsed}");

        using var cts = new CancellationTokenSource(300);
        Assert.Throws<OperationCanceledException>(() => MegaCrypto.SolveHashcash(SdkToken, 0, TimeSpan.FromMinutes(10), cts.Token));
    }

    [Fact]
    public async Task RequestIsResentUnchangedWithTheProof()
    {
        var progress = new List<string>();
        var result = await NewClient(progress).RequestAsync(new { a = "us0", user = "someone@example.com" }, CancellationToken.None);

        Assert.Equal(1, result.GetProperty("ok").GetInt32());
        var requests = _requests.ToArray();
        Assert.Equal(2, requests.Length);
        Assert.Null(requests[0].Hashcash);
        Assert.NotNull(requests[1].Hashcash);
        Assert.Equal(requests[0].Query, requests[1].Query);
        Assert.Contains(progress, p => p.Contains("proof of work", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TreeRequestAnswersTheProofToo()
    {
        await using var stream = await NewClient().RequestStreamAsync(new { a = "f", c = 1 }, CancellationToken.None);
        using var reader = new StreamReader(stream);
        Assert.Equal("[{\"ok\":1}]", await reader.ReadToEndAsync());
        var requests = _requests.ToArray();
        Assert.Equal(2, requests.Length);
        Assert.Equal(requests[0].Query, requests[1].Query);
    }

    [Fact]
    public async Task GivesUpWithAClearMessage()
    {
        _alwaysRefuse = true;
        var e = await Assert.ThrowsAsync<MegaException>(() => NewClient().RequestAsync(new { a = "us0" }, CancellationToken.None));
        Assert.Contains("accepted none", e.Message, StringComparison.Ordinal);
        Assert.Equal(13, _requests.Count);

        _alwaysRefuse = false;
        _challengeFormat = "9:{0}:{1}";
        e = await Assert.ThrowsAsync<MegaException>(() => NewClient().RequestAsync(new { a = "us0" }, CancellationToken.None));
        Assert.Contains("unknown format", e.Message, StringComparison.Ordinal);
    }
}
