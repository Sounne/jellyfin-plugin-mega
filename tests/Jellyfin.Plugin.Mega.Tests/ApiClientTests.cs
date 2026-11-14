using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Mega.Mega;
using Jellyfin.Plugin.Mega.Tests.Support;
using Xunit;

namespace Jellyfin.Plugin.Mega.Tests;

/// <summary>MEGA API transport: retries, request ids, error codes, session and folder parameters.</summary>
public sealed class ApiClientTests : IAsyncLifetime
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private FakeMega _mega = null!;

    public async Task InitializeAsync() => _mega = await FakeMega.StartAsync(account: true);

    public async Task DisposeAsync() => await _mega.DisposeAsync();

    private MegaApiClient Client() => new() { ApiBase = _mega.Url, Sid = _mega.Sid };

    private static string Id(string query) => Regex.Match(query, @"[?&]id=(\d+)").Groups[1].Value;

    [Fact]
    public async Task ServerErrorsResendTheSameRequest()
    {
        _mega.ApiFaults.Enqueue((500, string.Empty));
        _mega.ApiFaults.Enqueue((503, string.Empty));
        var r = await Client().RequestAsync(new { a = "ug" }, Ct);
        Assert.Equal(_mega.Email, r.GetProperty("email").GetString());
        var queries = _mega.ApiQueries.ToArray();
        Assert.Equal(3, queries.Length);
        Assert.Single(queries.Distinct());
    }

    [Fact]
    public async Task BusyCommandIsSentAgainAsANewRequest()
    {
        // The command ran and answered -3 ("try again"): a new request, with a new id.
        _mega.ApiFaults.Enqueue((200, "[-3]"));
        await Client().RequestAsync(new { a = "ug" }, Ct);
        var queries = _mega.ApiQueries.ToArray();
        Assert.Equal(2, queries.Length);
        Assert.NotEqual(Id(queries[0]), Id(queries[1]));
    }

    [Fact]
    public async Task BusyServerGetsTheSameRequest()
    {
        // The whole request answered -3 (not run): sent again unchanged.
        _mega.ApiFaults.Enqueue((200, "-3"));
        _mega.ApiFaults.Enqueue((200, "-4"));
        await Client().RequestAsync(new { a = "ug" }, Ct);
        Assert.Single(_mega.ApiQueries.Distinct());
        Assert.Equal(3, _mega.ApiQueries.Count);
    }

    [Theory]
    [InlineData(-2, false, false)]
    [InlineData(-9, false, false)]
    [InlineData(-11, false, false)]
    [InlineData(-15, true, false)]
    [InlineData(-16, false, false)]
    [InlineData(-17, false, true)]
    public async Task ErrorCodesBecomeExceptions(int code, bool badSession, bool quota)
    {
        _mega.ApiFaults.Enqueue((200, $"[{code}]"));
        var e = await Assert.ThrowsAsync<MegaException>(() => Client().RequestAsync(new { a = "ug" }, Ct));
        Assert.Equal(code, e.Code);
        Assert.Equal(badSession, e.IsBadSession);
        Assert.Equal(quota, e.IsQuotaExceeded);
        Assert.Equal(MegaException.Describe(code), e.Message);
        Assert.Single(_mega.ApiQueries);
    }

    [Theory]
    [InlineData("<html>busy</html>")]
    [InlineData("[]")]
    [InlineData("{\"x\":1}")]
    public async Task UnexpectedAnswersAreErrors(string body)
    {
        _mega.ApiFaults.Enqueue((200, body));
        var e = await Assert.ThrowsAsync<MegaException>(() => Client().RequestAsync(new { a = "ug" }, Ct));
        Assert.Equal("unexpected MEGA API response", e.Message);
    }

    [Fact]
    public async Task ClientErrorsAreNotRetried()
    {
        _mega.ApiFaults.Enqueue((403, string.Empty));
        var e = await Assert.ThrowsAsync<MegaException>(() => Client().RequestAsync(new { a = "ug" }, Ct));
        Assert.Equal(403, e.HttpStatus);
        Assert.Single(_mega.ApiQueries);
    }

    [Fact]
    public async Task QueryCarriesRequestIdSessionAndFolder()
    {
        var client = new MegaApiClient { ApiBase = _mega.Url, Sid = "SESSION", FolderHandle = "FOLDER01" };
        _mega.OnCommand = (_, _) => 0;
        await client.RequestAsync(new { a = "x" }, Ct);
        await client.RequestAsync(new { a = "x" }, Ct, "OTHERSID");
        var q = _mega.ApiQueries.ToArray();
        Assert.Matches(@"^\?id=\d+&sid=SESSION&n=FOLDER01$", q[0]);
        Assert.Contains("sid=OTHERSID", q[1], StringComparison.Ordinal);
        Assert.Equal(long.Parse(Id(q[0])) + 1, long.Parse(Id(q[1])));

        // Commands go as a JSON array; null fields are left out.
        await client.RequestAsync(new { a = "x", skip = (string?)null }, Ct);
        Assert.False(_mega.Commands.Last().Command.TryGetProperty("skip", out _));
    }

    [Fact]
    public async Task TreeStreamRetriesServerErrorsOnly()
    {
        _mega.ApiFaults.Enqueue((502, string.Empty));
        await using (var stream = await Client().RequestStreamAsync(new { a = "f", c = 1 }, Ct))
        {
            using var reader = new StreamReader(stream);
            Assert.Contains("\"sn\"", await reader.ReadToEndAsync(), StringComparison.Ordinal);
        }

        _mega.ApiFaults.Enqueue((404, string.Empty));
        var e = await Assert.ThrowsAsync<MegaException>(() => Client().RequestStreamAsync(new { a = "f", c = 1 }, Ct));
        Assert.Equal(404, e.HttpStatus);
        Assert.Equal(3, _mega.ApiQueries.Count);
    }

    [Fact]
    public async Task ChangePolls()
    {
        var client = Client();
        var r = await client.PollAsync("SN000001", Ct);
        Assert.True(r.TryGetProperty("w", out _));
        Assert.Contains("sn=SN000001", Assert.Single(_mega.Polls), StringComparison.Ordinal);

        _mega.Publish(new { a = "d", n = "FILE0001" });
        r = await client.PollAsync("SN000001", Ct);
        Assert.Equal("d", r.GetProperty("a")[0].GetProperty("a").GetString());
        Assert.Equal("SN000002", r.GetProperty("sn").GetString());

        _mega.PollError = -6;
        var e = await Assert.ThrowsAsync<MegaException>(() => client.PollAsync("SN000002", Ct));
        Assert.Equal(-6, e.Code);
    }
}
