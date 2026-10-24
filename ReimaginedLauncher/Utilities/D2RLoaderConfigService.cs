using System;
using System.IO;
using System.Linq;

namespace ReimaginedLauncher.Utilities;

internal sealed record ExtensionConfigTarget(string Root, string? File);

internal static class D2RLoaderConfigService
{
    internal static bool HasExistingConfig(InstallationProfile profile, D2RLoaderExtensionInfo entry)
    {
        try { return Resolve(profile, entry).File is not null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException) { return false; }
    }

    internal static ExtensionConfigTarget Resolve(InstallationProfile profile, D2RLoaderExtensionInfo entry)
    {
        LocalPluginInstallService.RefuseLadder(profile.LaunchExperience);
        var modRoot = LocalPluginInstallService.ResolveRoot(profile);
        var game = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(modRoot)))!;
        var global = entry.Scope == D2RLoaderExtensionScope.Global;
        var root = global ? Path.Combine(game, "d2rloader") : modRoot;
        var rawRoot = global ? Path.Combine(profile.InstallDirectory!, "d2rloader")
            : Path.Combine(profile.InstallDirectory!, "mods", ModInstallationPaths.NormalModName, "d2rloader");
        if (Path.GetFileName(entry.FileName) != entry.FileName || entry.FileName.Contains('\\'))
            throw new InvalidDataException("Invalid extension filename.");
        var folder = entry.Kind switch
        {
            D2RLoaderExtensionKind.Plugin when entry.FileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) => "plugins",
            D2RLoaderExtensionKind.Patch when entry.FileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase) => "patches",
            _ => throw new InvalidDataException("Unsupported extension file.")
        };
        var relative = (entry.IsEnabled ? "" : "launcher-disabled/") + folder + "/" + entry.FileName;
        var expected = Path.GetFullPath(Path.Combine(rawRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!Path.GetFullPath(entry.FilePath).Equals(expected, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException("The extension is outside the selected installation and scope.");
        var source = LocalPluginInstallService.SafePath(root, relative);
        if (!System.IO.File.Exists(source)) throw new FileNotFoundException("The extension changed. Refresh the list and retry.", source);
        if (entry.Kind == D2RLoaderExtensionKind.Patch) return new(Path.GetDirectoryName(source)!, source);
        var config = LocalPluginInstallService.SafePath(root, "config");
        if (!Directory.Exists(config)) throw new DirectoryNotFoundException("No config folder exists for this scope yet. Run the plugin once to generate its settings.");
        var stem = Normalize(Path.GetFileNameWithoutExtension(entry.FileName));
        var matches = Directory.EnumerateFiles(config).Where(IsTextConfig)
            .Where(path => Normalize(Path.GetFileNameWithoutExtension(path)) == stem).ToArray();
        return new(config, matches.Length == 1 ? ValidateSelection(profile, new(config, null), matches[0]) : null);
    }

    internal static string ValidateSelection(InstallationProfile profile, ExtensionConfigTarget target, string path)
    {
        LocalPluginInstallService.RefuseLadder(profile.LaunchExperience);
        if (!IsTextConfig(path)) throw new InvalidDataException("Choose a text configuration file.");
        var relative = Path.GetRelativePath(target.Root, Path.GetFullPath(path)).Replace('\\', '/');
        var safe = LocalPluginInstallService.SafePath(target.Root, relative);
        if (!System.IO.File.Exists(safe)) throw new FileNotFoundException("The configuration file no longer exists.", safe);
        return safe;
    }

    private static bool IsTextConfig(string path) => new[] { ".toml", ".json", ".ini", ".cfg", ".yaml", ".yml", ".txt" }
        .Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private static string Normalize(string name)
    {
        if (name.StartsWith("d2rl-", StringComparison.OrdinalIgnoreCase)) name = name[5..];
        return string.Concat(name.Where(char.IsLetterOrDigit)).ToUpperInvariant();
    }
}
