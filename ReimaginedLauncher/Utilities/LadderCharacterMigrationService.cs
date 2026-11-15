using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using D2SSharp;
using D2SSharp.Model;
using ReimaginedLauncher.SaveMigration;

namespace ReimaginedLauncher.Utilities;

public enum LadderSaveCopyOrigin
{
    ActiveSave,
    PendingUpload,
    LocalBackup
}

public sealed record KnownLadderSaveHint(Guid LadderId, string Name, bool IsSelectedLadder);

public sealed record LadderCharacterCopySnapshot(
    string SourcePath,
    string SourceSha256,
    LadderSaveCopyOrigin Origin,
    string OriginLabel,
    string CharacterName,
    string OriginalStem,
    uint SaveVersion,
    int Level,
    string ClassName,
    bool IsHardcore,
    bool HasDied,
    DateTimeOffset TimestampUtc,
    int FileSizeBytes,
    string? PairedBindPath,
    IReadOnlyList<string> CompanionPaths)
{
    public string DisplayLabel =>
        $"{OriginLabel} · Lv {Level} {ClassName}{(IsHardcore ? " (HC)" : string.Empty)}";
}

public sealed record ExistingNonLadderCharacterInfo(
    string FilePath,
    string FileName,
    string Sha256,
    string? EmbeddedName,
    int? Level,
    string? ClassName,
    bool? IsHardcore,
    DateTimeOffset LastModifiedUtc,
    bool IsFilenameMatch)
{
    public string DescribeExisting()
    {
        var parts = new List<string>();
        if (Level is { } level)
        {
            parts.Add($"Lv {level}");
        }

        if (!string.IsNullOrWhiteSpace(ClassName))
        {
            parts.Add(ClassName);
        }

        if (IsHardcore == true)
        {
            parts.Add("HC");
        }

        var summary = parts.Count > 0 ? string.Join(" ", parts) : "Saved character";
        return $"{FileName} ({summary}, modified {LastModifiedUtc.ToLocalTime():yyyy-MM-dd HH:mm})";
    }
}

public sealed record LadderCharacterCandidate(
    string CharacterKey,
    string CharacterName,
    bool IsMigratable,
    string? BlockedReason,
    IReadOnlyList<LadderCharacterCopySnapshot> Copies,
    ExistingNonLadderCharacterInfo? ExistingConflict,
    string? SuggestedSafeRename);

public sealed record LadderSharedStashOption(
    string FileName,
    string SourcePath,
    string SourceSha256,
    bool IsHardcore,
    int TabCount,
    int FileSizeBytes,
    bool ExistsInDestination,
    string OriginLabel);

public sealed record LadderSaveSourceFolder(
    string FolderName,
    string FullPath,
    Guid? LadderId,
    string DisplayName,
    DateTimeOffset LatestActivityUtc,
    IReadOnlyList<LadderCharacterCandidate> Characters,
    IReadOnlyList<LadderSharedStashOption> SharedStashes);

public sealed record LadderCharacterMigrationInspection(
    string? SavedGamesPath,
    string? ModsSaveRootDirectory,
    string TargetSavePath,
    string? TargetSaveDirectory,
    string? BackupRootDirectory,
    bool TargetHasExistingSaves,
    int ExistingTargetCharacterCount,
    bool TargetHasAnySharedStash,
    IReadOnlyList<string> ReservedTargetNames,
    IReadOnlyList<LadderSaveSourceFolder> Sources,
    string? UnavailableReason);

public sealed record LadderCharacterMigrationSelection(
    string SourcePath,
    string ExpectedSourceSha256,
    string SourceCharacterName,
    string TargetCharacterName,
    bool AllowOverwriteExisting,
    string? ExpectedExistingTargetSha256,
    string? PairedBindPath,
    IReadOnlyList<string> CompanionPaths);

public sealed record LadderSharedStashSelection(
    string FileName,
    string SourcePath,
    string ExpectedSourceSha256);

public sealed record LadderCharacterMigrationRequest(
    string SourceFolderPath,
    string TargetSaveDirectory,
    string BackupRootDirectory,
    string? LauncherBackupSaveDirectory,
    IReadOnlyList<LadderCharacterMigrationSelection> Characters,
    IReadOnlyList<LadderSharedStashSelection> SharedStashes);

public sealed record LadderCharacterMigrationResult(
    string TargetSaveDirectory,
    string? BackupDirectory,
    string? LauncherZipBackupPath,
    int MigratedCharacterCount,
    int RenamedCharacterCount,
    int OverwrittenCharacterCount,
    int CopiedSharedStashCount,
    IReadOnlyList<string> MigratedCharacterNames);

internal sealed class LadderCharacterMigrationRollbackException : InvalidOperationException
{
    public LadderCharacterMigrationRollbackException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

internal static class LadderCharacterMigrationService
{
    internal const string MigrationBackupFolderName = "character-migration-backups";
    internal const int RetainedMigrationBackupCount = 10;

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private const uint SaveMagic = 0xAA55AA55U;
    private const int MinimumCharacterBytes = 64;
    private const int CreationHeaderBytes = 403;
    private const int MinimumLegacyHeaderBytes = 765;
    private const int MinimumV104HeaderBytes = 833;
    private const int MaximumInspectFileBytes = 1024 * 1024;
    private const int MaximumSharedStashBytes = 16 * 1024 * 1024;
    private const int SharedStashTabHeaderBytes = 64;
    private const int SharedStashTabSizeOffset = 16;
    private const int ChecksumOffset = 12;
    private const int V104NameOffset = 16 + 0x9F + 0x7C;
    private const int V104NameLength = 96;

    private const uint SummonStateMagic = 0x31534442U;
    private const int SummonStateHeaderBytes = 0x30;
    private const int SummonStateHeaderSizeOffset = 0x06;
    private const int SummonStateRecordCountOffset = 0x08;
    private const int SummonStatePayloadSizeOffset = 0x0C;
    private const int SummonStatePairedSizeOffset = 0x18;
    private const uint SummonStateMaximumRecords = 4096;

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static int _backupSequence;
    private static readonly Regex LadderFolderSuffixRegex =
        new("-(?<id>[0-9a-fA-F]{8}|[0-9a-fA-F]{32})$", RegexOptions.Compiled);
    private static readonly Regex BackupStemRegex =
        new(@"^(?<stem>.+)\.(?<stamp>\d{8}-\d{6})(?:-\d+)?$", RegexOptions.Compiled);

    private static readonly string[] CarriedCompanionExtensions = [".key", ".keyo", ".ctl", ".ctlo"];
    private static readonly string[] OverwriteRemovedSidecarExtensions =
        [".bind", ".d2rl", ".key", ".keyo", ".ctl", ".ctlo", ".map", ".ma0", ".ma1", ".ma2", ".ma3"];
    private static readonly HashSet<string> ReservedWindowsNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    private static readonly string[] SoftcoreStashNames =
        ["ModernSharedStashSoftCoreV2.d2i", "SharedStashSoftCoreV2.d2i"];
    private static readonly string[] HardcoreStashNames =
        ["ModernSharedStashHardCoreV2.d2i", "SharedStashHardCoreV2.d2i"];

    private sealed record TargetInventory(
        bool Exists,
        IReadOnlyList<ExistingNonLadderCharacterInfo> Characters,
        IReadOnlySet<string> ReservedNames,
        bool HasAnySoftcoreStash,
        bool HasAnyHardcoreStash,
        bool HasAnySharedStash,
        string? BlockedReason = null);

    private sealed record ScannedD2sCopy(
        string OriginalStem,
        string CanonicalCharacterKey,
        LadderCharacterCopySnapshot? ValidSnapshot,
        string? RejectedReason,
        bool IsCreationStub,
        DateTimeOffset TimestampUtc);

    internal static string? ResolveTargetSaveDirectory(string targetSavePath, string? savedGamesPath = null) =>
        ResolveSavePaths(targetSavePath, savedGamesPath).TargetSaveDirectory;

    private static (string? SavedGamesPath, string? ExistingModsPath, string? ModsSaveRoot, string? TargetSaveDirectory) ResolveSavePaths(
        string? targetSavePath,
        string? savedGamesPath)
    {
        var normalizedTargetPath = NormalizeRelativeSavePath(targetSavePath);
        if (string.IsNullOrWhiteSpace(normalizedTargetPath))
        {
            return (savedGamesPath, null, null, null);
        }

        savedGamesPath ??= SaveFileService.GetSavedGamesPath();
        if (string.IsNullOrWhiteSpace(savedGamesPath)
            && !string.IsNullOrWhiteSpace(MainWindow.Settings?.CurrentProfile.SaveDirectory))
        {
            savedGamesPath = FileService.FindAncestorDirectory(
                MainWindow.Settings.CurrentProfile.SaveDirectory,
                "Saved Games");
        }

        if (string.IsNullOrWhiteSpace(savedGamesPath))
        {
            return (null, null, null, null);
        }

        savedGamesPath = LocalPluginInstallService.CanonicalDirectoryPath(savedGamesPath);

        var d2rPath = SaveFileService.ResolveDirectoryCaseInsensitive(savedGamesPath, "Diablo II Resurrected");
        var existingModsPath = d2rPath is null
            ? null
            : SaveFileService.ResolveDirectoryCaseInsensitive(d2rPath, "mods");
        var modsSaveRoot = existingModsPath
                           ?? Path.Combine(d2rPath ?? Path.Combine(savedGamesPath, "Diablo II Resurrected"), "mods");

        var resolvedExistingTarget = existingModsPath is null
            ? null
            : SaveFileService.ResolveDirectoryCaseInsensitive(existingModsPath, normalizedTargetPath);
        var targetSaveDirectory = resolvedExistingTarget ?? Path.Combine(modsSaveRoot, normalizedTargetPath);
        return (savedGamesPath, existingModsPath, modsSaveRoot, targetSaveDirectory);
    }

    internal static LadderCharacterMigrationInspection Inspect(
        string targetSavePath,
        IReadOnlyList<KnownLadderSaveHint>? knownLadders = null,
        string? savedGamesPath = null)
    {
        var normalizedTargetPath = NormalizeRelativeSavePath(targetSavePath);
        if (string.IsNullOrWhiteSpace(normalizedTargetPath))
        {
            return new LadderCharacterMigrationInspection(
                savedGamesPath,
                null,
                LadderToNormalCopyService.DefaultSavePath,
                null,
                null,
                false,
                0,
                false,
                [],
                [],
                "The target Non-Ladder save path is empty.");
        }

        var formattedTargetPath = $"{normalizedTargetPath}/";
        string? resolvedSavedGames;
        string? existingModsPath;
        string? modsSaveRoot;
        string? targetSaveDirectory;
        try
        {
            (resolvedSavedGames, existingModsPath, modsSaveRoot, targetSaveDirectory) =
                ResolveSavePaths(normalizedTargetPath, savedGamesPath);
        }
        catch (IOException ex)
        {
            return new LadderCharacterMigrationInspection(
                null,
                null,
                formattedTargetPath,
                null,
                null,
                false,
                0,
                false,
                [],
                [],
                ex.Message);
        }

        if (string.IsNullOrWhiteSpace(resolvedSavedGames)
            || string.IsNullOrWhiteSpace(modsSaveRoot)
            || string.IsNullOrWhiteSpace(targetSaveDirectory))
        {
            return new LadderCharacterMigrationInspection(
                null,
                null,
                formattedTargetPath,
                null,
                null,
                false,
                0,
                false,
                [],
                [],
                "Could not locate the Saved Games folder for this profile.");
        }

        var backupRoot = Path.Combine(modsSaveRoot, ".reimagined-launcher", MigrationBackupFolderName);

        var targetInventory = InspectTargetSaveDirectory(targetSaveDirectory);
        if (targetInventory.BlockedReason is not null)
        {
            return new LadderCharacterMigrationInspection(
                resolvedSavedGames,
                modsSaveRoot,
                formattedTargetPath,
                targetSaveDirectory,
                backupRoot,
                targetInventory.Exists,
                targetInventory.Characters.Count,
                targetInventory.HasAnySharedStash,
                targetInventory.ReservedNames.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray(),
                [],
                targetInventory.BlockedReason);
        }
        if (existingModsPath is null || !Directory.Exists(existingModsPath))
        {
            return new LadderCharacterMigrationInspection(
                resolvedSavedGames,
                modsSaveRoot,
                formattedTargetPath,
                targetSaveDirectory,
                backupRoot,
                false,
                0,
                false,
                targetInventory.ReservedNames.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray(),
                [],
                "No local Ladder save folders were found yet.");
        }

        var registryEntries = ReadRegistryEntriesReadOnly(existingModsPath);
        var hintById = (knownLadders ?? [])
            .GroupBy(hint => hint.LadderId)
            .ToDictionary(group => group.Key, group => group.First());

        var discoveredFolders = DiscoverSourceFolders(existingModsPath, targetSaveDirectory, registryEntries);
        var sources = discoveredFolders
            .Select(entry => BuildSourceFolder(entry.FullPath, entry.LadderId, hintById, targetInventory))
            .OrderByDescending(source => source.Characters.Count > 0 || source.SharedStashes.Count > 0)
            .ThenByDescending(source =>
                source.LadderId is { } id && hintById.TryGetValue(id, out var hint) && hint.IsSelectedLadder)
            .ThenByDescending(source => source.LatestActivityUtc)
            .ThenBy(source => source.FolderName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        string? unavailableReason = sources.Length == 0
            ? "No local Ladder save folders were found."
            : sources.All(source => source.Characters.Count == 0 && source.SharedStashes.Count == 0)
                ? "Local Ladder save folders were found, but none contain playable characters or stashes."
                : null;

        return new LadderCharacterMigrationInspection(
            resolvedSavedGames,
            modsSaveRoot,
            formattedTargetPath,
            targetSaveDirectory,
            backupRoot,
            targetInventory.Exists && Directory.EnumerateFiles(ToExtendedSafe(targetSaveDirectory), "*", SearchOption.AllDirectories).Any(),
            targetInventory.Characters.Count,
            targetInventory.HasAnySharedStash,
            targetInventory.ReservedNames.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray(),
            sources,
            unavailableReason);
    }

    internal static void ValidateRequest(LadderCharacterMigrationRequest request, Action? ensureSafe = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ensureSafe?.Invoke();

        var sourceRoot = LocalPluginInstallService.CanonicalDirectoryPath(StripExtendedPrefix(request.SourceFolderPath));
        var targetRoot = LocalPluginInstallService.CanonicalDirectoryPath(StripExtendedPrefix(request.TargetSaveDirectory));
        var backupRoot = LocalPluginInstallService.CanonicalDirectoryPath(StripExtendedPrefix(request.BackupRootDirectory));
        var extendedSourceRoot = ToExtendedSafe(sourceRoot);
        var extendedTargetRoot = ToExtendedSafe(targetRoot);
        var extendedBackupRoot = ToExtendedSafe(backupRoot);

        if (!Directory.Exists(extendedSourceRoot))
        {
            throw new DirectoryNotFoundException($"Ladder save folder '{sourceRoot}' no longer exists.");
        }

        LocalPluginInstallService.EnsureNoLinks(extendedSourceRoot);
        if (Directory.Exists(extendedTargetRoot))
        {
            LocalPluginInstallService.EnsureNoLinks(extendedTargetRoot);
        }

        if (Directory.Exists(extendedBackupRoot))
        {
            LocalPluginInstallService.EnsureNoLinks(extendedBackupRoot);
        }

        if (IsSameOrNested(sourceRoot, targetRoot)
            || IsSameOrNested(targetRoot, sourceRoot)
            || IsSameOrNested(backupRoot, targetRoot)
            || IsSameOrNested(targetRoot, backupRoot)
            || IsSameOrNested(backupRoot, sourceRoot)
            || IsSameOrNested(sourceRoot, backupRoot))
        {
            throw new InvalidOperationException(
                "Source Ladder saves, destination Non-Ladder saves, and backup folders cannot overlap.");
        }

        if (request.Characters.Count == 0 && request.SharedStashes.Count == 0)
        {
            throw new InvalidOperationException("Select at least one character or shared stash to migrate.");
        }

        var targetInventory = InspectTargetSaveDirectory(targetRoot);
        if (targetInventory.BlockedReason is not null)
        {
            throw new IOException(targetInventory.BlockedReason);
        }
        var chosenTargetNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var chosenStashFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var selection in request.Characters)
        {
            ValidateCharacterSelection(selection, sourceRoot, targetRoot, targetInventory, chosenTargetNames);
        }

        foreach (var stash in request.SharedStashes)
        {
            ValidateSharedStashSelection(stash, sourceRoot, targetRoot, targetInventory, chosenStashFiles);
        }
    }

    internal static async Task<LadderCharacterMigrationResult> MigrateAsync(
        LadderCharacterMigrationRequest request,
        Action? ensureSafe = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        progress?.Report("Validating Ladder save selection and safeguards...");
        ValidateRequest(request, ensureSafe);
        cancellationToken.ThrowIfCancellationRequested();

        var sourceRoot = LocalPluginInstallService.CanonicalDirectoryPath(StripExtendedPrefix(request.SourceFolderPath));
        var targetRoot = LocalPluginInstallService.CanonicalDirectoryPath(StripExtendedPrefix(request.TargetSaveDirectory));
        var backupRoot = LocalPluginInstallService.CanonicalDirectoryPath(StripExtendedPrefix(request.BackupRootDirectory));
        var extendedTargetRoot = ToExtendedSafe(targetRoot);

        string? backupBatchDir = null;
        string? backupSnapshotDir = null;
        if (Directory.Exists(extendedTargetRoot) && Directory.EnumerateFileSystemEntries(extendedTargetRoot).Any())
        {
            progress?.Report($"Creating verified backup of {Path.GetFileName(targetRoot)}...");
            (backupBatchDir, backupSnapshotDir) = await Task.Run(
                () => CreateVerifiedTargetBackup(targetRoot, backupRoot, request, cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }

        string? launcherZipBackupPath = null;
        if (!string.IsNullOrWhiteSpace(request.LauncherBackupSaveDirectory)
            && Directory.Exists(extendedTargetRoot)
            && Directory.EnumerateFiles(extendedTargetRoot, "*", SearchOption.AllDirectories).Any())
        {
            string? canonicalLauncherBackupRoot = null;
            try
            {
                canonicalLauncherBackupRoot = LocalPluginInstallService.CanonicalDirectoryPath(
                    StripExtendedPrefix(request.LauncherBackupSaveDirectory));
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
            {
                LaunchDiagnostics.Log($"Skipping secondary zip save backup: invalid backup directory ({ex.Message}).");
            }

            if (canonicalLauncherBackupRoot is not null)
            {
                if (IsSameOrNested(canonicalLauncherBackupRoot, targetRoot)
                    || IsSameOrNested(canonicalLauncherBackupRoot, sourceRoot))
                {
                    LaunchDiagnostics.Log(
                        $"Skipping secondary zip save backup because '{canonicalLauncherBackupRoot}' is inside the source or destination save folder.");
                }
                else
                {
                    launcherZipBackupPath = TryCreateLauncherZipBackup(targetRoot, canonicalLauncherBackupRoot);
                }
            }
        }

        ensureSafe?.Invoke();
        cancellationToken.ThrowIfCancellationRequested();

        var preparedCharacters = new List<(LadderCharacterMigrationSelection Selection, byte[] Bytes)>(request.Characters.Count);
        foreach (var selection in request.Characters)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bytes = PrepareCharacterOutputBytes(selection);
            preparedCharacters.Add((selection, bytes));
        }

        var preparedStashes = new List<(LadderSharedStashSelection Selection, byte[] Bytes)>(request.SharedStashes.Count);
        foreach (var stash in request.SharedStashes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bytes = File.ReadAllBytes(ToExtendedSafe(stash.SourcePath));
            preparedStashes.Add((stash, bytes));
        }

        ensureSafe?.Invoke();
        cancellationToken.ThrowIfCancellationRequested();

        Directory.CreateDirectory(extendedTargetRoot);
        var createdNewFiles = new List<string>();
        var modifiedOrDeletedRelativePaths = new HashSet<string>(PathComparer);
        var renamedCount = 0;
        var overwrittenCount = 0;
        var migratedNames = new List<string>(preparedCharacters.Count);

        progress?.Report("Writing verified character saves to Non-Ladder folder...");
        try
        {
            foreach (var (selection, outputBytes) in preparedCharacters)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ensureSafe?.Invoke();

                var destinationFileName = selection.TargetCharacterName + ".d2s";
                var destinationD2s = Path.Combine(targetRoot, destinationFileName);
                var existingD2sMatches = Directory.Exists(extendedTargetRoot)
                    ? Directory.EnumerateFiles(extendedTargetRoot)
                        .Select(StripExtendedPrefix)
                        .Where(path => Path.GetFileName(path).Equals(destinationFileName, StringComparison.OrdinalIgnoreCase))
                        .ToArray()
                    : [];
                if (existingD2sMatches.Length > 0 && !selection.AllowOverwriteExisting)
                {
                    throw new InvalidOperationException(
                        $"Destination character '{destinationFileName}' already exists and overwrite was not authorized.");
                }

                if (existingD2sMatches.Length > 0 && selection.AllowOverwriteExisting)
                {
                    foreach (var existingMatch in existingD2sMatches)
                    {
                        var isExactSamePath = string.Equals(
                            Path.GetFullPath(existingMatch),
                            Path.GetFullPath(destinationD2s),
                            StringComparison.Ordinal);
                        if (!isExactSamePath)
                        {
                            var extendedMatch = ToExtendedSafe(existingMatch);
                            LocalPluginInstallService.EnsureNoLinks(extendedMatch);
                            ClearReadOnlyAttribute(extendedMatch);
                            File.Delete(extendedMatch);
                            modifiedOrDeletedRelativePaths.Add(Path.GetRelativePath(targetRoot, existingMatch));
                        }
                    }
                }

                var overwriteExactTarget = File.Exists(ToExtendedSafe(destinationD2s)) && selection.AllowOverwriteExisting;
                WriteFileAtomically(
                    destinationD2s,
                    outputBytes,
                    overwrite: overwriteExactTarget,
                    createdNewFiles,
                    modifiedOrDeletedRelativePaths,
                    targetRoot);

                if (existingD2sMatches.Length > 0 && selection.AllowOverwriteExisting)
                {
                    overwrittenCount++;
                }

                RemoveStaleDestinationSidecars(
                    targetRoot,
                    selection.TargetCharacterName,
                    modifiedOrDeletedRelativePaths);

                if (!string.Equals(selection.TargetCharacterName, selection.SourceCharacterName, StringComparison.Ordinal))
                {
                    renamedCount++;
                }

                CopyVerifiedCompanions(
                    selection,
                    outputBytes.Length,
                    targetRoot,
                    createdNewFiles,
                    modifiedOrDeletedRelativePaths,
                    cancellationToken);

                migratedNames.Add(selection.TargetCharacterName);
            }

            foreach (var (stash, stashBytes) in preparedStashes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ensureSafe?.Invoke();

                var destinationStash = Path.Combine(targetRoot, stash.FileName);
                WriteFileAtomically(
                    destinationStash,
                    stashBytes,
                    overwrite: false,
                    createdNewFiles,
                    modifiedOrDeletedRelativePaths,
                    targetRoot);
            }
        }
        catch (Exception ex)
        {
            var rollbackErrors = RollbackTargetChanges(
                targetRoot,
                backupSnapshotDir,
                createdNewFiles,
                modifiedOrDeletedRelativePaths);
            if (rollbackErrors.Count > 0)
            {
                var backupHint = string.IsNullOrWhiteSpace(backupBatchDir)
                    ? "Check the Non-Ladder save folder manually."
                    : $"Restore manually from '{backupBatchDir}'.";
                throw new LadderCharacterMigrationRollbackException(
                    $"Character migration failed ({ex.Message}) and automatic rollback hit {rollbackErrors.Count} error(s): {string.Join("; ", rollbackErrors)}. {backupHint}",
                    ex);
            }

            throw;
        }

        if (backupBatchDir is not null)
        {
            PruneOldMigrationBackups(backupRoot, RetainedMigrationBackupCount, backupBatchDir);
        }

        return new LadderCharacterMigrationResult(
            targetRoot,
            backupBatchDir,
            launcherZipBackupPath,
            preparedCharacters.Count,
            renamedCount,
            overwrittenCount,
            preparedStashes.Count,
            migratedNames);
    }

    private static TargetInventory InspectTargetSaveDirectory(string targetSaveDirectory)
    {
        var reservedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var characters = new List<ExistingNonLadderCharacterInfo>();
        var hasSoftcoreStash = false;
        var hasHardcoreStash = false;

        var extendedTargetDir = ToExtendedSafe(targetSaveDirectory);
        if (!Directory.Exists(extendedTargetDir))
        {
            return new TargetInventory(false, characters, reservedNames, false, false, false);
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(extendedTargetDir, "*", SearchOption.AllDirectories))
        {
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
            {
                return new TargetInventory(
                    true,
                    characters,
                    reservedNames,
                    hasSoftcoreStash,
                    hasHardcoreStash,
                    hasSoftcoreStash || hasHardcoreStash,
                    "The Non-Ladder save folder contains symbolic links or junctions, which cannot be backed up safely for character migration.");
            }
        }

        foreach (var extendedFile in Directory.EnumerateFiles(extendedTargetDir))
        {
            var file = StripExtendedPrefix(extendedFile);
            var fileName = Path.GetFileName(file);
            var extension = Path.GetExtension(fileName);
            if (extension.Equals(".d2i", StringComparison.OrdinalIgnoreCase))
            {
                if (SoftcoreStashNames.Contains(fileName, StringComparer.OrdinalIgnoreCase))
                {
                    hasSoftcoreStash = true;
                }
                else if (HardcoreStashNames.Contains(fileName, StringComparer.OrdinalIgnoreCase))
                {
                    hasHardcoreStash = true;
                }

                continue;
            }

            if (!extension.Equals(".d2s", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var stem = Path.GetFileNameWithoutExtension(fileName);
            reservedNames.Add(stem);

            var info = new FileInfo(extendedFile);
            string? embeddedName = null;
            int? level = null;
            string? className = null;
            bool? isHardcore = null;
            var sha256 = string.Empty;

            try
            {
                if (info.Length <= MaximumInspectFileBytes)
                {
                    var bytes = File.ReadAllBytes(extendedFile);
                    sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
                    if (TryReadHeaderSummary(bytes, out var parsedEmbedded, out _, out var parsedLevel, out var parsedClass, out var parsedHardcore, out _))
                    {
                        embeddedName = parsedEmbedded;
                        level = parsedLevel;
                        className = parsedClass;
                        isHardcore = parsedHardcore;
                        if (!string.IsNullOrWhiteSpace(parsedEmbedded))
                        {
                            reservedNames.Add(parsedEmbedded);
                        }
                    }
                }
                else
                {
                    using var stream = File.OpenRead(extendedFile);
                    sha256 = Convert.ToHexStringLower(SHA256.HashData(stream));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                sha256 = string.Empty;
            }

            characters.Add(new ExistingNonLadderCharacterInfo(
                file,
                fileName,
                sha256,
                embeddedName,
                level,
                className,
                isHardcore,
                new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
                IsFilenameMatch: true));
        }

        return new TargetInventory(
            true,
            characters,
            reservedNames,
            hasSoftcoreStash,
            hasHardcoreStash,
            hasSoftcoreStash || hasHardcoreStash);
    }

    private static IReadOnlyDictionary<Guid, string> ReadRegistryEntriesReadOnly(string modsSaveRoot)
    {
        var registryPath = LadderSavePathRegistry.RegistryPath(modsSaveRoot);
        if (!File.Exists(registryPath))
        {
            return new Dictionary<Guid, string>();
        }

        try
        {
            var attrs = File.GetAttributes(registryPath);
            if ((attrs & FileAttributes.ReparsePoint) != 0)
            {
                return new Dictionary<Guid, string>();
            }

            return JsonSerializer.Deserialize<Dictionary<Guid, string>>(File.ReadAllText(registryPath))
                   ?? new Dictionary<Guid, string>();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new Dictionary<Guid, string>();
        }
    }

    private static IReadOnlyList<(string FullPath, Guid? LadderId)> DiscoverSourceFolders(
        string modsSaveRoot,
        string targetSaveDirectory,
        IReadOnlyDictionary<Guid, string> registryEntries)
    {
        var normalizedTarget = Path.TrimEndingDirectorySeparator(Path.GetFullPath(StripExtendedPrefix(targetSaveDirectory)));
        var seenPaths = new HashSet<string>(PathComparer);
        var discovered = new List<(string FullPath, Guid? LadderId)>();

        void TryAdd(string candidatePath, Guid? ladderId)
        {
            if (string.IsNullOrWhiteSpace(candidatePath) || !Directory.Exists(candidatePath))
            {
                return;
            }

            var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(StripExtendedPrefix(candidatePath)));
            if (string.Equals(fullPath, normalizedTarget, PathComparison))
            {
                return;
            }

            var folderName = Path.GetFileName(fullPath);
            if (folderName.StartsWith('.') || LadderSaveDirectoryService.IsPreviewSavePath(folderName))
            {
                return;
            }

            if ((File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
            {
                return;
            }

            if (seenPaths.Add(fullPath))
            {
                discovered.Add((fullPath, ladderId));
            }
        }

        foreach (var (ladderId, relativePath) in registryEntries)
        {
            if (string.IsNullOrWhiteSpace(relativePath) || LadderSaveDirectoryService.IsPreviewSavePath(relativePath))
            {
                continue;
            }

            var normalizedRel = relativePath.Replace('/', Path.DirectorySeparatorChar)
                .Replace('\\', Path.DirectorySeparatorChar)
                .Trim(Path.DirectorySeparatorChar);
            if (normalizedRel.Contains("..", StringComparison.Ordinal))
            {
                continue;
            }

            TryAdd(Path.Combine(modsSaveRoot, normalizedRel), ladderId);
        }

        foreach (var subdir in Directory.EnumerateDirectories(modsSaveRoot))
        {
            var folderName = Path.GetFileName(subdir);
            var match = LadderFolderSuffixRegex.Match(folderName);
            var hasServerSaves = Directory.Exists(Path.Combine(subdir, ".server-saves"));
            if (!match.Success && !hasServerSaves)
            {
                continue;
            }

            Guid? matchedLadderId = null;
            if (match.Success && Guid.TryParseExact(match.Groups["id"].Value, "N", out var fullGuid))
            {
                matchedLadderId = fullGuid;
            }

            TryAdd(subdir, matchedLadderId);
        }

        return discovered;
    }

    private static LadderSaveSourceFolder BuildSourceFolder(
        string folderPath,
        Guid? ladderId,
        IReadOnlyDictionary<Guid, KnownLadderSaveHint> hintById,
        TargetInventory targetInventory)
    {
        var folderName = Path.GetFileName(folderPath);
        var serverSavesDir = Path.Combine(folderPath, ".server-saves");
        var pendingUploadDir = Path.Combine(serverSavesDir, "pending-upload");
        var backupsDir = Path.Combine(serverSavesDir, "backups");

        var rootBindFiles = IndexRootSidecars(folderPath, ".bind");
        var rootCompanions = IndexRootCompanions(folderPath);

        var scannedCopies = new List<ScannedD2sCopy>();
        ScanD2sDirectory(folderPath, LadderSaveCopyOrigin.ActiveSave, rootBindFiles, rootCompanions, scannedCopies);
        if (Directory.Exists(pendingUploadDir) && (File.GetAttributes(pendingUploadDir) & FileAttributes.ReparsePoint) == 0)
        {
            ScanD2sDirectory(pendingUploadDir, LadderSaveCopyOrigin.PendingUpload, rootBindFiles, rootCompanions, scannedCopies);
        }

        if (Directory.Exists(backupsDir) && (File.GetAttributes(backupsDir) & FileAttributes.ReparsePoint) == 0)
        {
            ScanD2sDirectory(backupsDir, LadderSaveCopyOrigin.LocalBackup, rootBindFiles, rootCompanions, scannedCopies);
        }

        var reservedSourceNames = new HashSet<string>(
            scannedCopies.Select(copy => copy.CanonicalCharacterKey),
            StringComparer.OrdinalIgnoreCase);

        var candidates = new List<LadderCharacterCandidate>();
        foreach (var group in scannedCopies.GroupBy(copy => copy.CanonicalCharacterKey, StringComparer.OrdinalIgnoreCase))
        {
            var candidate = BuildCharacterCandidate(group.Key, group.ToList(), targetInventory, reservedSourceNames);
            if (candidate is not null)
            {
                candidates.Add(candidate);
            }
        }

        candidates = candidates
            .OrderByDescending(candidate => candidate.IsMigratable)
            .ThenByDescending(candidate => candidate.Copies.FirstOrDefault()?.TimestampUtc ?? DateTimeOffset.MinValue)
            .ThenBy(candidate => candidate.CharacterName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var stashes = DiscoverSharedStashes(folderPath, backupsDir, targetInventory);

        var latestActivity = candidates
            .SelectMany(candidate => candidate.Copies)
            .Select(copy => copy.TimestampUtc)
            .Concat(stashes.Select(stash =>
            {
                var extendedStash = ToExtendedSafe(stash.SourcePath);
                return File.Exists(extendedStash)
                    ? new DateTimeOffset(DateTime.SpecifyKind(File.GetLastWriteTimeUtc(extendedStash), DateTimeKind.Utc))
                    : DateTimeOffset.MinValue;
            }))
            .DefaultIfEmpty(new DateTimeOffset(Directory.GetLastWriteTimeUtc(folderPath), TimeSpan.Zero))
            .Max();

        var displayName = ResolveFolderDisplayName(folderName, ladderId, hintById, candidates.Count);

        return new LadderSaveSourceFolder(
            folderName,
            folderPath,
            ladderId,
            displayName,
            latestActivity,
            candidates,
            stashes);
    }

    private static string ResolveFolderDisplayName(
        string folderName,
        Guid? ladderId,
        IReadOnlyDictionary<Guid, KnownLadderSaveHint> hintById,
        int characterCount)
    {
        var baseName = folderName;
        if (ladderId is { } id && hintById.TryGetValue(id, out var hint) && !string.IsNullOrWhiteSpace(hint.Name))
        {
            baseName = hint.IsSelectedLadder ? $"{hint.Name} (Active Ladder)" : hint.Name;
        }
        else if (folderName.StartsWith("ReimaginedThree-", StringComparison.OrdinalIgnoreCase))
        {
            var stripped = LadderFolderSuffixRegex.Replace(folderName["ReimaginedThree-".Length..], string.Empty);
            if (!string.IsNullOrWhiteSpace(stripped))
            {
                baseName = stripped.Replace('-', ' ');
            }
        }

        return $"{baseName} ({characterCount} char{(characterCount == 1 ? string.Empty : "s")} · {folderName})";
    }

    private static Dictionary<string, string> IndexRootSidecars(string folderPath, string extension)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(folderPath))
        {
            if (!Path.GetExtension(file).Equals(extension, StringComparison.OrdinalIgnoreCase)
                || (File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
            {
                continue;
            }

            map[Path.GetFileNameWithoutExtension(file)] = file;
        }

        return map;
    }

    private static Dictionary<string, IReadOnlyList<string>> IndexRootCompanions(string folderPath)
    {
        var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(folderPath))
        {
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
            {
                continue;
            }

            var ext = Path.GetExtension(file);
            if (!CarriedCompanionExtensions.Any(known => ext.Equals(known, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var stem = Path.GetFileNameWithoutExtension(file);
            if (!map.TryGetValue(stem, out var list))
            {
                list = [];
                map[stem] = list;
            }

            list.Add(file);
        }

        return map.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<string>)pair.Value.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray(),
            StringComparer.OrdinalIgnoreCase);
    }

    private static void ScanD2sDirectory(
        string directory,
        LadderSaveCopyOrigin origin,
        IReadOnlyDictionary<string, string> rootBindFiles,
        IReadOnlyDictionary<string, IReadOnlyList<string>> rootCompanions,
        List<ScannedD2sCopy> output)
    {
        var scanDirectory = origin == LadderSaveCopyOrigin.ActiveSave
            ? directory
            : LadderSaveDirectoryService.ToExtendedPath(directory);
        if (!Directory.Exists(scanDirectory))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(scanDirectory))
        {
            if (!Path.GetExtension(file).Equals(".d2s", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
            {
                continue;
            }

            var rawStem = Path.GetFileNameWithoutExtension(file);
            var (originalStem, parsedTimestampUtc) = ResolveStemAndTimestamp(file, rawStem, origin);
            var info = new FileInfo(file);
            var timestampUtc = parsedTimestampUtc ?? new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);

            if (info.Length < MinimumCharacterBytes || info.Length > MaximumInspectFileBytes)
            {
                output.Add(new ScannedD2sCopy(
                    originalStem,
                    originalStem,
                    null,
                    $"'{Path.GetFileName(file)}' has unsupported file size ({info.Length} bytes).",
                    false,
                    timestampUtc));
                continue;
            }

            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                output.Add(new ScannedD2sCopy(
                    originalStem,
                    originalStem,
                    null,
                    $"Could not read '{Path.GetFileName(file)}': {ex.Message}",
                    false,
                    timestampUtc));
                continue;
            }

            var inspectionError = ValidateD2sBytes(bytes, out var version, out var isCreationStub);
            if (inspectionError is not null)
            {
                TryReadHeaderSummary(bytes, out var stubEmbedded, out _, out _, out _, out _, out _);
                var key = ResolveCanonicalCharacterName(stubEmbedded, originalStem) ?? originalStem;
                output.Add(new ScannedD2sCopy(
                    originalStem,
                    key,
                    null,
                    inspectionError,
                    isCreationStub,
                    timestampUtc));
                continue;
            }

            if (!TryReadHeaderSummary(
                    bytes,
                    out var embeddedName,
                    out _,
                    out var level,
                    out var className,
                    out var isHardcore,
                    out var hasDied))
            {
                output.Add(new ScannedD2sCopy(
                    originalStem,
                    originalStem,
                    null,
                    $"Could not decode character header of '{Path.GetFileName(file)}'.",
                    false,
                    timestampUtc));
                continue;
            }

            var canonicalName = ResolveCanonicalCharacterName(embeddedName, originalStem);
            if (canonicalName is null)
            {
                output.Add(new ScannedD2sCopy(
                    originalStem,
                    originalStem,
                    null,
                    $"Character name in '{Path.GetFileName(file)}' contains unsafe path characters.",
                    false,
                    timestampUtc));
                continue;
            }

            var sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
            var originLabel = origin switch
            {
                LadderSaveCopyOrigin.ActiveSave => "Active save",
                LadderSaveCopyOrigin.PendingUpload => $"Unsynced exit save ({timestampUtc.ToLocalTime():yyyy-MM-dd HH:mm})",
                _ => $"Local backup ({timestampUtc.ToLocalTime():yyyy-MM-dd HH:mm})"
            };

            var pairedBind = FindPairedBindSidecar(file, rawStem, originalStem, bytes.Length, rootBindFiles);
            var companions = rootCompanions.TryGetValue(originalStem, out var foundCompanions)
                ? foundCompanions
                : [];

            var snapshot = new LadderCharacterCopySnapshot(
                StripExtendedPrefix(file),
                sha256,
                origin,
                originLabel,
                canonicalName,
                originalStem,
                version,
                level,
                className,
                isHardcore,
                hasDied,
                timestampUtc,
                bytes.Length,
                pairedBind,
                companions);

            output.Add(new ScannedD2sCopy(
                originalStem,
                canonicalName,
                snapshot,
                null,
                false,
                timestampUtc));
        }
    }

    private static (string OriginalStem, DateTimeOffset? TimestampUtc) ResolveStemAndTimestamp(
        string d2sPath,
        string rawStem,
        LadderSaveCopyOrigin origin)
    {
        if (origin == LadderSaveCopyOrigin.LocalBackup)
        {
            var match = BackupStemRegex.Match(rawStem);
            if (match.Success
                && DateTime.TryParseExact(
                    match.Groups["stamp"].Value,
                    "yyyyMMdd-HHmmss",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var parsedUtc))
            {
                return (match.Groups["stem"].Value, new DateTimeOffset(parsedUtc, TimeSpan.Zero));
            }
        }
        else if (origin == LadderSaveCopyOrigin.PendingUpload)
        {
            var originNote = d2sPath + ".origin";
            if (File.Exists(originNote) && (File.GetAttributes(originNote) & FileAttributes.ReparsePoint) == 0)
            {
                try
                {
                    var firstLine = File.ReadLines(originNote).FirstOrDefault()?.Trim();
                    if (!string.IsNullOrWhiteSpace(firstLine)
                        && firstLine.EndsWith(".d2s", StringComparison.OrdinalIgnoreCase)
                        && !firstLine.Contains('/')
                        && !firstLine.Contains('\\'))
                    {
                        return (Path.GetFileNameWithoutExtension(firstLine), null);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }

        return (rawStem, null);
    }

    private static string? FindPairedBindSidecar(
        string d2sPath,
        string rawStem,
        string originalStem,
        int d2sLength,
        IReadOnlyDictionary<string, string> rootBindFiles)
    {
        var candidates = new List<string>();
        var directory = Path.GetDirectoryName(d2sPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            candidates.Add(Path.Combine(directory, rawStem + ".bind"));
        }

        if (rootBindFiles.TryGetValue(originalStem, out var rootBind))
        {
            candidates.Add(rootBind);
        }

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (IsValidPairedBindFile(candidate, d2sLength))
            {
                return StripExtendedPrefix(candidate);
            }
        }

        return null;
    }

    internal static bool IsValidPairedBindFile(string bindPath, int d2sByteLength)
    {
        var extendedBindPath = ToExtendedSafe(bindPath);
        if (!File.Exists(extendedBindPath) || (File.GetAttributes(extendedBindPath) & FileAttributes.ReparsePoint) != 0)
        {
            return false;
        }

        var info = new FileInfo(extendedBindPath);
        if (info.Length < SummonStateHeaderBytes || info.Length > MaximumInspectFileBytes)
        {
            return false;
        }

        try
        {
            var bytes = File.ReadAllBytes(extendedBindPath);
            if (bytes.Length < SummonStateHeaderBytes)
            {
                return false;
            }

            var magic = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0, 4));
            if (magic != SummonStateMagic)
            {
                return false;
            }

            var headerBytes = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(SummonStateHeaderSizeOffset, 2));
            var recordCount = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(SummonStateRecordCountOffset, 4));
            var payloadBytes = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(SummonStatePayloadSizeOffset, 4));
            var pairedD2sBytes = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(SummonStatePairedSizeOffset, 8));

            return headerBytes >= SummonStateHeaderBytes
                   && recordCount <= SummonStateMaximumRecords
                   && (ulong)headerBytes + payloadBytes == (ulong)bytes.Length
                   && pairedD2sBytes == (ulong)d2sByteLength;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static LadderCharacterCandidate? BuildCharacterCandidate(
        string characterName,
        IReadOnlyList<ScannedD2sCopy> copies,
        TargetInventory targetInventory,
        IReadOnlySet<string> reservedSourceNames)
    {
        var activeCopy = copies
            .Where(copy => copy.ValidSnapshot?.Origin == LadderSaveCopyOrigin.ActiveSave)
            .Select(copy => copy.ValidSnapshot!)
            .OrderByDescending(snapshot => snapshot.TimestampUtc)
            .FirstOrDefault();

        var orderedValid = copies
            .Where(copy => copy.ValidSnapshot is not null)
            .Select(copy => copy.ValidSnapshot!)
            .OrderBy(snapshot => OriginSortRank(snapshot, activeCopy))
            .ThenByDescending(snapshot => snapshot.TimestampUtc)
            .ToList();

        var deduplicated = new List<LadderCharacterCopySnapshot>();
        var seenHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var snapshot in orderedValid)
        {
            if (seenHashes.Add(snapshot.SourceSha256))
            {
                deduplicated.Add(snapshot);
            }
        }

        if (deduplicated.Count == 0)
        {
            var isOnlyCreationStub = copies.All(copy => copy.IsCreationStub);
            var blockedReason = isOnlyCreationStub
                ? "Unplayed level 1 creation stub — no gameplay save exists yet."
                : copies.Select(copy => copy.RejectedReason).FirstOrDefault(reason => !string.IsNullOrWhiteSpace(reason))
                  ?? "No valid character save copy was found.";

            if (!IsSafeFileStem(characterName))
            {
                return null;
            }

            return new LadderCharacterCandidate(
                characterName,
                characterName,
                false,
                blockedReason,
                [],
                FindExistingConflict(characterName, targetInventory),
                null);
        }

        var primary = deduplicated[0];
        var existingConflict = FindExistingConflict(primary.CharacterName, targetInventory);
        var suggestedRename = existingConflict is not null && primary.SaveVersion >= 104
            ? SuggestSafeRename(primary.CharacterName, targetInventory.ReservedNames, reservedSourceNames)
            : null;

        return new LadderCharacterCandidate(
            primary.CharacterName,
            primary.CharacterName,
            true,
            null,
            deduplicated,
            existingConflict,
            suggestedRename);
    }

    private static int OriginSortRank(LadderCharacterCopySnapshot snapshot, LadderCharacterCopySnapshot? activeCopy)
    {
        if (activeCopy is not null)
        {
            if (snapshot.Origin == LadderSaveCopyOrigin.PendingUpload && snapshot.TimestampUtc > activeCopy.TimestampUtc)
            {
                return 0;
            }

            return snapshot.Origin switch
            {
                LadderSaveCopyOrigin.ActiveSave => 1,
                LadderSaveCopyOrigin.LocalBackup => 2,
                LadderSaveCopyOrigin.PendingUpload => 3,
                _ => 4
            };
        }

        return snapshot.Origin switch
        {
            LadderSaveCopyOrigin.PendingUpload => 0,
            LadderSaveCopyOrigin.LocalBackup => 1,
            LadderSaveCopyOrigin.ActiveSave => 2,
            _ => 3
        };
    }

    private static ExistingNonLadderCharacterInfo? FindExistingConflict(
        string characterName,
        TargetInventory targetInventory)
    {
        var expectedFileName = characterName + ".d2s";
        var matches = targetInventory.Characters
            .Where(info =>
                string.Equals(info.FileName, expectedFileName, StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrWhiteSpace(info.EmbeddedName)
                    && string.Equals(info.EmbeddedName, characterName, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        if (matches.Length == 0)
        {
            return null;
        }

        var filenameMatch = matches.FirstOrDefault(info =>
            string.Equals(info.FileName, expectedFileName, StringComparison.OrdinalIgnoreCase));
        var representative = filenameMatch ?? matches[0];
        var canOverwriteSingleExactFile = matches.Length == 1 && filenameMatch is not null;
        return representative with { IsFilenameMatch = canOverwriteSingleExactFile };
    }

    private static string? SuggestSafeRename(
        string characterName,
        IReadOnlySet<string> reservedTargetNames,
        IReadOnlySet<string> reservedSourceNames)
    {
        var candidates = new List<string>();
        foreach (var suffix in new[] { "-L", "_L", "Lad", "-S", "_S" })
        {
            var maxStemLength = Math.Max(1, 15 - suffix.Length);
            var trimmed = characterName.Length > maxStemLength ? characterName[..maxStemLength] : characterName;
            trimmed = trimmed.TrimEnd('-', '_');
            if (trimmed.Contains('-') || trimmed.Contains('_'))
            {
                var plain = new string(trimmed.Where(char.IsLetter).ToArray());
                if (plain.Length >= 1)
                {
                    candidates.Add(plain + suffix);
                }
            }
            else if (trimmed.Length >= 1)
            {
                candidates.Add(trimmed + suffix);
            }
        }

        foreach (var letter in "ABCDEFGHIJKLM")
        {
            var prefix = characterName.Length >= 15 ? characterName[..14] : characterName;
            candidates.Add(prefix + letter);
        }

        foreach (var candidate in candidates.Distinct(StringComparer.Ordinal))
        {
            if (SaveConversionService.ValidateCharacterName(candidate) is null
                && !ReservedWindowsNames.Contains(candidate)
                && !reservedTargetNames.Contains(candidate)
                && !reservedSourceNames.Contains(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static IReadOnlyList<LadderSharedStashOption> DiscoverSharedStashes(
        string folderPath,
        string backupsDir,
        TargetInventory targetInventory)
    {
        var results = new List<LadderSharedStashOption>();
        if (TryPickSharedStash(folderPath, backupsDir, SoftcoreStashNames, isHardcore: false, targetInventory.HasAnySoftcoreStash, out var softcore))
        {
            results.Add(softcore);
        }

        if (TryPickSharedStash(folderPath, backupsDir, HardcoreStashNames, isHardcore: true, targetInventory.HasAnyHardcoreStash, out var hardcore))
        {
            results.Add(hardcore);
        }

        return results;
    }

    private static bool TryPickSharedStash(
        string folderPath,
        string backupsDir,
        IReadOnlyList<string> preferredNames,
        bool isHardcore,
        bool existsInDestination,
        out LadderSharedStashOption option)
    {
        option = null!;
        foreach (var name in preferredNames)
        {
            var rootPath = Path.Combine(folderPath, name);
            if (TryInspectSharedStashFile(rootPath, name, isHardcore, existsInDestination, "Active stash", out option))
            {
                return true;
            }
        }

        if (!Directory.Exists(backupsDir) || (File.GetAttributes(backupsDir) & FileAttributes.ReparsePoint) != 0)
        {
            return false;
        }

        var extendedBackupsDir = LadderSaveDirectoryService.ToExtendedPath(backupsDir);
        var backupCandidates = new List<(string FileName, string Path, DateTimeOffset TimestampUtc)>();
        foreach (var file in Directory.EnumerateFiles(extendedBackupsDir, "*.d2i"))
        {
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
            {
                continue;
            }

            var rawStem = Path.GetFileNameWithoutExtension(file);
            var (originalStem, timestampUtc) = ResolveStemAndTimestamp(file, rawStem, LadderSaveCopyOrigin.LocalBackup);
            var reconstructedName = originalStem + ".d2i";
            var matchedName = preferredNames.FirstOrDefault(known => known.Equals(reconstructedName, StringComparison.OrdinalIgnoreCase));
            if (matchedName is null)
            {
                continue;
            }

            var effectiveTime = timestampUtc ?? new DateTimeOffset(File.GetLastWriteTimeUtc(file), TimeSpan.Zero);
            backupCandidates.Add((matchedName, StripExtendedPrefix(file), effectiveTime));
        }

        foreach (var candidate in backupCandidates.OrderByDescending(item => item.TimestampUtc))
        {
            var originLabel = $"Local stash backup ({candidate.TimestampUtc.ToLocalTime():yyyy-MM-dd HH:mm})";
            if (TryInspectSharedStashFile(candidate.Path, candidate.FileName, isHardcore, existsInDestination, originLabel, out option))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryInspectSharedStashFile(
        string filePath,
        string canonicalFileName,
        bool isHardcore,
        bool existsInDestination,
        string originLabel,
        out LadderSharedStashOption option)
    {
        option = null!;
        var extendedPath = ToExtendedSafe(filePath);
        if (!File.Exists(extendedPath) || (File.GetAttributes(extendedPath) & FileAttributes.ReparsePoint) != 0)
        {
            return false;
        }

        var info = new FileInfo(extendedPath);
        if (info.Length < SharedStashTabHeaderBytes || info.Length > MaximumSharedStashBytes)
        {
            return false;
        }

        try
        {
            var bytes = File.ReadAllBytes(extendedPath);
            if (!ValidateSharedStashBytes(bytes, out var tabCount))
            {
                return false;
            }

            option = new LadderSharedStashOption(
                canonicalFileName,
                StripExtendedPrefix(filePath),
                Convert.ToHexStringLower(SHA256.HashData(bytes)),
                isHardcore,
                tabCount,
                bytes.Length,
                existsInDestination,
                originLabel);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static bool ValidateSharedStashBytes(ReadOnlySpan<byte> bytes, out int tabCount)
    {
        tabCount = 0;
        if (bytes.Length < SharedStashTabHeaderBytes)
        {
            return false;
        }

        var offset = 0;
        while (offset < bytes.Length)
        {
            if (bytes.Length - offset < SharedStashTabHeaderBytes)
            {
                return false;
            }

            var magic = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset, 4));
            if (magic != SaveMagic)
            {
                return false;
            }

            var tabSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset + SharedStashTabSizeOffset, 4));
            if (tabSize < SharedStashTabHeaderBytes || tabSize > (uint)(bytes.Length - offset))
            {
                return false;
            }

            offset += (int)tabSize;
            tabCount++;
        }

        return tabCount > 0;
    }

    private static void ValidateCharacterSelection(
        LadderCharacterMigrationSelection selection,
        string sourceRoot,
        string targetRoot,
        TargetInventory targetInventory,
        HashSet<string> chosenTargetNames)
    {
        if (string.IsNullOrWhiteSpace(selection.TargetCharacterName) || !IsSafeFileStem(selection.TargetCharacterName))
        {
            throw new InvalidOperationException(
                $"Invalid destination character name '{selection.TargetCharacterName}'.");
        }

        if (!chosenTargetNames.Add(selection.TargetCharacterName))
        {
            throw new InvalidOperationException(
                $"Two selected characters target the same Non-Ladder name '{selection.TargetCharacterName}'.");
        }

        var fullSource = Path.GetFullPath(StripExtendedPrefix(selection.SourcePath));
        var extendedSource = ToExtendedSafe(fullSource);
        if (!IsSameOrNested(fullSource, sourceRoot) || !File.Exists(extendedSource))
        {
            throw new FileNotFoundException($"Selected Ladder save '{selection.SourcePath}' was not found.", fullSource);
        }

        LocalPluginInstallService.EnsureNoLinks(extendedSource);
        var sourceBytes = File.ReadAllBytes(extendedSource);
        var actualSourceSha = Convert.ToHexStringLower(SHA256.HashData(sourceBytes));
        if (!string.Equals(actualSourceSha, selection.ExpectedSourceSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"'{Path.GetFileName(fullSource)}' changed on disk after it was inspected. Refresh the dialog before migrating.");
        }

        var d2sError = ValidateD2sBytes(sourceBytes, out var version, out _);
        if (d2sError is not null)
        {
            throw new InvalidOperationException(
                $"Cannot migrate '{selection.SourceCharacterName}': {d2sError}");
        }

        var isRename = !string.Equals(selection.TargetCharacterName, selection.SourceCharacterName, StringComparison.Ordinal);
        if (isRename)
        {
            if (string.Equals(selection.TargetCharacterName, selection.SourceCharacterName, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Renaming '{selection.SourceCharacterName}' by letter case alone is not supported.");
            }

            if (version < 104)
            {
                throw new InvalidOperationException(
                    $"Character '{selection.SourceCharacterName}' uses save format v{version}, which does not support renaming on migration.");
            }

            if (SaveConversionService.ValidateCharacterName(selection.TargetCharacterName) is { } nameProblem)
            {
                throw new InvalidOperationException(
                    $"Destination name '{selection.TargetCharacterName}' is invalid: {nameProblem}");
            }
        }

        var matchingConflicts = targetInventory.Characters
            .Where(existing =>
                string.Equals(existing.FileName, selection.TargetCharacterName + ".d2s", StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrWhiteSpace(existing.EmbeddedName)
                    && string.Equals(existing.EmbeddedName, selection.TargetCharacterName, StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        if (matchingConflicts.Length > 0)
        {
            if (isRename || !selection.AllowOverwriteExisting)
            {
                throw new InvalidOperationException(
                    $"Non-Ladder character '{selection.TargetCharacterName}' already exists. Pick a new name or confirm overwriting it.");
            }

            var expectedDestinationD2s = Path.Combine(targetRoot, selection.TargetCharacterName + ".d2s");
            if (matchingConflicts.Length != 1
                || !string.Equals(Path.GetFullPath(matchingConflicts[0].FilePath), Path.GetFullPath(expectedDestinationD2s), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"A differently named file in Non-Ladder uses character name '{selection.TargetCharacterName}'. Rename the Ladder character instead of overwriting.");
            }

            if (string.IsNullOrWhiteSpace(matchingConflicts[0].Sha256)
                || string.IsNullOrWhiteSpace(selection.ExpectedExistingTargetSha256)
                || !string.Equals(matchingConflicts[0].Sha256, selection.ExpectedExistingTargetSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Existing Non-Ladder save '{matchingConflicts[0].FileName}' changed after inspection or could not be verified. Reopen the dialog and confirm before overwriting.");
            }
        }

        if (!string.IsNullOrWhiteSpace(selection.PairedBindPath))
        {
            var fullBind = Path.GetFullPath(StripExtendedPrefix(selection.PairedBindPath));
            var extendedBind = ToExtendedSafe(fullBind);
            if (!IsSameOrNested(fullBind, sourceRoot) || !IsValidPairedBindFile(fullBind, sourceBytes.Length))
            {
                throw new InvalidOperationException(
                    $"Companion summon file for '{selection.SourceCharacterName}' failed pairing verification.");
            }

            LocalPluginInstallService.EnsureNoLinks(extendedBind);
        }

        var seenCompanionExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var companion in selection.CompanionPaths)
        {
            var extension = Path.GetExtension(companion);
            if (!CarriedCompanionExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Companion file '{Path.GetFileName(companion)}' has unsupported extension '{extension}'.");
            }

            if (!seenCompanionExtensions.Add(extension))
            {
                throw new InvalidOperationException(
                    $"Duplicate companion extension '{extension}' selected for '{selection.SourceCharacterName}'.");
            }

            var fullCompanion = Path.GetFullPath(StripExtendedPrefix(companion));
            var extendedCompanion = ToExtendedSafe(fullCompanion);
            if (!IsSameOrNested(fullCompanion, sourceRoot) || !File.Exists(extendedCompanion))
            {
                throw new FileNotFoundException($"Companion file '{companion}' is missing.", fullCompanion);
            }

            LocalPluginInstallService.EnsureNoLinks(extendedCompanion);
        }
    }

    private static void ValidateSharedStashSelection(
        LadderSharedStashSelection stash,
        string sourceRoot,
        string targetRoot,
        TargetInventory targetInventory,
        HashSet<string> chosenStashFiles)
    {
        if (string.IsNullOrWhiteSpace(stash.FileName)
            || !SoftcoreStashNames.Concat(HardcoreStashNames).Contains(stash.FileName, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Unsupported shared stash file '{stash.FileName}'.");
        }

        if (!chosenStashFiles.Add(stash.FileName))
        {
            throw new InvalidOperationException($"Duplicate shared stash selection '{stash.FileName}'.");
        }

        var isHardcore = stash.FileName.Contains("HardCore", StringComparison.OrdinalIgnoreCase);
        var extendedTargetRoot = ToExtendedSafe(targetRoot);
        var targetHasMatchingStashName = Directory.Exists(extendedTargetRoot)
            && Directory.EnumerateFiles(extendedTargetRoot)
                .Any(path => Path.GetFileName(path).Equals(stash.FileName, StringComparison.OrdinalIgnoreCase));
        if ((isHardcore && targetInventory.HasAnyHardcoreStash)
            || (!isHardcore && targetInventory.HasAnySoftcoreStash)
            || targetHasMatchingStashName)
        {
            throw new InvalidOperationException(
                $"Existing Non-Ladder {(isHardcore ? "Hardcore" : "Softcore")} shared stash cannot be overwritten.");
        }

        var fullSource = Path.GetFullPath(StripExtendedPrefix(stash.SourcePath));
        var extendedSource = ToExtendedSafe(fullSource);
        if (!IsSameOrNested(fullSource, sourceRoot) || !File.Exists(extendedSource))
        {
            throw new FileNotFoundException($"Selected shared stash '{stash.SourcePath}' was not found.", fullSource);
        }

        LocalPluginInstallService.EnsureNoLinks(extendedSource);
        var bytes = File.ReadAllBytes(extendedSource);
        var actualSha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (!string.Equals(actualSha, stash.ExpectedSourceSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Shared stash '{stash.FileName}' changed on disk after it was inspected.");
        }

        if (!ValidateSharedStashBytes(bytes, out _))
        {
            throw new InvalidOperationException($"Shared stash '{stash.FileName}' failed framing verification.");
        }
    }

    private static byte[] PrepareCharacterOutputBytes(LadderCharacterMigrationSelection selection)
    {
        var bytes = File.ReadAllBytes(ToExtendedSafe(selection.SourcePath));
        var error = ValidateD2sBytes(bytes, out var version, out _);
        if (error is not null)
        {
            throw new InvalidOperationException(error);
        }

        if (string.Equals(selection.TargetCharacterName, selection.SourceCharacterName, StringComparison.Ordinal))
        {
            return bytes;
        }

        var before = bytes.ToArray();
        if (version < 104)
        {
            throw new InvalidOperationException($"Cannot rename v{version} save.");
        }

        D2SaveLayoutV104.From(bytes).Name = selection.TargetCharacterName;
        D2SaveLayoutV104.UpdateChecksum(bytes);

        for (var index = 0; index < bytes.Length; index++)
        {
            var allowed = index is >= ChecksumOffset and < ChecksumOffset + 4
                or >= V104NameOffset and < V104NameOffset + V104NameLength;
            if (before[index] != bytes[index] && !allowed)
            {
                throw new InvalidOperationException(
                    $"Renaming '{selection.SourceCharacterName}' altered unexpected byte {index}.");
            }
        }

        if (!ChecksumCalculator.Verify(bytes)
            || !string.Equals(D2SaveLayoutV104.From(bytes).Name, selection.TargetCharacterName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Renamed save checksum or header name verification failed for '{selection.TargetCharacterName}'.");
        }

        return bytes;
    }

    private static (string BatchDir, string SnapshotDir) CreateVerifiedTargetBackup(
        string targetRoot,
        string backupRoot,
        LadderCharacterMigrationRequest request,
        CancellationToken cancellationToken)
    {
        var extendedTargetRoot = ToExtendedSafe(targetRoot);
        var extendedBackupRoot = ToExtendedSafe(backupRoot);
        Directory.CreateDirectory(extendedBackupRoot);
        var folderName = Path.GetFileName(Path.TrimEndingDirectorySeparator(targetRoot));
        var sequence = Interlocked.Increment(ref _backupSequence) & 0xFFFFF;
        var token = $"{DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff", CultureInfo.InvariantCulture)}-{sequence:x5}";
        var batchDir = Path.Combine(backupRoot, $"{token}-{folderName}");
        while (Directory.Exists(ToExtendedSafe(batchDir)))
        {
            sequence = Interlocked.Increment(ref _backupSequence) & 0xFFFFF;
            token = $"{DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff", CultureInfo.InvariantCulture)}-{sequence:x5}";
            batchDir = Path.Combine(backupRoot, $"{token}-{folderName}");
        }

        var snapshotDir = Path.Combine(batchDir, folderName);
        Directory.CreateDirectory(ToExtendedSafe(snapshotDir));

        try
        {
            LocalPluginInstallService.EnsureNoLinks(extendedTargetRoot);
            var manifest = new StringBuilder()
                .AppendLine("Reimagined Launcher Ladder -> Non-Ladder character migration backup.")
                .AppendLine($"Created (UTC): {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss 'UTC'}")
                .AppendLine($"Source Ladder saves: {request.SourceFolderPath}")
                .AppendLine($"Destination Non-Ladder saves: {targetRoot}")
                .AppendLine("Backed up files:");

            foreach (var sourceFile in Directory.EnumerateFiles(extendedTargetRoot, "*", SearchOption.AllDirectories)
                         .OrderBy(path => path, PathComparer))
            {
                cancellationToken.ThrowIfCancellationRequested();
                LocalPluginInstallService.EnsureNoLinks(sourceFile);

                var plainSource = StripExtendedPrefix(sourceFile);
                var relative = Path.GetRelativePath(targetRoot, plainSource);
                var destinationFile = Path.Combine(snapshotDir, relative);
                Directory.CreateDirectory(ToExtendedSafe(Path.GetDirectoryName(destinationFile)!));

                var sourceBytes = File.ReadAllBytes(sourceFile);
                WriteVerifiedNewFile(destinationFile, sourceBytes);
                manifest.AppendLine($"  {relative} ({sourceBytes.Length} bytes, sha256:{Convert.ToHexStringLower(SHA256.HashData(sourceBytes))})");
            }

            manifest
                .AppendLine()
                .AppendLine("To restore manually:")
                .AppendLine("1. Close D2R and D2RLoader.")
                .AppendLine($"2. Replace the files in '{targetRoot}' with the files inside '{snapshotDir}'.");

            WriteVerifiedNewFile(
                Path.Combine(batchDir, "restore-instructions.txt"),
                Utf8NoBom.GetBytes(manifest.ToString()));

            return (batchDir, snapshotDir);
        }
        catch
        {
            TryDeleteDirectory(batchDir);
            throw;
        }
    }

    private static string? TryCreateLauncherZipBackup(string targetRoot, string launcherBackupDirectory)
    {
        try
        {
            Directory.CreateDirectory(launcherBackupDirectory);
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            var zipPath = Path.Combine(launcherBackupDirectory, $"{stamp}-ladder-migration-backup.zip");
            for (var attempt = 2; File.Exists(zipPath); attempt++)
            {
                zipPath = Path.Combine(launcherBackupDirectory, $"{stamp}-{attempt}-ladder-migration-backup.zip");
            }

            var tempZip = zipPath + $".{Guid.NewGuid():N}.tmp";
            try
            {
                using (var stream = new FileStream(tempZip, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
                {
                    foreach (var filePath in Directory.EnumerateFiles(targetRoot, "*", SearchOption.AllDirectories))
                    {
                        if ((File.GetAttributes(filePath) & FileAttributes.ReparsePoint) != 0)
                        {
                            continue;
                        }

                        var relative = Path.GetRelativePath(targetRoot, filePath).Replace(Path.DirectorySeparatorChar, '/');
                        var entry = archive.CreateEntry(relative, CompressionLevel.Optimal);
                        using var input = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        using var entryStream = entry.Open();
                        input.CopyTo(entryStream);
                    }
                }

                File.Move(tempZip, zipPath, overwrite: false);
                return zipPath;
            }
            finally
            {
                if (File.Exists(tempZip))
                {
                    File.Delete(tempZip);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LaunchDiagnostics.LogException("Secondary launcher zip backup before character migration failed", ex);
            return null;
        }
    }

    private static void WriteFileAtomically(
        string destinationPath,
        byte[] content,
        bool overwrite,
        List<string> createdNewFiles,
        HashSet<string> modifiedOrDeletedRelativePaths,
        string targetRoot)
    {
        var plainDestination = StripExtendedPrefix(destinationPath);
        var extendedDestination = ToExtendedSafe(plainDestination);
        var existedBefore = File.Exists(extendedDestination);
        if (existedBefore && !overwrite)
        {
            throw new IOException($"Destination file '{plainDestination}' already exists.");
        }

        var relativePath = Path.GetRelativePath(targetRoot, plainDestination);
        var tempPath = $"{plainDestination}.{Guid.NewGuid():N}.migrating";
        var extendedTemp = ToExtendedSafe(tempPath);
        try
        {
            WriteVerifiedNewFile(tempPath, content);
            if (existedBefore && overwrite)
            {
                LocalPluginInstallService.EnsureNoLinks(extendedDestination);
                ClearReadOnlyAttribute(extendedDestination);
            }

            File.Move(extendedTemp, extendedDestination, overwrite);
            if (existedBefore)
            {
                modifiedOrDeletedRelativePaths.Add(relativePath);
            }
            else
            {
                createdNewFiles.Add(plainDestination);
            }
        }
        finally
        {
            if (File.Exists(extendedTemp))
            {
                File.Delete(extendedTemp);
            }
        }
    }

    private static void CopyVerifiedCompanions(
        LadderCharacterMigrationSelection selection,
        int d2sOutputLength,
        string targetRoot,
        List<string> createdNewFiles,
        HashSet<string> modifiedOrDeletedRelativePaths,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(selection.PairedBindPath)
            && IsValidPairedBindFile(selection.PairedBindPath, d2sOutputLength))
        {
            var bindBytes = File.ReadAllBytes(ToExtendedSafe(selection.PairedBindPath));
            var bindDestination = Path.Combine(targetRoot, selection.TargetCharacterName + ".bind");
            WriteFileAtomically(
                bindDestination,
                bindBytes,
                overwrite: true,
                createdNewFiles,
                modifiedOrDeletedRelativePaths,
                targetRoot);
        }

        foreach (var companionPath in selection.CompanionPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var extendedCompanion = ToExtendedSafe(companionPath);
            if (!File.Exists(extendedCompanion))
            {
                continue;
            }

            var extension = Path.GetExtension(companionPath);
            var companionDestination = Path.Combine(targetRoot, selection.TargetCharacterName + extension);
            var bytes = File.ReadAllBytes(extendedCompanion);
            WriteFileAtomically(
                companionDestination,
                bytes,
                overwrite: true,
                createdNewFiles,
                modifiedOrDeletedRelativePaths,
                targetRoot);
        }
    }

    private static void RemoveStaleDestinationSidecars(
        string targetRoot,
        string characterStem,
        HashSet<string> modifiedOrDeletedRelativePaths)
    {
        var extendedTargetRoot = ToExtendedSafe(targetRoot);
        if (!Directory.Exists(extendedTargetRoot))
        {
            return;
        }

        foreach (var extendedFile in Directory.EnumerateFiles(extendedTargetRoot))
        {
            var plainFile = StripExtendedPrefix(extendedFile);
            var fileName = Path.GetFileName(plainFile);
            var stem = Path.GetFileNameWithoutExtension(fileName);
            var extension = Path.GetExtension(fileName);
            if (!stem.Equals(characterStem, StringComparison.OrdinalIgnoreCase)
                || !OverwriteRemovedSidecarExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            LocalPluginInstallService.EnsureNoLinks(extendedFile);
            ClearReadOnlyAttribute(extendedFile);
            File.Delete(extendedFile);
            modifiedOrDeletedRelativePaths.Add(Path.GetRelativePath(targetRoot, plainFile));
        }
    }

    private static IReadOnlyList<string> RollbackTargetChanges(
        string targetRoot,
        string? backupSnapshotDir,
        List<string> createdNewFiles,
        HashSet<string> modifiedOrDeletedRelativePaths)
    {
        var errors = new List<string>();
        foreach (var createdPath in createdNewFiles)
        {
            try
            {
                var extendedCreated = ToExtendedSafe(createdPath);
                if (File.Exists(extendedCreated))
                {
                    ClearReadOnlyAttribute(extendedCreated);
                    File.Delete(extendedCreated);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LaunchDiagnostics.LogException($"Rollback could not remove '{createdPath}'", ex);
                errors.Add($"could not delete '{Path.GetFileName(createdPath)}': {ex.Message}");
            }
        }

        if (modifiedOrDeletedRelativePaths.Count == 0)
        {
            return errors;
        }

        if (string.IsNullOrWhiteSpace(backupSnapshotDir) || !Directory.Exists(ToExtendedSafe(backupSnapshotDir)))
        {
            errors.Add("verified backup snapshot folder was not available for restore.");
            return errors;
        }

        foreach (var relativePath in modifiedOrDeletedRelativePaths)
        {
            try
            {
                var extendedBackupFile = ToExtendedSafe(Path.Combine(backupSnapshotDir, relativePath));
                if (!File.Exists(extendedBackupFile))
                {
                    continue;
                }

                var plainDestination = Path.Combine(targetRoot, relativePath);
                var extendedDestination = ToExtendedSafe(plainDestination);
                Directory.CreateDirectory(ToExtendedSafe(Path.GetDirectoryName(plainDestination)!));
                var bytes = File.ReadAllBytes(extendedBackupFile);
                var temp = $"{plainDestination}.{Guid.NewGuid():N}.rollback";
                var extendedTemp = ToExtendedSafe(temp);
                try
                {
                    WriteVerifiedNewFile(temp, bytes);
                    ClearReadOnlyAttribute(extendedDestination);
                    File.Move(extendedTemp, extendedDestination, overwrite: true);
                }
                finally
                {
                    if (File.Exists(extendedTemp))
                    {
                        File.Delete(extendedTemp);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LaunchDiagnostics.LogException($"Rollback could not restore '{relativePath}'", ex);
                errors.Add($"could not restore '{relativePath}': {ex.Message}");
            }
        }

        return errors;
    }

    private static void PruneOldMigrationBackups(string backupRoot, int retainCount, string? activeBatchDir = null)
    {
        var extendedBackupRoot = ToExtendedSafe(backupRoot);
        if (!Directory.Exists(extendedBackupRoot))
        {
            return;
        }

        var normalizedActive = string.IsNullOrWhiteSpace(activeBatchDir)
            ? null
            : Path.TrimEndingDirectorySeparator(Path.GetFullPath(StripExtendedPrefix(activeBatchDir)));
        var keepActive = normalizedActive is not null
                         && File.Exists(ToExtendedSafe(Path.Combine(normalizedActive, "restore-instructions.txt")));
        var olderRetainCount = keepActive ? Math.Max(0, retainCount - 1) : retainCount;

        var completed = Directory.EnumerateDirectories(extendedBackupRoot)
            .Select(StripExtendedPrefix)
            .Where(directory =>
                File.Exists(ToExtendedSafe(Path.Combine(directory, "restore-instructions.txt")))
                && (normalizedActive is null
                    || !string.Equals(
                        Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)),
                        normalizedActive,
                        PathComparison)))
            .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
            .Skip(olderRetainCount)
            .ToArray();

        foreach (var obsolete in completed)
        {
            TryDeleteDirectory(obsolete);
        }
    }

    private static void WriteVerifiedNewFile(string path, byte[] bytes)
    {
        var extendedPath = ToExtendedSafe(path);
        using (var stream = new FileStream(extendedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }

        if (!File.ReadAllBytes(extendedPath).AsSpan().SequenceEqual(bytes))
        {
            throw new IOException($"Written file '{Path.GetFileName(path)}' failed read-back verification.");
        }
    }

    private static string? ValidateD2sBytes(ReadOnlySpan<byte> bytes, out uint version, out bool isCreationStub)
    {
        version = 0;
        isCreationStub = false;

        if (bytes.Length < MinimumCharacterBytes)
        {
            return "file is smaller than the minimum character save header.";
        }

        var magic = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(0, 4));
        if (magic != SaveMagic)
        {
            return "missing Diablo II save magic.";
        }

        version = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(4, 4));
        var declared = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(8, 4));
        if (declared != (uint)bytes.Length)
        {
            return $"declared byte length ({declared}) does not match file length ({bytes.Length}).";
        }

        if ((version is 104 or 105) && bytes.Length == CreationHeaderBytes)
        {
            isCreationStub = true;
            return "unplayed character creation stub (403 bytes) with no gameplay save.";
        }

        if (bytes.Length > SaveMigrator.RetailCharacterSizeLimit)
        {
            return $"file length ({bytes.Length}) exceeds the {SaveMigrator.RetailCharacterSizeLimit}-byte retail limit.";
        }

        var bytesArray = bytes.ToArray();
        var minimumHeaderBytes = version >= 104 ? MinimumV104HeaderBytes : MinimumLegacyHeaderBytes;
        if (bytesArray.Length < minimumHeaderBytes)
        {
            return "character save header is smaller than expected for this version.";
        }

        try
        {
            _ = version >= 104
                ? D2SaveLayoutV104.From(bytesArray).Name
                : D2SaveLayout.From(bytesArray).Name;
        }
        catch (Exception)
        {
            return "character save header is truncated or malformed.";
        }

        if (!ChecksumCalculator.Verify(bytesArray))
        {
            return "header checksum verification failed (file may be incomplete or corrupted).";
        }

        return null;
    }

    private static bool TryReadHeaderSummary(
        byte[] bytes,
        out string? embeddedName,
        out uint version,
        out int level,
        out string className,
        out bool isHardcore,
        out bool hasDied)
    {
        embeddedName = null;
        version = 0;
        level = 1;
        className = "Unknown";
        isHardcore = false;
        hasDied = false;

        if (bytes.Length < MinimumCharacterBytes
            || BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0, 4)) != SaveMagic)
        {
            return false;
        }

        version = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4, 4));
        var statusOffset = version >= 104 ? 0x14 : 0x24;
        var classOffset = version >= 104 ? 0x18 : 0x28;
        var levelOffset = version >= 104 ? 0x1B : 0x2B;
        if (bytes.Length <= levelOffset)
        {
            return false;
        }

        var statusFlags = bytes[statusOffset];
        isHardcore = (statusFlags & 0x04) != 0;
        hasDied = (statusFlags & 0x08) != 0;
        className = ResolveClassName(bytes[classOffset]);
        level = Math.Max(1, (int)bytes[levelOffset]);

        try
        {
            embeddedName = version >= 104
                ? D2SaveLayoutV104.From(bytes).Name?.TrimEnd('\0')
                : D2SaveLayout.From(bytes).Name?.TrimEnd('\0');
        }
        catch (Exception)
        {
            embeddedName = null;
        }

        return true;
    }

    private static string ResolveClassName(byte classId) => classId switch
    {
        0 => "Amazon",
        1 => "Sorceress",
        2 => "Necromancer",
        3 => "Paladin",
        4 => "Barbarian",
        5 => "Druid",
        6 => "Assassin",
        7 => "Warlock",
        _ => $"Class {classId}"
    };

    private static string? ResolveCanonicalCharacterName(string? embeddedName, string originalStem)
    {
        if (!string.IsNullOrWhiteSpace(embeddedName) && IsSafeFileStem(embeddedName))
        {
            return embeddedName;
        }

        return IsSafeFileStem(originalStem) ? originalStem : null;
    }

    internal static bool IsReservedWindowsName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && ReservedWindowsNames.Contains(name.Trim());

    private static bool IsSafeFileStem(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)
            || !string.Equals(name, name.Trim(), StringComparison.Ordinal)
            || name.Length > 32
            || name is "." or ".."
            || ReservedWindowsNames.Contains(name))
        {
            return false;
        }

        var invalid = Path.GetInvalidFileNameChars();
        return name.All(ch => ch >= 32
                              && ch is not '/' and not '\\' and not ':' and not '.'
                              && !invalid.Contains(ch));
    }

    private static string NormalizeRelativeSavePath(string? savePath) =>
        (savePath ?? string.Empty).Trim().Trim('/', '\\');

    private static string StripExtendedPrefix(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + path[8..];
        }

        return path.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase)
            ? path[4..]
            : path;
    }

    private static string ToExtendedSafe(string path) =>
        LadderSaveDirectoryService.ToExtendedPath(StripExtendedPrefix(path));

    private static bool IsSameOrNested(string candidatePath, string parentDirectory)
    {
        var normalizedCandidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(StripExtendedPrefix(candidatePath)));
        var normalizedParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(StripExtendedPrefix(parentDirectory)));
        return string.Equals(normalizedCandidate, normalizedParent, PathComparison)
               || normalizedCandidate.StartsWith(normalizedParent + Path.DirectorySeparatorChar, PathComparison);
    }

    private static void ClearReadOnlyAttribute(string filePath)
    {
        var extended = ToExtendedSafe(filePath);
        if (!File.Exists(extended))
        {
            return;
        }

        var attributes = File.GetAttributes(extended);
        if ((attributes & FileAttributes.ReadOnly) != 0)
        {
            File.SetAttributes(extended, attributes & ~FileAttributes.ReadOnly);
        }
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            var extendedDir = ToExtendedSafe(directory);
            if (Directory.Exists(extendedDir))
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(extendedDir, "*", SearchOption.AllDirectories))
                {
                    var attrs = File.GetAttributes(entry);
                    if ((attrs & FileAttributes.ReadOnly) != 0)
                    {
                        File.SetAttributes(entry, attrs & ~FileAttributes.ReadOnly);
                    }
                }

                Directory.Delete(extendedDir, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LaunchDiagnostics.LogException($"Could not clean up backup directory '{directory}'", ex);
        }
    }
}
