using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace ReimaginedLauncher.Utilities;

public sealed record LadderCopyExtensionItem(
    string Key,
    string Name,
    string FileName,
    string SourcePath,
    D2RLoaderExtensionKind Kind,
    string? Version,
    string? Author,
    string? Description,
    int PatchCount,
    bool DefaultEnabled,
    string? WarningNote);

public sealed record LadderToNormalCopyPlan(
    string InstallDirectory,
    string LadderModRoot,
    string NormalModRoot,
    string BackupRootDirectory,
    string TargetSavePath,
    bool HasExistingNormalMod,
    bool HasExistingNormalConfigs,
    IReadOnlyList<LadderCopyExtensionItem> Extensions);

public sealed record LadderToNormalCopyResult(
    string DestinationModRoot,
    string? BackupDirectory,
    string ResolvedSavePath,
    int EnabledCount,
    int DisabledCount);

internal static class LadderToNormalCopyService
{
    internal const string DefaultSavePath = "ReimaginedThree/";
    internal const int RetainedBackupCount = 2;
    internal const string BackupFolderName = "normal-mod-backups";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static long _backupSequence;

    private static readonly HashSet<string> DefaultDisabledPlugins = new(StringComparer.OrdinalIgnoreCase)
    {
        "d2rl-server-saves.dll",
        "d2rl-lobby-bridge.dll",
        "d2rl-d2rds.dll",
        "d2rl-ruffneckk-bnet-simulation.dll"
    };

    private static readonly Dictionary<string, string> WarningPluginNotes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["d2rl-ruffneckk-force-map-reset.dll"] = "Generates a fresh offline map every time a new game starts unless unchecked.",
        ["d2rl-ladder-droprates.dll"] = "Applies Ladder drop-rate rebalancing to Non-Ladder games unless unchecked."
    };

    private static readonly (string StagedRelativePath, string LadderRelativePath)[] CharacterSelectLayoutPaths =
    [
        ("Reimagined.mpq/data/global/ui/layouts/characterselectpanelhd.json",
            "ReimaginedLadder.mpq/data/global/ui/layouts/characterselectpanelhd.json"),
        ("Reimagined.mpq/data/global/ui/layouts/controller/characterselectpanelhd.json",
            "ReimaginedLadder.mpq/data/global/ui/layouts/controller/characterselectpanelhd.json"),
        ("data/global/ui/layouts/characterselectpanelhd.json",
            "data/global/ui/layouts/characterselectpanelhd.json"),
        ("data/global/ui/layouts/controller/characterselectpanelhd.json",
            "data/global/ui/layouts/controller/characterselectpanelhd.json")
    ];

    internal static string ExtensionKey(D2RLoaderExtensionKind kind, string fileName) => $"{kind}:{fileName}";

    internal static bool HasInstalledLadderMod(string? installDirectory)
    {
        var normalized = InstallDirectoryValidator.NormalizeInstallDirectory(installDirectory);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        if (ModInstallationPaths.FindLadderModInfo(normalized) is null)
        {
            return false;
        }

        var ladderRoot = ModInstallationPaths.LadderModRoot(normalized);
        return Directory.Exists(Path.Combine(ladderRoot, ModInstallationPaths.LadderModName + ".mpq", "data", "global", "excel"))
               || Directory.Exists(Path.Combine(ladderRoot, "data", "global", "excel"));
    }

    internal static string ResolveTargetSavePath(string installDirectory)
    {
        installDirectory = InstallDirectoryValidator.NormalizeInstallDirectory(installDirectory) ?? installDirectory;
        var legacyActiveLadderMarker = Path.Combine(
            installDirectory,
            "mods",
            ModInstallationPaths.NormalModName,
            "d2rloader",
            "ladder-bundle-state.json");
        if (!File.Exists(legacyActiveLadderMarker))
        {
            var activeModInfo = NormalModInstallationService.FindModInfo(
                Path.Combine(installDirectory, "mods", ModInstallationPaths.NormalModName));
            if (TryReadValidNormalSavePath(activeModInfo, out var activeSavePath))
            {
                return activeSavePath;
            }
        }

        var preservedModInfo = NormalModInstallationService.FindModInfo(
            NormalModInstallationService.NormalModRoot(installDirectory));
        if (TryReadValidNormalSavePath(preservedModInfo, out var preservedSavePath))
        {
            return preservedSavePath;
        }

        return DefaultSavePath;
    }

    internal static LadderToNormalCopyPlan Inspect(string installDirectory)
    {
        var normalized = RequireValidLadderInstall(installDirectory);
        var ladderModRoot = ModInstallationPaths.LadderModRoot(normalized);
        var normalModRoot = Path.Combine(normalized, "mods", ModInstallationPaths.NormalModName);
        var backupRoot = Path.Combine(normalized, ".reimagined-launcher", BackupFolderName);
        var normalConfigDir = Path.Combine(normalModRoot, "d2rloader", "config");
        var extensions = D2RLoaderService.DiscoverLadderModExtensions(ladderModRoot)
            .Select(ext =>
            {
                var defaultEnabled = !(ext.Kind == D2RLoaderExtensionKind.Plugin && DefaultDisabledPlugins.Contains(ext.FileName));
                var warningNote = ext.Kind == D2RLoaderExtensionKind.Plugin
                                  && WarningPluginNotes.TryGetValue(ext.FileName, out var note)
                    ? note
                    : null;
                return new LadderCopyExtensionItem(
                    ExtensionKey(ext.Kind, ext.FileName),
                    ext.Name,
                    ext.FileName,
                    ext.FilePath,
                    ext.Kind,
                    ext.Version,
                    ext.Author,
                    ext.Description,
                    ext.PatchCount,
                    defaultEnabled,
                    warningNote);
            })
            .ToArray();

        var hasExistingNormalConfigs = Directory.Exists(normalConfigDir)
                                       && Directory.EnumerateFiles(normalConfigDir, "*", SearchOption.AllDirectories).Any();

        return new LadderToNormalCopyPlan(
            normalized,
            ladderModRoot,
            normalModRoot,
            backupRoot,
            ResolveTargetSavePath(normalized),
            Directory.Exists(normalModRoot),
            hasExistingNormalConfigs,
            extensions);
    }

    internal static async Task<LadderToNormalCopyResult> CopyAsync(
        string installDirectory,
        IReadOnlySet<string> enabledExtensionKeys,
        bool keepExistingConfigs,
        Action? ensureSafe = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(enabledExtensionKeys);

        var normalized = RequireValidLadderInstall(installDirectory);
        var ladderModRoot = ModInstallationPaths.LadderModRoot(normalized);
        var normalModRoot = Path.Combine(normalized, "mods", ModInstallationPaths.NormalModName);
        var managementRoot = Path.Combine(normalized, ".reimagined-launcher");
        var backupRoot = Path.Combine(managementRoot, BackupFolderName);

        LocalPluginInstallService.EnsureNoLinks(ladderModRoot);
        if (Directory.Exists(normalModRoot))
        {
            LocalPluginInstallService.EnsureNoLinks(normalModRoot);
        }

        if (Directory.Exists(managementRoot))
        {
            LocalPluginInstallService.EnsureNoLinks(managementRoot);
        }

        if (Directory.Exists(backupRoot))
        {
            LocalPluginInstallService.EnsureNoLinks(backupRoot);
        }

        ensureSafe?.Invoke();
        cancellationToken.ThrowIfCancellationRequested();

        var enabledSet = new HashSet<string>(enabledExtensionKeys, StringComparer.OrdinalIgnoreCase);
        var targetSavePath = ResolveTargetSavePath(normalized);
        var discoveredExtensions = D2RLoaderService.DiscoverLadderModExtensions(ladderModRoot);

        var stagingFolderName = $".staging-{CreateUniqueFolderToken()}";
        var stagingParent = Path.Combine(backupRoot, stagingFolderName);
        var stagedModRoot = Path.Combine(stagingParent, ModInstallationPaths.NormalModName);
        Directory.CreateDirectory(stagingParent);

        string? backupEntryDir = null;
        try
        {
            progress?.Report("Staging Ladder mod files...");
            CopyModTree(ladderModRoot, stagedModRoot, depth: 0, cancellationToken);

            var (enabledCount, disabledCount) = RouteExtensions(
                discoveredExtensions,
                enabledSet,
                stagedModRoot,
                cancellationToken);

            var existingConfigDir = Path.Combine(normalModRoot, "d2rloader", "config");
            var stagedConfigDir = Path.Combine(stagedModRoot, "d2rloader", "config");
            if (keepExistingConfigs && Directory.Exists(existingConfigDir))
            {
                RemoveSupersededStagedConfigs(existingConfigDir, stagedConfigDir, cancellationToken);
                CopyDirectoryContents(existingConfigDir, stagedConfigDir, overwrite: true, cancellationToken);
            }

            ScrubConfigSecrets(stagedConfigDir, cancellationToken);
            StripLadderCharacterSelectWidgets(normalized, ladderModRoot, stagedModRoot);
            RemoveDisableDebugFiles(stagedModRoot, cancellationToken);
            RewriteModInfoFiles(stagedModRoot, targetSavePath);

            ensureSafe?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();

            progress?.Report("Restoring managed plugin assets...");
            await PluginAssetBackupService.RestoreUnderDirectoryAsync(normalModRoot).ConfigureAwait(false);
            ensureSafe?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();

            progress?.Report("Backing up existing Non-Ladder folder and applying copy...");
            backupEntryDir = SwapStagedModIntoPlace(
                stagedModRoot,
                normalModRoot,
                backupRoot,
                cancellationToken);

            if (backupEntryDir is not null)
            {
                ScrubConfigSecrets(
                    Path.Combine(backupEntryDir, ModInstallationPaths.NormalModName, "d2rloader", "config"),
                    cancellationToken);
            }

            PruneOldBackups(backupRoot, RetainedBackupCount, stagingFolderName);
            NormalModInstallationService.RecordNexusInstallation(normalized);

            return new LadderToNormalCopyResult(
                normalModRoot,
                backupEntryDir is null ? null : Path.Combine(backupEntryDir, ModInstallationPaths.NormalModName),
                targetSavePath,
                enabledCount,
                disabledCount);
        }
        finally
        {
            TryDeleteDirectory(stagingParent);
        }
    }

    private static string RequireValidLadderInstall(string installDirectory)
    {
        var normalized = InstallDirectoryValidator.NormalizeInstallDirectory(installDirectory);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new DirectoryNotFoundException(
                "ReimaginedLadder is not installed in the selected Diablo II: Resurrected directory.");
        }

        normalized = LocalPluginInstallService.CanonicalDirectoryPath(normalized);
        if (!HasInstalledLadderMod(normalized))
        {
            throw new DirectoryNotFoundException(
                "ReimaginedLadder is not installed in the selected Diablo II: Resurrected directory.");
        }

        return normalized;
    }

    private static bool TryReadValidNormalSavePath(string? modInfoPath, out string savePath)
    {
        savePath = DefaultSavePath;
        if (string.IsNullOrWhiteSpace(modInfoPath) || !NormalModInstallationService.HasNormalSavePath(modInfoPath))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(modInfoPath));
            if (document.RootElement.TryGetProperty("savepath", out var value)
                && value.ValueKind == JsonValueKind.String)
            {
                var trimmed = value.GetString()?.Trim().Trim('/', '\\');
                if (!string.IsNullOrWhiteSpace(trimmed)
                    && !trimmed.Equals(ModInstallationPaths.LadderModName, StringComparison.OrdinalIgnoreCase)
                    && !trimmed.StartsWith(ModInstallationPaths.LadderModName + "-", StringComparison.OrdinalIgnoreCase))
                {
                    savePath = $"{trimmed}/";
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
        }

        return false;
    }

    private static void CopyModTree(
        string sourceDirectory,
        string destinationDirectory,
        int depth,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LocalPluginInstallService.EnsureNoLinks(sourceDirectory);
        Directory.CreateDirectory(destinationDirectory);

        foreach (var file in Directory.EnumerateFiles(sourceDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            LocalPluginInstallService.EnsureNoLinks(file);

            var fileName = Path.GetFileName(file);
            if (fileName.Equals(".DISABLE_DEBUG", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var destinationFileName = depth == 0
                && fileName.Equals(ModInstallationPaths.LadderModName + ".mpq", StringComparison.OrdinalIgnoreCase)
                ? ModInstallationPaths.NormalModName + ".mpq"
                : fileName;
            CopyAndClearReadOnly(file, Path.Combine(destinationDirectory, destinationFileName), overwrite: true);
        }

        foreach (var subdirectory in Directory.EnumerateDirectories(sourceDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            LocalPluginInstallService.EnsureNoLinks(subdirectory);

            var dirName = Path.GetFileName(subdirectory);
            if (depth == 0 && dirName.Equals("d2rloader", StringComparison.OrdinalIgnoreCase))
            {
                CopyLoaderTree(subdirectory, Path.Combine(destinationDirectory, "d2rloader"), cancellationToken);
                continue;
            }

            var destinationDirName = depth == 0 && dirName.Equals(ModInstallationPaths.LadderModName + ".mpq", StringComparison.OrdinalIgnoreCase)
                ? ModInstallationPaths.NormalModName + ".mpq"
                : dirName;
            CopyModTree(
                subdirectory,
                Path.Combine(destinationDirectory, destinationDirName),
                depth + 1,
                cancellationToken);
        }
    }

    private static void CopyLoaderTree(
        string sourceLoaderRoot,
        string destinationLoaderRoot,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LocalPluginInstallService.EnsureNoLinks(sourceLoaderRoot);
        Directory.CreateDirectory(destinationLoaderRoot);

        foreach (var file in Directory.EnumerateFiles(sourceLoaderRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            LocalPluginInstallService.EnsureNoLinks(file);

            var fileName = Path.GetFileName(file);
            if (fileName.Equals(".DISABLE_DEBUG", StringComparison.OrdinalIgnoreCase)
                || fileName.Equals("ladder-bundle-state.json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            CopyAndClearReadOnly(file, Path.Combine(destinationLoaderRoot, fileName), overwrite: true);
        }

        foreach (var subdirectory in Directory.EnumerateDirectories(sourceLoaderRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            LocalPluginInstallService.EnsureNoLinks(subdirectory);

            var dirName = Path.GetFileName(subdirectory);
            if (dirName.Equals("logs", StringComparison.OrdinalIgnoreCase)
                || dirName.Equals("ladder-disabled", StringComparison.OrdinalIgnoreCase)
                || dirName.Equals("launcher-disabled", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (dirName.Equals("plugins", StringComparison.OrdinalIgnoreCase)
                || dirName.Equals("patches", StringComparison.OrdinalIgnoreCase))
            {
                CopyLoaderExtensionSupportFiles(
                    subdirectory,
                    Path.Combine(destinationLoaderRoot, dirName),
                    managedExtensionSuffix: dirName.Equals("plugins", StringComparison.OrdinalIgnoreCase) ? ".dll" : ".json",
                    cancellationToken);
                continue;
            }

            CopyModTree(
                subdirectory,
                Path.Combine(destinationLoaderRoot, dirName),
                depth: 2,
                cancellationToken);
        }
    }

    private static void CopyLoaderExtensionSupportFiles(
        string sourceDirectory,
        string destinationDirectory,
        string managedExtensionSuffix,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destinationDirectory);
        foreach (var file in Directory.EnumerateFiles(sourceDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            LocalPluginInstallService.EnsureNoLinks(file);

            var fileName = Path.GetFileName(file);
            if (fileName.Equals(".DISABLE_DEBUG", StringComparison.OrdinalIgnoreCase)
                || fileName.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)
                || fileName.EndsWith(managedExtensionSuffix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            CopyAndClearReadOnly(file, Path.Combine(destinationDirectory, fileName), overwrite: true);
        }

        foreach (var subdirectory in Directory.EnumerateDirectories(sourceDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            LocalPluginInstallService.EnsureNoLinks(subdirectory);
            CopyModTree(
                subdirectory,
                Path.Combine(destinationDirectory, Path.GetFileName(subdirectory)),
                depth: 3,
                cancellationToken);
        }
    }

    private static (int EnabledCount, int DisabledCount) RouteExtensions(
        IReadOnlyList<D2RLoaderExtensionInfo> discoveredExtensions,
        HashSet<string> enabledKeys,
        string stagedModRoot,
        CancellationToken cancellationToken)
    {
        var enabledCount = 0;
        var disabledCount = 0;

        foreach (var extension in discoveredExtensions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LocalPluginInstallService.EnsureNoLinks(extension.FilePath);

            var kindFolder = extension.Kind == D2RLoaderExtensionKind.Plugin ? "plugins" : "patches";
            var isEnabled = enabledKeys.Contains(ExtensionKey(extension.Kind, extension.FileName));
            var targetFolder = isEnabled
                ? Path.Combine(stagedModRoot, "d2rloader", kindFolder)
                : Path.Combine(stagedModRoot, "d2rloader", "launcher-disabled", kindFolder);

            Directory.CreateDirectory(targetFolder);
            CopyAndClearReadOnly(extension.FilePath, Path.Combine(targetFolder, extension.FileName), overwrite: true);

            if (isEnabled)
            {
                enabledCount++;
            }
            else
            {
                disabledCount++;
            }
        }

        return (enabledCount, disabledCount);
    }

    private static void RemoveSupersededStagedConfigs(
        string existingConfigDir,
        string stagedConfigDir,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(stagedConfigDir))
        {
            return;
        }

        var existingConfigs = Directory.EnumerateFiles(existingConfigDir)
            .Select(path => (
                FileName: Path.GetFileName(path),
                Extension: Path.GetExtension(path),
                IsText: D2RLoaderConfigService.IsTextConfigFile(path),
                Stem: D2RLoaderConfigService.IsTextConfigFile(path)
                    ? D2RLoaderConfigService.GetCanonicalConfigStem(path)
                    : string.Empty))
            .ToArray();
        if (existingConfigs.Length == 0)
        {
            return;
        }

        foreach (var stagedFile in Directory.EnumerateFiles(stagedConfigDir).ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            LocalPluginInstallService.EnsureNoLinks(stagedFile);
            if (!D2RLoaderConfigService.IsTextConfigFile(stagedFile))
            {
                continue;
            }

            var stagedName = Path.GetFileName(stagedFile);
            var stagedExt = Path.GetExtension(stagedFile);
            var stagedStem = D2RLoaderConfigService.GetCanonicalConfigStem(stagedFile);
            var superseded = existingConfigs.Any(existing =>
                string.Equals(existing.FileName, stagedName, StringComparison.OrdinalIgnoreCase)
                || (existing.IsText
                    && !string.IsNullOrEmpty(stagedStem)
                    && string.Equals(existing.Extension, stagedExt, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(existing.Stem, stagedStem, StringComparison.OrdinalIgnoreCase)));
            if (!superseded)
            {
                continue;
            }

            ClearReadOnlyAttribute(stagedFile);
            File.Delete(stagedFile);
        }
    }

    private static void CopyDirectoryContents(
        string sourceDirectory,
        string destinationDirectory,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LocalPluginInstallService.EnsureNoLinks(sourceDirectory);
        Directory.CreateDirectory(destinationDirectory);

        Dictionary<string, string>? existingCaseInsensitive = null;
        if (overwrite && !OperatingSystem.IsWindows() && Directory.Exists(destinationDirectory))
        {
            existingCaseInsensitive = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var existing in Directory.EnumerateFiles(destinationDirectory))
            {
                existingCaseInsensitive.TryAdd(Path.GetFileName(existing), existing);
            }
        }

        foreach (var file in Directory.EnumerateFiles(sourceDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            LocalPluginInstallService.EnsureNoLinks(file);

            var fileName = Path.GetFileName(file);
            if (fileName.Equals(".DISABLE_DEBUG", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (existingCaseInsensitive is not null
                && existingCaseInsensitive.TryGetValue(fileName, out var variantPath)
                && !Path.GetFileName(variantPath).Equals(fileName, StringComparison.Ordinal))
            {
                ClearReadOnlyAttribute(variantPath);
                File.Delete(variantPath);
            }

            CopyAndClearReadOnly(file, Path.Combine(destinationDirectory, fileName), overwrite);
        }

        foreach (var subdirectory in Directory.EnumerateDirectories(sourceDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            LocalPluginInstallService.EnsureNoLinks(subdirectory);
            CopyDirectoryContents(
                subdirectory,
                Path.Combine(destinationDirectory, Path.GetFileName(subdirectory)),
                overwrite,
                cancellationToken);
        }
    }

    private static void ScrubConfigSecrets(string configDirectory, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(configDirectory))
        {
            return;
        }

        foreach (var tomlPath in Directory.EnumerateFiles(configDirectory, "*", SearchOption.AllDirectories))
        {
            if (!tomlPath.EndsWith(".toml", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            LocalPluginInstallService.EnsureNoLinks(tomlPath);
            var content = File.ReadAllText(tomlPath);
            var scrubbed = GameSessionSecretsService.ScrubSecrets(content);
            if (!string.Equals(content, scrubbed, StringComparison.Ordinal))
            {
                ClearReadOnlyAttribute(tomlPath);
                File.WriteAllText(tomlPath, scrubbed, Utf8NoBom);
            }
        }
    }

    private static void StripLadderCharacterSelectWidgets(
        string installDirectory,
        string ladderModRoot,
        string stagedModRoot)
    {
        foreach (var (stagedRelative, ladderRelative) in CharacterSelectLayoutPaths)
        {
            var stagedPath = Path.Combine(stagedModRoot, stagedRelative.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(stagedPath))
            {
                continue;
            }

            var ladderPath = Path.Combine(ladderModRoot, ladderRelative.Replace('/', Path.DirectorySeparatorChar));
            var baselinePath = LadderRuntimeFileService.TryGetExistingBaselinePath(installDirectory, ladderPath);
            if (baselinePath is not null)
            {
                LocalPluginInstallService.EnsureNoLinks(baselinePath);
                CopyAndClearReadOnly(baselinePath, stagedPath, overwrite: true);
            }

            var original = File.ReadAllText(stagedPath);
            var cleaned = LadderCharacterSelectService.RemoveGeneratedWidgets(original);
            if (!string.Equals(original, cleaned, StringComparison.Ordinal))
            {
                ClearReadOnlyAttribute(stagedPath);
                File.WriteAllText(stagedPath, cleaned, Utf8NoBom);
            }
        }
    }

    private static void RemoveDisableDebugFiles(string stagedModRoot, CancellationToken cancellationToken)
    {
        foreach (var file in Directory.EnumerateFiles(stagedModRoot, ".DISABLE_DEBUG", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            File.Delete(file);
        }
    }

    private static void RewriteModInfoFiles(string stagedModRoot, string savePath)
    {
        var updatedCount = 0;
        foreach (var modInfoPath in new[]
                 {
                     Path.Combine(stagedModRoot, ModInstallationPaths.NormalModName + ".mpq", "modinfo.json"),
                     Path.Combine(stagedModRoot, "modinfo.json")
                 })
        {
            if (!File.Exists(modInfoPath))
            {
                continue;
            }

            if (JsonNode.Parse(File.ReadAllText(modInfoPath)) is not JsonObject node)
            {
                throw new InvalidDataException($"Could not parse {modInfoPath} as a JSON object.");
            }

            node["name"] = ModInstallationPaths.NormalModName;
            node["savepath"] = savePath;
            File.WriteAllText(
                modInfoPath,
                node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
                Utf8NoBom);
            updatedCount++;
        }

        if (updatedCount == 0)
        {
            throw new InvalidDataException("Copied Ladder mod does not contain modinfo.json.");
        }
    }

    private static string? SwapStagedModIntoPlace(
        string stagedModRoot,
        string normalModRoot,
        string backupRoot,
        CancellationToken cancellationToken)
    {
        var modsParent = Path.GetDirectoryName(normalModRoot)!;
        Directory.CreateDirectory(modsParent);

        if (!Directory.Exists(normalModRoot))
        {
            Directory.Move(stagedModRoot, normalModRoot);
            return null;
        }

        var backupEntryDir = Path.Combine(backupRoot, CreateUniqueFolderToken());
        var backupModDir = Path.Combine(backupEntryDir, ModInstallationPaths.NormalModName);
        Directory.CreateDirectory(backupEntryDir);

        var movedExistingOut = false;
        try
        {
            Directory.Move(normalModRoot, backupModDir);
            movedExistingOut = true;
            Directory.Move(stagedModRoot, normalModRoot);
            return backupEntryDir;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && !movedExistingOut)
        {
            // Fall back to an in-place content transaction when Explorer holds the directory handle.
            TryDeleteDirectory(backupModDir);
            var transaction = new DirectoryContentTransaction(stagedModRoot, normalModRoot, backupModDir, cancellationToken);
            try
            {
                transaction.Apply();
                return backupEntryDir;
            }
            catch
            {
                transaction.Rollback();
                TryDeleteDirectory(backupEntryDir);
                throw;
            }
        }
        catch
        {
            if (movedExistingOut && !Directory.Exists(normalModRoot) && Directory.Exists(backupModDir))
            {
                try
                {
                    Directory.Move(backupModDir, normalModRoot);
                }
                catch (Exception restoreEx)
                {
                    LaunchDiagnostics.LogException($"Could not restore previous mod folder from '{backupModDir}'", restoreEx);
                }
            }

            if (Directory.Exists(normalModRoot))
            {
                TryDeleteDirectory(backupEntryDir);
            }

            throw;
        }
    }

    private static void PruneOldBackups(
        string backupRoot,
        int retainCount,
        string? activeStagingFolderName = null)
    {
        if (!Directory.Exists(backupRoot))
        {
            return;
        }

        foreach (var directory in Directory.EnumerateDirectories(backupRoot))
        {
            var name = Path.GetFileName(directory);
            if (name.StartsWith('.')
                && !string.Equals(name, activeStagingFolderName, StringComparison.Ordinal))
            {
                TryDeleteDirectory(directory);
            }
        }

        var completedBackups = Directory.EnumerateDirectories(backupRoot)
            .Where(directory =>
            {
                var name = Path.GetFileName(directory);
                return !name.StartsWith('.')
                       && Directory.Exists(Path.Combine(directory, ModInstallationPaths.NormalModName));
            })
            .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
            .Skip(retainCount)
            .ToArray();

        foreach (var obsolete in completedBackups)
        {
            TryDeleteDirectory(obsolete);
        }
    }

    private static string CreateUniqueFolderToken()
    {
        var sequence = Interlocked.Increment(ref _backupSequence) & 0xFFFFF;
        return $"{DateTime.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture)}-{sequence:x5}-{Guid.NewGuid():N}"[..31];
    }

    private static void CopyAndClearReadOnly(string sourcePath, string destinationPath, bool overwrite)
    {
        if (overwrite)
        {
            ClearReadOnlyAttribute(destinationPath);
        }

        File.Copy(sourcePath, destinationPath, overwrite);
        ClearReadOnlyAttribute(destinationPath);
    }

    private static void ClearReadOnlyAttribute(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return;
        }

        var attributes = File.GetAttributes(filePath);
        if ((attributes & FileAttributes.ReadOnly) != 0)
        {
            File.SetAttributes(filePath, attributes & ~FileAttributes.ReadOnly);
        }
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                ClearReadOnlyAttributesRecursive(directory);
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LaunchDiagnostics.LogException($"Could not remove temporary/old backup folder '{directory}'", ex);
        }
    }

    private static void ClearReadOnlyAttributesRecursive(string directory)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReadOnly) != 0)
            {
                File.SetAttributes(entry, attributes & ~FileAttributes.ReadOnly);
            }
        }

        var rootAttributes = File.GetAttributes(directory);
        if ((rootAttributes & FileAttributes.ReadOnly) != 0)
        {
            File.SetAttributes(directory, rootAttributes & ~FileAttributes.ReadOnly);
        }
    }
}
