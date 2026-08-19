using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ReimaginedLauncher.Utilities;

public enum D2RLoaderExtensionKind
{
    Plugin,
    Patch
}

public enum D2RLoaderExtensionScope
{
    Global,
    Reimagined
}

public sealed class D2RLoaderExtensionInfo
{
    public required string Name { get; init; }
    public required string FileName { get; init; }
    public required string FilePath { get; init; }
    public required D2RLoaderExtensionKind Kind { get; init; }
    public required D2RLoaderExtensionScope Scope { get; init; }
    public string? Version { get; init; }
    public string? Description { get; init; }
    public int PatchCount { get; init; }
    public string? Error { get; init; }

    public string ScopeLabel => Scope == D2RLoaderExtensionScope.Global ? "GLOBAL" : "REIMAGINED";
    public string VersionLabel => string.IsNullOrWhiteSpace(Version) ? string.Empty : $"v{Version}";
    public string Detail => !string.IsNullOrWhiteSpace(Error)
        ? Error
        : Kind == D2RLoaderExtensionKind.Patch
            ? $"{PatchCount} memory patch{(PatchCount == 1 ? string.Empty : "es")}"
            : FileName;
}

public sealed class D2RLoaderInventory
{
    public required string InstallDirectory { get; init; }
    public required string LoaderPath { get; init; }
    public required string GlobalRoot { get; init; }
    public required string ModRoot { get; init; }
    public bool IsInstalled { get; init; }
    public string? Version { get; init; }
    public bool AllowGlobalExtensions { get; init; } = true;
    public bool AllowModExtensions { get; init; } = true;
    public IReadOnlyList<D2RLoaderExtensionInfo> Extensions { get; init; } = [];

    public IReadOnlyList<D2RLoaderExtensionInfo> Plugins => Extensions
        .Where(extension => extension.Kind == D2RLoaderExtensionKind.Plugin)
        .ToArray();

    public IReadOnlyList<D2RLoaderExtensionInfo> Patches => Extensions
        .Where(extension => extension.Kind == D2RLoaderExtensionKind.Patch)
        .ToArray();
}

public static partial class D2RLoaderService
{
    private const string LoaderExecutableName = "D2RLoader.exe";
    private const string LoaderFolderName = "d2rloader";
    private const string ModName = "Reimagined";

    public static string? GetLoaderPath(string? installDirectory)
    {
        var normalized = InstallDirectoryValidator.NormalizeInstallDirectory(installDirectory);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        return Path.Combine(normalized, LoaderExecutableName);
    }

    public static bool IsInstalled(string? installDirectory)
    {
        var loaderPath = GetLoaderPath(installDirectory);
        return !string.IsNullOrWhiteSpace(loaderPath) && File.Exists(loaderPath);
    }

    public static D2RLoaderInventory Discover(string? installDirectory)
    {
        var normalized = InstallDirectoryValidator.NormalizeInstallDirectory(installDirectory) ?? string.Empty;
        var loaderPath = Path.Combine(normalized, LoaderExecutableName);
        var globalRoot = Path.Combine(normalized, LoaderFolderName);
        var modRoot = Path.Combine(normalized, "mods", ModName, LoaderFolderName);
        var installed = File.Exists(loaderPath);
        var extensions = new List<D2RLoaderExtensionInfo>();

        if (installed)
        {
            AddExtensions(extensions, globalRoot, D2RLoaderExtensionScope.Global);
            AddExtensions(extensions, modRoot, D2RLoaderExtensionScope.Reimagined);
        }

        var (allowGlobal, allowMod) = ReadExtensionSettings(Path.Combine(globalRoot, "config", "d2rloader.toml"));

        return new D2RLoaderInventory
        {
            InstallDirectory = normalized,
            LoaderPath = loaderPath,
            GlobalRoot = globalRoot,
            ModRoot = modRoot,
            IsInstalled = installed,
            Version = installed ? ReadFileVersion(loaderPath) : null,
            AllowGlobalExtensions = allowGlobal,
            AllowModExtensions = allowMod,
            Extensions = extensions
                .OrderBy(extension => extension.Kind)
                .ThenBy(extension => extension.Scope)
                .ThenBy(extension => extension.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray()
        };
    }

    public static bool CanUseOnlineExperience(InstallationProfile profile, out string? reason)
    {
        if (profile.Type == InstallationType.D2RMM)
        {
            reason = "The Online experience is not available for D2RMM profiles.";
            return false;
        }

        if (!OperatingSystem.IsWindows())
        {
            reason = "D2RLoader launching is currently supported on Windows only.";
            return false;
        }

        if (!InstallDirectoryValidator.IsValidInstallDirectory(profile.InstallDirectory))
        {
            reason = "Select the Diablo II: Resurrected folder that contains D2R.exe.";
            return false;
        }

        if (!IsInstalled(profile.InstallDirectory))
        {
            reason = "D2RLoader.exe was not found beside D2R.exe.";
            return false;
        }

        reason = null;
        return true;
    }

    private static void AddExtensions(
        List<D2RLoaderExtensionInfo> extensions,
        string root,
        D2RLoaderExtensionScope scope)
    {
        var pluginsDirectory = Path.Combine(root, "plugins");
        if (Directory.Exists(pluginsDirectory))
        {
            foreach (var file in EnumerateFilesSafe(pluginsDirectory, "*.dll"))
            {
                extensions.Add(ReadPlugin(file, scope));
            }
        }

        var patchesDirectory = Path.Combine(root, "patches");
        if (Directory.Exists(patchesDirectory))
        {
            foreach (var file in EnumerateFilesSafe(patchesDirectory, "*.json"))
            {
                extensions.Add(ReadPatch(file, scope));
            }
        }
    }

    private static IEnumerable<string> EnumerateFilesSafe(string directory, string pattern)
    {
        try
        {
            return Directory.GetFiles(directory, pattern, SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static D2RLoaderExtensionInfo ReadPlugin(string path, D2RLoaderExtensionScope scope)
    {
        var fileName = Path.GetFileName(path);
        try
        {
            var versionInfo = FileVersionInfo.GetVersionInfo(path);
            var name = versionInfo.ProductName;
            if (string.IsNullOrWhiteSpace(name) || name.Equals("D2RLoader Plugin", StringComparison.OrdinalIgnoreCase))
            {
                name = HumanizePluginName(Path.GetFileNameWithoutExtension(path));
            }

            return new D2RLoaderExtensionInfo
            {
                Name = name,
                FileName = fileName,
                FilePath = path,
                Kind = D2RLoaderExtensionKind.Plugin,
                Scope = scope,
                Version = NormalizeVersion(versionInfo.FileVersion),
                Description = versionInfo.FileDescription
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return new D2RLoaderExtensionInfo
            {
                Name = HumanizePluginName(Path.GetFileNameWithoutExtension(path)),
                FileName = fileName,
                FilePath = path,
                Kind = D2RLoaderExtensionKind.Plugin,
                Scope = scope,
                Error = $"Could not read file metadata: {ex.Message}"
            };
        }
    }

    private static D2RLoaderExtensionInfo ReadPatch(string path, D2RLoaderExtensionScope scope)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            var name = GetString(root, "name") ?? HumanizePluginName(Path.GetFileNameWithoutExtension(path));
            var version = root.TryGetProperty("version", out var versionElement)
                ? versionElement.ToString()
                : null;
            var patchCount = root.TryGetProperty("patches", out var patchesElement)
                             && patchesElement.ValueKind == JsonValueKind.Array
                ? patchesElement.GetArrayLength()
                : 0;

            return new D2RLoaderExtensionInfo
            {
                Name = name,
                FileName = Path.GetFileName(path),
                FilePath = path,
                Kind = D2RLoaderExtensionKind.Patch,
                Scope = scope,
                Version = version,
                Description = GetString(root, "description"),
                PatchCount = patchCount
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new D2RLoaderExtensionInfo
            {
                Name = HumanizePluginName(Path.GetFileNameWithoutExtension(path)),
                FileName = Path.GetFileName(path),
                FilePath = path,
                Kind = D2RLoaderExtensionKind.Patch,
                Scope = scope,
                Error = $"Could not read manifest: {ex.Message}"
            };
        }
    }

    private static (bool AllowGlobal, bool AllowMod) ReadExtensionSettings(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return (true, true);
            }

            var content = File.ReadAllText(path);
            return (
                ReadBooleanSetting(content, "allow_global_extensions", true),
                ReadBooleanSetting(content, "allow_mod_extensions", true));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (true, true);
        }
    }

    private static bool ReadBooleanSetting(string content, string key, bool fallback)
    {
        var match = Regex.Match(
            content,
            $@"(?m)^\s*{Regex.Escape(key)}\s*=\s*(true|false)\s*(?:#.*)?$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? bool.Parse(match.Groups[1].Value) : fallback;
    }

    private static string? ReadFileVersion(string path)
    {
        try
        {
            return NormalizeVersion(FileVersionInfo.GetVersionInfo(path).FileVersion);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static string? NormalizeVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        return version.Split('+', 2)[0].Trim();
    }

    private static string? GetString(JsonElement root, string propertyName)
        => root.TryGetProperty(propertyName, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    private static string HumanizePluginName(string value)
    {
        var withoutPrefix = PluginPrefixRegex().Replace(value, string.Empty);
        var words = SeparatorRegex().Replace(withoutPrefix, " ").Trim();
        return string.IsNullOrWhiteSpace(words)
            ? value
            : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(words);
    }

    [GeneratedRegex("^d2rl-(?:[^-]+-)?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PluginPrefixRegex();

    [GeneratedRegex("[-_]+", RegexOptions.CultureInvariant)]
    private static partial Regex SeparatorRegex();
}
