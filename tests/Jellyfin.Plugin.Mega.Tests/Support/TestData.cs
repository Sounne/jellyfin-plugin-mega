using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;

namespace Jellyfin.Plugin.Mega.Tests.Support;

internal static class TestData
{
    /// <summary>Deterministic content of <paramref name="length"/> bytes (not encrypted-looking, so mismatches are easy to read).</summary>
    public static byte[] Bytes(int length, int seed = 1)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    public static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mega-plugin-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static void TryDelete(string dir)
    {
        try
        {
            Directory.Delete(dir, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>MEGA multi-precision integer: bit count on 2 bytes, then the big-endian value.</summary>
    public static byte[] Mpi(byte[] bigEndian)
    {
        int start = 0;
        while (start < bigEndian.Length - 1 && bigEndian[start] == 0)
        {
            start++;
        }

        var v = bigEndian[start..];
        int bits = (v.Length * 8) - BitOperations.LeadingZeroCount((uint)v[0]) + 24;
        return new[] { (byte)(bits >> 8), (byte)bits }.Concat(v).ToArray();
    }

    /// <summary>Textbook RSA (what MEGA does for session ids and share keys).</summary>
    public static byte[] RawEncrypt(RSAParameters p, byte[] message)
    {
        var n = new BigInteger(p.Modulus, true, true);
        var e = new BigInteger(p.Exponent, true, true);
        var m = new BigInteger(message, true, true);
        return BigInteger.ModPow(m, e, n).ToByteArray(true, true);
    }

    /// <summary>Private key as MEGA stores it (<c>privk</c>): p, q, d, u as MPIs, zero-padded, encrypted with the master key.</summary>
    public static byte[] Privk(RSAParameters p, byte[] masterKey)
    {
        var plain = Mpi(p.P!).Concat(Mpi(p.Q!)).Concat(Mpi(p.D!)).Concat(Mpi(p.InverseQ!)).ToArray();
        var padded = new byte[(plain.Length + 15) / 16 * 16];
        plain.CopyTo(padded, 0);
        Mega.MegaCrypto.EcbEncrypt(masterKey, padded);
        return padded;
    }
}
