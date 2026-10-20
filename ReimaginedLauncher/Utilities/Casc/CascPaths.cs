using System;
using System.IO;

namespace ReimaginedLauncher.Utilities.Casc;

/// <summary>
/// Converts between canonical CASC/manifest paths (relative, backslash-delimited, e.g.
/// <c>data\global\excel\armor.txt</c>) and native filesystem paths. Manifest keys stay
/// case-insensitive on every platform; filesystem containment checks follow the host.
/// </summary>
internal static class CascPaths
{
    /// <summary>Comparison for native filesystem paths: case-insensitive on Windows only.</summary>
    public static StringComparison FileSystemComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Comparer matching <see cref="FileSystemComparison"/>.</summary>
    public static StringComparer FileSystemComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>
    /// Maps a canonical CASC path to a relative path for <see cref="Path.Combine(string, string)"/>
    /// on this host. Windows input is returned unchanged (after stripping any TVFS namespace).
    /// </summary>
    public static string ToNativeRelative(string cascPath)
    {
        if (string.IsNullOrEmpty(cascPath))
        {
            return cascPath;
        }

        var stripped = CascExtractionFilter.StripCascNamespace(cascPath);
        return Path.DirectorySeparatorChar == '\\'
            ? stripped
            : stripped.Replace('\\', Path.DirectorySeparatorChar);
    }

    /// <summary>
    /// Canonical form of a path relative to a native directory, or <c>null</c> when it cannot be
    /// represented: on Linux a file name may legally contain <c>'\'</c>, which the manifest would
    /// misread as a directory separator.
    /// </summary>
    public static string? FromNativeRelative(string nativeRelative)
    {
        if (Path.DirectorySeparatorChar == '\\')
        {
            return nativeRelative;
        }

        return nativeRelative.Contains('\\')
            ? null
            : nativeRelative.Replace(Path.DirectorySeparatorChar, '\\');
    }

    /// <summary>Canonical form of a <c>'/'</c>-delimited archive entry path.</summary>
    public static string FromArchiveRelative(string archiveRelative) => archiveRelative.Replace('/', '\\');

    /// <summary>True when <paramref name="cascPath"/> is relative and has no <c>..</c> segment.</summary>
    public static bool IsSafeRelative(string? cascPath)
    {
        if (string.IsNullOrWhiteSpace(cascPath))
        {
            return false;
        }

        var stripped = CascExtractionFilter.StripCascNamespace(cascPath);
        if (string.IsNullOrEmpty(stripped) ||
            stripped[0] is '\\' or '/' ||
            Path.IsPathRooted(ToNativeRelative(stripped)))
        {
            return false;
        }

        foreach (var segment in stripped.Split('\\', '/'))
        {
            if (segment == "..")
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Full native path of <paramref name="cascPath"/> beneath <paramref name="root"/>, or <c>null</c>
    /// when the path is unsafe (rooted, has <c>..</c> segments) or would land outside the root.
    /// </summary>
    public static string? ResolveUnderRoot(string root, string cascPath)
    {
        if (string.IsNullOrWhiteSpace(root) || !IsSafeRelative(cascPath))
        {
            return null;
        }

        try
        {
            var rootFull = Path.GetFullPath(root);
            var full = Path.GetFullPath(Path.Combine(rootFull, ToNativeRelative(cascPath)));
            return IsUnder(rootFull, full) ? full : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>True when full path <paramref name="candidate"/> is strictly beneath directory <paramref name="directory"/>.</summary>
    public static bool IsUnder(string directory, string candidate)
    {
        var prefix = Path.EndsInDirectorySeparator(directory)
            ? directory
            : directory + Path.DirectorySeparatorChar;
        return candidate.Length > prefix.Length &&
               candidate.StartsWith(prefix, FileSystemComparison);
    }

    /// <summary>True when full path <paramref name="candidate"/> equals or is beneath <paramref name="directory"/>.</summary>
    public static bool IsSameOrUnder(string directory, string candidate) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(directory),
            Path.TrimEndingDirectorySeparator(candidate),
            FileSystemComparison) ||
        IsUnder(directory, candidate);
}
