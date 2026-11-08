using System;
using System.Buffers;
using System.Globalization;
using System.Threading.Tasks;
using Jellyfin.Plugin.Mega.Mega;
using Jellyfin.Plugin.Mega.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Mega.Streaming;

/// <summary>HTTP response serving a decrypted MEGA file with full byte-range support (seeking).</summary>
internal sealed class MegaRangeResult : IActionResult
{
    private readonly MegaManager _manager;
    private readonly MegaSource _source;
    private readonly MegaNode _node;
    private readonly ILogger _logger;

    public MegaRangeResult(MegaManager manager, MegaSource source, MegaNode node, ILogger logger)
    {
        _manager = manager;
        _source = source;
        _node = node;
        _logger = logger;
    }

    public async Task ExecuteResultAsync(ActionContext context)
    {
        var http = context.HttpContext;
        var req = http.Request;
        var resp = http.Response;
        long size = _node.Size;

        resp.Headers.AcceptRanges = "bytes";
        resp.ContentType = MediaFileTypes.GetMimeType(_node.Name);
        resp.Headers.LastModified = _node.Modified.ToString("R", CultureInfo.InvariantCulture);
        resp.Headers.ETag = "\"" + _node.Handle + "-" + size.ToString(CultureInfo.InvariantCulture) + "\"";
        resp.Headers.CacheControl = "no-transform";

        long start = 0;
        long end = size - 1;
        bool partial = false;
        var range = req.Headers.Range.ToString();
        if (!string.IsNullOrEmpty(range) && size > 0)
        {
            if (!TryParseRange(range, size, out start, out end))
            {
                resp.StatusCode = StatusCodes.Status416RangeNotSatisfiable;
                resp.Headers.ContentRange = "bytes */" + size.ToString(CultureInfo.InvariantCulture);
                return;
            }

            partial = true;
        }

        long length = size == 0 ? 0 : end - start + 1;
        resp.StatusCode = partial ? StatusCodes.Status206PartialContent : StatusCodes.Status200OK;
        if (partial)
        {
            resp.Headers.ContentRange = string.Create(CultureInfo.InvariantCulture, $"bytes {start}-{end}/{size}");
        }

        resp.ContentLength = length;
        if (HttpMethods.IsHead(req.Method) || length == 0)
        {
            return;
        }

        http.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        var ct = http.RequestAborted;
        var cfg = _manager.Configuration;
        using var lease = _manager.Readers.Acquire(_source, _node, cfg.ReadAheadMegabytes, cfg.ConnectionsPerFile);
        var reader = lease.Reader;
        var buffer = ArrayPool<byte>.Shared.Rent(MegaFileReader.BlockSize);
        long pos = start;
        long remaining = length;
        try
        {
            while (remaining > 0)
            {
                int want = (int)Math.Min(buffer.Length, remaining);
                int n = await reader.ReadAsync(pos, buffer.AsMemory(0, want), ct).ConfigureAwait(false);
                if (n <= 0)
                {
                    break;
                }

                await resp.Body.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                pos += n;
                remaining -= n;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Client gone (seek, stop): nothing to do.
        }
        catch (Exception e) when (e is MegaException or TimeoutException or System.Net.Http.HttpRequestException)
        {
            _logger.LogWarning("MEGA stream of {Name} interrupted at {Position}: {Message}", _node.Name, pos, e.Message);
            if (!resp.HasStarted)
            {
                resp.Headers.ContentRange = default;
                resp.ContentLength = null;
                resp.StatusCode = e is MegaException { IsQuotaExceeded: true } ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status502BadGateway;
                await resp.WriteAsync(e.Message, ct).ConfigureAwait(false);
            }
            else
            {
                http.Abort();
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Parses <c>bytes=a-b</c>, <c>bytes=a-</c> and <c>bytes=-n</c> (first range only).</summary>
    internal static bool TryParseRange(string header, long size, out long start, out long end)
    {
        start = 0;
        end = size - 1;
        var h = header.Trim();
        if (!h.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var spec = h[6..].Split(',')[0].Trim();
        int dash = spec.IndexOf('-', StringComparison.Ordinal);
        if (dash < 0)
        {
            return false;
        }

        var a = spec[..dash].Trim();
        var b = spec[(dash + 1)..].Trim();
        if (a.Length == 0)
        {
            if (!long.TryParse(b, NumberStyles.None, CultureInfo.InvariantCulture, out var suffix) || suffix <= 0)
            {
                return false;
            }

            start = Math.Max(0, size - suffix);
            end = size - 1;
            return true;
        }

        if (!long.TryParse(a, NumberStyles.None, CultureInfo.InvariantCulture, out start) || start >= size)
        {
            return false;
        }

        if (b.Length == 0)
        {
            end = size - 1;
            return true;
        }

        if (!long.TryParse(b, NumberStyles.None, CultureInfo.InvariantCulture, out end) || end < start)
        {
            return false;
        }

        end = Math.Min(end, size - 1);
        return true;
    }
}
