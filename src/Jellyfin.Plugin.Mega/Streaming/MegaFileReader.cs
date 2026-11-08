using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Mega.Mega;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Mega.Streaming;

/// <summary>
/// Decrypted MEGA file with random access, shared by every request on that file
/// (client range requests, ffprobe, ffmpeg).
/// <para>
/// The file is cut into 1 MiB blocks kept in memory. Several worker connections download blocks in
/// parallel: first the blocks a reader is waiting for, then the read-ahead window in front of the most
/// recent read positions. The window grows with sequential reading (a probe reading a few MB of a file
/// does not trigger a large download; playback quickly gets the full window). Blocks right in front of a
/// read position are fetched one per connection, further ones by longer runs. A worker busy with
/// read-ahead gives up its run as soon as a reader waits for a block nobody fetches (seek), so a seek
/// never waits behind read-ahead. Blocks are decrypted (AES-CTR) as they arrive; the start and end of the file are
/// also kept on disk (still encrypted) so that probing and playback start are instant next time.
/// </para>
/// </summary>
internal sealed class MegaFileReader : IDisposable
{
    public const int BlockSize = 1 << 20;
    private const int MaxRegions = 3;
    private const int RunBlocks = 8;
    private const int MinWindow = 3;
    private readonly MegaSource _source;
    private readonly MegaNode _node;
    private readonly BlockDiskCache? _disk;
    private readonly ILogger _logger;
    private readonly int _ahead;
    private readonly int _workerCount;
    private readonly int _maxBlocks;
    private readonly long _lastBlock;
    private readonly object _gate = new();
    private readonly Dictionary<long, Block> _blocks = new();
    private readonly List<Region> _regions = new();
    private readonly List<long> _prefetch = new();
    private readonly SemaphoreSlim _wake = new(0, int.MaxValue);
    private readonly CancellationTokenSource _cts = new();
    private int _workersStarted;
    private int _idleWorkers;
    private Exception? _fatal;
    private string? _url;
    private bool _plainHttp;
    private long _lastAccess = Environment.TickCount64;
    private bool _disposed;

    public MegaFileReader(MegaSource source, MegaNode node, BlockDiskCache? disk, ILogger logger, int aheadMegabytes, int connections)
    {
        _source = source;
        _node = node;
        _disk = disk;
        _logger = logger;
        _ahead = Math.Clamp(aheadMegabytes, 4, 512);
        _workerCount = Math.Clamp(connections, 1, 16);
        _maxBlocks = _ahead + 32;
        _lastBlock = Math.Max(0, (node.Size - 1) / BlockSize);

        // The end of video files (MP4 index, MKV cues) is needed right after the start.
        if (_lastBlock > 2 && MediaFileTypes.IsVideo(node.Name))
        {
            _prefetch.Add(_lastBlock);
        }
    }

    public long Size => _node.Size;

    public MegaNode Node => _node;

    public bool IsFailed => _fatal is not null;

    public long IdleMilliseconds => Environment.TickCount64 - Interlocked.Read(ref _lastAccess);

    /// <summary>
    /// Reads at most <paramref name="buffer"/>.Length decrypted bytes at <paramref name="offset"/>.
    /// Only waits for the first block involved; the following ones are copied only if already there.
    /// </summary>
    public async ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        if (offset >= Size || buffer.IsEmpty)
        {
            return 0;
        }

        Interlocked.Exchange(ref _lastAccess, Environment.TickCount64);
        int done = 0;
        long off = offset;
        while (done < buffer.Length && off < Size)
        {
            long idx = off / BlockSize;
            byte[]? data = done == 0 ? await GetBlockAsync(idx, cancellationToken).ConfigureAwait(false) : TryGetReady(idx);
            if (data is null)
            {
                break;
            }

            int inBlock = (int)(off - (idx * BlockSize));
            if (inBlock >= data.Length)
            {
                break;
            }

            int n = Math.Min(data.Length - inBlock, buffer.Length - done);
            data.AsSpan(inBlock, n).CopyTo(buffer.Span[done..]);
            done += n;
            off += n;
        }

        return done;
    }

    private int BlockLength(long idx) => (int)(Math.Min((idx + 1) * BlockSize, Size) - (idx * BlockSize));

    private byte[]? TryGetReady(long idx)
    {
        lock (_gate)
        {
            if (_blocks.TryGetValue(idx, out var b) && b.Tcs.Task.IsCompletedSuccessfully)
            {
                b.LastUse = Environment.TickCount64;
                return b.Tcs.Task.Result;
            }
        }

        return null;
    }

    private async Task<byte[]> GetBlockAsync(long idx, CancellationToken cancellationToken)
    {
        Block b;
        bool created;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_fatal is not null)
            {
                throw new MegaException(_fatal.Message, _fatal);
            }

            Touch(idx);
            created = !_blocks.TryGetValue(idx, out b!);
            if (created)
            {
                b = new Block();
                _blocks[idx] = b;
            }

            b.Waiters++;
            b.LastUse = Environment.TickCount64;
            StartWorkersLocked();
        }

        try
        {
            if (created && _disk is not null && _disk.IsCandidate(idx, _lastBlock))
            {
                var enc = _disk.TryLoad(_source.Id, _node.Handle, idx, BlockLength(idx));
                if (enc is not null)
                {
                    Complete(idx, Decrypt(idx, enc));
                }
            }

            if (!b.Tcs.Task.IsCompleted)
            {
                lock (_gate)
                {
                    WakeLocked();
                }
            }

            return await b.Tcs.Task.WaitAsync(TimeSpan.FromSeconds(120), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                b.Waiters--;
            }
        }
    }

    /// <summary>Records the read position: it extends a tracked read, or starts a new one (the oldest is dropped).</summary>
    private void Touch(long idx)
    {
        if (_regions.Count > 0 && _regions[0].Pos == idx)
        {
            return;
        }

        long runStart = idx;
        for (int i = _regions.Count - 1; i >= 0; i--)
        {
            var r = _regions[i];
            if (idx + 2 >= r.Pos && idx <= r.Pos + Window(r) + 1)
            {
                runStart = Math.Min(runStart, r.Start);
                _regions.RemoveAt(i);
            }
        }

        _regions.Insert(0, new Region(runStart, idx));
        if (_regions.Count > MaxRegions)
        {
            _regions.RemoveRange(MaxRegions, _regions.Count - MaxRegions);
        }

        WakeLocked();
    }

    /// <summary>Read-ahead of a read position: as many blocks as were read sequentially so far, up to the setting.</summary>
    private long Window(Region r) => Math.Clamp(r.Pos - r.Start + 1, MinWindow, _ahead);

    /// <summary>Wakes an idle worker (the semaphore count stays bounded).</summary>
    private void WakeLocked()
    {
        if (_idleWorkers > 0 && _wake.CurrentCount < _workerCount)
        {
            _wake.Release();
        }
    }

    private void StartWorkersLocked()
    {
        while (_workersStarted < _workerCount)
        {
            _workersStarted++;
            _ = Task.Run(WorkerAsync);
        }
    }

    private bool IsMissing(long idx) => !_blocks.TryGetValue(idx, out var b) || (!b.InFlight && !b.Tcs.Task.IsCompleted);

    private bool InWindow(long idx)
    {
        foreach (var r in _regions)
        {
            if (idx >= r.Pos && idx <= r.Pos + Window(r))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Longest run starting at <paramref name="idx"/>: one block right in front of a read position (several
    /// connections share the next blocks, so the data needed now arrives in parallel), longer runs further.
    /// </summary>
    private int RunLimit(long idx)
    {
        long distance = long.MaxValue;
        foreach (var r in _regions)
        {
            if (idx >= r.Pos)
            {
                distance = Math.Min(distance, idx - r.Pos);
            }
        }

        return distance == long.MaxValue ? RunBlocks : (int)Math.Clamp(distance / _workerCount, 1, RunBlocks);
    }

    /// <summary>Next run of blocks to download (claimed by the caller), or false when there is nothing to do.</summary>
    private bool PickRunLocked(out long start, out int count)
    {
        start = -1;
        count = 0;
        long first = -1;
        foreach (var kv in _blocks)
        {
            var b = kv.Value;
            if (b.Waiters > 0 && !b.InFlight && !b.Tcs.Task.IsCompleted && (first < 0 || kv.Key < first))
            {
                first = kv.Key;
            }
        }

        if (first < 0)
        {
            for (int i = 0; i < _prefetch.Count; i++)
            {
                if (IsMissing(_prefetch[i]))
                {
                    first = _prefetch[i];
                    _prefetch.RemoveAt(i);
                    break;
                }
            }
        }

        if (first < 0)
        {
            foreach (var r in _regions)
            {
                long end = Math.Min(r.Pos + Window(r), _lastBlock);
                for (long i = r.Pos; i <= end; i++)
                {
                    if (IsMissing(i))
                    {
                        first = i;
                        break;
                    }
                }

                if (first >= 0)
                {
                    break;
                }
            }
        }

        if (first < 0)
        {
            return false;
        }

        count = 1;
        int limit = RunLimit(first);
        while (count < limit && first + count <= _lastBlock && IsMissing(first + count) && InWindow(first + count))
        {
            count++;
        }

        for (long i = first; i < first + count; i++)
        {
            if (!_blocks.TryGetValue(i, out var b))
            {
                b = new Block();
                _blocks[i] = b;
            }

            b.InFlight = true;
        }

        start = first;
        return true;
    }

    private void ReleaseClaims(long from, long to)
    {
        lock (_gate)
        {
            for (long i = from; i < to; i++)
            {
                if (_blocks.TryGetValue(i, out var b) && !b.Tcs.Task.IsCompleted)
                {
                    b.InFlight = false;
                    if (b.Waiters == 0)
                    {
                        _blocks.Remove(i);
                    }
                }
            }
        }
    }

    /// <summary>A reader waits for a block nobody fetches, no worker is free, and nobody waits for the rest of this run.</summary>
    private bool ShouldYield(long next, long end)
    {
        lock (_gate)
        {
            if (_idleWorkers > 0)
            {
                return false;
            }

            for (long i = next; i < end; i++)
            {
                if (_blocks.TryGetValue(i, out var mine) && mine.Waiters > 0)
                {
                    return false;
                }
            }

            foreach (var b in _blocks.Values)
            {
                if (b.Waiters > 0 && !b.InFlight && !b.Tcs.Task.IsCompleted)
                {
                    return true;
                }
            }

            return false;
        }
    }

    private void Complete(long idx, byte[] data)
    {
        lock (_gate)
        {
            if (!_blocks.TryGetValue(idx, out var b))
            {
                b = new Block();
                _blocks[idx] = b;
            }

            b.InFlight = false;
            b.LastUse = Environment.TickCount64;
            b.Tcs.TrySetResult(data);
            Evict();
        }
    }

    /// <summary>Frees the least recently used blocks, except those just in front of the latest read and those awaited.</summary>
    private void Evict()
    {
        int ready = 0;
        foreach (var b in _blocks.Values)
        {
            if (b.Tcs.Task.IsCompletedSuccessfully)
            {
                ready++;
            }
        }

        if (ready <= _maxBlocks)
        {
            return;
        }

        long latest = _regions.Count > 0 ? _regions[0].Pos : -1;
        long window = _regions.Count > 0 ? Window(_regions[0]) : 0;
        var victims = _blocks
            .Where(kv => kv.Value.Tcs.Task.IsCompletedSuccessfully && kv.Value.Waiters == 0 && !(latest >= 0 && kv.Key >= latest && kv.Key <= latest + window))
            .OrderBy(kv => kv.Value.LastUse)
            .Take(ready - _maxBlocks)
            .Select(kv => kv.Key)
            .ToList();
        foreach (var v in victims)
        {
            _blocks.Remove(v);
        }
    }

    private void Fail(Exception e)
    {
        List<Block> pending;
        lock (_gate)
        {
            _fatal = e;
            pending = _blocks.Values.Where(b => !b.Tcs.Task.IsCompleted).ToList();
            foreach (var kv in _blocks.Where(kv => !kv.Value.Tcs.Task.IsCompleted).ToList())
            {
                _blocks.Remove(kv.Key);
            }
        }

        foreach (var b in pending)
        {
            b.Tcs.TrySetException(e);
        }
    }

    private static bool IsFatal(Exception e) => e is MegaException m
        && (m.IsQuotaExceeded || m.IsBadSession || m.Code is -9 or -11 or -14 or -16 || m.HttpStatus == 509);

    private async Task WorkerAsync()
    {
        var ct = _cts.Token;
        int fails = 0;
        while (!ct.IsCancellationRequested)
        {
            long start;
            int count;
            bool got = false;
            start = 0;
            count = 0;
            lock (_gate)
            {
                if (_fatal is null)
                {
                    got = PickRunLocked(out start, out count);
                }

                if (!got)
                {
                    _idleWorkers++;
                }
            }

            if (!got)
            {
                try
                {
                    await _wake.WaitAsync(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                finally
                {
                    lock (_gate)
                    {
                        _idleWorkers--;
                    }
                }

                continue;
            }

            try
            {
                await FetchRunAsync(start, count, ct).ConfigureAwait(false);
                fails = 0;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                ReleaseClaims(start, start + count);
                return;
            }
            catch (Exception e)
            {
                ReleaseClaims(start, start + count);
                fails++;
                if (IsFatal(e) || fails >= 6)
                {
                    _logger.LogWarning("MEGA stream of {Name} failed: {Message}", _node.Name, e.Message);
                    Fail(e);
                    return;
                }

                _logger.LogDebug(e, "MEGA block fetch failed for {Name}, retrying", _node.Name);
                try
                {
                    await Task.Delay(300 * fails, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private byte[] Decrypt(long idx, byte[] data)
    {
        using var aes = MegaCrypto.CreateAes(_node.Key!.AesKey);
        MegaCrypto.CtrTransform(aes, _node.Key.Nonce, idx * BlockSize, data);
        return data;
    }

    private async Task FetchRunAsync(long start, int count, CancellationToken ct)
    {
        long end = start + count;

        // Start / end of the file already on disk.
        while (start < end && _disk is not null && _disk.IsCandidate(start, _lastBlock))
        {
            var enc = _disk.TryLoad(_source.Id, _node.Handle, start, BlockLength(start));
            if (enc is null)
            {
                break;
            }

            Complete(start, Decrypt(start, enc));
            start++;
        }

        if (start >= end)
        {
            return;
        }

        long from = start * BlockSize;
        long to = Math.Min(end * BlockSize, Size);
        using var resp = await OpenRangeAsync(from, to, ct).ConfigureAwait(false);
        await using var body = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        for (long idx = start; idx < end; idx++)
        {
            var buf = new byte[BlockLength(idx)];
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(60));
                await body.ReadExactlyAsync(buf, timeout.Token).ConfigureAwait(false);
            }

            if (_disk is not null && _disk.IsCandidate(idx, _lastBlock))
            {
                _disk.Save(_source.Id, _node.Handle, idx, buf);
            }

            Complete(idx, Decrypt(idx, buf));
            if (idx + 1 < end && ShouldYield(idx + 1, end))
            {
                ReleaseClaims(idx + 1, end);
                return;
            }
        }
    }

    private async Task<HttpResponseMessage> OpenRangeAsync(long from, long to, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            var url = _url ??= await _source.GetDownloadUrlAsync(_node.Handle, false, _plainHttp, ct).ConfigureAwait(false);
            HttpResponseMessage resp;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                using var req = new HttpRequestMessage(HttpMethod.Get, $"{url}/{from}-{to - 1}");
                resp = await MegaHttp.Storage.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            }
            catch (HttpRequestException e) when (MegaHttp.IsTlsRefusal(e) && !_plainHttp)
            {
                _plainHttp = true;
                _url = null;
                continue;
            }

            if (resp.IsSuccessStatusCode)
            {
                return resp;
            }

            int status = (int)resp.StatusCode;
            resp.Dispose();
            if (status == 509)
            {
                throw new MegaException("MEGA transfer quota exceeded, try again later") { HttpStatus = 509 };
            }

            if (status is 403 or 404 or 410 && attempt < 2)
            {
                // Expired URL: one fresh request for all connections.
                lock (_gate)
                {
                    if (_url == url)
                    {
                        _url = null;
                    }
                }

                _source.ForgetDownloadUrl(_node.Handle);
                continue;
            }

            throw new MegaException($"HTTP {status} from the MEGA storage server") { HttpStatus = status };
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _cts.Cancel();
        Fail(new ObjectDisposedException(nameof(MegaFileReader)));
    }

    /// <summary>A sequential read: where it started and the block read last.</summary>
    private readonly record struct Region(long Start, long Pos);

    private sealed class Block
    {
        public TaskCompletionSource<byte[]> Tcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool InFlight { get; set; }

        public int Waiters { get; set; }

        public long LastUse { get; set; }
    }
}
