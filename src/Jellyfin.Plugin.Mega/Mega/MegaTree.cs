using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Mega.Mega;

/// <summary>Decrypts node keys: account (master key, share keys, RSA) or public folder link (folder key).</summary>
internal interface INodeKeyring
{
    /// <summary>Decrypts the <c>k</c> field of a node: 16 bytes (folder) or 32 bytes (file).</summary>
    byte[]? DecryptNodeKey(string? k, bool isFile, IReadOnlyDictionary<string, byte[]> shareKeys);

    /// <summary>Decrypts a share key (<c>sk</c> of a node, <c>k</c> of an <c>ok</c> entry).</summary>
    byte[]? DecryptShareKey(string? sk);
}

/// <summary>Account keyring.</summary>
internal sealed class AccountKeyring : INodeKeyring
{
    private readonly string _user;
    private readonly byte[] _master;
    private readonly MegaRsa? _rsa;

    public AccountKeyring(string user, byte[] master, MegaRsa? rsa)
    {
        _user = user;
        _master = master;
        _rsa = rsa;
    }

    public byte[]? DecryptNodeKey(string? k, bool isFile, IReadOnlyDictionary<string, byte[]> shareKeys)
    {
        if (string.IsNullOrEmpty(k))
        {
            return null;
        }

        int want = isFile ? 32 : 16;
        foreach (var part in k.Split('/'))
        {
            int colon = part.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                continue;
            }

            var owner = part[..colon];
            var enc = MegaCrypto.B64Decode(part[(colon + 1)..]);
            if (enc is null)
            {
                continue;
            }

            if (enc.Length > 32)
            {
                // Key encrypted with our RSA public key.
                if (owner != _user || _rsa is null)
                {
                    continue;
                }

                var m = _rsa.DecryptMpi(enc);
                if (m is null || m.Length < want)
                {
                    continue;
                }

                return m.AsSpan(0, want).ToArray();
            }

            byte[]? key = owner == _user ? _master : (shareKeys.TryGetValue(owner, out var sk) ? sk : null);
            if (key is null || enc.Length != want)
            {
                continue;
            }

            MegaCrypto.EcbDecrypt(key, enc);
            return enc;
        }

        return null;
    }

    public byte[]? DecryptShareKey(string? sk)
    {
        var data = MegaCrypto.B64Decode(sk);
        if (data is null)
        {
            return null;
        }

        if (data.Length == 16)
        {
            MegaCrypto.EcbDecrypt(_master, data);
            return data;
        }

        var m = _rsa?.DecryptMpi(data);
        return m is { Length: >= 16 } ? m.AsSpan(0, 16).ToArray() : null;
    }
}

/// <summary>Keyring of a public folder link: every key is encrypted with the folder key.</summary>
internal sealed class FolderLinkKeyring : INodeKeyring
{
    private readonly byte[] _folderKey;

    public FolderLinkKeyring(byte[] folderKey)
    {
        _folderKey = folderKey;
    }

    public byte[]? DecryptNodeKey(string? k, bool isFile, IReadOnlyDictionary<string, byte[]> shareKeys)
    {
        if (string.IsNullOrEmpty(k))
        {
            return null;
        }

        int want = isFile ? 32 : 16;
        foreach (var part in k.Split('/'))
        {
            int colon = part.IndexOf(':', StringComparison.Ordinal);
            var enc = MegaCrypto.B64Decode(colon >= 0 ? part[(colon + 1)..] : part);
            if (enc is null || enc.Length != want)
            {
                continue;
            }

            MegaCrypto.EcbDecrypt(_folderKey, enc);
            return enc;
        }

        return null;
    }

    public byte[]? DecryptShareKey(string? sk) => null;
}

/// <summary>Decrypted node tree of an account or of a public folder link.</summary>
internal sealed class MegaTree
{
    private readonly Dictionary<string, MegaNode> _nodes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _children = new(StringComparer.Ordinal);

    public string? CloudRoot { get; private set; }

    public string? RubbishRoot { get; private set; }

    /// <summary>Gets the incoming share roots.</summary>
    public List<string> InShares { get; } = new();

    /// <summary>Gets or sets the root of a public folder link.</summary>
    public string? LinkRoot { get; set; }

    /// <summary>Gets or sets the sequence number of the tree (for action packets).</summary>
    public string? Sn { get; set; }

    public int Count => _nodes.Count;

    /// <summary>Gets the share keys known for this tree (needed to decrypt nodes added later).</summary>
    public Dictionary<string, byte[]> ShareKeys { get; } = new(StringComparer.Ordinal);

    /// <summary>Gets the number of modifications made to the tree (nodes added, replaced or removed).</summary>
    public long Changes { get; private set; }

    /// <summary>Gets every node (insertion order).</summary>
    public IEnumerable<MegaNode> Nodes => _nodes.Values;

    public MegaNode? Get(string? handle) => handle is not null && _nodes.TryGetValue(handle, out var n) ? n : null;

    public IReadOnlyList<MegaNode> GetChildren(string handle)
    {
        if (!_children.TryGetValue(handle, out var list))
        {
            return Array.Empty<MegaNode>();
        }

        var result = new List<MegaNode>(list.Count);
        foreach (var h in list)
        {
            if (_nodes.TryGetValue(h, out var n))
            {
                result.Add(n);
            }
        }

        return result;
    }

    public void Add(MegaNode node, bool isInShare)
    {
        switch (node.Type)
        {
            case MegaNodeType.Root:
                CloudRoot = node.Handle;
                break;
            case MegaNodeType.Rubbish:
                RubbishRoot = node.Handle;
                break;
        }

        if (isInShare && !InShares.Contains(node.Handle))
        {
            InShares.Add(node.Handle);
        }

        // Same node again (moved, renamed, listed twice): detach it from its previous parent.
        if (_nodes.TryGetValue(node.Handle, out var previous) && previous.Parent is not null && previous.Parent != node.Parent
            && _children.TryGetValue(previous.Parent, out var oldSiblings))
        {
            oldSiblings.Remove(node.Handle);
        }

        _nodes[node.Handle] = node;
        Changes++;
        if (node.Parent is not null && (previous is null || previous.Parent != node.Parent))
        {
            if (!_children.TryGetValue(node.Parent, out var list))
            {
                list = new List<string>();
                _children[node.Parent] = list;
            }

            list.Add(node.Handle);
        }
    }

    /// <summary>Replaces a node already in the tree (same parent).</summary>
    public void Replace(MegaNode node)
    {
        if (_nodes.ContainsKey(node.Handle))
        {
            _nodes[node.Handle] = node;
            Changes++;
        }
    }

    /// <summary>Removes a node and everything below it.</summary>
    public void Remove(string handle)
    {
        if (!_nodes.TryGetValue(handle, out var node))
        {
            return;
        }

        if (node.Parent is not null && _children.TryGetValue(node.Parent, out var siblings))
        {
            siblings.Remove(handle);
        }

        InShares.Remove(handle);
        var stack = new Stack<string>();
        stack.Push(handle);
        while (stack.Count > 0)
        {
            var h = stack.Pop();
            _nodes.Remove(h);
            if (_children.Remove(h, out var kids))
            {
                foreach (var k in kids)
                {
                    stack.Push(k);
                }
            }
        }

        Changes++;
    }

    /// <summary>Copy sharing the (immutable) nodes: updates are applied to a copy, then swapped in.</summary>
    public MegaTree Clone()
    {
        var t = new MegaTree
        {
            CloudRoot = CloudRoot,
            RubbishRoot = RubbishRoot,
            LinkRoot = LinkRoot,
            Sn = Sn,
        };
        t.InShares.AddRange(InShares);
        foreach (var (h, n) in _nodes)
        {
            t._nodes[h] = n;
        }

        foreach (var (h, list) in _children)
        {
            t._children[h] = new List<string>(list);
        }

        foreach (var (h, k) in ShareKeys)
        {
            t.ShareKeys[h] = k;
        }

        return t;
    }

    /// <summary>
    /// Applies action packets (<c>sc</c> poll) to this tree: new / moved nodes, deletions, renames, new thumbnails.
    /// Returns <c>false</c> when a packet needs the whole tree again (shares changed, unknown key).
    /// </summary>
    public bool ApplyPackets(JsonElement packets, INodeKeyring keyring, Func<string, long, bool> keepFile)
    {
        var ingestor = new NodeIngestor(this, keyring, keepFile);
        foreach (var p in packets.EnumerateArray())
        {
            if (p.ValueKind != JsonValueKind.Object || !p.TryGetProperty("a", out var kind))
            {
                continue;
            }

            switch (kind.GetString())
            {
                case "t":
                    if (p.TryGetProperty("t", out var t) && t.ValueKind == JsonValueKind.Object
                        && t.TryGetProperty("f", out var f) && f.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var raw in f.EnumerateArray())
                        {
                            var node = raw.Deserialize<RawNode>();
                            if (node is not null)
                            {
                                ingestor.Ingest(node, allowPending: true);
                            }
                        }

                        ingestor.Flush();
                    }

                    break;

                case "d":
                    if (p.TryGetProperty("n", out var d) && d.GetString() is string dh)
                    {
                        Remove(dh);
                    }

                    break;

                case "u":
                    if (p.TryGetProperty("n", out var u) && Get(u.GetString()) is { } existing && p.TryGetProperty("at", out var at))
                    {
                        var key = existing.IsFile ? existing.Key?.AesKey : existing.FolderKey;
                        if (key is null)
                        {
                            return false;
                        }

                        var attrs = MegaCrypto.DecryptAttributes(key, at.GetString());
                        if (attrs is not JsonElement a || a.ValueKind != JsonValueKind.Object)
                        {
                            break;
                        }

                        var name = a.TryGetProperty("n", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
                        var mtime = a.TryGetProperty("c", out var c) && c.ValueKind == JsonValueKind.String ? MegaNode.MtimeFromFingerprint(c.GetString()) : null;
                        if (existing.IsFile && name is not null && !keepFile(name, existing.Size))
                        {
                            Remove(existing.Handle);
                        }
                        else if ((name is not null && name != existing.Name) || (mtime is not null && mtime != existing.ModifiedTime))
                        {
                            Replace(existing.With(name: name, modifiedTime: mtime));
                        }
                    }

                    break;

                case "fa":
                    if (p.TryGetProperty("n", out var fan) && Get(fan.GetString()) is { } withFa
                        && p.TryGetProperty("fa", out var fa) && fa.GetString() is string fas && fas != withFa.FileAttributes)
                    {
                        Replace(withFa.With(fileAttributes: fas));
                    }

                    break;

                case "s":
                case "s2":
                    // Folder shared with or unshared from this account: share keys change.
                    return false;

                case "ua":
                    if (p.TryGetProperty("ua", out var names) && names.ValueKind == JsonValueKind.Array
                        && names.EnumerateArray().Any(x => x.ValueKind == JsonValueKind.String && x.GetString() == "^!keys"))
                    {
                        return false;
                    }

                    break;
            }
        }

        return true;
    }

    /// <summary>Root node of the default namespace: the folder link root, or the Cloud Drive.</summary>
    public MegaNode? DefaultRoot => Get(LinkRoot ?? CloudRoot);

    /// <summary>
    /// Resolves a path. <c>/A/B</c>: from the Cloud Drive (or the folder link root);
    /// <c>in:Share/A</c>: from an incoming share named <c>Share</c>.
    /// </summary>
    public MegaNode? Resolve(string? path)
    {
        path = (path ?? string.Empty).Trim().Replace('\\', '/');
        MegaNode? current;
        if (path.StartsWith("in:", StringComparison.OrdinalIgnoreCase))
        {
            var rest = path[3..].Trim('/');
            int slash = rest.IndexOf('/', StringComparison.Ordinal);
            var shareName = slash < 0 ? rest : rest[..slash];
            current = InShares.Select(Get).FirstOrDefault(n => n is not null && string.Equals(n.Name, shareName, StringComparison.Ordinal))
                ?? InShares.Select(Get).FirstOrDefault(n => n is not null && string.Equals(n.Name, shareName, StringComparison.OrdinalIgnoreCase));
            path = slash < 0 ? string.Empty : rest[(slash + 1)..];
        }
        else
        {
            current = DefaultRoot;
        }

        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (current is null)
            {
                return null;
            }

            var children = GetChildren(current.Handle).Where(c => !c.IsFile).ToList();
            current = children.FirstOrDefault(c => string.Equals(c.Name, segment, StringComparison.Ordinal))
                ?? children.FirstOrDefault(c => string.Equals(c.Name, segment, StringComparison.OrdinalIgnoreCase));
        }

        return current;
    }

    /// <summary>Path of a node, in the syntax accepted by <see cref="Resolve"/>.</summary>
    public string GetPath(MegaNode node)
    {
        var parts = new List<string>();
        var current = node;
        string prefix = "/";
        while (current is not null)
        {
            if (current.Handle == CloudRoot || current.Handle == LinkRoot)
            {
                break;
            }

            if (InShares.Contains(current.Handle))
            {
                prefix = "in:" + current.Name + "/";
                break;
            }

            parts.Add(current.Name);
            current = Get(current.Parent);
        }

        parts.Reverse();
        return prefix + string.Join('/', parts);
    }

    public IEnumerable<MegaNode> Descendants(string handle)
    {
        var stack = new Stack<string>();
        stack.Push(handle);
        while (stack.Count > 0)
        {
            var h = stack.Pop();
            foreach (var c in GetChildren(h))
            {
                yield return c;
                if (!c.IsFile)
                {
                    stack.Push(c.Handle);
                }
            }
        }
    }

    /// <summary>
    /// Parses a <c>f</c> response as a stream (very large accounts never get loaded as a whole document).
    /// Folders are all kept (paths, folder picker); files are kept when <paramref name="keepFile"/> accepts them.
    /// </summary>
    public static async Task<MegaTree> ParseAsync(
        Stream stream,
        INodeKeyring keyring,
        IReadOnlyDictionary<string, byte[]> extraShareKeys,
        Func<string, long, bool> keepFile,
        CancellationToken cancellationToken)
    {
        var builder = new Builder(keyring, extraShareKeys, keepFile);
        var buffer = new byte[256 * 1024];
        int len = 0;
        bool final = false;
        var state = new JsonReaderState(new JsonReaderOptions { MaxDepth = 64 });
        while (true)
        {
            if (!final && len < buffer.Length)
            {
                int n = await stream.ReadAsync(buffer.AsMemory(len), cancellationToken).ConfigureAwait(false);
                if (n == 0)
                {
                    final = true;
                }
                else
                {
                    len += n;
                }
            }

            int consumed = builder.Process(buffer.AsSpan(0, len), final, ref state);
            if (builder.ErrorCode is int code)
            {
                throw new MegaException(code);
            }

            if (consumed > 0)
            {
                Buffer.BlockCopy(buffer, consumed, buffer, 0, len - consumed);
                len -= consumed;
            }
            else if (len == buffer.Length)
            {
                Array.Resize(ref buffer, buffer.Length * 2);
            }

            if (builder.Done || (final && consumed == 0))
            {
                break;
            }
        }

        if (!builder.Done)
        {
            throw new MegaException("incomplete node tree received from MEGA");
        }

        return builder.Finish();
    }

    /// <summary>Incremental builder fed by a streaming JSON reader.</summary>
    private sealed class Builder
    {
        private readonly INodeKeyring _keyring;
        private readonly MegaTree _tree = new();
        private readonly NodeIngestor _ingestor;
        private string? _section;

        public Builder(INodeKeyring keyring, IReadOnlyDictionary<string, byte[]> extraShareKeys, Func<string, long, bool> keepFile)
        {
            _keyring = keyring;
            foreach (var (h, k) in extraShareKeys)
            {
                _tree.ShareKeys[h] = k;
            }

            _ingestor = new NodeIngestor(_tree, keyring, keepFile);
        }

        public int? ErrorCode { get; private set; }

        public bool Done { get; private set; }

        public int Process(ReadOnlySpan<byte> data, bool final, ref JsonReaderState state)
        {
            var reader = new Utf8JsonReader(data, final, state);
            while (!Done)
            {
                var checkpoint = reader;
                if (!reader.Read())
                {
                    break;
                }

                int depth = reader.CurrentDepth;
                var token = reader.TokenType;
                if (token == JsonTokenType.Number && depth <= 1)
                {
                    if (reader.TryGetInt32(out var code) && code < 0)
                    {
                        ErrorCode = code;
                    }

                    Done = depth == 0;
                    continue;
                }

                if (depth == 0 && token == JsonTokenType.EndArray)
                {
                    Done = true;
                    break;
                }

                if (depth == 2 && token == JsonTokenType.PropertyName)
                {
                    var name = reader.GetString();
                    if (!reader.Read())
                    {
                        reader = checkpoint;
                        break;
                    }

                    if ((name == "f" || name == "ok") && reader.TokenType == JsonTokenType.StartArray)
                    {
                        _section = name;
                    }
                    else if (name == "sn" && reader.TokenType == JsonTokenType.String)
                    {
                        _tree.Sn = reader.GetString();
                    }
                    else if (reader.TokenType is JsonTokenType.StartArray or JsonTokenType.StartObject)
                    {
                        if (!reader.TrySkip())
                        {
                            reader = checkpoint;
                            break;
                        }
                    }

                    continue;
                }

                if (depth == 2 && token == JsonTokenType.EndArray)
                {
                    _section = null;
                    continue;
                }

                if (depth == 3 && token == JsonTokenType.StartObject && _section is not null)
                {
                    int start = (int)reader.TokenStartIndex;
                    if (!reader.TrySkip())
                    {
                        reader = checkpoint;
                        break;
                    }

                    var slice = data[start..(int)reader.BytesConsumed];
                    if (_section == "f")
                    {
                        var raw = JsonSerializer.Deserialize<RawNode>(slice);
                        if (raw is not null)
                        {
                            _ingestor.Ingest(raw, allowPending: true);
                        }
                    }
                    else
                    {
                        var ok = JsonSerializer.Deserialize<RawShareKey>(slice);
                        var key = ok is null ? null : _keyring.DecryptShareKey(ok.K);
                        if (key is not null)
                        {
                            _tree.ShareKeys[ok!.H] = key;
                        }
                    }
                }
            }

            state = reader.CurrentState;
            return (int)reader.BytesConsumed;
        }

        public MegaTree Finish()
        {
            _ingestor.Flush();
            return _tree;
        }
    }
}

/// <summary>Turns raw nodes (tree download or action packets) into decrypted nodes of a tree.</summary>
internal sealed class NodeIngestor
{
    private readonly MegaTree _tree;
    private readonly INodeKeyring _keyring;
    private readonly Func<string, long, bool> _keepFile;
    private readonly List<RawNode> _pending = new();

    public NodeIngestor(MegaTree tree, INodeKeyring keyring, Func<string, long, bool> keepFile)
    {
        _tree = tree;
        _keyring = keyring;
        _keepFile = keepFile;
    }

    /// <summary>Retries the nodes whose key arrived later (share keys of folders listed after them).</summary>
    public void Flush()
    {
        foreach (var raw in _pending)
        {
            Ingest(raw, allowPending: false);
        }

        _pending.Clear();
    }

    /// <summary>Decrypts a raw node and adds it to the tree (when kept). Returns <c>false</c> when its key is not known yet.</summary>
    public void Ingest(RawNode raw, bool allowPending)
    {
        if (string.IsNullOrEmpty(raw.H))
        {
            return;
        }

        if (raw.Sk is not null)
        {
            var sk = _keyring.DecryptShareKey(raw.Sk);
            if (sk is not null)
            {
                _tree.ShareKeys[raw.H] = sk;
            }
        }

        var type = raw.T switch
        {
            0 => MegaNodeType.File,
            2 => MegaNodeType.Root,
            3 => MegaNodeType.Inbox,
            4 => MegaNodeType.Rubbish,
            _ => MegaNodeType.Folder,
        };

        if (type is MegaNodeType.Root or MegaNodeType.Inbox or MegaNodeType.Rubbish)
        {
            _tree.Add(
                new MegaNode
                {
                    Handle = raw.H,
                    Parent = string.IsNullOrEmpty(raw.P) ? null : raw.P,
                    Type = type,
                    Name = type switch { MegaNodeType.Root => "Cloud Drive", MegaNodeType.Inbox => "Vault", _ => "Rubbish Bin" },
                    Timestamp = raw.Ts ?? 0,
                },
                false);
            return;
        }

        bool isFile = type == MegaNodeType.File;
        if (isFile && raw.S is long size0 && size0 < 0)
        {
            return;
        }

        var key = _keyring.DecryptNodeKey(raw.K, isFile, _tree.ShareKeys);
        if (key is null)
        {
            if (allowPending && !string.IsNullOrEmpty(raw.K))
            {
                _pending.Add(raw);
            }

            return;
        }

        var attrKey = isFile ? FileKey.Unpack(key)?.AesKey : key;
        if (attrKey is null)
        {
            return;
        }

        var attrs = MegaCrypto.DecryptAttributes(attrKey, raw.A);
        string name = "[undecryptable]";
        long? mtime = null;
        if (attrs is JsonElement a && a.ValueKind == JsonValueKind.Object)
        {
            if (a.TryGetProperty("n", out var n) && n.ValueKind == JsonValueKind.String)
            {
                name = n.GetString() ?? name;
            }

            if (a.TryGetProperty("c", out var c) && c.ValueKind == JsonValueKind.String)
            {
                mtime = MegaNode.MtimeFromFingerprint(c.GetString());
            }
        }

        long size = raw.S ?? 0;
        if (isFile && !_keepFile(name, size))
        {
            return;
        }

        bool isInShare = !string.IsNullOrEmpty(raw.Su);
        _tree.Add(
            new MegaNode
            {
                Handle = raw.H,
                Parent = string.IsNullOrEmpty(raw.P) || isInShare ? null : raw.P,
                Type = type,
                Name = name,
                Size = size,
                Timestamp = raw.Ts ?? 0,
                ModifiedTime = mtime,
                Key = isFile ? FileKey.Unpack(key) : null,
                FolderKey = isFile ? null : key,
                FileAttributes = raw.Fa,
            },
            isInShare && !isFile);
    }
}
