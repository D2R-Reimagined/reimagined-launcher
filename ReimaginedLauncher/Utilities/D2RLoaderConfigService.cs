using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ReimaginedLauncher.HttpClients.Models;

namespace ReimaginedLauncher.Utilities;

internal sealed record ExtensionConfigTarget(string Root, string? File);

internal static class D2RLoaderConfigService
{
    // Plugins and configs whose settings are locked on Ladder launches so every
    // player plays by the same rules:
    // - Ladder Servers and Lobby (server-saves, lobby-bridge, chat-relay, d2rds)
    // - Reimagined Maps (maps)
    // - Auto Waypoint (auto-waypoint)
    // - Identify On Click, Bind And Summon, Charm Inventory, Left Click Auras
    // - Mod game settings (mod.toml)
    private static readonly HashSet<string> LockedLadderStems = new(StringComparer.OrdinalIgnoreCase)
    {
        "SERVERSAVES",
        "LOBBYBRIDGE",
        "CHATRELAY",
        "D2RDS",
        "MAPS",
        "AUTOWAYPOINT",
        "IDENTIFYONCLICK",
        "BINDANDSUMMON",
        "CHARMINV",
        "CHARMINVENTORY",
        "CELESTIALRAYONELEFTCLICKAURAS",
        "LEFTCLICKAURAS",
        "AURALEFTSKILL",
        "MOD"
    };

    private static readonly IReadOnlyDictionary<string, string> ConfigStemAliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["CONTROLLERQOLUPDATES"] = "CONTROLLERQOL",
            ["PROJECTILEPERFORMANCEOPTIMIZER"] = "PROJECTILEPERF",
            ["UNHOARDER"] = "FILTER"
        };

    internal static bool IsLockedLadderExtension(
        string fileName,
        D2RLoaderExtensionKind kind = D2RLoaderExtensionKind.Plugin,
        LadderBundleResponse? activeBundle = null)
    {
        if (kind == D2RLoaderExtensionKind.Patch)
        {
            return true;
        }

        var stem = Normalize(Path.GetFileNameWithoutExtension(fileName));
        if (LockedLadderStems.Contains(stem))
        {
            return true;
        }

        if (activeBundle is null)
        {
            return false;
        }

        return activeBundle.Files.Any(file =>
            !LadderRuntimeFileService.IsPlayerFilterPath(file.TargetPath)
            && ModInstallationPaths.ToSignedPath(file.TargetPath)
                .StartsWith("mods/Reimagined/d2rloader/config/", StringComparison.OrdinalIgnoreCase)
            && Normalize(Path.GetFileNameWithoutExtension(file.FileName)) == stem);
    }

    internal static bool IsLockedLadderConfigPath(string path, LadderBundleResponse? activeBundle = null)
    {
        var stem = Normalize(Path.GetFileNameWithoutExtension(path));
        if (LockedLadderStems.Contains(stem))
        {
            return true;
        }

        if (activeBundle is null)
        {
            return false;
        }

        var fileName = Path.GetFileName(path);
        return activeBundle.Files.Any(file =>
            !LadderRuntimeFileService.IsPlayerFilterPath(file.TargetPath)
            && ModInstallationPaths.ToSignedPath(file.TargetPath)
                .StartsWith("mods/Reimagined/d2rloader/config/", StringComparison.OrdinalIgnoreCase)
            && (string.Equals(file.FileName, fileName, StringComparison.OrdinalIgnoreCase)
                || Normalize(Path.GetFileNameWithoutExtension(file.FileName)) == stem));
    }

    internal static D2RLoaderExtensionInfo CreateLadderExtensionInfo(
        InstallationProfile profile,
        string name,
        string fileName,
        D2RLoaderExtensionKind kind,
        bool isLadderDisabled)
    {
        var folder = kind == D2RLoaderExtensionKind.Plugin ? "plugins" : "patches";
        var relative = isLadderDisabled
            ? Path.Combine("ladder-disabled", folder, fileName)
            : Path.Combine(folder, fileName);
        var filePath = string.IsNullOrWhiteSpace(profile.InstallDirectory)
            ? relative
            : Path.Combine(
                profile.InstallDirectory,
                "mods",
                ModInstallationPaths.LadderModName,
                "d2rloader",
                relative);
        return new D2RLoaderExtensionInfo
        {
            Name = name,
            FileName = fileName,
            FilePath = filePath,
            Kind = kind,
            Scope = D2RLoaderExtensionScope.Reimagined,
            IsEnabled = !isLadderDisabled,
            IsLadderDisabled = isLadderDisabled
        };
    }

    internal static bool HasExistingConfig(
        InstallationProfile profile,
        D2RLoaderExtensionInfo entry,
        LadderBundleResponse? activeBundle = null)
    {
        try
        {
            return Resolve(profile, entry, activeBundle).File is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            return false;
        }
    }

    internal static ExtensionConfigTarget Resolve(
        InstallationProfile profile,
        D2RLoaderExtensionInfo entry,
        LadderBundleResponse? activeBundle = null)
    {
        if (profile.LaunchExperience == LaunchExperience.Offline)
            throw new InvalidOperationException("Configuration editing is not available in Offline mode.");
        if (profile.LaunchExperience == LaunchExperience.Ladder)
        {
            if (entry.Scope == D2RLoaderExtensionScope.Global
                || IsLockedLadderExtension(entry.FileName, entry.Kind, activeBundle))
            {
                throw new InvalidOperationException("This configuration is locked in Ladder mode.");
            }
        }

        var modRoot = LocalPluginInstallService.ResolveRoot(profile);
        var game = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(modRoot)))!;
        var global = entry.Scope == D2RLoaderExtensionScope.Global;
        var root = global ? Path.Combine(game, "d2rloader") : modRoot;
        var modName = ModInstallationPaths.ModName(profile.LaunchExperience);
        var rawRoot = global ? Path.Combine(profile.InstallDirectory!, "d2rloader")
            : Path.Combine(profile.InstallDirectory!, "mods", modName, "d2rloader");
        if (Path.GetFileName(entry.FileName) != entry.FileName || entry.FileName.Contains('\\'))
            throw new InvalidDataException("Invalid extension filename.");
        var folder = entry.Kind switch
        {
            D2RLoaderExtensionKind.Plugin when entry.FileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) => "plugins",
            D2RLoaderExtensionKind.Patch when entry.FileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase) => "patches",
            _ => throw new InvalidDataException("Unsupported extension file.")
        };
        var prefix = entry.IsLadderDisabled
            ? "ladder-disabled/"
            : entry.IsEnabled
                ? ""
                : "launcher-disabled/";
        var relative = prefix + folder + "/" + entry.FileName;
        var expected = Path.GetFullPath(Path.Combine(rawRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!Path.GetFullPath(entry.FilePath).Equals(expected, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException("The extension is outside the selected installation and scope.");
        var source = LocalPluginInstallService.SafePath(root, relative);
        if (!System.IO.File.Exists(source)) throw new FileNotFoundException("The extension changed. Refresh the list and retry.", source);
        if (entry.Kind == D2RLoaderExtensionKind.Patch) return new(Path.GetDirectoryName(source)!, source);
        var config = LocalPluginInstallService.SafePath(root, "config");
        if (!Directory.Exists(config)) throw new DirectoryNotFoundException("No config folder exists for this scope yet. Run the plugin once to generate its settings.");
        var stem = Normalize(Path.GetFileNameWithoutExtension(entry.FileName));
        var files = Directory.EnumerateFiles(config).Where(IsTextConfig).ToArray();
        var matches = files
            .Where(path => Normalize(Path.GetFileNameWithoutExtension(path)) == stem)
            .ToArray();
        if (matches.Length == 0 && ConfigStemAliases.TryGetValue(stem, out var aliasStem))
        {
            matches = files
                .Where(path => Normalize(Path.GetFileNameWithoutExtension(path)) == aliasStem)
                .ToArray();
        }

        return new(config, matches.Length == 1 ? ValidateSelection(profile, new(config, null), matches[0], activeBundle) : null);
    }

    internal static string ValidateSelection(
        InstallationProfile profile,
        ExtensionConfigTarget target,
        string path,
        LadderBundleResponse? activeBundle = null)
    {
        if (profile.LaunchExperience == LaunchExperience.Offline)
            throw new InvalidOperationException("Configuration editing is not available in Offline mode.");
        if (profile.LaunchExperience == LaunchExperience.Ladder)
        {
            if (string.IsNullOrWhiteSpace(profile.InstallDirectory))
                throw new InvalidOperationException("This configuration is locked in Ladder mode.");
            var expectedLadderConfigRoot = Path.Combine(
                profile.InstallDirectory,
                "mods",
                ModInstallationPaths.LadderModName,
                "d2rloader",
                "config");
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!Path.GetFullPath(target.Root).Equals(Path.GetFullPath(expectedLadderConfigRoot), comparison)
                || IsLockedLadderConfigPath(path, activeBundle))
            {
                throw new InvalidOperationException("This configuration is locked in Ladder mode.");
            }
        }

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
