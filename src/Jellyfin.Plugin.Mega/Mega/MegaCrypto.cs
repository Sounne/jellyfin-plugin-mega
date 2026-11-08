using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Mega.Mega;

/// <summary>
/// Cryptographic primitives of the MEGA protocol.
/// AES-128 (ECB for keys, CBC with a zero IV for attributes, CTR for file data),
/// password derivation (v1 and v2), raw RSA for the session id and share keys,
/// and the "hashcash" proof of work.
/// </summary>
internal static class MegaCrypto
{
    private static readonly byte[] ZeroIv = new byte[16];

    /// <summary>URL-safe base64 without padding, as used by MEGA.</summary>
    public static string B64Encode(ReadOnlySpan<byte> data)
    {
        return Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>Decodes MEGA base64 (URL alphabet, no padding; the standard alphabet is tolerated).</summary>
    public static byte[]? B64Decode(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value is null ? null : Array.Empty<byte>();
        }

        var t = value.Trim().TrimEnd('=').Replace('-', '+').Replace('_', '/');
        switch (t.Length % 4)
        {
            case 1:
                return null;
            case 2:
                t += "==";
                break;
            case 3:
                t += "=";
                break;
        }

        try
        {
            return Convert.FromBase64String(t);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public static Aes CreateAes(ReadOnlySpan<byte> key)
    {
        var aes = Aes.Create();
        aes.Key = key[..16].ToArray();
        return aes;
    }

    public static void EcbEncrypt(ReadOnlySpan<byte> key, Span<byte> data)
    {
        using var aes = CreateAes(key);
        aes.EncryptEcb(data, data, PaddingMode.None);
    }

    public static void EcbDecrypt(ReadOnlySpan<byte> key, Span<byte> data)
    {
        using var aes = CreateAes(key);
        aes.DecryptEcb(data, data, PaddingMode.None);
    }

    public static void CbcEncrypt(ReadOnlySpan<byte> key, Span<byte> data)
    {
        using var aes = CreateAes(key);
        aes.EncryptCbc(data, ZeroIv, data, PaddingMode.None);
    }

    public static void CbcDecrypt(ReadOnlySpan<byte> key, Span<byte> data)
    {
        using var aes = CreateAes(key);
        aes.DecryptCbc(data, ZeroIv, data, PaddingMode.None);
    }

    /// <summary>
    /// Encrypts or decrypts (CTR is symmetric) <paramref name="data"/>, located at byte
    /// <paramref name="offset"/> of the file. Counter block = nonce (8 bytes) || block index (8 bytes, big endian).
    /// </summary>
    public static void CtrTransform(Aes aes, ReadOnlySpan<byte> nonce, long offset, Span<byte> data)
    {
        const int ChunkBlocks = 4096; // 64 KiB of keystream per pass
        var keystream = ArrayPool<byte>.Shared.Rent(ChunkBlocks * 16);
        try
        {
            long block = offset / 16;
            int skip = (int)(offset % 16);
            int done = 0;
            while (done < data.Length)
            {
                int want = Math.Min(data.Length - done, (ChunkBlocks * 16) - skip);
                int blocks = (skip + want + 15) / 16;
                var ks = keystream.AsSpan(0, blocks * 16);
                for (int i = 0; i < blocks; i++)
                {
                    var c = ks.Slice(i * 16, 16);
                    nonce[..8].CopyTo(c);
                    BinaryPrimitives.WriteInt64BigEndian(c[8..], block + i);
                }

                aes.EncryptEcb(ks, ks, PaddingMode.None);
                Xor(data.Slice(done, want), ks.Slice(skip, want));
                done += want;
                block += blocks;
                skip = 0;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(keystream);
        }
    }

    public static void CtrTransform(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, long offset, Span<byte> data)
    {
        using var aes = CreateAes(key);
        CtrTransform(aes, nonce, offset, data);
    }

    private static void Xor(Span<byte> data, ReadOnlySpan<byte> ks)
    {
        int i = 0;
        if (Vector.IsHardwareAccelerated && data.Length >= Vector<byte>.Count)
        {
            var dv = MemoryMarshal.Cast<byte, Vector<byte>>(data);
            var kv = MemoryMarshal.Cast<byte, Vector<byte>>(ks);
            for (int j = 0; j < dv.Length; j++)
            {
                dv[j] ^= kv[j];
            }

            i = dv.Length * Vector<byte>.Count;
        }

        for (; i < data.Length; i++)
        {
            data[i] ^= ks[i];
        }
    }

    /// <summary>Password key of v1 accounts (65 536 AES rounds).</summary>
    public static byte[] PrepareKeyV1(byte[] password)
    {
        byte[] pkey = [0x93, 0xC4, 0x67, 0xE3, 0x7D, 0xB0, 0xC7, 0xA4, 0xD1, 0xBE, 0x3F, 0x81, 0x01, 0x52, 0xCB, 0x56];
        int count = (password.Length + 15) / 16;
        if (count == 0)
        {
            return pkey;
        }

        var ciphers = new Aes[count];
        try
        {
            for (int i = 0; i < count; i++)
            {
                var k = new byte[16];
                password.AsSpan(i * 16, Math.Min(16, password.Length - (i * 16))).CopyTo(k);
                ciphers[i] = CreateAes(k);
            }

            for (int r = 0; r < 65536; r++)
            {
                foreach (var c in ciphers)
                {
                    c.EncryptEcb(pkey, pkey, PaddingMode.None);
                }
            }
        }
        finally
        {
            foreach (var c in ciphers)
            {
                c?.Dispose();
            }
        }

        return pkey;
    }

    /// <summary>E-mail hash of v1 accounts (<c>uh</c>).</summary>
    public static byte[] EmailHashV1(byte[] emailLower, byte[] passwordKey)
    {
        var h = new byte[16];
        for (int i = 0; i < emailLower.Length; i++)
        {
            h[i % 16] ^= emailLower[i];
        }

        using var aes = CreateAes(passwordKey);
        for (int r = 0; r < 16384; r++)
        {
            aes.EncryptEcb(h, h, PaddingMode.None);
        }

        var outp = new byte[8];
        h.AsSpan(0, 4).CopyTo(outp);
        h.AsSpan(8, 4).CopyTo(outp.AsSpan(4));
        return outp;
    }

    /// <summary>v2 accounts: PBKDF2-HMAC-SHA512, 100 000 iterations, 32 bytes.</summary>
    public static byte[] DeriveV2(byte[] password, byte[] salt)
    {
        return Rfc2898DeriveBytes.Pbkdf2(password, salt, 100_000, HashAlgorithmName.SHA512, 32);
    }

    /// <summary>MEGA multi-precision integer: bit length on 2 bytes, then big endian bytes.</summary>
    public static bool TryReadMpi(ReadOnlySpan<byte> data, ref int pos, out byte[] value)
    {
        value = Array.Empty<byte>();
        if (data.Length - pos < 2)
        {
            return false;
        }

        int bits = (data[pos] << 8) | data[pos + 1];
        int len = (bits + 7) / 8;
        if (data.Length - pos - 2 < len)
        {
            return false;
        }

        value = data.Slice(pos + 2, len).ToArray();
        pos += 2 + len;
        return true;
    }

    /// <summary>Decrypts node attributes (<c>MEGA{json}</c> padded with zeros, AES-CBC with a zero IV).</summary>
    public static JsonElement? DecryptAttributes(ReadOnlySpan<byte> key, string? b64)
    {
        var data = B64Decode(b64);
        if (data is null || data.Length < 16)
        {
            return null;
        }

        int len = data.Length / 16 * 16;
        var span = data.AsSpan(0, len);
        CbcDecrypt(key, span);
        if (len < 5 || span[0] != (byte)'M' || span[1] != (byte)'E' || span[2] != (byte)'G' || span[3] != (byte)'A' || span[4] != (byte)'{')
        {
            return null;
        }

        int end = len;
        while (end > 4 && span[end - 1] == 0)
        {
            end--;
        }

        try
        {
            using var doc = JsonDocument.Parse(data.AsMemory(4, end - 4));
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Share keys of the <c>^!keys</c> user attribute (new share format):
    /// AES-GCM with a key derived by HKDF-SHA256 from the master key.
    /// </summary>
    public static System.Collections.Generic.Dictionary<string, byte[]> ParseKeysAttribute(byte[] masterKey, byte[] data)
    {
        var result = new System.Collections.Generic.Dictionary<string, byte[]>(StringComparer.Ordinal);
        if (data.Length < 14 + 16 || data[0] != 20)
        {
            return result;
        }

        try
        {
            var derived = HKDF.DeriveKey(HashAlgorithmName.SHA256, masterKey, 16, Array.Empty<byte>(), new byte[] { 1 });
            var nonce = data.AsSpan(2, 12);
            var cipher = data.AsSpan(14, data.Length - 14 - 16);
            var tag = data.AsSpan(data.Length - 16, 16);
            var plain = new byte[cipher.Length];
            using (var gcm = new AesGcm(derived, 16))
            {
                gcm.Decrypt(nonce, cipher, tag, plain);
            }

            int pos = 0;
            while (pos + 4 <= plain.Length)
            {
                int tagId = plain[pos];
                int len = (plain[pos + 1] << 16) | (plain[pos + 2] << 8) | plain[pos + 3];
                pos += 4;
                if (pos + len > plain.Length)
                {
                    break;
                }

                if (tagId == 48)
                {
                    for (int r = pos; r + 23 <= pos + len; r += 23)
                    {
                        var handle = B64Encode(plain.AsSpan(r, 6));
                        result[handle] = plain.AsSpan(r + 6, 16).ToArray();
                    }
                }

                pos += len;
            }
        }
        catch (CryptographicException)
        {
            // Unreadable attribute: shares in the new format stay unavailable.
        }

        return result;
    }

    /// <summary>
    /// Proof of work requested by the API (HTTP 402, <c>X-Hashcash</c> header).
    /// Returns the header value to send back: <c>1:&lt;token&gt;:&lt;prefix&gt;</c>.
    /// </summary>
    public static string? SolveHashcash(string header)
    {
        var parts = header.Trim().Split(':');
        if (parts.Length < 4 || parts[0] != "1" || !int.TryParse(parts[1], out var easiness))
        {
            return null;
        }

        var tokenB64 = parts[3];
        var token = B64Decode(tokenB64);
        if (token is null || token.Length != 48)
        {
            return null;
        }

        uint e = (uint)easiness;
        uint threshold = (((e & 63) << 1) + 1) << (int)(((e >> 6) * 7) + 3);
        const int Repeat = 262_144;
        var area = new byte[Repeat * 48];
        for (int i = 0; i < Repeat; i++)
        {
            Buffer.BlockCopy(token, 0, area, i * 48, 48);
        }

        int threads = Math.Clamp(Environment.ProcessorCount, 1, 8);
        long found = -1;
        Parallel.For(0, threads, t =>
        {
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            Span<byte> prefix = stackalloc byte[4];
            Span<byte> digest = stackalloc byte[32];
            uint n = (uint)t;
            while (Interlocked.Read(ref found) < 0)
            {
                BinaryPrimitives.WriteUInt32BigEndian(prefix, n);
                sha.AppendData(prefix);
                sha.AppendData(area);
                sha.GetHashAndReset(digest);
                if (BinaryPrimitives.ReadUInt32BigEndian(digest) <= threshold)
                {
                    Interlocked.CompareExchange(ref found, n, -1);
                    break;
                }

                n = unchecked(n + (uint)threads);
            }
        });

        if (found < 0)
        {
            return null;
        }

        var answer = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(answer, (uint)found);
        return "1:" + tokenB64 + ":" + B64Encode(answer);
    }

    public static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);
}

/// <summary>Key of a file: AES key, CTR nonce and condensed MAC.</summary>
internal sealed class FileKey
{
    private FileKey(byte[] aesKey, byte[] nonce, byte[] mac)
    {
        AesKey = aesKey;
        Nonce = nonce;
        Mac = mac;
    }

    public byte[] AesKey { get; }

    public byte[] Nonce { get; }

    public byte[] Mac { get; }

    public static FileKey FromParts(byte[] aesKey, byte[] nonce, byte[] mac) => new(aesKey, nonce, mac);

    /// <summary>From the 32 decrypted key bytes.</summary>
    public static FileKey? Unpack(ReadOnlySpan<byte> k)
    {
        if (k.Length < 32)
        {
            return null;
        }

        var aes = new byte[16];
        for (int i = 0; i < 16; i++)
        {
            aes[i] = (byte)(k[i] ^ k[i + 16]);
        }

        return new FileKey(aes, k.Slice(16, 8).ToArray(), k.Slice(24, 8).ToArray());
    }
}

/// <summary>Account RSA private key (p, q, d, u) for raw decryption.</summary>
internal sealed class MegaRsa
{
    private readonly BigInteger _n;
    private readonly BigInteger _d;

    private MegaRsa(BigInteger n, BigInteger d)
    {
        _n = n;
        _d = d;
    }

    /// <summary>From <c>privk</c> decrypted with the master key.</summary>
    public static MegaRsa? FromPrivk(byte[] data)
    {
        int pos = 0;
        if (!MegaCrypto.TryReadMpi(data, ref pos, out var p)
            || !MegaCrypto.TryReadMpi(data, ref pos, out var q)
            || !MegaCrypto.TryReadMpi(data, ref pos, out var d)
            || !MegaCrypto.TryReadMpi(data, ref pos, out _))
        {
            return null;
        }

        var bp = new BigInteger(p, isUnsigned: true, isBigEndian: true);
        var bq = new BigInteger(q, isUnsigned: true, isBigEndian: true);
        if (bp.GetBitLength() < 512 || bq.GetBitLength() < 512)
        {
            return null;
        }

        return new MegaRsa(bp * bq, new BigInteger(d, isUnsigned: true, isBigEndian: true));
    }

    /// <summary>Raw RSA decryption of an MPI.</summary>
    public byte[]? DecryptMpi(byte[] data)
    {
        int pos = 0;
        if (!MegaCrypto.TryReadMpi(data, ref pos, out var c))
        {
            return null;
        }

        var m = BigInteger.ModPow(new BigInteger(c, isUnsigned: true, isBigEndian: true), _d, _n);
        return m.ToByteArray(isUnsigned: true, isBigEndian: true);
    }
}
