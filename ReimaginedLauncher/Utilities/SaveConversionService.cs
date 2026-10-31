using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using D2SSharp;
using D2SSharp.Model;
using ReimaginedLauncher.SaveMigration;

namespace ReimaginedLauncher.Utilities;

public enum SaveConversionState
{
    AlreadyCompatible,
    Verified,
    VerifiedWithWarnings,

    /// <summary>Output that passed the critical structural checks, but only because Force Conversion or a source choice accepted uncertainties.</summary>
    Experimental,

    /// <summary>Recoverable but withheld: some data cannot be verified (Force Conversion may accept it) or the source version is uncertain.</summary>
    Unresolved,

    /// <summary>No usable output can be produced: the file cannot be decoded or serialized, or the output fails a critical check.</summary>
    Blocked
}

public enum SaveIssueCategory
{
    HistoricalModifiers,
    ItemIdentity,
    UnknownReferences,
    SaveFormat,
    Unverified,
    Verification
}

public sealed record SaveIssueGroup(SaveIssueCategory Category, bool Blocking, int Count, int Items)
{
    public string Title => Category switch
    {
        SaveIssueCategory.HistoricalModifiers => "Historical item modifiers",
        SaveIssueCategory.ItemIdentity => "Item identity changes",
        SaveIssueCategory.UnknownReferences => "Unsupported or unknown references",
        SaveIssueCategory.SaveFormat => "Save-format incompatibility",
        SaveIssueCategory.Unverified => "Unverified, changed or lost data",
        _ => "Verification failure"
    };

    public override string ToString() =>
        $"{Title}: {Count} {(Blocking ? "blocking" : Count == 1 ? "warning" : "warnings")}" +
        (Items > 0 ? $" on {Items} item(s)" : "");
}

public enum SaveForceKind
{
    Force,
    ChooseSourceLayout
}

/// <param name="SourceLayouts">Layouts the user may choose from, the default first, or the one chosen for an experimental plan.</param>
/// <param name="Risks">Each issue Force Conversion accepts: unresolved identities, unverifiable stats, repaired headers, reported losses.</param>
/// <param name="TargetAlsoFits">The file also reads completely as the target, so "already converted" is a valid choice too.</param>
public sealed record SaveForceOption(
    SaveForceKind Kind,
    IReadOnlyList<string> SourceLayouts,
    IReadOnlyList<string> Risks,
    bool TargetAlsoFits = false);

public sealed class SaveConversionPlan
{
    public required string SourcePath { get; init; }
    public required SaveFileKind Kind { get; init; }
    public required SaveConversionState State { get; init; }
    public required string Operation { get; init; }
    public required IReadOnlyList<string> Issues { get; init; }
    public required string Report { get; init; }
    public required string SourceSha256 { get; init; }

    public string TargetLabel { get; init; } = string.Empty;
    public string? TargetFingerprint { get; init; }

    /// <summary>The source layout the conversion reads the file as, when one was identified or chosen.</summary>
    public string? SourceLabel { get; init; }

    /// <summary>A viable historical interpretation whose migration differs from the installed-target parse.</summary>
    public string? HistoricalAlternative { get; init; }

    /// <summary>The output was rebuilt by parsing with the installed target definitions.</summary>
    public bool IsTargetRebuild { get; init; }

    /// <summary>One or two plain sentences for players; technical detail stays in <see cref="Report"/>.</summary>
    public string Summary { get; init; } = string.Empty;

    /// <summary>The actual reason a blocked or unresolved file is not converted, separate from nonblocking warnings.</summary>
    public string? BlockingReason { get; init; }

    public IReadOnlyList<SaveIssueGroup> IssueGroups { get; init; } = [];

    /// <summary>For <see cref="SaveConversionState.Unresolved"/>: what Force Conversion may override. For an experimental plan: what it overrode.</summary>
    public SaveForceOption? ForceOption { get; init; }

    /// <summary>Blocked, but a serialized candidate exists that a Force Conversion analysis keeps as a recovery candidate.</summary>
    public bool CanAttemptRecovery { get; init; }

    /// <summary>An unapproved output kept outside the save folder by a Force Conversion analysis; it is never applied.</summary>
    public string? RecoveryCandidatePath { get; init; }

    public bool IsBlocked => State is SaveConversionState.Blocked or SaveConversionState.Unresolved;

    /// <summary>Verified output with the target definitions; null when nothing needs converting or conversion is blocked.</summary>
    public byte[]? ConvertedBytes { get; init; }

    public string? EmbeddedName { get; init; }
    public string? SidecarPath { get; init; }

    public string FileName => Path.GetFileName(SourcePath);
    public string FileStem => Path.GetFileNameWithoutExtension(SourcePath);

    public bool HasNameMismatch => Kind == SaveFileKind.Character
                                   && !IsBlocked
                                   && EmbeddedName is not null
                                   && !string.Equals(EmbeddedName, FileStem, StringComparison.Ordinal);
}

public sealed record SaveConversionOutcome(
    bool Succeeded,
    string Message,
    string? FinalPath = null,
    string? BackupDirectory = null);

/// <summary>
/// Plans and applies offline save conversions with <see cref="SaveMigrator"/>. Planning only reads the save;
/// applying writes a permanent backup first and replaces the active file only after the new bytes are verified.
/// </summary>
public sealed class SaveConversionService
{
    private const int NameMinLength = 2;
    private const int NameMaxLength = 15;
    private const int ChecksumOffset = 12;

    // D2SaveLayoutV104: 16-byte header, preview data at character offset 0x9F, 96-byte UTF-8 name at preview offset 0x7C.
    private const int V104NameOffset = 16 + 0x9F + 0x7C;
    private const int V104NameLength = 96;

    // Per-character files the game keys by the save's file name.
    private static readonly string[] CompanionExtensions =
        [".key", ".keyo", ".ctl", ".ctlo", ".map", ".ma0", ".ma1", ".ma2", ".ma3"];

    private static readonly Regex ItemPathPrefix = new(
        @"^(?:Player|Corpse \d+|Mercenary|Iron golem|Stash tab \d+) #\d+ '[^']*' \([^)]*\)(?: > socket \d+ '[^']*')*: ",
        RegexOptions.Compiled);

    private static readonly Regex FriendlyItemPath = new(
        @"^(?<location>Player|Corpse \d+|Mercenary|Iron golem|Stash tab \d+) #(?<index>\d+) '(?<code>[^']*)' \([^)]*\)(?<socket>(?: > socket \d+ '[^']*')*)",
        RegexOptions.Compiled);

    private static readonly Regex LadderSaveFolderSuffix =
        new("-(?:[0-9a-fA-F]{8}|[0-9a-fA-F]{32})$", RegexOptions.Compiled);

    private readonly Lazy<SaveMigrator> _migrator;
    private readonly Func<bool> _isGameRunning;
    private readonly InstalledGameData? _installedTarget;

    /// <param name="createMigrator">Creates the migration definitions used for analysis.</param>
    /// <param name="backupRoot">Directory for permanent save backups and recovery candidates.</param>
    /// <param name="isGameRunning">Checks whether D2R is running before any save is written.</param>
    /// <param name="defaultSourceLayout">Listed first when the source layout of a save is genuinely ambiguous.</param>
    /// <param name="installedTarget">Installed TXT snapshot whose fingerprint must still match when applying a plan.</param>
    public SaveConversionService(Func<SaveMigrator> createMigrator, string backupRoot, Func<bool> isGameRunning,
        string? defaultSourceLayout = null, InstalledGameData? installedTarget = null)
    {
        _migrator = new Lazy<SaveMigrator>(createMigrator);
        BackupRoot = Path.GetFullPath(backupRoot);
        _isGameRunning = isGameRunning;
        DefaultSourceLayout = defaultSourceLayout;
        _installedTarget = installedTarget;
    }

    public static string DefaultDataDirectory => Path.Combine(AppContext.BaseDirectory, "Assets", "SaveMigrationData");

    public static string DefaultBackupRoot => Path.Combine(SettingsManager.AppDirectoryPath, "SaveMigrationBackups");

    public string BackupRoot { get; }

    public string? DefaultSourceLayout { get; }

    public static SaveConversionService CreateForInstalledTarget(SaveCheckpointRegistry registry,
        InstalledGameData installedTarget,
        string? backupRoot = null, Func<bool>? isGameRunning = null) => new(
        () => SaveMigrator.ForInstalledTarget(registry, installedTarget),
        backupRoot ?? DefaultBackupRoot,
        isGameRunning ?? MainWindow.IsGameRunning,
        registry.Checkpoints
            .Where(checkpoint => !installedTarget.HasSameTables(registry.PathOf(checkpoint.Directory)))
            .MaxBy(checkpoint => checkpoint.Order)?.Label,
        installedTarget);

    /// <summary>The modinfo.json version of the normal (offline) Reimagined mod, using the same folders as the save path.</summary>
    internal static string? ReadInstalledReimaginedVersion(InstallationProfile profile)
    {
        var modInfo = FindNormalModInfo(profile);
        return modInfo is null ? null : ReadModInfoString(modInfo, "version");
    }

    internal static string ResolveInstalledExcelDirectory(InstallationProfile profile)
    {
        var modInfo = FindNormalModInfo(profile)
                      ?? throw new InvalidDataException(
                          "The normal/offline Reimagined installation could not be identified. Install or repair the normal mod before converting saves.");
        var infoDirectory = Path.GetDirectoryName(modInfo)!;
        var mpqDirectory = Path.GetFileName(infoDirectory).Equals("Reimagined.mpq", StringComparison.OrdinalIgnoreCase)
            ? infoDirectory
            : Path.Combine(infoDirectory, "Reimagined.mpq");
        return Path.Combine(mpqDirectory, "data", "global", "excel");
    }

    internal static InstalledGameData LoadInstalledTarget(InstallationProfile profile, string copyDirectory)
    {
        var modInfo = FindNormalModInfo(profile)
                      ?? throw new InvalidDataException(
                          "The normal/offline Reimagined installation could not be identified. Install or repair the normal mod before converting saves.");
        var infoDirectory = Path.GetDirectoryName(modInfo)!;
        var mpqDirectory = Path.GetFileName(infoDirectory).Equals("Reimagined.mpq", StringComparison.OrdinalIgnoreCase)
            ? infoDirectory
            : Path.Combine(infoDirectory, "Reimagined.mpq");
        return InstalledGameData.Load(
            Path.Combine(mpqDirectory, "data", "global", "excel"),
            copyDirectory,
            ReadModInfoString(modInfo, "version"));
    }

    internal static string? FindNormalModInfo(InstallationProfile profile)
    {
        var installDirectory = profile.InstallDirectory;
        if (profile.Type == InstallationType.D2RMM || string.IsNullOrWhiteSpace(installDirectory))
        {
            return null;
        }

        var activeMod = Path.Combine(installDirectory, "mods", "Reimagined");
        var managedNormalMod = NormalModInstallationService.NormalModRoot(installDirectory);
        var candidates = NormalModInstallationService.HasLadderInstallation(installDirectory)
            ? new[] { managedNormalMod, activeMod }
            : new[] { activeMod, managedNormalMod };
        return candidates.Select(NormalModInstallationService.FindModInfo)
            .FirstOrDefault(path => path is not null && NormalModInstallationService.HasNormalSavePath(path));
    }

    /// <summary>
    /// The normal (offline) Reimagined save folder for this installation, whatever play mode is selected.
    /// Ladder save folders are never returned.
    /// </summary>
    internal static string ResolveOfflineSaveDirectory(InstallationProfile profile, string? savedGamesPath = null)
    {
        var manualDirectory = profile.SaveDirectory?.Trim();
        if (profile.Type == InstallationType.D2RMM)
        {
            return manualDirectory ?? string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(manualDirectory) && !IsLadderSaveFolder(manualDirectory))
        {
            return manualDirectory;
        }

        var installDirectory = profile.InstallDirectory;
        if (string.IsNullOrWhiteSpace(installDirectory))
        {
            return string.Empty;
        }

        // While Ladder is active, mods/Reimagined points at the ladder folder and the normal mod is kept aside.
        var savePath = new[]
            {
                Path.Combine(installDirectory, "mods", "Reimagined"),
                NormalModInstallationService.NormalModRoot(installDirectory)
            }
            .Select(NormalModInstallationService.FindModInfo)
            .Where(path => path is not null && NormalModInstallationService.HasNormalSavePath(path))
            .Select(path => ReadModInfoString(path!, "savepath")?.Trim().Trim('/', '\\'))
            .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));
        if (savePath is null)
        {
            return string.Empty;
        }

        savedGamesPath ??= SaveFileService.GetSavedGamesPath();
        var d2rPath = SaveFileService.ResolveDirectoryCaseInsensitive(savedGamesPath, "Diablo II Resurrected");
        var modsPath = d2rPath is null ? null : SaveFileService.ResolveDirectoryCaseInsensitive(d2rPath, "mods");
        return modsPath is null ? string.Empty : Path.Combine(modsPath, savePath);
    }

    /// <summary>Characters and shared stashes directly inside the save folder; subfolders and backup files are ignored.</summary>
    public static IReadOnlyList<string> DiscoverSaveFiles(string saveDirectory) => Directory.Exists(saveDirectory)
        ? Directory.EnumerateFiles(saveDirectory)
            .Where(path => Path.GetExtension(path).Equals(".d2s", StringComparison.OrdinalIgnoreCase)
                           || Path.GetExtension(path).Equals(".d2i", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList()
        : [];

    /// <summary>
    /// A file the automatic prompt reports: verified conversions, and files that only need a source choice, including
    /// saves that also parse as the target but migrate differently from a historical layout (e.g. classic or non-Warlock saves).
    /// Files that would need experimental Force Conversion are left to the manual conversion window.
    /// </summary>
    public static bool NeedsConversion(SaveConversionPlan plan) =>
        plan.State is SaveConversionState.Verified or SaveConversionState.VerifiedWithWarnings
        || plan is
        {
            State: SaveConversionState.Unresolved or SaveConversionState.AlreadyCompatible,
            ForceOption.Kind: SaveForceKind.ChooseSourceLayout
        };

    internal static string AutomaticPromptKey(InstallationProfile profile, string saveDirectory,
        string targetFingerprint) =>
        $"{AutomaticPromptDirectory(profile.InstallDirectory)}|{AutomaticPromptDirectory(saveDirectory)}|{targetFingerprint}";

    private static string AutomaticPromptDirectory(string? directory) =>
        Path.TrimEndingDirectorySeparator(directory?.Trim() ?? string.Empty);

    /// <summary>
    /// True when the profile still points at the install directory and effective offline save folder captured before an automatic check began.
    /// </summary>
    internal static bool IsAnalyzedInstallation(InstallationProfile profile, string? analyzedInstallDirectory,
        string analyzedSaveDirectory) =>
        AutomaticPromptDirectory(profile.InstallDirectory) == AutomaticPromptDirectory(analyzedInstallDirectory)
        && AutomaticPromptDirectory(ResolveOfflineSaveDirectory(profile)) == AutomaticPromptDirectory(analyzedSaveDirectory);

    /// <summary>True when the automatic prompt already opened for this profile's installation, save folder and installed target data.</summary>
    internal static bool WasAutomaticPromptShown(InstallationProfile profile, string saveDirectory,
        string targetFingerprint) =>
        profile.SaveConversionPromptKey == AutomaticPromptKey(profile, saveDirectory, targetFingerprint);

    /// <summary>
    /// Records the prompt only when the window analyzed the same installed target and still lists a save that needs conversion.
    /// A failed initialization (no <paramref name="shownTargetFingerprint"/>) or a profile that moved away from the analyzed
    /// installation or save folder records nothing.
    /// </summary>
    internal static bool RecordAutomaticPromptShown(InstallationProfile profile, string? analyzedInstallDirectory,
        string analyzedSaveDirectory, string targetFingerprint, string? shownTargetFingerprint,
        IEnumerable<SaveConversionPlan> shownPlans)
    {
        if (shownTargetFingerprint != targetFingerprint || !shownPlans.Any(NeedsConversion) ||
            !IsAnalyzedInstallation(profile, analyzedInstallDirectory, analyzedSaveDirectory))
        {
            return false;
        }

        profile.SaveConversionPromptKey = AutomaticPromptKey(profile, analyzedSaveDirectory, targetFingerprint);
        return true;
    }

    /// <summary>Analyzes every save in <paramref name="saveDirectory"/> and returns those that need conversion. Nothing is written.</summary>
    public IReadOnlyList<string> FindSavesNeedingConversion(string saveDirectory, string stagingDirectory) =>
        DiscoverSaveFiles(saveDirectory)
            .Where((path, index) =>
                NeedsConversion(Plan(path, Path.Combine(stagingDirectory, index.ToString()))))
            .ToList();

    /// <summary>
    /// Checks the offline saves of <paramref name="profile"/> against the installed normal Reimagined data.
    /// Blocking; call it off the UI thread. Returns no files and no fingerprint when there is nothing to analyze, and skips
    /// planning when the automatic prompt was already shown for this installation, save folder and installed target.
    /// </summary>
    /// <param name="findSaves">Replaces <see cref="FindSavesNeedingConversion"/>; tests use it to observe planning.</param>
    internal static (IReadOnlyList<string> Files, string? TargetFingerprint, string SaveDirectory, bool PromptAlreadyShown)
        FindOfflineSavesNeedingConversion(InstallationProfile profile,
            Func<SaveConversionService, string, string, IReadOnlyList<string>>? findSaves = null)
    {
        var saveDirectory = ResolveOfflineSaveDirectory(profile);
        if (DiscoverSaveFiles(saveDirectory).Count == 0 || FindNormalModInfo(profile) is null)
        {
            return ([], null, saveDirectory, false);
        }

        var staging = Path.Combine(Path.GetTempPath(), "ReimaginedSaveConversion", Guid.NewGuid().ToString("N"));
        try
        {
            var installedTarget = LoadInstalledTarget(profile, Path.Combine(staging, "installed-target"));
            if (WasAutomaticPromptShown(profile, saveDirectory, installedTarget.Fingerprint))
            {
                return ([], installedTarget.Fingerprint, saveDirectory, true);
            }

            var service = CreateForInstalledTarget(SaveCheckpointRegistry.Load(DefaultDataDirectory), installedTarget);
            var plans = Path.Combine(staging, "plans");
            var files = findSaves is null
                ? service.FindSavesNeedingConversion(saveDirectory, plans)
                : findSaves(service, saveDirectory, plans);
            return (files, installedTarget.Fingerprint, saveDirectory, false);
        }
        finally
        {
            try
            {
                if (Directory.Exists(staging))
                {
                    Directory.Delete(staging, recursive: true);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                LaunchDiagnostics.LogException("Could not remove the save conversion check staging folder", exception);
            }
        }
    }

    /// <summary>
    /// Restrictions documented for Diablo II character names: 2-15 letters with at most one dash or underscore,
    /// which cannot be the first or last character. Non-Latin letters are accepted because D2R saves use them.
    /// </summary>
    public static string? ValidateCharacterName(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return "Enter a character name.";
        }

        if (name.Length is < NameMinLength or > NameMaxLength)
        {
            return $"Character names must be {NameMinLength} to {NameMaxLength} characters long.";
        }

        if (!name.All(character => char.IsLetter(character) || character is '-' or '_'))
        {
            return "Character names may only contain letters and a single dash or underscore.";
        }

        if (name.Count(character => character is '-' or '_') > 1)
        {
            return "Character names may contain only one dash or underscore.";
        }

        return name[0] is '-' or '_' || name[^1] is '-' or '_'
            ? "A dash or underscore cannot be the first or last character."
            : null;
    }

    /// <summary>
    /// Reads the save and prepares a verified conversion in <paramref name="stagingDirectory"/>. The save is never written.
    /// <paramref name="overrides"/> carries the Force Conversion setting and a manual source choice; neither relaxes decoding, serialization or
    /// the critical output checks. A forced analysis keeps an output that fails a critical check as a recovery candidate beside the backups.
    /// </summary>
    public SaveConversionPlan Plan(string path, string stagingDirectory, MigrationOverrides? overrides = null)
    {
        var kind = Path.GetExtension(path).Equals(".d2i", StringComparison.OrdinalIgnoreCase)
            ? SaveFileKind.SharedStash
            : SaveFileKind.Character;
        byte[] data;
        try
        {
            data = File.ReadAllBytes(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Blocked(path, kind, string.Empty, $"The file could not be read: {exception.Message}");
        }

        var sha256 = Convert.ToHexStringLower(SHA256.HashData(data));
        var sidecar = kind == SaveFileKind.Character && File.Exists(Path.ChangeExtension(path, ".d2rl"))
            ? Path.ChangeExtension(path, ".d2rl")
            : null;
        var embeddedName = kind == SaveFileKind.Character ? ReadEmbeddedName(data) : null;

        try
        {
            var migrator = _migrator.Value;
            var target = migrator.Target.Label;
            var inspection = SaveInspector.Inspect(path, [migrator.Target], []);
            var targetAttempt = inspection.Attempts[0];
            var (targetFits, targetCheck) = DescribeTargetFit(inspection, targetAttempt);

            var forced = overrides?.Force == true;
            var targetRebuild = forced && targetFits && overrides?.SourceLayout is null;
            var result = targetRebuild
                ? migrator.Rebuild(path, Path.Combine(stagingDirectory, "target-rebuild"))
                : migrator.Migrate(path, stagingDirectory, overrides);
            var output = result.OutputPath is { } outputPath ? File.ReadAllBytes(outputPath) : null;
            var targetReadableWithoutHistoricalSource = targetFits && result.SourceLayout is null
                                                                   && overrides?.SourceLayout is null && !forced;
            var reportBuilder = new StringBuilder()
                .AppendLine($"{target} read check: {targetCheck}")
                .AppendLine(
                    "A parse with installed target definitions does not guarantee that the game can load the save or that its data is semantically correct.")
                .Append(forced
                    ? "Force conversion (experimental) was requested for this analysis." + Environment.NewLine
                    : "")
                .Append(overrides?.SourceLayout is { } chosenSource
                    ? $"Source layout {chosenSource} was chosen by the user." + Environment.NewLine
                    : "")
                .AppendLine();
            if (targetReadableWithoutHistoricalSource)
            {
                reportBuilder.AppendLine(
                    "Historical source detection: no registered historical source layout decoded this save.");
                foreach (var layoutFailure in result.Identification.Where(line =>
                             line.Contains(": rejected", StringComparison.Ordinal)))
                {
                    reportBuilder.AppendLine($"  {layoutFailure}");
                }

                foreach (var blocker in result.Blockers)
                {
                    reportBuilder.AppendLine($"  Historical source failure: {Friendly(blocker)}");
                }

                reportBuilder.AppendLine(
                    "An out-of-range stat id reported by a failed historical layout may come from the wrong bit widths; it is not proof that the id is present in the save.");
                reportBuilder.AppendLine("No conversion was required or attempted.");
            }
            else
            {
                reportBuilder.Append(SaveMigrationReport.Write(result));
            }

            var report = reportBuilder.ToString();
            var risks = result.References.Where(reference => reference.Blocking)
                .Select(reference => DescribeRisk(reference, target))
                .Concat(result.Uncertainties.Select(Friendly))
                .Distinct()
                .ToList();

            SaveConversionPlan Create(SaveConversionState state, string operation, string summary,
                string? blockingReason, byte[]? converted,
                SaveForceOption? force = null, string? recoveryCandidate = null,
                string? historicalAlternative = null, bool isTargetRebuild = false) => new()
            {
                SourcePath = path,
                Kind = kind,
                State = state,
                Operation = operation,
                Issues = state == SaveConversionState.AlreadyCompatible ? [] : GroupIssues(result),
                Report = report,
                SourceSha256 = sha256,
                TargetLabel = target,
                TargetFingerprint = _installedTarget?.Fingerprint,
                SourceLabel = result.SourceLayout,
                Summary = summary,
                BlockingReason = blockingReason,
                IssueGroups = state == SaveConversionState.AlreadyCompatible ? [] : CategorizeIssues(result),
                ForceOption = force,
                HistoricalAlternative = historicalAlternative,
                IsTargetRebuild = isTargetRebuild,
                CanAttemptRecovery = state == SaveConversionState.Blocked && !forced &&
                                     result.RejectedOutputPath is not null,
                RecoveryCandidatePath = recoveryCandidate,
                ConvertedBytes = converted,
                EmbeddedName = embeddedName,
                SidecarPath = sidecar
            };

            if (targetRebuild)
            {
                if (output is null)
                {
                    var rebuildProblems = result.Blockers.Select(Friendly)
                        .Concat(result.Checks.Where(check => check is { Passed: false, Critical: true })
                            .Select(check => $"Critical check failed: {check.Name} ({check.Detail})."))
                        .ToList();
                    var rebuildFailure = rebuildProblems.Count == 0
                        ? "The target-definition rebuild could not be verified."
                        : string.Join(" ", rebuildProblems);
                    var rebuildCandidate = result.RejectedOutputPath is { } rejectedRebuild
                        ? KeepRecoveryCandidate(path, rejectedRebuild, report)
                        : null;
                    if (rebuildCandidate is not null)
                    {
                        rebuildFailure +=
                            $" The unapproved output was kept as an experimental recovery candidate, not applied: {rebuildCandidate}";
                    }

                    return Create(SaveConversionState.Blocked,
                        "Not rebuilt: no structurally usable output could be produced, so the file is left untouched.",
                        "The target-definition rebuild could not be verified. The original was not changed.",
                        rebuildFailure, null, recoveryCandidate: rebuildCandidate, isTargetRebuild: true);
                }

                if (output.AsSpan().SequenceEqual(data))
                {
                    return Create(SaveConversionState.AlreadyCompatible,
                        $"The forced {target} target-definition round-trip was byte-identical; no repair occurred. The parser cannot establish in-game compatibility.",
                        "The target-definition round-trip produced identical bytes, so no repair occurred. The in-game error remains unexplained.",
                        null, null, isTargetRebuild: true);
                }

                var rebuildDifferences = new List<string> { DescribeByteDifferences(data, output) };
                rebuildDifferences.AddRange(result.Warnings.Where(warning =>
                    warning.StartsWith("Target-definition rebuild model difference:", StringComparison.Ordinal)));
                rebuildDifferences.AddRange(risks);
                rebuildDifferences = rebuildDifferences.Distinct().ToList();
                return Create(SaveConversionState.Experimental,
                    $"EXPERIMENTAL target-definition rebuild with {target}. Structural checks passed; {DescribeByteDifferences(data, output)} "
                    + "This does not guarantee that the game can load the save.",
                    "Force prepared a best-effort round-trip using installed target definitions. Review every reported difference; game compatibility is not guaranteed.",
                    null, output,
                    new SaveForceOption(SaveForceKind.Force, [], rebuildDifferences),
                    isTargetRebuild: true);
            }

            if (targetFits && overrides?.SourceLayout is null)
            {
                var historicalOutputPath = result.OutputPath ?? result.RejectedOutputPath;
                var historicalOutput =
                    historicalOutputPath is not null ? File.ReadAllBytes(historicalOutputPath) : null;
                var historicalLayouts = result.AmbiguousSourceLayouts.Count > 1
                    ? result.AmbiguousSourceLayouts.OrderBy(layout => layout == DefaultSourceLayout ? 0 : 1).ToList()
                    : result.SourceLayout is { } historical
                        ? [historical]
                        : [];
                var hasHistoricalAlternative = result.IsRecoverable && result.SourceLayout is not null
                                                                    && historicalOutput is not null
                                                                    && !historicalOutput.AsSpan().SequenceEqual(data);
                var failedAttempts = result.Identification
                    .Where(line => line.Contains(": rejected", StringComparison.Ordinal))
                    .ToList();
                failedAttempts.AddRange(result.Blockers.Select(blocker =>
                    $"historical migration blocker: {Friendly(blocker)}"));
                failedAttempts.AddRange(result.Checks.Where(check => check is { Passed: false, Critical: true })
                    .Select(check => $"historical migration critical check failed: {check.Name} ({check.Detail})"));
                var attemptDetails = failedAttempts.Count == 0
                    ? string.Empty
                    : " Historical attempts: " + string.Join("; ", failedAttempts) + ".";
                var alternativeDetails = hasHistoricalAlternative
                    ? $" A recoverable historical interpretation ({string.Join(", ", historicalLayouts)}) also produces different output; choose it to attempt migration."
                    : " No viable historical migration output differing from the input was established.";
                var noHistoricalSource = result.SourceLayout is null;
                var forceOption = hasHistoricalAlternative
                    ? new SaveForceOption(SaveForceKind.ChooseSourceLayout, historicalLayouts, risks,
                        TargetAlsoFits: true)
                    : null;
                return Create(SaveConversionState.AlreadyCompatible,
                    noHistoricalSource
                        ? $"Parses with installed {target} definitions; no historical source layout decoded the save, so no conversion was required or attempted. This does not prove game compatibility.{attemptDetails}"
                        : $"Parses with installed {target} definitions; this does not prove game compatibility.{alternativeDetails}{attemptDetails}",
                    hasHistoricalAlternative
                        ? $"The save parses with installed {target} definitions, but a different historical migration is available. Either interpretation is experimental evidence, not a game-compatibility guarantee."
                        : noHistoricalSource
                            ? $"The save parses with the current installed layout. No historical source decoded it, so no conversion was required or attempted. This does not guarantee that the game can load it; Force Conversion remains available for an experimental target rebuild."
                            : $"The save parses with installed {target} definitions. This does not guarantee that the game can load it; no different viable historical output was found.",
                    null, null, forceOption, historicalAlternative: hasHistoricalAlternative
                        ? string.Join(", ", historicalLayouts)
                        : null);
            }

            if (overrides?.SourceLayout is null && result.AmbiguousSourceLayouts.Count > 1)
            {
                var reason =
                    $"The file decodes as {string.Join(" and as ", result.AmbiguousSourceLayouts)}, which read it differently. "
                    + "The launcher cannot tell which version made it.";
                return Create(SaveConversionState.Unresolved, $"Not converted: {reason}",
                    "The launcher cannot tell which Reimagined version this save was made with. Review the details before continuing.",
                    reason, null,
                    new SaveForceOption(SaveForceKind.ChooseSourceLayout,
                        [.. result.AmbiguousSourceLayouts.OrderBy(layout => layout == DefaultSourceLayout ? 0 : 1)],
                        []));
            }

            var layouts =
                $"{result.SourceLayout ?? "unidentified layout"} (version {result.SourceVersion}) to {target} (version {SaveMigrator.TargetVersion})";
            switch (result.Status)
            {
                case MigrationStatus.Verified:
                    return Create(SaveConversionState.Verified,
                        $"Convert {layouts}. The output passed every verification check.",
                        $"Your save can be converted. The original will be backed up. It converts from {layouts}.",
                        null, output);
                case MigrationStatus.NeedsManualInspection:
                    return Create(SaveConversionState.VerifiedWithWarnings,
                        $"Convert {layouts}. The output passed verification, but some historical references need your attention.",
                        "Some older items have unusual definitions. Their saved properties will be preserved, but their behavior may differ.",
                        null, output);
                case MigrationStatus.Experimental:
                    return Create(SaveConversionState.Experimental,
                        $"EXPERIMENTAL conversion of {layouts}. The critical structural checks passed; "
                        + (overrides?.SourceLayout is { } manual
                            ? $"the source layout {manual} was chosen manually; "
                            : "")
                        + $"{risks.Count} unverified issue(s) were accepted by Force conversion. This output is NOT fully verified.",
                        risks.Count == 0
                            ? $"Converts from {result.SourceLayout} (chosen by you) to {target}. The launcher could not confirm the source version itself."
                            : "Force conversion prepared an experimental output. Some data may be changed, lost or appear differently; "
                              + "the full report lists every accepted issue.", null, output,
                        new SaveForceOption(
                            overrides?.SourceLayout is null ? SaveForceKind.Force : SaveForceKind.ChooseSourceLayout,
                            overrides?.SourceLayout is { } chosen ? [chosen] : [], risks));
            }

            if (result.NeedsForce)
            {
                var reason = $"{risks.Count} issue(s) cannot be verified: {string.Join(" ", risks.Take(3))}"
                             + (risks.Count > 3 ? $" ({risks.Count - 3} more in the full report.)" : "")
                             + " All critical verification checks passed; Force conversion (experimental) can accept these issues.";
                return Create(SaveConversionState.Unresolved, $"Not converted: {reason}",
                    "Some data cannot be verified. Force conversion can attempt an experimental conversion.", reason,
                    null,
                    overrides?.SourceLayout is { } selected
                        ? new SaveForceOption(SaveForceKind.ChooseSourceLayout, [selected], risks)
                        : new SaveForceOption(SaveForceKind.Force, [], risks));
            }

            var problems = result.Blockers.Select(Friendly)
                .Concat(result.Checks.Where(check => check is { Passed: false, Critical: true })
                    .Select(check => $"Critical check failed: {check.Name} ({check.Detail})."))
                .Concat(result.Checks.Where(check => check is { Passed: false, Critical: false })
                    .Select(check => $"Verification failed: {check.Name} ({check.Detail})."))
                .ToList();
            var failure = problems.Count == 0
                ? "The conversion could not be verified."
                : problems[0] + (problems.Count > 1
                    ? $" ({problems.Count - 1} more problem(s) in the full report.)"
                    : "");
            var candidate = forced && result.RejectedOutputPath is { } rejected
                ? KeepRecoveryCandidate(path, rejected, report)
                : null;
            if (candidate is not null)
            {
                failure +=
                    $" The unapproved output was kept as an experimental recovery candidate, not applied: {candidate}";
            }

            return Create(SaveConversionState.Blocked,
                "Not converted: no usable output could be produced, so the file is left untouched.",
                "The save could not be converted. Your original was not changed."
                + (forced ? " Force conversion cannot override this." : ""),
                failure, null, recoveryCandidate: candidate);
        }
        catch (Exception exception)
        {
            return Blocked(path, kind, sha256, $"Analysis failed: {exception.GetType().Name}: {exception.Message}",
                embeddedName, sidecar);
        }
    }

    /// <summary>Null when <paramref name="newName"/> can be written into this character; otherwise the reason it cannot.</summary>
    public string? CheckRename(SaveConversionPlan plan, string newName)
    {
        if (ValidateCharacterName(newName) is { } invalid)
        {
            return invalid;
        }

        if (plan.Kind != SaveFileKind.Character || plan.IsBlocked)
        {
            return "Only characters that can be converted or are already compatible can be renamed.";
        }

        var directory = Path.GetDirectoryName(plan.SourcePath)!;
        if (!string.Equals(newName, plan.FileStem, StringComparison.Ordinal))
        {
            if (string.Equals(newName, plan.FileStem, StringComparison.OrdinalIgnoreCase))
            {
                return
                    $"Use the file name exactly ('{plan.FileStem}') or a different name; changing only letter case is not supported.";
            }

            if (plan.SidecarPath is not null)
            {
                return
                    $"'{Path.GetFileName(plan.SidecarPath)}' is a D2RLoader sidecar for this file. Its format is not documented, so the launcher "
                    + $"cannot confirm it stays valid for a renamed file. Keep the name '{plan.FileStem}' or move the sidecar yourself first.";
            }

            foreach (var extension in CompanionExtensions.Prepend(".d2s"))
            {
                var destination = Path.Combine(directory, newName + extension);
                if (File.Exists(destination))
                {
                    return $"'{Path.GetFileName(destination)}' already exists in the save folder.";
                }
            }
        }

        foreach (var other in DiscoverSaveFiles(directory).Where(path =>
                     path.EndsWith(".d2s", StringComparison.OrdinalIgnoreCase) && !SamePath(path, plan.SourcePath)))
        {
            if (string.Equals(Path.GetFileNameWithoutExtension(other), newName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(TryReadEmbeddedName(other), newName, StringComparison.OrdinalIgnoreCase))
            {
                return $"'{Path.GetFileName(other)}' already uses the name '{newName}'.";
            }
        }

        return null;
    }

    /// <summary>A new, uniquely named folder for one batch of conversions. Existing backups are never reused or overwritten.</summary>
    public string CreateBackupBatchDirectory() => CreateUniqueDirectory(BackupRoot);

    private static string CreateUniqueDirectory(string root)
    {
        var name = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var path = Path.Combine(root, name);
        for (var attempt = 2; Directory.Exists(path); attempt++)
        {
            path = Path.Combine(root, $"{name}-{attempt}");
        }

        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>
    /// Copies an unapproved Force Conversion output beside the backups, never into the save folder, with a name the game does not load.
    /// Returns null when it could not be kept.
    /// </summary>
    private string? KeepRecoveryCandidate(string sourcePath, string candidatePath, string report)
    {
        try
        {
            var saveDirectory = Path.GetDirectoryName(Path.GetFullPath(sourcePath))!;
            if (IsWithin(BackupRoot, saveDirectory) || IsWithin(saveDirectory, BackupRoot))
            {
                return null;
            }

            var directory = CreateUniqueDirectory(Path.Combine(BackupRoot, "RecoveryCandidates"));
            var destination = Path.Combine(directory, Path.GetFileName(sourcePath) + ".recovery-candidate");
            WriteNewFile(destination, File.ReadAllBytes(candidatePath));
            WriteNewFile(destination + ".report.txt", Encoding.UTF8.GetBytes(
                "EXPERIMENTAL RECOVERY CANDIDATE - FAILED A CRITICAL CHECK AND WAS NOT APPLIED. The original save was not changed."
                + Environment.NewLine + $"Original file: {sourcePath}" + Environment.NewLine + Environment.NewLine +
                report));
            return destination;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            LaunchDiagnostics.LogException($"Could not keep the recovery candidate for {sourcePath}", exception);
            return null;
        }
    }

    /// <summary>
    /// Backs up the original, then replaces it with the planned output and, when <paramref name="newName"/> is given,
    /// the corrected character name. The original file is only removed after its replacement is written and verified.
    /// </summary>
    public SaveConversionOutcome Apply(SaveConversionPlan plan, string? newName, string backupBatchDirectory)
    {
        try
        {
            var desiredName = newName?.Trim();
            var changesName = !string.IsNullOrEmpty(desiredName) &&
                              (desiredName != plan.EmbeddedName || desiredName != plan.FileStem);
            return ApplyCore(plan, changesName ? desiredName : null, backupBatchDirectory);
        }
        catch (Exception exception)
        {
            LaunchDiagnostics.LogException($"Save conversion failed for {plan.SourcePath}", exception);
            return new SaveConversionOutcome(false,
                $"Failed: {exception.Message} The original file was left in place.", plan.SourcePath);
        }
    }

    private SaveConversionOutcome ApplyCore(SaveConversionPlan plan, string? newName, string backupBatchDirectory)
    {
        if (plan.IsBlocked)
        {
            return new SaveConversionOutcome(false, "Blocked: this file is not converted.", plan.SourcePath);
        }

        if (plan.TargetFingerprint is { } targetFingerprint
            && (_installedTarget is null || _installedTarget.Fingerprint != targetFingerprint ||
                !_installedTarget.IsUnchanged()))
        {
            return new SaveConversionOutcome(false,
                "The installed Reimagined TXT files changed after analysis. Reanalyze the saves before converting; nothing was changed.",
                plan.SourcePath);
        }

        if (plan.ConvertedBytes is null && newName is null)
        {
            return new SaveConversionOutcome(true,
                plan.IsTargetRebuild
                    ? "The installed-target round-trip was byte-identical; no repair occurred. Game compatibility was not established."
                    : "No changes needed.", plan.SourcePath);
        }

        if (_isGameRunning())
        {
            return new SaveConversionOutcome(false,
                "Diablo II: Resurrected is running. Close the game and try again; nothing was changed.",
                plan.SourcePath);
        }

        var directory = Path.GetFullPath(Path.GetDirectoryName(plan.SourcePath)!);
        if (IsWithin(BackupRoot, directory) || IsWithin(directory, BackupRoot))
        {
            return new SaveConversionOutcome(false,
                "The migration backup folder overlaps the save folder; nothing was changed.", plan.SourcePath);
        }

        var original = File.ReadAllBytes(plan.SourcePath);
        if (Convert.ToHexStringLower(SHA256.HashData(original)) != plan.SourceSha256)
        {
            return new SaveConversionOutcome(false,
                "The file changed after it was analyzed. Close this window and analyze it again; nothing was changed.",
                plan.SourcePath);
        }

        var bytes = (plan.ConvertedBytes ?? original).ToArray();
        var destination = plan.SourcePath;
        var companions = new List<(string From, string To)>();
        if (newName is not null)
        {
            if (CheckRename(plan, newName) is { } renameProblem)
            {
                return new SaveConversionOutcome(false, $"Rename blocked: {renameProblem} Nothing was changed.",
                    plan.SourcePath);
            }

            if (newName != plan.EmbeddedName && WriteCharacterName(bytes, newName) is { } nameProblem)
            {
                return new SaveConversionOutcome(false, $"Rename blocked: {nameProblem} Nothing was changed.",
                    plan.SourcePath);
            }

            if (newName != plan.FileStem)
            {
                destination = Path.Combine(directory, newName + ".d2s");
                companions = CompanionExtensions
                    .Select(extension => (Path.Combine(directory, plan.FileStem + extension),
                        Path.Combine(directory, newName + extension)))
                    .Where(pair => File.Exists(pair.Item1))
                    .ToList();
            }
        }

        if (plan.Kind == SaveFileKind.Character && (newName is null || newName == plan.FileStem))
        {
            companions = CompanionExtensions.Prepend(".d2rl")
                .Select(extension =>
                {
                    var path = Path.Combine(directory, plan.FileStem + extension);
                    return (From: path, To: path);
                })
                .Where(pair => File.Exists(pair.From))
                .ToList();
        }

        WriteBackup(backupBatchDirectory, plan, original, companions, destination);

        var temporary = $"{destination}.{Guid.NewGuid():N}.converting";
        try
        {
            WriteNewFile(temporary, bytes);
            if (SamePath(destination, plan.SourcePath))
            {
                File.Move(temporary, destination, overwrite: true);
            }
            else
            {
                File.Move(temporary, destination, overwrite: false);
                var moved = new List<(string From, string To)>();
                try
                {
                    foreach (var companion in companions)
                    {
                        File.Move(companion.From, companion.To, overwrite: false);
                        moved.Add(companion);
                    }

                    File.Delete(plan.SourcePath);
                }
                catch
                {
                    foreach (var companion in moved)
                    {
                        File.Move(companion.To, companion.From, overwrite: false);
                    }

                    File.Delete(destination);
                    throw;
                }
            }
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }

        if (!File.ReadAllBytes(destination).AsSpan().SequenceEqual(bytes))
        {
            return new SaveConversionOutcome(false,
                $"The written file does not match the verified output. Restore it from {backupBatchDirectory}.",
                destination, backupBatchDirectory);
        }

        var actions = new List<string>();
        if (plan.ConvertedBytes is not null)
        {
            actions.Add(plan.State switch
            {
                SaveConversionState.Experimental when plan.IsTargetRebuild =>
                    "Rebuilt experimentally with the installed target definitions (see the experimental report in the backup)",
                SaveConversionState.Experimental =>
                    "Converted experimentally (accepted issues are not verified; see the experimental report in the backup)",
                SaveConversionState.VerifiedWithWarnings => "Converted (with accepted warnings)",
                _ => "Converted"
            });
        }

        if (newName is not null && newName != plan.EmbeddedName)
        {
            actions.Add($"character renamed '{plan.EmbeddedName}' -> '{newName}'");
        }

        if (!SamePath(destination, plan.SourcePath))
        {
            actions.Add($"file renamed {plan.FileName} -> {Path.GetFileName(destination)}");
        }

        if (newName is null && plan.HasNameMismatch)
        {
            actions.Add($"the file name still differs from the character name '{plan.EmbeddedName}'");
        }

        return new SaveConversionOutcome(true, $"{string.Join("; ", actions)}. Backup: {backupBatchDirectory}",
            destination, backupBatchDirectory);
    }

    /// <returns>Null when the name was written and verified; otherwise the problem, with <paramref name="bytes"/> unusable.</returns>
    private string? WriteCharacterName(byte[] bytes, string newName)
    {
        var version = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4));
        if (version < 104)
        {
            return $"renaming is only supported for binary version 104 and later (this file is version {version}).";
        }

        var before = bytes.ToArray();
        D2SaveLayoutV104.From(bytes).Name = newName;
        D2SaveLayoutV104.UpdateChecksum(bytes);

        for (var index = 0; index < bytes.Length; index++)
        {
            var allowed = index is >= ChecksumOffset and < ChecksumOffset + 4
                or >= V104NameOffset and < V104NameOffset + V104NameLength;
            if (before[index] != bytes[index] && !allowed)
            {
                return $"writing the name changed byte {index} outside the name field.";
            }
        }

        if (!ChecksumCalculator.Verify(bytes))
        {
            return "the recalculated checksum does not verify.";
        }

        var externalData = _migrator.Value.Target.CreateExternalData(version);
        var save = D2Save.Read(bytes, externalData);
        return save.Character.Preview.Name == newName
               && save.TrailingData.AsSpan().SequenceEqual(D2Save.Read(before, externalData).TrailingData)
               && ReadEmbeddedName(bytes) == newName
            ? null
            : "the renamed save did not reparse with the new name.";
    }

    private static void WriteBackup(string batchDirectory, SaveConversionPlan plan, byte[] original,
        IReadOnlyList<(string From, string To)> companions, string destination)
    {
        Directory.CreateDirectory(batchDirectory);
        var backupPath = Path.Combine(batchDirectory, plan.FileName);
        WriteNewFile(backupPath, original);

        var manifest = new StringBuilder()
            .AppendLine("Reimagined Launcher save conversion backup. These files are never cleaned up automatically.")
            .AppendLine($"Created: {DateTime.Now:yyyy-MM-dd HH:mm:ss}")
            .AppendLine($"Original file: {plan.SourcePath}")
            .AppendLine($"Original SHA-256: {plan.SourceSha256}")
            .AppendLine($"Operation: {plan.Operation}")
            .AppendLine($"Written to: {destination}");
        if (plan.State == SaveConversionState.Experimental)
        {
            var reportPath = Path.Combine(batchDirectory, plan.FileName + ".experimental-report.txt");
            WriteNewFile(reportPath, Encoding.UTF8.GetBytes(plan.Report));
            manifest.AppendLine(
                    "EXPERIMENTAL: the written file is not fully verified. The report lists every accepted change, loss and unverified item.")
                .AppendLine($"Full technical report: {reportPath}");
        }

        foreach (var companion in companions)
        {
            var companionBackup = Path.Combine(batchDirectory, Path.GetFileName(companion.From));
            WriteNewFile(companionBackup, File.ReadAllBytes(companion.From));
            manifest.AppendLine(SamePath(companion.From, companion.To)
                ? $"Companion file: {companion.From} (left unchanged)"
                : $"Companion file: {companion.From} -> {companion.To}");
        }

        manifest
            .AppendLine()
            .AppendLine(
                "To restore: close the game, remove the written file and any renamed companion files, then copy")
            .AppendLine($"the backed-up file(s) from this folder back into {Path.GetDirectoryName(plan.SourcePath)}.");
        WriteNewFile(Path.Combine(batchDirectory, plan.FileName + ".restore.txt"),
            Encoding.UTF8.GetBytes(manifest.ToString()));
    }

    /// <summary>Writes a file that must not already exist, flushes it to disk and confirms the bytes read back.</summary>
    private static void WriteNewFile(string path, byte[] bytes)
    {
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }

        if (!File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))
        {
            throw new IOException($"'{path}' did not read back identically after writing.");
        }
    }

    private static (bool Fits, string Description) DescribeTargetFit(SaveInspection inspection, ParseAttempt attempt)
    {
        var header = inspection.Header;
        if (!attempt.Succeeded)
        {
            return (false, $"does not parse ({attempt.Error})");
        }

        var undefined = attempt.CharacterStats.Concat(attempt.AllItems.SelectMany(item => item.Stats))
            .Where(stat => stat.Name.StartsWith('('))
            .Select(stat => stat.Id)
            .Distinct()
            .ToList();
        var problems = new List<string>();
        if (header.Version != SaveMigrator.TargetVersion)
        {
            problems.Add($"binary version {header.Version}");
        }

        if (undefined.Count > 0)
        {
            problems.Add($"undefined stat ids [{string.Join(", ", undefined)}]");
        }

        if (header.ChecksumValid == false || header.DeclaredSize is { } declared && declared != header.ActualSize)
        {
            problems.Add("invalid checksum or declared size");
        }

        // Trailing extension data is carried by D2SSharp and is not an incompatibility.
        var trailer = attempt.TrailingBytes > 0
            ? $", {attempt.TrailingBytes} byte(s) of extension data after the standard sections"
            : "";
        return problems.Count == 0
            ? (true, $"complete parse, {attempt.AllItems.Count()} item(s){trailer}")
            : (false, $"parses, but {string.Join(", ", problems)}{trailer}");
    }

    private static string DescribeByteDifferences(byte[] before, byte[] after)
    {
        var maxLength = Math.Max(before.Length, after.Length);
        var changedOffsets = Enumerable.Range(0, maxLength)
            .Where(offset => offset >= before.Length || offset >= after.Length || before[offset] != after[offset])
            .ToList();
        var lengthChange = before.Length == after.Length
            ? $"length unchanged at {before.Length} byte(s)"
            : $"length {before.Length} -> {after.Length} byte(s)";
        return
            $"{changedOffsets.Count} differing byte offset(s) [{string.Join(", ", changedOffsets)}]; {lengthChange}.";
    }

    /// <summary>Counts per category; blocking groups first. Individual technical lines stay in the report.</summary>
    private static List<SaveIssueGroup> CategorizeIssues(SaveMigrationResult result)
    {
        var entries = new List<(SaveIssueCategory Category, bool Blocking, string? Item)>();
        entries.AddRange(result.Blockers.Select(blocker => (SaveIssueCategory.SaveFormat, true, ItemOf(blocker))));
        entries.AddRange(result.Checks.Where(check => check is { Passed: false, Critical: true })
            .Select(_ => (SaveIssueCategory.Verification, true, (string?)null)));
        foreach (var reference in result.References)
        {
            if (reference.Blocking)
            {
                entries.Add((SaveIssueCategory.ItemIdentity, !result.Overrides.Force, reference.ItemPath));
            }
            else if (reference.Outcome is ReferenceOutcome.PreservedWithWarning or ReferenceOutcome.ManualAttention)
            {
                entries.Add((reference.Kind is TableKind.MagicPrefix or TableKind.MagicSuffix
                        ? SaveIssueCategory.HistoricalModifiers
                        : reference.Outcome == ReferenceOutcome.ManualAttention
                            ? SaveIssueCategory.ItemIdentity
                            : SaveIssueCategory.UnknownReferences,
                    false, reference.ItemPath));
            }
        }

        entries.AddRange(result.Uncertainties.Select(line =>
            (SaveIssueCategory.Unverified, !result.Overrides.Force, ItemOf(line))));
        foreach (var warning in result.Warnings)
        {
            var category = warning.Contains("maps by identity", StringComparison.Ordinal) ||
                           warning.StartsWith("Registered identity mapping", StringComparison.Ordinal)
                ? SaveIssueCategory.ItemIdentity
                : warning.Contains("conversion changed", StringComparison.Ordinal) ||
                  warning.Contains("not marked Saved", StringComparison.Ordinal)
                    ? SaveIssueCategory.SaveFormat
                    : SaveIssueCategory.UnknownReferences;
            entries.Add((category, false, ItemOf(warning)));
        }

        return entries
            .GroupBy(entry => (entry.Category, entry.Blocking))
            .OrderByDescending(group => group.Key.Blocking)
            .ThenBy(group => group.Key.Category)
            .Select(group => new SaveIssueGroup(group.Key.Category, group.Key.Blocking, group.Count(),
                group.Select(entry => entry.Item).OfType<string>().Distinct(StringComparer.Ordinal).Count()))
            .ToList();

        static string? ItemOf(string line) =>
            ItemPathPrefix.Match(line) is { Success: true } match ? match.Value : null;
    }

    private static string DescribeRisk(ReferenceResult reference, string target)
    {
        var table = reference.Kind switch
        {
            TableKind.UniqueItems => "unique",
            TableKind.SetItems => "set item",
            TableKind.MagicPrefix => "magic prefix",
            TableKind.MagicSuffix => "magic suffix",
            _ => reference.Kind.ToString()
        };
        var name = reference.SourceKey is null ? "" : $" '{reference.SourceKey}'";
        var meaning = reference.SameIdTargetKey is { } current
            ? $"keeping id {reference.SourceId} would show it as '{current}' in {target}."
            : $"id {reference.SourceId} names no {table} in {target}.";
        return $"{Friendly(reference.ItemPath)}: {table} {reference.SourceId}{name} has no reliable match; {meaning}";
    }

    /// <summary>"Player #41 'cm2 ' (Stored Inventory 0,0)" becomes "Player item #41 (cm2)".</summary>
    private static string Friendly(string text) => FriendlyItemPath.Replace(text, match =>
        $"{match.Groups["location"].Value} item #{match.Groups["index"].Value} ({match.Groups["code"].Value.Trim()})"
        + (match.Groups["socket"].Length > 0 ? ", socketed item" : ""));

    /// <summary>Collapses repeated warnings (the same issue on many items) into one line with a count.</summary>
    private static List<string> GroupIssues(SaveMigrationResult result)
    {
        var lines = result.Blockers.Select(blocker => "Blocker: " + ItemPathPrefix.Replace(blocker, ""))
            .Concat(result.Checks.Where(check => check is { Passed: false, Critical: true })
                .Select(check => $"Critical check failed: {check.Name}: {check.Detail}"))
            .Concat(result.Uncertainties.Select(line =>
                (result.Overrides.Force ? "Accepted by Force conversion: " : "Needs Force conversion: ")
                + ItemPathPrefix.Replace(line, "")))
            .Concat(result.Warnings.Select(warning => "Warning: " + ItemPathPrefix.Replace(warning, "")))
            .Concat(result.References
                .Where(reference =>
                    reference.Blocking ||
                    reference.Outcome is ReferenceOutcome.PreservedWithWarning or ReferenceOutcome.ManualAttention)
                .Select(reference =>
                    $"{(reference.Blocking ? "Blocking" : reference.Outcome == ReferenceOutcome.ManualAttention ? "Needs attention" : "Preserved with warning")}: "
                    + $"{reference.Kind} {reference.Role} {reference.SourceId}"
                    + (reference.TargetId == reference.SourceId ? "" : $" -> {reference.TargetId}")
                    + $": {reference.Reason}"));
        return lines
            .GroupBy(line => line, StringComparer.Ordinal)
            .Select(group => group.Count() > 1 ? $"{group.Key} (x{group.Count()})" : group.Key)
            .ToList();
    }

    private static SaveConversionPlan Blocked(string path, SaveFileKind kind, string sha256, string reason,
        string? embeddedName = null, string? sidecar = null) => new()
    {
        SourcePath = path,
        Kind = kind,
        State = SaveConversionState.Blocked,
        Operation = "Not converted: " + reason,
        Issues = [],
        Report = reason,
        Summary = "The save could not be converted or verified safely. Your original was not changed.",
        BlockingReason = reason,
        IssueGroups = [new SaveIssueGroup(SaveIssueCategory.Verification, true, 1, 0)],
        SourceSha256 = sha256,
        EmbeddedName = embeddedName,
        SidecarPath = sidecar
    };

    private static string? ReadEmbeddedName(byte[] data)
    {
        try
        {
            if (data.Length < 16)
            {
                return null;
            }

            return BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4)) >= 104
                ? D2SaveLayoutV104.From(data).Name
                : D2SaveLayout.From(data).Name;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? TryReadEmbeddedName(string path)
    {
        try
        {
            return ReadEmbeddedName(File.ReadAllBytes(path));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? ReadModInfoString(string modInfoPath, string propertyName)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(modInfoPath));
            return document.RootElement.TryGetProperty(propertyName, out var value) &&
                   value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static bool IsLadderSaveFolder(string directory) =>
        LadderSaveFolderSuffix.IsMatch(Path.GetFileName(Path.TrimEndingDirectorySeparator(directory)));

    private static bool SamePath(string first, string second) =>
        string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase);

    private static bool IsWithin(string path, string directory)
    {
        var normalizedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var normalizedDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        return string.Equals(normalizedPath, normalizedDirectory, StringComparison.OrdinalIgnoreCase)
               || normalizedPath.StartsWith(normalizedDirectory + Path.DirectorySeparatorChar,
                   StringComparison.OrdinalIgnoreCase);
    }
}