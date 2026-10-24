using System;
using System.IO;
namespace ReimaginedLauncher.Utilities;

internal static class D2RLoaderExtensionToggleService
{
    internal static string SetEnabled(InstallationProfile profile, D2RLoaderExtensionInfo extension, bool enabled, Action ensureSafe)
    {
        LocalPluginInstallService.RefuseLadder(profile.LaunchExperience);
        ensureSafe();
        if (!extension.CanToggle) throw new IOException("Both active and disabled copies exist. Resolve the duplicate before toggling.");
        if (enabled == extension.IsEnabled) return extension.FilePath;
        var folder = extension.Kind switch
        {
            D2RLoaderExtensionKind.Plugin when extension.FileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) => "plugins",
            D2RLoaderExtensionKind.Patch when extension.FileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase) => "patches",
            _ => throw new InvalidOperationException("Only plugin DLLs and patch JSON files can be toggled.")
        };
        if (Path.GetFileName(extension.FileName) != extension.FileName || extension.FileName.Contains('\\'))
            throw new InvalidDataException("Invalid extension filename.");
        var modRoot = LocalPluginInstallService.ResolveRoot(profile);
        var game = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(modRoot)))!;
        var root = extension.Scope == D2RLoaderExtensionScope.Global ? Path.Combine(game, "d2rloader") : modRoot;
        var rawRoot = extension.Scope == D2RLoaderExtensionScope.Global
            ? Path.Combine(profile.InstallDirectory!, "d2rloader")
            : Path.Combine(profile.InstallDirectory!, "mods", ModInstallationPaths.NormalModName, "d2rloader");
        var activeRelative = folder + "/" + extension.FileName;
        var parkedRelative = "launcher-disabled/" + activeRelative;
        var sourceRelative = extension.IsEnabled ? activeRelative : parkedRelative;
        var expectedSource = Path.GetFullPath(Path.Combine(rawRoot, sourceRelative.Replace('/', Path.DirectorySeparatorChar)));
        if (!Path.GetFullPath(extension.FilePath).Equals(expectedSource, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException("The extension is outside the selected installation and scope.");
        var source = LocalPluginInstallService.SafePath(root, sourceRelative);
        var destination = LocalPluginInstallService.SafePath(root, enabled ? activeRelative : parkedRelative);
        if (!File.Exists(source)) throw new FileNotFoundException("The extension changed since refresh. Refresh the list and retry.", source);
        if (File.Exists(destination) || Directory.Exists(destination)) throw new IOException("The destination already exists. No files were overwritten.");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        ensureSafe();
        LocalPluginInstallService.RefuseLadder(profile.LaunchExperience);
        File.Move(source, destination, false);
        return destination;
    }
}
