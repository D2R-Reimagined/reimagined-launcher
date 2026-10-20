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
    /// <summary>Structurally verified output that exists only because Force Conversion overrides were requested.</summary>
    Experimental,
    /// <summary>Withheld for a policy reason Force Conversion may override: an unresolved item identity or an uncertain source version.</summary>
    Unresolved,
    /// <summary>The conversion failed or could not be verified safely; nothing can override this.</summary>
    Blocked
}

public enum SaveIssueCategory
{
    HistoricalModifiers,
    ItemIdentity,
    UnknownReferences,
    SaveFormat,
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
        _ => "Verification failure"
    };

    public override string ToString() =>
        $"{Title}: {Count} {(Blocking ? "blocking" : Count == 1 ? "warning" : "warnings")}" + (Items > 0 ? $" on {Items} item(s)" : "");
}

public enum SaveForceKind
{
    AcceptUnresolvedIdentities,
    ChooseSourceLayout
}

/// <param name="SourceLayouts">Layouts the user may choose from, the default first, or the one chosen for an experimental plan.</param>
/// <param name="Risks">Each unresolved identity and what its unchanged saved id names in the target.</param>
/// <param name="TargetAlsoFits">The file also reads completely as the target, so "already converted" is a valid choice too.</param>
public sealed record SaveForceOption(SaveForceKind Kind, IReadOnlyList<string> SourceLayouts, IReadOnlyList<string> Risks, bool TargetAlsoFits = false);

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

    /// <summary>The source layout the conversion reads the file as, when one was identified or chosen.</summary>
    public string? SourceLabel { get; init; }

    /// <summary>One or two plain sentences for players; technical detail stays in <see cref="Report"/>.</summary>
    public string Summary { get; init; } = string.Empty;

    /// <summary>The actual reason a blocked or unresolved file is not converted, separate from nonblocking warnings.</summary>
    public string? BlockingReason { get; init; }

    public IReadOnlyList<SaveIssueGroup> IssueGroups { get; init; } = [];

    /// <summary>For <see cref="SaveConversionState.Unresolved"/>: what Force Conversion may override. For an experimental plan: what it overrode.</summary>
    public SaveForceOption? ForceOption { get; init; }

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

public sealed record SaveConversionOutcome(bool Succeeded, string Message, string? FinalPath = null, string? BackupDirectory = null);

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
    private static readonly string[] CompanionExtensions = [".key", ".keyo", ".ctl", ".ctlo", ".map", ".ma0", ".ma1", ".ma2", ".ma3"];

    private static readonly Regex ItemPathPrefix = new(
        @"^(?:Player|Corpse \d+|Mercenary|Iron golem|Stash tab \d+) #\d+ '[^']*' \([^)]*\)(?: > socket \d+ '[^']*')*: ",
        RegexOptions.Compiled);

    private static readonly Regex FriendlyItemPath = new(
        @"^(?<location>Player|Corpse \d+|Mercenary|Iron golem|Stash tab \d+) #(?<index>\d+) '(?<code>[^']*)' \([^)]*\)(?<socket>(?: > socket \d+ '[^']*')*)",
        RegexOptions.Compiled);

    private static readonly Regex LadderSaveFolderSuffix = new("-(?:[0-9a-fA-F]{8}|[0-9a-fA-F]{32})$", RegexOptions.Compiled);

    private readonly Lazy<SaveMigrator> _migrator;
    private readonly Func<bool> _isGameRunning;

    /// <param name="defaultSourceLayout">Listed first when the source layout of a save is genuinely ambiguous.</param>
    public SaveConversionService(Func<SaveMigrator> createMigrator, string backupRoot, Func<bool> isGameRunning, string? defaultSourceLayout = null)
    {
        _migrator = new Lazy<SaveMigrator>(createMigrator);
        BackupRoot = Path.GetFullPath(backupRoot);
        _isGameRunning = isGameRunning;
        DefaultSourceLayout = defaultSourceLayout;
    }

    public static string DefaultDataDirectory => Path.Combine(AppContext.BaseDirectory, "Assets", "SaveMigrationData");

    public static string DefaultBackupRoot => Path.Combine(SettingsManager.AppDirectoryPath, "SaveMigrationBackups");

    public string BackupRoot { get; }

    public string? DefaultSourceLayout { get; }

    public static SaveConversionService CreateForTarget(SaveCheckpointRegistry registry, string targetId) => new(
        () => SaveMigrator.ForTarget(registry, targetId),
        DefaultBackupRoot,
        MainWindow.IsGameRunning,
        registry.PreviousRelease(registry.Get(targetId))?.Label);

    /// <summary>The modinfo.json version of the normal (offline) Reimagined mod, using the same folders as the save path.</summary>
    internal static string? ReadInstalledReimaginedVersion(InstallationProfile profile)
    {
        var installDirectory = profile.InstallDirectory;
        if (profile.Type == InstallationType.D2RMM || string.IsNullOrWhiteSpace(installDirectory))
        {
            return null;
        }

        return new[] { Path.Combine(installDirectory, "mods", "Reimagined"), NormalModInstallationService.NormalModRoot(installDirectory) }
            .Select(NormalModInstallationService.FindModInfo)
            .Where(path => path is not null && NormalModInstallationService.HasNormalSavePath(path))
            .Select(path => ReadModInfoString(path!, "version"))
            .FirstOrDefault(version => !string.IsNullOrWhiteSpace(version));
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
        var savePath = new[] { Path.Combine(installDirectory, "mods", "Reimagined"), NormalModInstallationService.NormalModRoot(installDirectory) }
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
    /// <paramref name="overrides"/> is only passed for one explicitly requested Force Conversion; it never relaxes structural checks.
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

            var result = migrator.Migrate(path, stagingDirectory, overrides);
            var output = result.OutputPath is { } outputPath ? File.ReadAllBytes(outputPath) : null;
            var report = new StringBuilder()
                .AppendLine($"{target} read check: {targetCheck}")
                .Append(overrides?.AcceptUnresolvedIdentities == true ? "Force Conversion (Experimental) was explicitly requested for this analysis." + Environment.NewLine : "")
                .Append(overrides?.SourceLayout is { } chosenSource ? $"Source layout {chosenSource} was chosen by the user." + Environment.NewLine : "")
                .AppendLine()
                .Append(SaveMigrationReport.Write(result))
                .ToString();
            var risks = result.References.Where(reference => reference.Blocking).Select(reference => DescribeRisk(reference, target)).Distinct().ToList();

            SaveConversionPlan Create(SaveConversionState state, string operation, string summary, string? blockingReason, byte[]? converted,
                SaveForceOption? force = null) => new()
            {
                SourcePath = path,
                Kind = kind,
                State = state,
                Operation = operation,
                Issues = state == SaveConversionState.AlreadyCompatible ? [] : GroupIssues(result),
                Report = report,
                SourceSha256 = sha256,
                TargetLabel = target,
                SourceLabel = result.SourceLayout,
                Summary = summary,
                BlockingReason = blockingReason,
                IssueGroups = state == SaveConversionState.AlreadyCompatible ? [] : CategorizeIssues(result),
                ForceOption = force,
                ConvertedBytes = converted,
                EmbeddedName = embeddedName,
                SidecarPath = sidecar
            };

            if (targetFits && (output is null || output.AsSpan().SequenceEqual(data)))
            {
                return Create(SaveConversionState.AlreadyCompatible,
                    $"Already compatible: the file reads completely with the {target} definitions. No conversion is needed.",
                    "This save is already compatible with the selected Reimagined version.", null, null);
            }

            if (targetFits && overrides?.SourceLayout is null)
            {
                var reason = $"The file reads completely both as {target} and as {result.SourceLayout}, and converting it would change it. "
                             + "The launcher cannot tell which version made it.";
                return Create(SaveConversionState.Unresolved, $"Not converted: {reason} The file is left untouched.",
                    "The launcher cannot tell which Reimagined version this save was made with. Review the details before continuing.", reason, null,
                    new SaveForceOption(SaveForceKind.ChooseSourceLayout, [result.SourceLayout!], risks, TargetAlsoFits: true));
            }

            var layouts = $"{result.SourceLayout ?? "unidentified layout"} (version {result.SourceVersion}) to {target} (version {SaveMigrator.TargetVersion})";
            switch (result.Status)
            {
                case MigrationStatus.Verified:
                    return Create(SaveConversionState.Verified, $"Convert {layouts}. The output passed every verification check.",
                        $"Your save can be converted. The original will be backed up. It converts from {layouts}.", null, output);
                case MigrationStatus.NeedsManualInspection:
                    return Create(SaveConversionState.VerifiedWithWarnings,
                        $"Convert {layouts}. The output passed verification, but some historical references need your attention.",
                        "Some older items have unusual definitions. Their saved properties will be preserved, but their behavior may differ.", null, output);
                case MigrationStatus.Experimental:
                    return Create(SaveConversionState.Experimental,
                        $"EXPERIMENTAL forced conversion of {layouts}. Every structural check passed; "
                        + (overrides?.SourceLayout is { } manual ? $"the source layout {manual} was chosen manually; " : "")
                        + $"{risks.Count} unresolved item identit(ies) were accepted and are not verified.",
                        risks.Count == 0
                            ? $"Converts from {result.SourceLayout} (chosen by you) to {target}. The launcher could not confirm the source version itself."
                            : "Force Conversion prepared an experimental output that passed every structural check. "
                              + "Some items may appear under different identities or behave unexpectedly.", null, output,
                        new SaveForceOption(overrides?.SourceLayout is null ? SaveForceKind.AcceptUnresolvedIdentities : SaveForceKind.ChooseSourceLayout,
                            overrides?.SourceLayout is { } chosen ? [chosen] : [], risks));
            }

            if (overrides?.AcceptUnresolvedIdentities != true && result.Blockers.Count == 0 && result.RejectedOutputPath is not null
                && result.Checks.Count > 0 && result.Checks.All(check => check.Passed) && risks.Count > 0)
            {
                var blocking = result.References.Count(reference => reference.Blocking);
                var reason = $"{blocking} saved item reference(s) have no reliable match in {target}: {string.Join(" ", risks.Take(3))}"
                             + (risks.Count > 3 ? $" ({risks.Count - 3} more in the full report.)" : "")
                             + $" All {result.Checks.Count} verification checks passed; the conversion is withheld only because of this.";
                return Create(SaveConversionState.Unresolved, $"Not converted: {reason}",
                    "One or more items could change identity. Review the affected items before continuing.", reason, null,
                    overrides?.SourceLayout is { } selected
                        ? new SaveForceOption(SaveForceKind.ChooseSourceLayout, [selected], risks)
                        : new SaveForceOption(SaveForceKind.AcceptUnresolvedIdentities, [], risks));
            }

            if (overrides?.SourceLayout is null && result.AmbiguousSourceLayouts.Count > 1 && result.Blockers.Count == 1)
            {
                var reason = $"The file parses completely as {string.Join(" and as ", result.AmbiguousSourceLayouts)}, which read it differently. "
                             + "The launcher cannot tell which version made it.";
                return Create(SaveConversionState.Unresolved, $"Not converted: {reason}",
                    "The launcher cannot tell which Reimagined version this save was made with. Review the details before continuing.", reason, null,
                    new SaveForceOption(SaveForceKind.ChooseSourceLayout,
                        [.. result.AmbiguousSourceLayouts.OrderBy(layout => layout == DefaultSourceLayout ? 0 : 1)], []));
            }

            var problems = result.Blockers.Select(Friendly)
                .Concat(result.Checks.Where(check => !check.Passed).Select(check => $"Verification failed: {check.Name} ({check.Detail})."))
                .ToList();
            var failure = problems.Count == 0
                ? "The conversion could not be verified."
                : problems[0] + (problems.Count > 1 ? $" ({problems.Count - 1} more problem(s) in the full report.)" : "");
            return Create(SaveConversionState.Blocked, "Not converted: the conversion could not be verified, so the file is left untouched.",
                "The save could not be converted or verified safely. Your original was not changed."
                + (overrides?.Any == true ? " Force Conversion cannot override this." : ""),
                failure, null);
        }
        catch (Exception exception)
        {
            return Blocked(path, kind, sha256, $"Analysis failed: {exception.GetType().Name}: {exception.Message}", embeddedName, sidecar);
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
                return $"Use the file name exactly ('{plan.FileStem}') or a different name; changing only letter case is not supported.";
            }

            if (plan.SidecarPath is not null)
            {
                return $"'{Path.GetFileName(plan.SidecarPath)}' is a D2RLoader sidecar for this file. Its format is not documented, so the launcher "
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
    public string CreateBackupBatchDirectory()
    {
        var name = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var path = Path.Combine(BackupRoot, name);
        for (var attempt = 2; Directory.Exists(path); attempt++)
        {
            path = Path.Combine(BackupRoot, $"{name}-{attempt}");
        }

        Directory.CreateDirectory(path);
        return path;
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
            var changesName = !string.IsNullOrEmpty(desiredName) && (desiredName != plan.EmbeddedName || desiredName != plan.FileStem);
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

        if (plan.ConvertedBytes is null && newName is null)
        {
            return new SaveConversionOutcome(true, "No changes needed.", plan.SourcePath);
        }

        if (_isGameRunning())
        {
            return new SaveConversionOutcome(false,
                "Diablo II: Resurrected is running. Close the game and try again; nothing was changed.", plan.SourcePath);
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
                "The file changed after it was analyzed. Close this window and analyze it again; nothing was changed.", plan.SourcePath);
        }

        var bytes = (plan.ConvertedBytes ?? original).ToArray();
        var destination = plan.SourcePath;
        var companions = new List<(string From, string To)>();
        if (newName is not null)
        {
            if (CheckRename(plan, newName) is { } renameProblem)
            {
                return new SaveConversionOutcome(false, $"Rename blocked: {renameProblem} Nothing was changed.", plan.SourcePath);
            }

            if (newName != plan.EmbeddedName && WriteCharacterName(bytes, newName) is { } nameProblem)
            {
                return new SaveConversionOutcome(false, $"Rename blocked: {nameProblem} Nothing was changed.", plan.SourcePath);
            }

            if (newName != plan.FileStem)
            {
                destination = Path.Combine(directory, newName + ".d2s");
                companions = CompanionExtensions
                    .Select(extension => (Path.Combine(directory, plan.FileStem + extension), Path.Combine(directory, newName + extension)))
                    .Where(pair => File.Exists(pair.Item1))
                    .ToList();
            }
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
                $"The written file does not match the verified output. Restore it from {backupBatchDirectory}.", destination, backupBatchDirectory);
        }

        var actions = new List<string>();
        if (plan.ConvertedBytes is not null)
        {
            actions.Add(plan.State switch
            {
                SaveConversionState.Experimental => "Converted experimentally (Force Conversion; overridden identities are not verified)",
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

        return new SaveConversionOutcome(true, $"{string.Join("; ", actions)}. Backup: {backupBatchDirectory}", destination, backupBatchDirectory);
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
            var allowed = index is >= ChecksumOffset and < ChecksumOffset + 4 or >= V104NameOffset and < V104NameOffset + V104NameLength;
            if (before[index] != bytes[index] && !allowed)
            {
                return $"writing the name changed byte {index} outside the name field.";
            }
        }

        if (!ChecksumCalculator.Verify(bytes))
        {
            return "the recalculated checksum does not verify.";
        }

        var save = D2Save.Read(bytes, _migrator.Value.Target.CreateExternalData(version));
        return save.Character.Preview.Name == newName && save.TrailingData.Length == 0 && ReadEmbeddedName(bytes) == newName
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
            manifest.AppendLine("EXPERIMENTAL: written by Force Conversion. Overridden item identities are not verified.")
                .AppendLine($"Full technical report: {reportPath}");
        }

        foreach (var companion in companions)
        {
            var companionBackup = Path.Combine(batchDirectory, Path.GetFileName(companion.From));
            WriteNewFile(companionBackup, File.ReadAllBytes(companion.From));
            manifest.AppendLine($"Companion file: {companion.From} -> {companion.To}");
        }

        manifest
            .AppendLine()
            .AppendLine("To restore: close the game, remove the written file and any renamed companion files, then copy")
            .AppendLine($"the backed-up file(s) from this folder back into {Path.GetDirectoryName(plan.SourcePath)}.");
        WriteNewFile(Path.Combine(batchDirectory, plan.FileName + ".restore.txt"), Encoding.UTF8.GetBytes(manifest.ToString()));
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

        if (attempt.TrailingBytes != 0)
        {
            problems.Add($"{attempt.TrailingBytes} trailing byte(s)");
        }

        if (undefined.Count > 0)
        {
            problems.Add($"undefined stat ids [{string.Join(", ", undefined)}]");
        }

        if (header.ChecksumValid == false || header.DeclaredSize is { } declared && declared != header.ActualSize)
        {
            problems.Add("invalid checksum or declared size");
        }

        return problems.Count == 0
            ? (true, $"complete parse, {attempt.AllItems.Count()} item(s)")
            : (false, $"parses, but {string.Join(", ", problems)}");
    }

    /// <summary>Counts per category; blocking groups first. Individual technical lines stay in the report.</summary>
    private static List<SaveIssueGroup> CategorizeIssues(SaveMigrationResult result)
    {
        var entries = new List<(SaveIssueCategory Category, bool Blocking, string? Item)>();
        entries.AddRange(result.Blockers.Select(blocker => (SaveIssueCategory.SaveFormat, true, ItemOf(blocker))));
        entries.AddRange(result.Checks.Where(check => !check.Passed).Select(_ => (SaveIssueCategory.Verification, true, (string?)null)));
        foreach (var reference in result.References)
        {
            if (reference.Blocking)
            {
                entries.Add((SaveIssueCategory.ItemIdentity, !result.Overrides.AcceptUnresolvedIdentities, reference.ItemPath));
            }
            else if (reference.Outcome is ReferenceOutcome.PreservedWithWarning or ReferenceOutcome.ManualAttention)
            {
                entries.Add((reference.Kind is TableKind.MagicPrefix or TableKind.MagicSuffix
                    ? SaveIssueCategory.HistoricalModifiers
                    : reference.Outcome == ReferenceOutcome.ManualAttention ? SaveIssueCategory.ItemIdentity : SaveIssueCategory.UnknownReferences,
                    false, reference.ItemPath));
            }
        }

        foreach (var warning in result.Warnings.Where(warning => !warning.StartsWith("Experimental:", StringComparison.Ordinal)))
        {
            var category = warning.Contains("maps by identity", StringComparison.Ordinal) || warning.StartsWith("Registered identity mapping", StringComparison.Ordinal)
                ? SaveIssueCategory.ItemIdentity
                : warning.Contains("conversion changed", StringComparison.Ordinal) || warning.Contains("not marked Saved", StringComparison.Ordinal)
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

        static string? ItemOf(string line) => ItemPathPrefix.Match(line) is { Success: true } match ? match.Value : null;
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
            .Concat(result.Checks.Where(check => !check.Passed).Select(check => $"Verification failed: {check.Name}: {check.Detail}"))
            .Concat(result.Warnings.Select(warning => "Warning: " + ItemPathPrefix.Replace(warning, "")))
            .Concat(result.References
                .Where(reference => reference.Blocking || reference.Outcome is ReferenceOutcome.PreservedWithWarning or ReferenceOutcome.ManualAttention)
                .Select(reference => $"{(reference.Blocking ? "Blocking" : reference.Outcome == ReferenceOutcome.ManualAttention ? "Needs attention" : "Preserved with warning")}: "
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
            return document.RootElement.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
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
               || normalizedPath.StartsWith(normalizedDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
