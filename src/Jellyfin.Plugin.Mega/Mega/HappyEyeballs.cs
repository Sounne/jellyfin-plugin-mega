using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Mega.Mega;

/// <summary>
/// Connects to the first address that answers (RFC 8305 "Happy Eyeballs"): IPv6 and IPv4 attempts are started
/// 250 ms apart instead of one after the other. Some MEGA storage servers publish an IPv6 address that is not
/// reachable from many networks; a plain connect waits for the TCP timeout on it before trying IPv4.
/// </summary>
internal static class HappyEyeballs
{
    private static readonly TimeSpan AttemptDelay = TimeSpan.FromMilliseconds(250);

    // Address family that answered last time, per host: tried first next time.
    private static readonly ConcurrentDictionary<string, AddressFamily> Preferred = new(StringComparer.OrdinalIgnoreCase);

    public static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var host = context.DnsEndPoint.Host;
        int port = context.DnsEndPoint.Port;
        var addresses = IPAddress.TryParse(host, out var literal)
            ? new[] { literal }
            : await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
        var ordered = Order(addresses, Preferred.TryGetValue(host, out var family) ? family : AddressFamily.InterNetworkV6);
        var socket = await RaceAsync(ordered, port, cancellationToken).ConfigureAwait(false);
        Preferred[host] = socket.AddressFamily;
        return new NetworkStream(socket, ownsSocket: true);
    }

    /// <summary>Alternates the address families, starting with <paramref name="first"/>.</summary>
    internal static List<IPAddress> Order(IReadOnlyList<IPAddress> addresses, AddressFamily first)
    {
        var a = addresses.Where(x => x.AddressFamily == first).ToList();
        var b = addresses.Where(x => x.AddressFamily != first).ToList();
        var result = new List<IPAddress>(addresses.Count);
        for (int i = 0; i < Math.Max(a.Count, b.Count); i++)
        {
            if (i < a.Count)
            {
                result.Add(a[i]);
            }

            if (i < b.Count)
            {
                result.Add(b[i]);
            }
        }

        return result;
    }

    private static async Task<Socket> RaceAsync(List<IPAddress> addresses, int port, CancellationToken cancellationToken)
    {
        if (addresses.Count == 0)
        {
            throw new SocketException((int)SocketError.HostNotFound);
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pending = new List<Task<Socket>>();
        Exception? lastError = null;
        int next = 0;
        try
        {
            while (true)
            {
                if (next < addresses.Count)
                {
                    pending.Add(ConnectOneAsync(addresses[next++], port, cts.Token));
                }

                if (pending.Count == 0)
                {
                    throw lastError ?? new SocketException((int)SocketError.HostUnreachable);
                }

                // Wait for an attempt to finish, or for the delay before starting the next address.
                var wait = next < addresses.Count ? Task.Delay(AttemptDelay, cts.Token) : Task.Delay(Timeout.Infinite, cts.Token);
                var finished = await Task.WhenAny(pending.Cast<Task>().Append(wait)).ConfigureAwait(false);
                if (finished == wait)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    continue;
                }

                var attempt = (Task<Socket>)finished;
                pending.Remove(attempt);
                if (attempt.IsCompletedSuccessfully)
                {
                    foreach (var loser in pending)
                    {
                        _ = loser.ContinueWith(t => t.Result.Dispose(), CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
                    }

                    return attempt.Result;
                }

                cancellationToken.ThrowIfCancellationRequested();
                lastError = attempt.Exception?.InnerException ?? lastError;
            }
        }
        finally
        {
            cts.Cancel();
        }
    }

    private static async Task<Socket> ConnectOneAsync(IPAddress address, int port, CancellationToken cancellationToken)
    {
        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(new IPEndPoint(address, port), cancellationToken).ConfigureAwait(false);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
