using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.Mega.Mega;

/// <summary>
/// Copy of a decrypted node tree on disk, so a restart does not download the whole tree again (minutes on large
/// accounts): the tree is read back in a second and brought up to date with the action packets since its sequence
/// number. Encrypted (AES-GCM) with a key derived from the account session or folder link, so the file is useless
/// without the plugin settings, and invalid as soon as the credentials change.
/// </summary>
internal static class TreeCache
{
    private const int Version = 1;
    private static readonly byte[] Magic = "MGTC"u8.ToArray();

    public static void Save(MegaTree tree, string path, string credential)
    {
        byte[] plain;
        using (var ms = new MemoryStream())
        {
            using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
            using (var w = new BinaryWriter(gz, Encoding.UTF8))
            {
                Write(tree, w);
            }

            plain = ms.ToArray();
        }

        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var cipher = new byte[plain.Length];
        using (var gcm = new AesGcm(DeriveKey(credential), 16))
        {
            gcm.Encrypt(nonce, plain, cipher, tag);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        using (var f = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            f.Write(Magic);
            f.WriteByte(Version);
            f.Write(nonce);
            f.Write(tag);
            f.Write(cipher);
        }

        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Reads a saved tree; <c>null</c> when missing, from other credentials or damaged.</summary>
    public static MegaTree? Load(string path, string credential)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var data = File.ReadAllBytes(path);
        const int Header = 4 + 1 + 12 + 16;
        if (data.Length < Header || !data.AsSpan(0, 4).SequenceEqual(Magic) || data[4] != Version)
        {
            return null;
        }

        var plain = new byte[data.Length - Header];
        try
        {
            using var gcm = new AesGcm(DeriveKey(credential), 16);
            gcm.Decrypt(data.AsSpan(5, 12), data.AsSpan(Header), data.AsSpan(17, 16), plain);
        }
        catch (CryptographicException)
        {
            return null;
        }

        using var gz = new GZipStream(new MemoryStream(plain), CompressionMode.Decompress);
        using var r = new BinaryReader(gz, Encoding.UTF8);
        return Read(r);
    }

    private static byte[] DeriveKey(string credential)
        => SHA256.HashData(Encoding.UTF8.GetBytes("jellyfin-plugin-mega/tree-cache/v1\n" + credential));

    private static void Write(MegaTree tree, BinaryWriter w)
    {
        w.Write(tree.Sn ?? string.Empty);
        w.Write(tree.CloudRoot ?? string.Empty);
        w.Write(tree.RubbishRoot ?? string.Empty);
        w.Write(tree.LinkRoot ?? string.Empty);
        w.Write(tree.InShares.Count);
        foreach (var h in tree.InShares)
        {
            w.Write(h);
        }

        w.Write(tree.ShareKeys.Count);
        foreach (var (h, k) in tree.ShareKeys)
        {
            w.Write(h);
            w.Write(k.Length);
            w.Write(k);
        }

        w.Write(tree.Count);
        foreach (var n in tree.Nodes)
        {
            w.Write(n.Handle);
            w.Write(n.Parent ?? string.Empty);
            w.Write((byte)n.Type);
            w.Write(n.Name);
            w.Write(n.Size);
            w.Write(n.Timestamp);
            w.Write(n.ModifiedTime ?? -1);
            if (n.Key is { } fk)
            {
                w.Write((byte)1);
                w.Write(fk.AesKey);
                w.Write(fk.Nonce);
                w.Write(fk.Mac);
            }
            else if (n.FolderKey is { Length: 16 } k)
            {
                w.Write((byte)2);
                w.Write(k);
            }
            else
            {
                w.Write((byte)0);
            }

            w.Write(n.FileAttributes ?? string.Empty);
        }
    }

    private static MegaTree Read(BinaryReader r)
    {
        static string? Opt(string s) => s.Length == 0 ? null : s;
        var tree = new MegaTree { Sn = Opt(r.ReadString()) };
        _ = r.ReadString(); // cloud root: set again by Add
        _ = r.ReadString(); // rubbish root: same
        tree.LinkRoot = Opt(r.ReadString());
        var inShares = new string[r.ReadInt32()];
        for (int i = 0; i < inShares.Length; i++)
        {
            inShares[i] = r.ReadString();
        }

        int keys = r.ReadInt32();
        for (int i = 0; i < keys; i++)
        {
            var h = r.ReadString();
            tree.ShareKeys[h] = r.ReadBytes(r.ReadInt32());
        }

        int count = r.ReadInt32();
        for (int i = 0; i < count; i++)
        {
            var handle = r.ReadString();
            var parent = Opt(r.ReadString());
            var type = (MegaNodeType)r.ReadByte();
            var name = r.ReadString();
            long size = r.ReadInt64();
            long ts = r.ReadInt64();
            long mtime = r.ReadInt64();
            FileKey? fileKey = null;
            byte[]? folderKey = null;
            switch (r.ReadByte())
            {
                case 1:
                    fileKey = FileKey.FromParts(r.ReadBytes(16), r.ReadBytes(8), r.ReadBytes(8));
                    break;
                case 2:
                    folderKey = r.ReadBytes(16);
                    break;
            }

            tree.Add(
                new MegaNode
                {
                    Handle = handle,
                    Parent = parent,
                    Type = type,
                    Name = name,
                    Size = size,
                    Timestamp = ts,
                    ModifiedTime = mtime >= 0 ? mtime : null,
                    Key = fileKey,
                    FolderKey = folderKey,
                    FileAttributes = Opt(r.ReadString()),
                },
                false);
        }

        tree.InShares.AddRange(inShares);
        return tree;
    }
}
