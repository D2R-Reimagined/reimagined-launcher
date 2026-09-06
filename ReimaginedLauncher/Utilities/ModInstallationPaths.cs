using System;
using System.IO;

namespace ReimaginedLauncher.Utilities;

internal static class ModInstallationPaths
{
    internal const string NormalModName = "Reimagined";
    internal const string LadderModName = "ReimaginedLadder";
    private const string SignedPrefix = "mods/Reimagined/";
    private const string LadderPrefix = "mods/ReimaginedLadder/";

    internal static string ModName(LaunchExperience experience) =>
        experience == LaunchExperience.Ladder ? LadderModName : NormalModName;

    // Signed paths stay unchanged in manifests; only their on-disk location changes.
    internal static string ToLadderPath(string signedPath)
    {
        var path = signedPath.Replace('\\', '/');
        if (!path.StartsWith(SignedPrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A ladder file must target the signed Reimagined mod directory.");
        var relative = path[SignedPrefix.Length..];
        if (relative.Equals("Reimagined.mpq", StringComparison.OrdinalIgnoreCase)
            || relative.StartsWith("Reimagined.mpq/", StringComparison.OrdinalIgnoreCase))
            relative = LadderModName + ".mpq" + relative["Reimagined.mpq".Length..];
        return LadderPrefix + relative;
    }

    internal static string ToSignedPath(string installedPath)
    {
        var path = installedPath.Replace('\\', '/');
        if (!path.StartsWith(LadderPrefix, StringComparison.OrdinalIgnoreCase)) return path;
        var relative = path[LadderPrefix.Length..];
        if (relative.Equals(LadderModName + ".mpq", StringComparison.OrdinalIgnoreCase)
            || relative.StartsWith(LadderModName + ".mpq/", StringComparison.OrdinalIgnoreCase))
            relative = "Reimagined.mpq" + relative[(LadderModName.Length + 4)..];
        return SignedPrefix + relative;
    }

    internal static string LadderModRoot(string installDirectory) =>
        Path.Combine(installDirectory, "mods", LadderModName);

    internal static string? FindLadderModInfo(string installDirectory)
    {
        var root = LadderModRoot(installDirectory);
        foreach (var path in new[] { Path.Combine(root, LadderModName + ".mpq", "modinfo.json"), Path.Combine(root, "modinfo.json") })
            if (File.Exists(path)) return path;
        return null;
    }
}
