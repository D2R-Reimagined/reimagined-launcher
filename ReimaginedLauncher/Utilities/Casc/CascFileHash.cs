using System;
using System.IO;
using System.Security.Cryptography;

namespace ReimaginedLauncher.Utilities.Casc;

/// <summary>
/// Content checks for files under the fastload tree. A CASC CKey is the MD5 of the decoded
/// file, so a size + MD5 match against it proves the bytes on disk are the CASC default.
/// </summary>
internal static class CascFileHash
{
    private const int BufferSize = 1 << 20;

    /// <summary>Hex MD5 of the file at <paramref name="path"/>.</summary>
    public static string ComputeMd5Hex(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.SequentialScan);
        return Convert.ToHexString(MD5.HashData(stream));
    }

    /// <summary>True when <paramref name="value"/> is a 16-byte hex digest.</summary>
    public static bool IsMd5Hex(string? value)
    {
        if (value is null || value.Length != 32)
        {
            return false;
        }

        foreach (var c in value)
        {
            if (!char.IsAsciiHexDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// True when the file exists and its MD5 equals <paramref name="expectedMd5"/>. Returns
    /// false (never throws) when the file is missing, unreadable, or the digest is malformed.
    /// </summary>
    public static bool Matches(string path, string? expectedMd5, long? expectedSize = null)
    {
        if (!IsMd5Hex(expectedMd5))
        {
            return false;
        }

        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return false;
            }

            if (expectedSize is { } size && info.Length != size)
            {
                return false;
            }

            return string.Equals(ComputeMd5Hex(path), expectedMd5, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
