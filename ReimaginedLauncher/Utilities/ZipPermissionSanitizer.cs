using System;
using System.Buffers.Binary;
using System.IO;

namespace ReimaginedLauncher.Utilities;

/// <summary>
/// Clears the Unix permission bits that Windows archive tools write into a
/// zip's central directory, so extraction on Linux and macOS falls back to
/// the default file modes instead of unreadable or read-only ones.
/// </summary>
internal static class ZipPermissionSanitizer
{
    private const uint CentralDirectoryHeaderSignature = 0x02014B50;
    private const uint EndOfCentralDirectorySignature = 0x06054B50;
    private const uint Zip64EndOfCentralDirectorySignature = 0x06064B50;
    private const uint Zip64LocatorSignature = 0x07064B50;
    private const int EndOfCentralDirectoryLength = 22;
    private const int Zip64LocatorLength = 20;
    private const int Zip64EndOfCentralDirectoryLength = 56;
    private const int MaxCommentLength = ushort.MaxValue;
    private const long MaxCentralDirectoryBytes = 256L * 1024 * 1024;
    private const uint UnixPermissionMask = 0x0FFFu << 16;

    /// <summary>
    /// Rewrites the archive in place. The file is left untouched when it has
    /// no Unix bits, when running on Windows, or when its structure cannot be
    /// parsed; the caller's extraction then reports whatever is wrong with it.
    /// </summary>
    public static void StripUnixPermissions(string archivePath)
    {
        if (OperatingSystem.IsWindows() || !File.Exists(archivePath))
        {
            return;
        }

        try
        {
            using var stream = new FileStream(archivePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            if (!TryLocateCentralDirectory(stream, out var offset, out var length, out var count))
            {
                return;
            }

            var directory = new byte[length];
            stream.Position = offset;
            stream.ReadExactly(directory);

            if (ClearUnixBits(directory, count))
            {
                stream.Position = offset;
                stream.Write(directory);
                stream.Flush(flushToDisk: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static bool TryLocateCentralDirectory(FileStream stream, out long offset, out long length, out long count)
    {
        offset = length = count = 0;
        if (stream.Length < EndOfCentralDirectoryLength)
        {
            return false;
        }

        var tailStart = Math.Max(0, stream.Length - EndOfCentralDirectoryLength - MaxCommentLength);
        var tail = new byte[stream.Length - tailStart];
        stream.Position = tailStart;
        stream.ReadExactly(tail);

        var eocd = -1;
        for (var i = tail.Length - EndOfCentralDirectoryLength; i >= 0; i--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i)) == EndOfCentralDirectorySignature)
            {
                eocd = i;
                break;
            }
        }

        if (eocd < 0)
        {
            return false;
        }

        count = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(eocd + 10));
        length = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(eocd + 12));
        offset = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(eocd + 16));

        if (count == ushort.MaxValue || length == uint.MaxValue || offset == uint.MaxValue)
        {
            var locator = eocd - Zip64LocatorLength;
            if (locator < 0
                || BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(locator)) != Zip64LocatorSignature)
            {
                return false;
            }

            var zip64Offset = BinaryPrimitives.ReadInt64LittleEndian(tail.AsSpan(locator + 8));
            if (zip64Offset < 0 || zip64Offset + Zip64EndOfCentralDirectoryLength > stream.Length)
            {
                return false;
            }

            var record = new byte[Zip64EndOfCentralDirectoryLength];
            stream.Position = zip64Offset;
            stream.ReadExactly(record);
            if (BinaryPrimitives.ReadUInt32LittleEndian(record) != Zip64EndOfCentralDirectorySignature)
            {
                return false;
            }

            count = BinaryPrimitives.ReadInt64LittleEndian(record.AsSpan(32));
            length = BinaryPrimitives.ReadInt64LittleEndian(record.AsSpan(40));
            offset = BinaryPrimitives.ReadInt64LittleEndian(record.AsSpan(48));
        }

        return count >= 0
               && length is > 0 and <= MaxCentralDirectoryBytes
               && offset >= 0
               && offset + length <= stream.Length;
    }

    private static bool ClearUnixBits(byte[] directory, long count)
    {
        var changed = false;
        var position = 0;
        for (long entry = 0; entry < count; entry++)
        {
            if (position + 46 > directory.Length
                || BinaryPrimitives.ReadUInt32LittleEndian(directory.AsSpan(position)) != CentralDirectoryHeaderSignature)
            {
                return false;
            }

            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(directory.AsSpan(position + 28));
            var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(directory.AsSpan(position + 30));
            var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(directory.AsSpan(position + 32));
            var attributes = BinaryPrimitives.ReadUInt32LittleEndian(directory.AsSpan(position + 38));
            if ((attributes & UnixPermissionMask) != 0)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(directory.AsSpan(position + 38), attributes & ~UnixPermissionMask);
                changed = true;
            }

            position += 46 + nameLength + extraLength + commentLength;
        }

        return changed;
    }
}
