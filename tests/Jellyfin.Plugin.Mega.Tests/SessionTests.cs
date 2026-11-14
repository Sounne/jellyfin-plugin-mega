using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Mega.Mega;
using Jellyfin.Plugin.Mega.Tests.Support;
using Xunit;

namespace Jellyfin.Plugin.Mega.Tests;

/// <summary>Logging in (e-mail and password, 2FA) and resuming sessions (MEGAcmd, plugin), against a local MEGA.</summary>
public sealed class SessionTests : IAsyncLifetime
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private FakeMega _mega = null!;

    public async Task InitializeAsync() => _mega = await FakeMega.StartAsync(account: true);

    public async Task DisposeAsync() => await _mega.DisposeAsync();

    private MegaApiClient Api() => new() { ApiBase = _mega.Url };

    [Fact]
    public async Task MegaCmdSessionIsResumed()
    {
        var s = await MegaAccountSession.ResumeAsync(Api(), "  " + _mega.Session + "\n", Ct);
        Assert.Equal(_mega.MasterKey, s.MasterKey);
        Assert.Equal(_mega.Sid, s.Sid);
        Assert.Equal(FakeMega.User, s.User);
        Assert.Equal(_mega.SessionKey, s.SessionKey);
        Assert.Null(s.Rsa);

        // Same format both ways: what the plugin stores can be pasted in MEGAcmd and back.
        Assert.Equal(_mega.Session, s.ToSdkSession());
        var (query, command) = Assert.Single(_mega.Commands);
        Assert.Contains("sid=" + _mega.Sid, query, StringComparison.Ordinal);
        Assert.Equal("us", command.GetProperty("a").GetString());
    }

    [Fact]
    public async Task OldSessionFormatIsResumed()
    {
        // Version 0: master key in clear, then the session id.
        var v0 = MegaCrypto.B64Encode(_mega.MasterKey.Concat(_mega.SidBytes).ToArray());
        var s = await MegaAccountSession.ResumeAsync(Api(), v0, Ct);
        Assert.Equal(_mega.MasterKey, s.MasterKey);
        Assert.Equal(_mega.Sid, s.Sid);
    }

    [Theory]
    [InlineData("AAAA")]
    [InlineData("")]
    [InlineData("AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyAhIiMkJSYnKCkqKywtLi8wMTIzNDU2Nzg5Ojs8PT4_QA")]
    public async Task UnreadableSessionIsRefusedLocally(string session)
    {
        var e = await Assert.ThrowsAsync<MegaException>(() => MegaAccountSession.ResumeAsync(Api(), session, Ct));
        Assert.Contains("session", e.Message, StringComparison.Ordinal);
        Assert.Empty(_mega.ApiQueries);
    }

    [Fact]
    public async Task ClosedSessionIsReported()
    {
        await using var other = await FakeMega.StartAsync(account: true);
        var e = await Assert.ThrowsAsync<MegaException>(() => MegaAccountSession.ResumeAsync(Api(), other.Session, Ct));
        Assert.True(e.IsBadSession);
        Assert.Contains("connect the account again", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PasswordLogin()
    {
        var s = await MegaAccountSession.LoginAsync(Api(), "  SomeOne@Example.COM ", _mega.Password, null, Ct);
        Assert.Equal(_mega.MasterKey, s.MasterKey);
        Assert.Equal(_mega.Sid, s.Sid);
        Assert.Equal(FakeMega.User, s.User);

        // The e-mail is sent trimmed and in lower case, the password never.
        var us = _mega.Commands.Select(c => c.Command).Single(c => c.GetProperty("a").GetString() == "us");
        Assert.Equal("someone@example.com", us.GetProperty("user").GetString());
        Assert.DoesNotContain(_mega.Password, us.GetRawText(), StringComparison.Ordinal);
        Assert.False(us.TryGetProperty("mfa", out _));

        // The session saved after a login resumes.
        var resumed = await MegaAccountSession.ResumeAsync(Api(), s.ToSdkSession(), Ct);
        Assert.Equal(_mega.MasterKey, resumed.MasterKey);
    }

    [Fact]
    public async Task WrongPasswordIsSaidPlainly()
    {
        var e = await Assert.ThrowsAsync<MegaException>(() => MegaAccountSession.LoginAsync(Api(), _mega.Email, "wrong", null, Ct));
        Assert.Equal("wrong e-mail or password", e.Message);
    }

    [Fact]
    public async Task TwoFactorCode()
    {
        _mega.Mfa = "123456";
        var e = await Assert.ThrowsAsync<MegaException>(() => MegaAccountSession.LoginAsync(Api(), _mega.Email, _mega.Password, null, Ct));
        Assert.Equal(-26, e.Code);
        Assert.Contains("two-factor", e.Message, StringComparison.Ordinal);

        var s = await MegaAccountSession.LoginAsync(Api(), _mega.Email, _mega.Password, " 123456 ", Ct);
        Assert.Equal(_mega.MasterKey, s.MasterKey);
        Assert.Equal("123456", _mega.Commands.Last().Command.GetProperty("mfa").GetString());
    }

    [Fact]
    public async Task SessionIdEncryptedWithTheAccountKey()
    {
        // Accounts with an RSA key get their session id encrypted with it (csid), not in clear (tsid).
        using var rsa = RSA.Create(2048);
        var p = rsa.ExportParameters(true);
        var m = _mega.SidBytes.Concat(RandomNumberGenerator.GetBytes(255 - 43)).ToArray();
        var csid = MegaCrypto.B64Encode(TestData.Mpi(TestData.RawEncrypt(p, m)));
        var privk = MegaCrypto.B64Encode(TestData.Privk(p, _mega.MasterKey));
        _mega.OnCommand = (c, q) =>
        {
            if (c.GetProperty("a").GetString() != "us")
            {
                return null;
            }

            var k = (byte[])_mega.MasterKey.Clone();
            var derived = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(_mega.Password), _mega.Salt, 100_000, HashAlgorithmName.SHA512, 32);
            MegaCrypto.EcbEncrypt(derived.AsSpan(0, 16), k);
            return new { k = MegaCrypto.B64Encode(k), privk, csid, u = FakeMega.User };
        };

        var s = await MegaAccountSession.LoginAsync(Api(), _mega.Email, _mega.Password, null, Ct);
        Assert.Equal(_mega.Sid, s.Sid);
        Assert.NotNull(s.Rsa);
    }

    [Fact]
    public async Task OldAccountLogin()
    {
        // Version 1 accounts: password key from 65 536 AES rounds, e-mail hash as user hash.
        var passwordKey = MegaCrypto.PrepareKeyV1(Encoding.UTF8.GetBytes(_mega.Password));
        var uh = MegaCrypto.B64Encode(MegaCrypto.EmailHashV1(Encoding.UTF8.GetBytes(_mega.Email), passwordKey));
        _mega.OnCommand = (c, q) => c.GetProperty("a").GetString() switch
        {
            "us0" => new { v = 1 },
            "us" when c.GetProperty("uh").GetString() == uh => Login(passwordKey),
            "us" => -9,
            _ => null,
        };

        var s = await MegaAccountSession.LoginAsync(Api(), _mega.Email.ToUpperInvariant(), _mega.Password, null, Ct);
        Assert.Equal(_mega.MasterKey, s.MasterKey);
    }

    [Fact]
    public void EmptyPasswordKeyIsMegasConstant()
    {
        Assert.Equal(
            new byte[] { 0x93, 0xC4, 0x67, 0xE3, 0x7D, 0xB0, 0xC7, 0xA4, 0xD1, 0xBE, 0x3F, 0x81, 0x01, 0x52, 0xCB, 0x56 },
            MegaCrypto.PrepareKeyV1(Array.Empty<byte>()));
        Assert.NotEqual(MegaCrypto.PrepareKeyV1("a"u8.ToArray()), MegaCrypto.PrepareKeyV1("b"u8.ToArray()));
    }

    private object Login(byte[] passwordKey)
    {
        var k = (byte[])_mega.MasterKey.Clone();
        MegaCrypto.EcbEncrypt(passwordKey, k);
        return new { k = MegaCrypto.B64Encode(k), tsid = _mega.Sid, u = FakeMega.User };
    }
}
