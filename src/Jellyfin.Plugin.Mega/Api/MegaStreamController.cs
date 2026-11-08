using System;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Mega.Mega;
using Jellyfin.Plugin.Mega.Services;
using Jellyfin.Plugin.Mega.Streaming;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Mega.Api;

/// <summary>
/// Stream endpoint written in the <c>.strm</c> files. Used by the server itself (ffprobe, ffmpeg,
/// direct streaming proxy): no Jellyfin login, but a signed URL and, by default, local callers only.
/// </summary>
[ApiController]
[Route("MegaFs/stream")]
[AllowAnonymous]
public class MegaStreamController : ControllerBase
{
    private readonly MegaManager _manager;
    private readonly ILogger<MegaStreamController> _logger;

    public MegaStreamController(MegaManager manager, ILogger<MegaStreamController> logger)
    {
        _manager = manager;
        _logger = logger;
    }

    /// <summary>Decrypted file content, with byte ranges.</summary>
    [HttpGet("{sourceId}/{handle}/{signature}/{fileName}")]
    [HttpHead("{sourceId}/{handle}/{signature}/{fileName}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status206PartialContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetStream(
        [FromRoute] string sourceId,
        [FromRoute] string handle,
        [FromRoute] string signature,
        [FromRoute] string fileName,
        CancellationToken cancellationToken)
    {
        var cfg = _manager.Configuration;
        if (cfg.RestrictStreamToLocalhost && !IsLocal(HttpContext.Connection.RemoteIpAddress))
        {
            return Forbid403();
        }

        if (!StreamUrls.Verify(cfg.StreamSecret, sourceId, handle, signature))
        {
            return Forbid403();
        }

        (MegaSource Source, MegaNode Node)? found;
        try
        {
            found = await _manager.ResolveAsync(sourceId, handle, cancellationToken).ConfigureAwait(false);
        }
        catch (MegaException e)
        {
            _logger.LogWarning("MEGA stream {Name}: {Message}", fileName, e.Message);
            return StatusCode(StatusCodes.Status502BadGateway, e.Message);
        }

        if (found is null)
        {
            return NotFound();
        }

        return new MegaRangeResult(_manager, found.Value.Source, found.Value.Node, _logger);
    }

    private ObjectResult Forbid403() => StatusCode(StatusCodes.Status403Forbidden, "forbidden");

    private static bool IsLocal(IPAddress? address)
    {
        if (address is null)
        {
            return true;
        }

        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
            if (IPAddress.IsLoopback(address))
            {
                return true;
            }
        }

        // The server calling itself through one of its own addresses (custom stream base URL).
        foreach (var own in OwnAddresses.Value)
        {
            if (own.Equals(address))
            {
                return true;
            }
        }

        return false;
    }

    private static readonly Lazy<IPAddress[]> OwnAddresses = new(() =>
    {
        try
        {
            return System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .SelectMany(i => i.GetIPProperties().UnicastAddresses)
                .Select(a => a.Address.IsIPv4MappedToIPv6 ? a.Address.MapToIPv4() : a.Address)
                .ToArray();
        }
        catch (System.Net.NetworkInformation.NetworkInformationException)
        {
            return Array.Empty<IPAddress>();
        }
    });
}
