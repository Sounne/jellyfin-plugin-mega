using System;

namespace Jellyfin.Plugin.Mega.Mega;

/// <summary>Error returned by the MEGA API (negative code) or by its storage servers.</summary>
public class MegaException : Exception
{
    public MegaException(string message)
        : base(message)
    {
    }

    public MegaException(string message, Exception inner)
        : base(message, inner)
    {
    }

    public MegaException(int code)
        : base(Describe(code))
    {
        Code = code;
    }

    public MegaException()
    {
    }

    /// <summary>Gets the API error code (negative), or 0.</summary>
    public int Code { get; }

    /// <summary>Gets the HTTP status of a storage server, or 0.</summary>
    public int HttpStatus { get; init; }

    /// <summary>Gets a value indicating whether the session was refused (log in again).</summary>
    public bool IsBadSession => Code == -15;

    /// <summary>Gets a value indicating whether the transfer quota is exhausted.</summary>
    public bool IsQuotaExceeded => HttpStatus == 509 || Code == -17;

    public static string Describe(int code) => code switch
    {
        -1 => "MEGA internal error (-1)",
        -2 => "invalid arguments (-2)",
        -3 => "MEGA server busy, retry (-3)",
        -4 => "too many requests, slow down (-4)",
        -5 => "operation failed (-5)",
        -6 => "too many concurrent requests (-6)",
        -8 => "link or resource expired (-8)",
        -9 => "not found (-9)",
        -11 => "access denied (-11)",
        -13 => "incomplete (-13)",
        -14 => "invalid decryption key (-14)",
        -15 => "session expired or closed: connect the account again (-15)",
        -16 => "account or resource blocked (-16)",
        -17 => "quota exceeded (-17)",
        -18 => "resource temporarily unavailable (-18)",
        -19 => "too many connections (-19)",
        -26 => "two-factor authentication code required (-26)",
        _ => $"MEGA error ({code})",
    };
}
