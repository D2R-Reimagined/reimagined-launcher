using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection.PortableExecutable;
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

public sealed class D2RLoaderExtensionInfo : INotifyPropertyChanged
{
    private string _filePath = string.Empty;
    private bool _isEnabled = true;
    private bool _canEditConfig;
    private bool _toggleControlsEnabled = true;
    public event PropertyChangedEventHandler? PropertyChanged;

    public required string Name { get; init; }
    public required string FileName { get; init; }
    public required string FilePath { get => _filePath; init => _filePath = value; }
    public required D2RLoaderExtensionKind Kind { get; init; }
    public required D2RLoaderExtensionScope Scope { get; init; }
    public string? Version { get; init; }
    public string? Author { get; init; }
    public bool HasAuthor => !string.IsNullOrWhiteSpace(Author);
    public string? Description { get; init; }
    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);
    public int PatchCount { get; init; }
    public string? Error { get; init; }
    public bool IsLadderDisabled { get; init; }
    public bool IsEnabled { get => _isEnabled; init => _isEnabled = value; }
    public bool HasToggleConflict { get; set; }
    public bool CanToggle => _toggleControlsEnabled && !HasToggleConflict;
    internal bool ToggleControlsEnabled
    {
        set
        {
            if (_toggleControlsEnabled == value) return;
            _toggleControlsEnabled = value;
            PropertyChanged?.Invoke(this, new(nameof(CanToggle)));
        }
    }
    public bool CanEditConfig
    {
        get => _canEditConfig;
        set
        {
            if (_canEditConfig == value) return;
            _canEditConfig = value;
            PropertyChanged?.Invoke(this, new(nameof(CanEditConfig)));
            PropertyChanged?.Invoke(this, new(nameof(ConfigEditHint)));
        }
    }

    internal void ApplyToggleState(string filePath, bool enabled)
    {
        if (_filePath != filePath)
        {
            _filePath = filePath;
            PropertyChanged?.Invoke(this, new(nameof(FilePath)));
        }
        if (_isEnabled == enabled) return;
        _isEnabled = enabled;
        foreach (var name in new[] { nameof(IsEnabled), nameof(IsDisabled), nameof(ToggleStatus), nameof(InstallationDetail) })
            PropertyChanged?.Invoke(this, new(name));
    }

    public string ConfigEditHint => CanEditConfig ? "Open the existing configuration file. Unavailable on Ladder." : "No unique matching config found, or editing is unavailable on Ladder. Refresh after a config is created.";
    public string ScopeBadge => Scope == D2RLoaderExtensionScope.Global ? "GLOBAL" : "MOD";
    public bool IsDisabled => !IsEnabled;
    public string ScopeLabel => Scope == D2RLoaderExtensionScope.Global ? "GLOBAL - affects other mods" : "SELECTED MOD";
    public string ToggleStatus => HasToggleConflict ? "Active and disabled copies conflict; resolve the files before toggling."
        : IsEnabled ? "Enabled file" : "Disabled file";

    public string FileVersionDetail => string.IsNullOrWhiteSpace(Version) ? FileName : $"{FileName} · {VersionLabel}";
    public string InstallationDetail => (Scope == D2RLoaderExtensionScope.Global ? "Installed globally" : "Installed in mod")
        + (HasAuthor ? $" · by {Author!.Trim()}" : string.Empty)
        + (IsDisabled ? " · Disabled" : string.Empty);

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

    private IReadOnlyList<D2RLoaderExtensionInfo>? _plugins;
    private IReadOnlyList<D2RLoaderExtensionInfo>? _patches;

    public IReadOnlyList<D2RLoaderExtensionInfo> Plugins => _plugins ??= Extensions
        .Where(extension => extension.Kind == D2RLoaderExtensionKind.Plugin)
        .ToArray();

    public IReadOnlyList<D2RLoaderExtensionInfo> Patches => _patches ??= Extensions
        .Where(extension => extension.Kind == D2RLoaderExtensionKind.Patch)
        .ToArray();
}

public static partial class D2RLoaderService
{
    private const string LoaderExecutableName = "D2RLoader.exe";
    private const string LoaderFolderName = "d2rloader";

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

    public static D2RLoaderInventory Discover(string? installDirectory, LaunchExperience experience = LaunchExperience.Online, bool includeManualDisabled = false)
    {
        var normalized = InstallDirectoryValidator.NormalizeInstallDirectory(installDirectory) ?? string.Empty;
        var loaderPath = Path.Combine(normalized, LoaderExecutableName);
        var globalRoot = Path.Combine(normalized, LoaderFolderName);
        var modRoot = Path.Combine(normalized, "mods", ModInstallationPaths.ModName(experience), LoaderFolderName);
        var installed = File.Exists(loaderPath);
        var extensions = new List<D2RLoaderExtensionInfo>();

        if (installed)
        {
            AddExtensions(extensions, globalRoot, D2RLoaderExtensionScope.Global, includeManualDisabled: includeManualDisabled);
            AddExtensions(extensions, modRoot, D2RLoaderExtensionScope.Reimagined, includeManualDisabled: includeManualDisabled);
        }

        foreach (var group in extensions.GroupBy(extension => (extension.Scope, extension.Kind, Name: extension.FileName.ToUpperInvariant())))
            if (group.Count() > 1) foreach (var extension in group) extension.HasToggleConflict = true;

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
        D2RLoaderExtensionScope scope,
        bool isLadderDisabled = false,
        bool includeManualDisabled = false)
    {
        void Scan(string folder, bool enabled)
        {
            foreach (var file in EnumerateFilesSafe(Path.Combine(folder, "plugins"), "*").Where(path => Path.GetExtension(path).Equals(".dll", StringComparison.OrdinalIgnoreCase)))
                extensions.Add(ReadPlugin(file, scope, isLadderDisabled, enabled));
            foreach (var file in EnumerateFilesSafe(Path.Combine(folder, "patches"), "*").Where(path => Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase)))
                extensions.Add(ReadPatch(file, scope, isLadderDisabled, enabled));
        }
        Scan(root, true);
        if (includeManualDisabled) Scan(Path.Combine(root, "launcher-disabled"), false);
    }

    private static IEnumerable<string> EnumerateFilesSafe(string directory, string pattern)
    {
        try
        {
            return Directory.Exists(directory) ? Directory.GetFiles(directory, pattern, SearchOption.TopDirectoryOnly) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private sealed record CachedPluginMetadata(
        long LastWriteTicks,
        long Length,
        string Name,
        string? Version,
        string? Author,
        string? Description);

    private static readonly ConcurrentDictionary<string, CachedPluginMetadata> PluginMetadataCache =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    private static readonly ConcurrentDictionary<string, (long LastWriteTicks, long Length, string? Version)> FileVersionCache =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    private static D2RLoaderExtensionInfo ReadPlugin(
        string path,
        D2RLoaderExtensionScope scope,
        bool isLadderDisabled = false, bool isEnabled = true)
    {
        var fileName = Path.GetFileName(path);
        try
        {
            var fileInfo = new FileInfo(path);
            var writeTicks = fileInfo.Exists ? fileInfo.LastWriteTimeUtc.Ticks : 0;
            var length = fileInfo.Exists ? fileInfo.Length : -1;
            if (!fileInfo.Exists
                || !PluginMetadataCache.TryGetValue(path, out var cached)
                || cached.LastWriteTicks != writeTicks
                || cached.Length != length)
            {
                var versionInfo = FileVersionInfo.GetVersionInfo(path);
                var name = versionInfo.ProductName;
                if (string.IsNullOrWhiteSpace(name) || name.Equals("D2RLoader Plugin", StringComparison.OrdinalIgnoreCase))
                {
                    name = HumanizePluginName(Path.GetFileNameWithoutExtension(path));
                }

                var (peVersion, peAuthor) = D2RLoaderPluginVersion.ReadMetadata(path);
                cached = new CachedPluginMetadata(
                    writeTicks,
                    length,
                    name,
                    peVersion ?? NormalizeVersion(versionInfo.FileVersion),
                    peAuthor,
                    versionInfo.FileDescription);
                if (fileInfo.Exists)
                {
                    PluginMetadataCache[path] = cached;
                }
            }

            return new D2RLoaderExtensionInfo
            {
                Name = cached.Name,
                FileName = fileName,
                FilePath = path,
                Kind = D2RLoaderExtensionKind.Plugin,
                Scope = scope,
                IsLadderDisabled = isLadderDisabled,
                IsEnabled = isEnabled,
                Version = cached.Version,
                Author = cached.Author,
                Description = cached.Description
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
                IsLadderDisabled = isLadderDisabled,
                IsEnabled = isEnabled,
                Error = $"Could not read file metadata: {ex.Message}"
            };
        }
    }

    private static D2RLoaderExtensionInfo ReadPatch(
        string path,
        D2RLoaderExtensionScope scope,
        bool isLadderDisabled = false, bool isEnabled = true)
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
                IsLadderDisabled = isLadderDisabled,
                IsEnabled = isEnabled,
                Version = version,
                Author = GetString(root, "author")?.Trim(),
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
                IsLadderDisabled = isLadderDisabled,
                IsEnabled = isEnabled,
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

    internal static string? ReadFileVersion(string path)
    {
        try
        {
            var fileInfo = new FileInfo(path);
            if (fileInfo.Exists)
            {
                var writeTicks = fileInfo.LastWriteTimeUtc.Ticks;
                var length = fileInfo.Length;
                if (FileVersionCache.TryGetValue(path, out var cached)
                    && cached.LastWriteTicks == writeTicks
                    && cached.Length == length)
                {
                    return cached.Version;
                }

                var version = NormalizeVersion(FileVersionInfo.GetVersionInfo(path).FileVersion);
                if (string.IsNullOrWhiteSpace(version) && OperatingSystem.IsLinux())
                {
                    version = ReadPeFileVersion(path);
                }

                FileVersionCache[path] = (writeTicks, length, version);
                return version;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
        }

        // FileVersionInfo does not read PE metadata on Linux.
        if (OperatingSystem.IsLinux())
        {
            return ReadPeFileVersion(path);
        }

        return null;
    }

    /// <summary>
    /// Reads the binary file version from a PE file's VS_FIXEDFILEINFO resource.
    /// This is a Linux fallback because <see cref="FileVersionInfo"/> cannot
    /// read Windows PE metadata on non-Windows platforms.
    /// </summary>
    private static string? ReadPeFileVersion(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            var resourceDir = pe.PEHeaders.PEHeader?.ResourceTableDirectory;
            if (resourceDir is null || resourceDir.Value.RelativeVirtualAddress == 0)
            {
                return null;
            }

            var rva = resourceDir.Value.RelativeVirtualAddress;
            var section = pe.PEHeaders.SectionHeaders.FirstOrDefault(
                sh => rva >= sh.VirtualAddress && rva < sh.VirtualAddress + Math.Max(sh.VirtualSize, sh.SizeOfRawData));
            if (section.Name == null)
            {
                return null;
            }

            var offset = section.PointerToRawData + (rva - section.VirtualAddress);
            var length = Math.Min(resourceDir.Value.Size, section.SizeOfRawData - (rva - section.VirtualAddress));
            stream.Position = offset;
            var bytes = new byte[length];
            stream.ReadExactly(bytes);

            // Scan for VS_FIXEDFILEINFO signature: 0xFEEF04BD
            for (var i = 0; i <= bytes.Length - 52; i++)
            {
                if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i)) != 0xFEEF04BD)
                {
                    continue;
                }

                var fileVersionMs = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i + 8));
                var fileVersionLs = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i + 12));
                var major = (ushort)(fileVersionMs >> 16);
                var minor = (ushort)(fileVersionMs & 0xFFFF);
                var build = (ushort)(fileVersionLs >> 16);
                var revision = (ushort)(fileVersionLs & 0xFFFF);
                return NormalizeVersion($"{major}.{minor}.{build}.{revision}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException)
        {
        }

        return null;
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

    [GeneratedRegex("^d2rl-", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PluginPrefixRegex();

    [GeneratedRegex("[-_]+", RegexOptions.CultureInvariant)]
    private static partial Regex SeparatorRegex();
}
