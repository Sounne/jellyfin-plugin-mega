using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Mega.Mega;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Mega.Tests.Support;

/// <summary>
/// Local stand-in for MEGA: API (<c>/cs</c>, <c>/sc</c>), storage servers (<c>/dl</c>) and file attribute
/// servers (<c>/fa</c>), with encrypted nodes and files built like MEGA builds them. Either an account
/// (session, master key) or a public folder link (folder key).
/// </summary>
internal sealed class FakeMega : IAsyncDisposable
{
    public const string User = "UserHandle1";
    public const string RootHandle = "ROOT0001";
    public const string RubbishHandle = "RUBB0001";
    public const string FolderHandle = "LnkRoot1";
    public const uint Bundle = 924;

    private readonly WebApplication _app;
    private readonly object _gate = new();
    private readonly List<Dictionary<string, object?>> _nodes = new();
    private readonly Dictionary<string, StoredFile> _files = new(StringComparer.Ordinal);
    private readonly Dictionary<string, byte[]> _attributes = new(StringComparer.Ordinal);
    private readonly Queue<object[]> _packets = new();
    private int _sn = 1;
    private int _handles;
    private int _storageRequests;
    private long _storageBytes;
    private int _attributeRequests;

    private FakeMega(WebApplication app, bool account)
    {
        _app = app;
        IsAccount = account;
        if (account)
        {
            AddRaw(new() { ["h"] = RootHandle, ["p"] = string.Empty, ["t"] = 2, ["ts"] = 1_600_000_000 }, RootHandle, MasterKey);
            AddRaw(new() { ["h"] = RubbishHandle, ["p"] = string.Empty, ["t"] = 4, ["ts"] = 1_600_000_000 }, RubbishHandle, MasterKey);
        }
        else
        {
            AddRaw(
                new()
                {
                    ["h"] = FolderHandle,
                    ["p"] = "Pa_rent1",
                    ["t"] = 1,
                    ["a"] = Attrs(FolderKey, new { n = "Shared media" }),
                    ["k"] = EncKey(FolderKey),
                    ["ts"] = 1_600_000_000,
                },
                FolderHandle,
                FolderKey);
        }
    }

    public bool IsAccount { get; }

    /// <summary>Gets the folder that new nodes go into by default: the Cloud Drive or the link root.</summary>
    public string Root => IsAccount ? RootHandle : FolderHandle;

    public byte[] MasterKey { get; } = RandomNumberGenerator.GetBytes(16);

    public byte[] FolderKey { get; } = RandomNumberGenerator.GetBytes(16);

    public byte[] SessionKey { get; } = RandomNumberGenerator.GetBytes(16);

    /// <summary>Gets the 43-byte session id (first byte not zero: it survives a trip through RSA).</summary>
    public byte[] SidBytes { get; } = NewSid();

    public string Sid => MegaCrypto.B64Encode(SidBytes);

    /// <summary>Gets the session in the MEGA SDK / MEGAcmd format (version 1).</summary>
    public string Session
    {
        get
        {
            var k = (byte[])MasterKey.Clone();
            MegaCrypto.EcbEncrypt(SessionKey, k);
            return MegaCrypto.B64Encode(new byte[] { 1 }.Concat(k).Concat(SidBytes).ToArray());
        }
    }

    public string Link => $"https://mega.nz/folder/{FolderHandle}#{MegaCrypto.B64Encode(FolderKey)}";

    public string Email { get; set; } = "someone@example.com";

    public string Password { get; set; } = "correct horse battery staple";

    public byte[] Salt { get; } = RandomNumberGenerator.GetBytes(32);

    /// <summary>Gets or sets the 2FA code the login needs (none when null).</summary>
    public string? Mfa { get; set; }

    public string Url => _app.Urls.First() + "/";

    public string Sn => "SN" + Volatile.Read(ref _sn).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);

    // ---- What the plugin sent ---------------------------------------------------------------

    public ConcurrentQueue<(string Query, JsonElement Command)> Commands { get; } = new();

    public ConcurrentQueue<string> ApiQueries { get; } = new();

    public ConcurrentQueue<string> Polls { get; } = new();

    public int StorageRequests => Volatile.Read(ref _storageRequests);

    public long StorageBytes => Interlocked.Read(ref _storageBytes);

    public int AttributeRequests => Volatile.Read(ref _attributeRequests);

    // ---- Faults -------------------------------------------------------------------------------

    /// <summary>Gets answers given instead of the next <c>/cs</c> requests (HTTP status, body).</summary>
    public ConcurrentQueue<(int Status, string Body)> ApiFaults { get; } = new();

    /// <summary>Gets HTTP statuses given instead of the next storage downloads.</summary>
    public ConcurrentQueue<int> StorageFaults { get; } = new();

    /// <summary>Gets or sets a value indicating whether the API answers every request with HTTP 400 (MEGA unreachable).</summary>
    public bool ApiDown { get; set; }

    /// <summary>Gets or sets the error code answered to change polls (sequence number unknown).</summary>
    public int? PollError { get; set; }

    /// <summary>Gets or sets a handler that answers commands first (null: the default answer).</summary>
    public Func<JsonElement, IQueryCollection, object?>? OnCommand { get; set; }

    public static async Task<FakeMega> StartAsync(bool account = false)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        var mega = new FakeMega(app, account);
        app.MapPost("/cs", mega.ApiAsync);
        app.MapPost("/sc", mega.PollAsync);
        app.MapGet("/dl/{handle}/{range}", mega.DownloadAsync);
        app.MapPost("/fa/{type}", mega.AttributesAsync);
        await app.StartAsync();
        return mega;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    public int Count(string command) => Commands.Count(c => c.Command.GetProperty("a").GetString() == command);

    /// <summary>Points every new API client at this server until disposed (sources build their own clients).</summary>
    public IDisposable AsDefaultApi()
    {
        var previous = MegaApiClient.DefaultApiBase;
        MegaApiClient.DefaultApiBase = Url;
        return new Restore(() => MegaApiClient.DefaultApiBase = previous);
    }

    /// <summary>Source of this account or link, as the plugin builds it from its settings.</summary>
    internal MegaSource NewSource(string? cacheDirectory = null) => new(
        IsAccount
            ? new Configuration.MegaAccountConfig { Id = "acc1", Kind = Configuration.MegaAccountConfig.KindAccount, Session = Session }
            : new Configuration.MegaAccountConfig { Id = "link1", Kind = Configuration.MegaAccountConfig.KindFolderLink, FolderLink = Link },
        cacheDirectory);

    private sealed class Restore(Action action) : IDisposable
    {
        public void Dispose() => action();
    }

    // ---- Content ------------------------------------------------------------------------------

    public string AddFolder(string? parent, string name, string? handle = null, bool live = false)
    {
        handle ??= NewHandle();
        var key = RandomNumberGenerator.GetBytes(16);
        var node = new Dictionary<string, object?>
        {
            ["h"] = handle,
            ["p"] = parent ?? Root,
            ["t"] = 1,
            ["a"] = Attrs(key, new { n = name }),
            ["k"] = EncKey(key),
            ["ts"] = 1_650_000_000,
        };
        AddRaw(node, handle, key);
        if (live)
        {
            Publish(new { a = "t", t = new { f = new[] { node } } });
        }

        return handle;
    }

    /// <summary>Adds a file, encrypted with a new key. <paramref name="mtime"/>: modification time in its fingerprint.</summary>
    public string AddFile(string? parent, string name, byte[] content, long? mtime = null, bool preview = false, string? handle = null, bool live = false)
    {
        handle ??= NewHandle();
        var raw = RandomNumberGenerator.GetBytes(32);
        var key = FileKey.Unpack(raw)!;
        var cipher = (byte[])content.Clone();
        MegaCrypto.CtrTransform(key.AesKey, key.Nonce, 0, cipher);
        var node = new Dictionary<string, object?>
        {
            ["h"] = handle,
            ["p"] = parent ?? Root,
            ["t"] = 0,
            ["a"] = Attrs(key.AesKey, mtime is long m ? new { n = name, c = Fingerprint(m) } : new { n = name }),
            ["k"] = EncKey(raw),
            ["s"] = content.LongLength,
            ["ts"] = 1_650_000_000,
        };
        byte[]? image = null;
        if (preview)
        {
            image = NewJpeg();
            var fah = MegaCrypto.B64Encode(RandomNumberGenerator.GetBytes(8));
            var enc = (byte[])image.Clone();
            MegaCrypto.CbcEncrypt(key.AesKey, enc);
            node["fa"] = $"{Bundle}:1*{fah}";
            lock (_gate)
            {
                _attributes[fah] = enc;
            }
        }

        lock (_gate)
        {
            _files[handle] = new StoredFile(content, cipher, key, image);
        }

        AddRaw(node, handle, raw);
        if (live)
        {
            Publish(new { a = "t", t = new { f = new[] { node } } });
        }

        return handle;
    }

    public byte[] Content(string handle) => _files[handle].Plain;

    public byte[] Preview(string handle) => _files[handle].Preview!;

    public FileKey KeyOf(string handle) => _files[handle].Key;

    /// <summary>Deletes a node and what is below it.</summary>
    public void Delete(string handle, bool live = false)
    {
        lock (_gate)
        {
            var gone = new HashSet<string>(StringComparer.Ordinal) { handle };
            bool more = true;
            while (more)
            {
                more = false;
                foreach (var n in _nodes)
                {
                    if (n["p"] is string p && gone.Contains(p) && gone.Add((string)n["h"]!))
                    {
                        more = true;
                    }
                }
            }

            _nodes.RemoveAll(n => gone.Contains((string)n["h"]!));
        }

        if (live)
        {
            Publish(new { a = "d", n = handle });
        }
    }

    public void Rename(string handle, string name, bool live = false)
    {
        string at;
        lock (_gate)
        {
            var node = _nodes.Single(n => (string)n["h"]! == handle);
            var key = _files.TryGetValue(handle, out var f) ? f.Key.AesKey : NodeKeys[handle];
            at = Attrs(key, new { n = name });
            node["a"] = at;
        }

        if (live)
        {
            Publish(new { a = "u", n = handle, at });
        }
    }

    /// <summary>Queues one batch of action packets for the next change poll.</summary>
    public void Publish(params object[] packets)
    {
        lock (_gate)
        {
            _packets.Enqueue(packets);
        }
    }

    /// <summary>Gets the raw keys of the nodes (16 bytes for folders, 32 for files).</summary>
    public Dictionary<string, byte[]> NodeKeys { get; } = new(StringComparer.Ordinal);

    public static string Attrs(byte[] key, object attributes)
    {
        var json = Encoding.UTF8.GetBytes("MEGA" + JsonSerializer.Serialize(attributes));
        var padded = new byte[(json.Length + 15) / 16 * 16];
        json.CopyTo(padded, 0);
        MegaCrypto.CbcEncrypt(key, padded);
        return MegaCrypto.B64Encode(padded);
    }

    public static string Fingerprint(long mtime)
    {
        var fp = new byte[16 + 1 + 4];
        RandomNumberGenerator.Fill(fp.AsSpan(0, 16));
        fp[16] = 4;
        BinaryPrimitives.WriteInt32LittleEndian(fp.AsSpan(17), (int)mtime);
        return MegaCrypto.B64Encode(fp);
    }

    /// <summary>Random JPEG-looking bytes, a multiple of 16 long (attributes are padded to whole AES blocks).</summary>
    public static byte[] NewJpeg()
    {
        var data = RandomNumberGenerator.GetBytes(1024);
        data[0] = 0xFF;
        data[1] = 0xD8;
        return data;
    }

    private static byte[] NewSid()
    {
        var sid = RandomNumberGenerator.GetBytes(43);
        sid[0] = 0x42;
        return sid;
    }

    private string NewHandle() => "Hnd" + Interlocked.Increment(ref _handles).ToString("D5", System.Globalization.CultureInfo.InvariantCulture);

    private string EncKey(byte[] key)
    {
        var enc = (byte[])key.Clone();
        MegaCrypto.EcbEncrypt(IsAccount ? MasterKey : FolderKey, enc);
        return (IsAccount ? User : FolderHandle) + ":" + MegaCrypto.B64Encode(enc);
    }

    private void AddRaw(Dictionary<string, object?> node, string handle, byte[] key)
    {
        lock (_gate)
        {
            _nodes.RemoveAll(n => (string)n["h"]! == handle);
            _nodes.Add(node);
            NodeKeys[handle] = key;
        }
    }

    // ---- Endpoints ----------------------------------------------------------------------------

    private async Task ApiAsync(HttpContext ctx)
    {
        ApiQueries.Enqueue(ctx.Request.QueryString.Value ?? string.Empty);
        if (ApiDown)
        {
            ctx.Response.StatusCode = 400;
            return;
        }

        if (ApiFaults.TryDequeue(out var fault))
        {
            ctx.Response.StatusCode = fault.Status;
            await ctx.Response.WriteAsync(fault.Body);
            return;
        }

        using var doc = await JsonDocument.ParseAsync(ctx.Request.Body);
        var results = new List<object?>();
        foreach (var command in doc.RootElement.EnumerateArray())
        {
            var c = command.Clone();
            Commands.Enqueue((ctx.Request.QueryString.Value ?? string.Empty, c));
            results.Add(OnCommand?.Invoke(c, ctx.Request.Query) ?? Answer(c, ctx.Request.Query));
        }

        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsync(JsonSerializer.Serialize(results));
    }

    private object Answer(JsonElement c, IQueryCollection query)
    {
        var a = c.GetProperty("a").GetString();
        string? sid = query["sid"];
        if (a == "us0")
        {
            return new { v = 2, s = MegaCrypto.B64Encode(Salt) };
        }

        if (a == "us")
        {
            return sid is null ? Login(c) : sid == Sid ? new { sek = MegaCrypto.B64Encode(SessionKey), u = User } : (object)(-15);
        }

        if (IsAccount ? sid != Sid : query["n"] != FolderHandle)
        {
            return IsAccount ? -15 : -9;
        }

        switch (a)
        {
            case "f":
                lock (_gate)
                {
                    return new { f = _nodes.Select(n => new Dictionary<string, object?>(n)).ToList(), ok = Array.Empty<object>(), sn = Sn };
                }

            case "g":
                var h = c.GetProperty("n").GetString()!;
                lock (_gate)
                {
                    if (!_files.TryGetValue(h, out var file) || _nodes.All(n => (string)n["h"]! != h))
                    {
                        return -9;
                    }

                    return new { s = file.Plain.LongLength, g = Url + "dl/" + h, at = string.Empty };
                }

            case "ufa":
                return new { p = Url + "fa" };
            case "ug":
                return new { u = User, email = Email };
            default:
                // "uga" (no ^!keys attribute) and anything unknown.
                return -9;
        }
    }

    private object Login(JsonElement c)
    {
        var user = c.GetProperty("user").GetString();
        var derived = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(Password), Salt, 100_000, HashAlgorithmName.SHA512, 32);
        if (user != Email.ToLowerInvariant() || c.GetProperty("uh").GetString() != MegaCrypto.B64Encode(derived.AsSpan(16, 16)))
        {
            return -9;
        }

        if (Mfa is not null && (!c.TryGetProperty("mfa", out var mfa) || mfa.GetString() != Mfa))
        {
            return -26;
        }

        var k = (byte[])MasterKey.Clone();
        MegaCrypto.EcbEncrypt(derived.AsSpan(0, 16), k);
        return new { k = MegaCrypto.B64Encode(k), tsid = Sid, u = User, sek = MegaCrypto.B64Encode(SessionKey) };
    }

    private async Task PollAsync(HttpContext ctx)
    {
        Polls.Enqueue(ctx.Request.QueryString.Value ?? string.Empty);
        if (ApiDown)
        {
            ctx.Response.StatusCode = 400;
            return;
        }

        object answer;
        if (PollError is int code)
        {
            answer = code;
        }
        else
        {
            lock (_gate)
            {
                if (_packets.TryDequeue(out var batch))
                {
                    _sn++;
                    answer = new { a = batch, sn = Sn };
                }
                else
                {
                    answer = new { w = Url + "wait", sn = Sn };
                }
            }
        }

        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsync(JsonSerializer.Serialize(answer));
    }

    private async Task DownloadAsync(string handle, string range, HttpContext ctx)
    {
        Interlocked.Increment(ref _storageRequests);
        if (StorageFaults.TryDequeue(out var status))
        {
            ctx.Response.StatusCode = status;
            return;
        }

        StoredFile? file;
        lock (_gate)
        {
            _files.TryGetValue(handle, out file);
        }

        var parts = range.Split('-');
        if (file is null || parts.Length != 2 || !long.TryParse(parts[0], out var from) || !long.TryParse(parts[1], out var to) || from > to)
        {
            ctx.Response.StatusCode = 404;
            return;
        }

        to = Math.Min(to, file.Cipher.LongLength - 1);
        int length = (int)(to - from + 1);
        ctx.Response.ContentLength = length;
        Interlocked.Add(ref _storageBytes, length);
        await ctx.Response.Body.WriteAsync(file.Cipher.AsMemory((int)from, length), ctx.RequestAborted);
    }

    private async Task AttributesAsync(string type, HttpContext ctx)
    {
        Interlocked.Increment(ref _attributeRequests);
        using var body = new MemoryStream();
        await ctx.Request.Body.CopyToAsync(body);
        var handles = body.ToArray();
        using var output = new MemoryStream();
        for (int i = 0; i + 8 <= handles.Length; i += 8)
        {
            var fah = MegaCrypto.B64Encode(handles.AsSpan(i, 8));
            byte[]? data;
            lock (_gate)
            {
                _attributes.TryGetValue(fah, out data);
            }

            if (data is null || type != "1")
            {
                continue;
            }

            output.Write(handles, i, 8);
            output.Write(BitConverter.GetBytes(data.Length));
            output.Write(data);
        }

        await ctx.Response.Body.WriteAsync(output.ToArray());
    }

    private sealed record StoredFile(byte[] Plain, byte[] Cipher, FileKey Key, byte[]? Preview);
}
