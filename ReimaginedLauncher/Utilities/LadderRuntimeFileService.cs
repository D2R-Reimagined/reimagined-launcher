using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ReimaginedLauncher.Utilities;

internal static class LadderRuntimeFileService
{
    private const string ManagementDirectoryName = ".reimagined-launcher";
    private const string RuntimeDirectoryName = "ladder-runtime";

    private static readonly string[] MutableSignedPathSuffixes =
    [
        "/Reimagined.mpq/modinfo.json",
        "/Reimagined.mpq/data/global/ui/layouts/characterselectpanelhd.json",
        "/Reimagined.mpq/data/global/ui/layouts/controller/characterselectpanelhd.json"
    ];

    private static readonly HashSet<string> GeneratedRuntimePaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "mods/Reimagined/d2rloader/config/server-saves.toml",
        "mods/Reimagined/d2rloader/config/chat-relay.toml"
    };

    internal static string RestoreOrCaptureBaseline(string installDirectory, string targetPath)
    {
        var baselinePath = GetBaselinePath(installDirectory, targetPath);
        Directory.CreateDirectory(Path.GetDirectoryName(baselinePath)!);

        if (File.Exists(baselinePath))
        {
            File.Copy(baselinePath, targetPath, overwrite: true);
        }
        else
        {
            File.Copy(targetPath, baselinePath, overwrite: false);
        }

        return baselinePath;
    }

    /// <summary>
    /// The pristine copy of <paramref name="targetPath"/> if one has already been
    /// captured, or null. Unlike <see cref="RestoreOrCaptureBaseline"/> this never
    /// captures one, which is what makes it safe to call at times when the target
    /// may already be rewritten - capturing then would enshrine a ladder's
    /// savepath as the mod's pristine one, and every later restore would put the
    /// player back on the ladder folder instead of their own.
    /// </summary>
    internal static string? TryGetExistingBaselinePath(string installDirectory, string targetPath)
    {
        try
        {
            var baselinePath = GetBaselinePath(installDirectory, targetPath);
            return File.Exists(baselinePath) ? baselinePath : null;
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException or IOException)
        {
            return null;
        }
    }

    internal static string GetBaselinePath(string installDirectory, string targetPath)
    {
        var root = Path.GetFullPath(installDirectory).TrimEnd(Path.DirectorySeparatorChar)
                   + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(targetPath);
        if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("A ladder runtime file is outside the D2R installation.");
        }

        var relativePath = Path.GetRelativePath(root, target);
        return Path.Combine(root, ManagementDirectoryName, RuntimeDirectoryName, relativePath);
    }

    internal static bool IsMutableSignedPath(string targetPath)
    {
        var normalized = targetPath.Replace('\\', '/');
        return MutableSignedPathSuffixes.Any(suffix =>
            normalized.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
    }

    internal static bool IsGeneratedRuntimePath(
        string targetPath,
        IReadOnlySet<string>? approvedPluginIds = null)
    {
        var normalized = targetPath.Replace('\\', '/');
        if (GeneratedRuntimePaths.Contains(normalized)
            || normalized.StartsWith(
                "mods/Reimagined/d2rloader/logs/",
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        const string configPrefix = "mods/Reimagined/d2rloader/config/";
        if (approvedPluginIds is null
            || !normalized.StartsWith(configPrefix, StringComparison.OrdinalIgnoreCase)
            || !normalized.EndsWith(".toml", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var pluginId = Path.GetFileNameWithoutExtension(normalized);
        return approvedPluginIds.Contains(pluginId);
    }

    internal static void DeleteBaselines(string installDirectory)
    {
        var root = Path.Combine(installDirectory, ManagementDirectoryName, RuntimeDirectoryName);
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
