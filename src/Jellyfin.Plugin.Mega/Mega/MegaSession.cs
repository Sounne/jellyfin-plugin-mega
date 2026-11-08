using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Mega.Mega;

/// <summary>Logged-in account: user handle, master key, session id, RSA key.</summary>
internal sealed class MegaAccountSession
{
    public required string User { get; init; }

    public required byte[] MasterKey { get; init; }

    public required string Sid { get; init; }

    public required byte[] SessionKey { get; init; }

    public MegaRsa? Rsa { get; init; }

    /// <summary>
    /// Session in the MEGA SDK format (version 1): <c>0x01 || AES-ECB(sek, master) || sid</c>.
    /// Same format as <c>mega-session</c> of MEGAcmd: it can be pasted in either direction.
    /// </summary>
    public string ToSdkSession()
    {
        var k = (byte[])MasterKey.Clone();
        MegaCrypto.EcbEncrypt(SessionKey, k);
        var sid = MegaCrypto.B64Decode(Sid) ?? Array.Empty<byte>();
        var outp = new byte[1 + 16 + sid.Length];
        outp[0] = 1;
        k.CopyTo(outp, 1);
        sid.CopyTo(outp, 17);
        return MegaCrypto.B64Encode(outp);
    }

    private static MegaRsa? DecodePrivk(byte[] master, JsonElement r)
    {
        if (!r.TryGetProperty("privk", out var pk) || pk.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var p = MegaCrypto.B64Decode(pk.GetString());
        if (p is null || p.Length < 16)
        {
            return null;
        }

        var data = p.AsSpan(0, p.Length / 16 * 16).ToArray();
        MegaCrypto.EcbDecrypt(master, data);
        return MegaRsa.FromPrivk(data);
    }

    private static string? Str(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>Login with e-mail and password (v1 and v2 accounts). <paramref name="mfa"/>: 6-digit 2FA code.</summary>
    public static async Task<MegaAccountSession> LoginAsync(MegaApiClient api, string email, string password, string? mfa, CancellationToken cancellationToken)
    {
        email = email.Trim().ToLowerInvariant();
        var pre = await api.RequestAsync(new { a = "us0", user = email }, cancellationToken).ConfigureAwait(false);
        int version = pre.ValueKind == JsonValueKind.Object && pre.TryGetProperty("v", out var v) && v.TryGetInt32(out var vv) ? vv : 1;
        byte[] passwordKey;
        string uh;
        if (version == 2)
        {
            var salt = MegaCrypto.B64Decode(Str(pre, "s")) ?? throw new MegaException("MEGA did not send the password salt");
            var d = MegaCrypto.DeriveV2(MegaCrypto.Utf8(password), salt);
            passwordKey = d.AsSpan(0, 16).ToArray();
            uh = MegaCrypto.B64Encode(d.AsSpan(16, 16));
        }
        else
        {
            passwordKey = await Task.Run(() => MegaCrypto.PrepareKeyV1(MegaCrypto.Utf8(password)), cancellationToken).ConfigureAwait(false);
            uh = MegaCrypto.B64Encode(MegaCrypto.EmailHashV1(MegaCrypto.Utf8(email), passwordKey));
        }

        var sek = new byte[16];
        Random.Shared.NextBytes(sek);
        object cmd = string.IsNullOrWhiteSpace(mfa)
            ? new { a = "us", user = email, uh, sek = MegaCrypto.B64Encode(sek) }
            : new { a = "us", user = email, uh, sek = MegaCrypto.B64Encode(sek), mfa = mfa.Trim() };
        JsonElement r;
        try
        {
            r = await api.RequestAsync(cmd, cancellationToken).ConfigureAwait(false);
        }
        catch (MegaException e) when (e.Code == -9)
        {
            throw new MegaException("wrong e-mail or password", e);
        }

        var k = MegaCrypto.B64Decode(Str(r, "k"));
        if (k is null || k.Length < 16)
        {
            throw new MegaException("MEGA did not send the master key");
        }

        var master = k.AsSpan(0, 16).ToArray();
        MegaCrypto.EcbDecrypt(passwordKey, master);
        var rsa = DecodePrivk(master, r);
        string sid;
        var csid = MegaCrypto.B64Decode(Str(r, "csid"));
        if (csid is not null)
        {
            var m = rsa?.DecryptMpi(csid) ?? throw new MegaException("wrong e-mail or password");
            if (m.Length < 43)
            {
                throw new MegaException("invalid session id received from MEGA");
            }

            sid = MegaCrypto.B64Encode(m.AsSpan(0, 43));
        }
        else
        {
            sid = Str(r, "tsid") ?? throw new MegaException("MEGA did not send a session");
        }

        var serverSek = MegaCrypto.B64Decode(Str(r, "sek"));
        return new MegaAccountSession
        {
            User = Str(r, "u") ?? string.Empty,
            MasterKey = master,
            Sid = sid,
            SessionKey = serverSek is { Length: >= 16 } ? serverSek.AsSpan(0, 16).ToArray() : sek,
            Rsa = rsa,
        };
    }

    /// <summary>Resumes a session exported by the MEGA SDK / MEGAcmd (<c>mega-session</c>) or by this plugin.</summary>
    public static async Task<MegaAccountSession> ResumeAsync(MegaApiClient api, string session, CancellationToken cancellationToken)
    {
        var bytes = MegaCrypto.B64Decode(session.Trim()) ?? throw new MegaException("unreadable session (base64)");
        int version;
        byte[] encKey;
        byte[] sidBytes;
        if (bytes.Length == 60 && bytes[0] == 1)
        {
            version = 1;
            encKey = bytes.AsSpan(1, 16).ToArray();
            sidBytes = bytes.AsSpan(17, 43).ToArray();
        }
        else if (bytes.Length == 59)
        {
            version = 0;
            encKey = bytes.AsSpan(0, 16).ToArray();
            sidBytes = bytes.AsSpan(16, 43).ToArray();
        }
        else
        {
            throw new MegaException($"unsupported session format ({bytes.Length} bytes)");
        }

        var sid = MegaCrypto.B64Encode(sidBytes);
        var rnd = new byte[16];
        Random.Shared.NextBytes(rnd);
        var r = await api.RequestAsync(new { a = "us", sek = MegaCrypto.B64Encode(rnd) }, cancellationToken, sid).ConfigureAwait(false);
        var master = encKey;
        var sek = MegaCrypto.B64Decode(Str(r, "sek"));
        if (version == 1)
        {
            if (sek is null || sek.Length < 16)
            {
                throw new MegaException("MEGA did not send the session key");
            }

            MegaCrypto.EcbDecrypt(sek.AsSpan(0, 16), master);
        }

        var rsa = DecodePrivk(master, r);
        if (Str(r, "privk") is not null && rsa is null)
        {
            throw new MegaException("invalid session key");
        }

        return new MegaAccountSession
        {
            User = Str(r, "u") ?? string.Empty,
            MasterKey = master,
            Sid = sid,
            SessionKey = sek is { Length: >= 16 } ? sek.AsSpan(0, 16).ToArray() : rnd,
            Rsa = rsa,
        };
    }
}
