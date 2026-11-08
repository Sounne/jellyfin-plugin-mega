using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Jellyfin.Plugin.Mega;

/// <summary>MEGA names turned into names valid on the local file system.</summary>
internal static class PathNames
{
    private static readonly HashSet<char> Invalid = new(Path.GetInvalidFileNameChars()) { '<', '>', ':', '"', '/', '\\', '|', '?', '*' };

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public static StringComparer Comparer => OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    public static StringComparison Comparison => OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    public static string Sanitize(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            sb.Append(c < 32 || Invalid.Contains(c) ? '_' : c);
        }

        var s = sb.ToString().Trim().TrimEnd('.');
        if (s.Length == 0 || s == "." || s == "..")
        {
            s = "_";
        }

        var stem = s.Split('.')[0];
        if (Reserved.Contains(stem))
        {
            s = "_" + s;
        }

        // Keep names well below the usual 255-byte limit.
        if (Encoding.UTF8.GetByteCount(s) > 200)
        {
            var ext = Path.GetExtension(s);
            var baseName = Path.GetFileNameWithoutExtension(s);
            while (Encoding.UTF8.GetByteCount(baseName + ext) > 200 && baseName.Length > 1)
            {
                baseName = baseName[..^1];
            }

            s = baseName + ext;
        }

        return s;
    }

    /// <summary>Name not yet used in a folder: <c>name (2).ext</c>, <c>name (3).ext</c>…</summary>
    public static string Unique(string name, HashSet<string> used)
    {
        if (used.Add(name))
        {
            return name;
        }

        var ext = Path.GetExtension(name);
        var stem = Path.GetFileNameWithoutExtension(name);
        for (int i = 2; ; i++)
        {
            var candidate = stem + " (" + i.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")" + ext;
            if (used.Add(candidate))
            {
                return candidate;
            }
        }
    }
}
