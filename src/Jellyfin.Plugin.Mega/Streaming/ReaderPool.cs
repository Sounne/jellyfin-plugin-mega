using System;
using System.Collections.Generic;
using System.Threading;
using Jellyfin.Plugin.Mega.Mega;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Mega.Streaming;

/// <summary>
/// One <see cref="MegaFileReader"/> per file, shared by all requests on it (ffmpeg seeks open new HTTP
/// connections: the read-ahead and block cache survive them). Readers unused for a while are closed.
/// </summary>
internal sealed class ReaderPool : IDisposable
{
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly Timer _timer;
    private readonly ILogger _logger;

    public ReaderPool(ILogger logger)
    {
        _logger = logger;
        _timer = new Timer(_ => Sweep(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    public BlockDiskCache? Disk { get; set; }

    public int OpenReaders
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    public Lease Acquire(MegaSource source, MegaNode node, int aheadMegabytes, int connections)
    {
        var key = source.Id + "/" + node.Handle;
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var e) && (e.Reader.IsFailed || e.Reader.Node.Size != node.Size))
            {
                if (e.RefCount == 0)
                {
                    e.Reader.Dispose();
                }

                _entries.Remove(key);
                e = null;
            }

            if (e is null)
            {
                e = new Entry(new MegaFileReader(source, node, Disk, _logger, aheadMegabytes, connections));
                _entries[key] = e;
            }

            e.RefCount++;
            return new Lease(this, key, e);
        }
    }

    private void Release(string key, Entry entry)
    {
        lock (_gate)
        {
            entry.RefCount--;
            if (entry.RefCount == 0 && (!_entries.TryGetValue(key, out var current) || current != entry))
            {
                entry.Reader.Dispose();
            }
        }
    }

    private void Sweep()
    {
        var closing = new List<MegaFileReader>();
        lock (_gate)
        {
            foreach (var kv in new List<KeyValuePair<string, Entry>>(_entries))
            {
                if (kv.Value.RefCount == 0 && kv.Value.Reader.IdleMilliseconds > 120_000)
                {
                    _entries.Remove(kv.Key);
                    closing.Add(kv.Value.Reader);
                }
            }
        }

        foreach (var r in closing)
        {
            r.Dispose();
        }
    }

    public void Dispose()
    {
        _timer.Dispose();
        lock (_gate)
        {
            foreach (var e in _entries.Values)
            {
                e.Reader.Dispose();
            }

            _entries.Clear();
        }
    }

    internal sealed class Entry
    {
        public Entry(MegaFileReader reader)
        {
            Reader = reader;
        }

        public MegaFileReader Reader { get; }

        public int RefCount { get; set; }
    }

    public sealed class Lease : IDisposable
    {
        private readonly ReaderPool _pool;
        private readonly string _key;
        private readonly Entry _entry;
        private int _released;

        internal Lease(ReaderPool pool, string key, Entry entry)
        {
            _pool = pool;
            _key = key;
            _entry = entry;
        }

        public MegaFileReader Reader => _entry.Reader;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                _pool.Release(_key, _entry);
            }
        }
    }
}
