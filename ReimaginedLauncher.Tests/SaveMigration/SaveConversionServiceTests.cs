using System.Security.Cryptography;
using D2SSharp;
using D2SSharp.Enums;
using D2SSharp.Model;
using ReimaginedLauncher.SaveMigration;
using ReimaginedLauncher.Utilities;
using ReimaginedLauncher.Utilities.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace ReimaginedLauncher.Tests.SaveMigration;

public sealed class SaveConversionServiceTests(ITestOutputHelper testOutput) : IDisposable
{
    private static readonly Lazy<InstalledGameData> InstalledTarget = new(() => SaveMigrationFixtures.InstalledTarget);

    private static readonly Lazy<SaveMigrator> Migrator = new(() =>
        SaveMigrationFixtures.CreateMigrator(InstalledTarget.Value));

    private readonly string _root = Path.Combine(Path.GetTempPath(), "reimagined-save-conversion-tests",
        Guid.NewGuid().ToString("N"));

    private bool _gameRunning;

    private string SaveDirectory => Path.Combine(_root, "saves");
    private string StagingDirectory => Path.Combine(_root, "staging");
    private string BackupRoot => Path.Combine(_root, "backups");

    [Theory]
    [InlineData("FuckBenFuckingF.d2s", true)]
    [InlineData("basetolabpr.d2s", false)]
    [InlineData("Popsicle.d2s", false)]
    [InlineData("BonesMcPimp.d2s", false)]
    [InlineData("WereSorc.d2s", false)]
    [InlineData("BruhII.d2s", false)]
    [InlineData("ModernSharedStashSoftCoreV2.d2i", false)]
    public void CurrentSavesAreLeftAloneAndConvertedSavesAreNotConvertedAgain(string fixture, bool alreadyCompatible)
    {
        var service = CreateService();
        var path = CopyFixture(fixture);
        var original = File.ReadAllBytes(path);

        var plan = service.Plan(path, StagingDirectory);
        Log(plan);
        if (alreadyCompatible)
        {
            Assert.Equal(SaveConversionState.AlreadyCompatible, plan.State);
            Assert.Null(plan.ConvertedBytes);
            Assert.True(service.Apply(plan, null, service.CreateBackupBatchDirectory()).Succeeded);
        }
        else
        {
            Assert.Contains(plan.State,
                new[] { SaveConversionState.Verified, SaveConversionState.VerifiedWithWarnings });
            var outcome = service.Apply(plan, null, service.CreateBackupBatchDirectory());
            Assert.True(outcome.Succeeded, outcome.Message);
            Assert.Equal(plan.ConvertedBytes, File.ReadAllBytes(path));
            Assert.Equal(original, File.ReadAllBytes(Path.Combine(outcome.BackupDirectory!, fixture)));
            Assert.True(File.Exists(Path.Combine(outcome.BackupDirectory!, fixture + ".restore.txt")));
        }

        var converted = File.ReadAllBytes(path);
        var replan = service.Plan(path, Path.Combine(StagingDirectory, "again"));
        Log(replan);
        Assert.Equal(SaveConversionState.AlreadyCompatible, replan.State);
        Assert.True(service.Apply(replan, null, service.CreateBackupBatchDirectory()).Succeeded);
        Assert.Equal(converted, File.ReadAllBytes(path));
        Assert.Equal(alreadyCompatible ? 0 : 2,
            Directory.GetFiles(BackupRoot, "*", SearchOption.AllDirectories).Length);
        Assert.Equal(SHA256.HashData(original), SHA256.HashData(File.ReadAllBytes(FixturePath(fixture))));
    }

    [Fact]
    public void FindSavesNeedingConversionReportsOnlyConvertibleSavesAndWritesNothing()
    {
        var service = CreateService();
        var compatible = CopyFixture("FuckBenFuckingF.d2s");
        var outdated = CopyFixture("BruhII.d2s");
        var stash = CopyFixture("ModernSharedStashSoftCoreV2.d2i");
        var originals = new[] { compatible, outdated, stash }.ToDictionary(path => path, File.ReadAllBytes);

        var found = service.FindSavesNeedingConversion(SaveDirectory, StagingDirectory);

        Assert.Equal(new[] { outdated, stash }, found);
        foreach (var (path, bytes) in originals)
        {
            Assert.Equal(bytes, File.ReadAllBytes(path));
        }

        Assert.False(Directory.Exists(BackupRoot));
        Assert.Empty(service.FindSavesNeedingConversion(Path.Combine(_root, "missing"), StagingDirectory));
    }

    [Theory]
    [InlineData(SaveConversionState.Verified, null, true)]
    [InlineData(SaveConversionState.VerifiedWithWarnings, null, true)]
    [InlineData(SaveConversionState.Unresolved, SaveForceKind.ChooseSourceLayout, true)]
    [InlineData(SaveConversionState.Unresolved, SaveForceKind.Force, false)]
    [InlineData(SaveConversionState.Unresolved, null, false)]
    [InlineData(SaveConversionState.AlreadyCompatible, null, false)]
    [InlineData(SaveConversionState.AlreadyCompatible, SaveForceKind.ChooseSourceLayout, false)]
    [InlineData(SaveConversionState.Blocked, null, false)]
    [InlineData(SaveConversionState.Experimental, SaveForceKind.Force, false)]
    [InlineData(SaveConversionState.Experimental, SaveForceKind.ChooseSourceLayout, false)]
    public void AutomaticPromptOnlyIncludesVerifiedSavesAndSourceChoices(SaveConversionState state,
        SaveForceKind? forceKind, bool expected)
    {
        var plan = new SaveConversionPlan
        {
            SourcePath = Path.Combine(SaveDirectory, "Hero.d2s"),
            Kind = SaveFileKind.Character,
            State = state,
            Operation = "",
            Issues = [],
            Report = "",
            SourceSha256 = "",
            ForceOption = forceKind is { } kind ? new SaveForceOption(kind, ["3.0.12", "2.1.2"], []) : null
        };

        Assert.Equal(expected, SaveConversionService.NeedsConversion(plan));
    }

    [Fact]
    public void AutomaticPromptIsSuppressedOnlyForTheSameProfileInstallationAndTarget()
    {
        var install = Path.Combine(_root, "install");
        var profile = new InstallationProfile { Type = InstallationType.BattleNet, InstallDirectory = install };
        Assert.False(SaveConversionService.WasAutomaticPromptShown(profile, "target-a"));

        Assert.True(SaveConversionService.RecordAutomaticPromptShown(profile, install, "target-a"));
        var settings = new AppSettings { Profiles = [profile, new InstallationProfile { Type = InstallationType.Steam }] };
        var restored = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(
            System.Text.Json.JsonSerializer.Serialize(settings))!;
        var restoredProfile = restored.Profiles[0];

        Assert.True(SaveConversionService.WasAutomaticPromptShown(restoredProfile, "target-a"));
        Assert.False(SaveConversionService.WasAutomaticPromptShown(restoredProfile, "target-b"));
        var otherProfile = restored.Profiles[1];
        otherProfile.InstallDirectory = install;
        Assert.False(SaveConversionService.WasAutomaticPromptShown(otherProfile, "target-a"));
        restoredProfile.InstallDirectory = Path.Combine(_root, "other-install");
        Assert.False(SaveConversionService.WasAutomaticPromptShown(restoredProfile, "target-a"));
    }

    [Fact]
    public void AutomaticPromptForAnUnchangedInstallationIsStillRecorded()
    {
        var install = Path.Combine(_root, "install");
        var profile = new InstallationProfile
            { Type = InstallationType.BattleNet, InstallDirectory = install + Path.DirectorySeparatorChar };

        Assert.True(SaveConversionService.IsAnalyzedInstallation(profile, $" {install} "));
        Assert.True(SaveConversionService.RecordAutomaticPromptShown(profile, install, "target-a"));
        Assert.True(SaveConversionService.WasAutomaticPromptShown(profile, "target-a"));
    }

    [Fact]
    public void StaleAutomaticPromptCannotRecordSuppressionForAChangedInstallation()
    {
        var analyzedInstall = Path.Combine(_root, "install");
        var newInstall = Path.Combine(_root, "other-install");
        var profile = new InstallationProfile { Type = InstallationType.BattleNet, InstallDirectory = analyzedInstall };
        Assert.True(SaveConversionService.RecordAutomaticPromptShown(profile, analyzedInstall, "target-a"));
        var previousKey = profile.SaveConversionPromptKey;

        profile.InstallDirectory = newInstall;

        Assert.False(SaveConversionService.IsAnalyzedInstallation(profile, analyzedInstall));
        Assert.False(SaveConversionService.RecordAutomaticPromptShown(profile, analyzedInstall, "target-b"));
        Assert.Equal(previousKey, profile.SaveConversionPromptKey);
        Assert.False(SaveConversionService.WasAutomaticPromptShown(profile, "target-a"));
        Assert.False(SaveConversionService.WasAutomaticPromptShown(profile, "target-b"));
    }

    [Fact]
    public void AutomaticPromptSuppressionDoesNotAffectManualConversion()
    {
        var profile = new InstallationProfile
            { Type = InstallationType.BattleNet, InstallDirectory = Path.Combine(_root, "install") };
        Assert.True(SaveConversionService.RecordAutomaticPromptShown(profile, profile.InstallDirectory,
            InstalledTarget.Value.Fingerprint));
        var service = CreateService();
        var path = CopyFixture("BruhII.d2s");
        var original = File.ReadAllBytes(path);

        Assert.True(SaveConversionService.WasAutomaticPromptShown(profile, InstalledTarget.Value.Fingerprint));
        Assert.Equal(new[] { path }, service.FindSavesNeedingConversion(SaveDirectory, StagingDirectory));
        var plan = service.Plan(path, Path.Combine(StagingDirectory, "manual"));
        Assert.Contains(plan.State, new[] { SaveConversionState.Verified, SaveConversionState.VerifiedWithWarnings });
        var outcome = service.Apply(plan, null, service.CreateBackupBatchDirectory());
        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(outcome.BackupDirectory!, "BruhII.d2s")));
    }

    [Fact]
    public void ForceCanRebuildATargetParsingSaveWithoutPreselectingIt()
    {
        var service = CreateService();
        var path = CopyFixture("FuckBenFuckingF.d2s");
        var original = File.ReadAllBytes(path);
        var normal = service.Plan(path, Path.Combine(StagingDirectory, "normal"));
        var normalRow = new SaveConversionRowViewModel(normal, service.CheckRename);
        Assert.Equal(SaveConversionState.AlreadyCompatible, normal.State);
        Assert.Equal("Parses with target", normalRow.StatusLabel);
        Assert.False(normalRow.IsSelected);

        var forced = service.Plan(path, Path.Combine(StagingDirectory, "forced"), new MigrationOverrides(Force: true));
        var row = new SaveConversionRowViewModel(forced, service.CheckRename);
        Assert.True(forced.IsTargetRebuild);
        Assert.Contains("[PASS] Header, declared size and checksum", forced.Report);
        Assert.Contains("[PASS] Reparse with", forced.Report);
        Assert.Contains("installed target definitions", forced.Report);
        Assert.False(row.IsSelected);

        if (forced.ConvertedBytes is { } rebuilt)
        {
            Assert.Equal(SaveConversionState.Experimental, forced.State);
            Assert.True(row.CanSelect);
            row.IsSelected = true;
            Assert.True(row.IsSelected);
            var outcome = service.Apply(forced, null, service.CreateBackupBatchDirectory());
            Assert.True(outcome.Succeeded, outcome.Message);
            Assert.Equal(rebuilt, File.ReadAllBytes(path));
            Assert.True(ChecksumCalculator.Verify(rebuilt));
            Assert.Equal(original, File.ReadAllBytes(Path.Combine(outcome.BackupDirectory!, "FuckBenFuckingF.d2s")));
            Assert.True(File.Exists(Path.Combine(outcome.BackupDirectory!,
                "FuckBenFuckingF.d2s.experimental-report.txt")));
        }
        else
        {
            Assert.Equal(SaveConversionState.AlreadyCompatible, forced.State);
            Assert.Contains("no repair occurred", forced.Operation);
            var outcome = service.Apply(forced, null, service.CreateBackupBatchDirectory());
            Assert.True(outcome.Succeeded, outcome.Message);
            Assert.Contains("no repair occurred", outcome.Message);
            Assert.Equal(original, File.ReadAllBytes(path));
            Assert.Empty(Directory.GetFiles(BackupRoot, "*", SearchOption.AllDirectories));
        }
    }

    [Fact]
    public void TargetReadableSaveWithoutHistoricalSourceIsNotReportedAsConversionFailure()
    {
        var service = CreateService();
        var path = CopyFixture("SoManyBones.d2s");

        var plan = service.Plan(path, Path.Combine(StagingDirectory, "target-readable"));

        Assert.Equal(SaveConversionState.AlreadyCompatible, plan.State);
        Assert.Null(plan.BlockingReason);
        Assert.Null(plan.ConvertedBytes);
        Assert.Contains("no historical source layout decoded the save", plan.Operation);
        Assert.Contains("Historical source detection", plan.Report);
        Assert.Contains("Installed items with 3.0.13 development pre-swap stat encoding: rejected", plan.Report);
        Assert.Contains("wrong bit widths", plan.Report);
        Assert.Contains("No conversion was required or attempted", plan.Report);
        Assert.DoesNotContain("Status: FAILED", plan.Report);
        Assert.DoesNotContain("game compatibility is guaranteed", plan.Summary, StringComparison.OrdinalIgnoreCase);

        var forced = service.Plan(path, Path.Combine(StagingDirectory, "target-readable-force"),
            new MigrationOverrides(Force: true));
        Assert.True(forced.IsTargetRebuild);
        Assert.Contains("installed target definitions", forced.Report);
    }

    [Fact]
    public void IdenticalTargetRoundTripIsReportedAsNoRepair()
    {
        var service = CreateService();
        var path = CopyFixture("FuckBenFuckingF.d2s");
        var normalized = Migrator.Value.Rebuild(path, Path.Combine(StagingDirectory, "normalize"));
        Assert.NotNull(normalized.OutputPath);
        File.WriteAllBytes(path, File.ReadAllBytes(normalized.OutputPath));
        var original = File.ReadAllBytes(path);

        var plan = service.Plan(path, Path.Combine(StagingDirectory, "identical"), new MigrationOverrides(Force: true));

        Assert.Equal(SaveConversionState.AlreadyCompatible, plan.State);
        Assert.True(plan.IsTargetRebuild);
        Assert.Null(plan.ConvertedBytes);
        Assert.Contains("byte-identical", plan.Operation);
        Assert.Contains("no repair occurred", plan.Summary);
        var row = new SaveConversionRowViewModel(plan, service.CheckRename);
        Assert.Equal("No repair", row.StatusLabel);
        Assert.False(row.IsAlreadyCompatible);
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Fact]
    public void TargetParsingDoesNotHideARecoverableHistoricalMigrationWithUncertainties()
    {
        var installed = InstalledTarget.Value;
        var path = CopyFixture("FuckBenFuckingF.d2s");
        var targetDefinition = SaveDataDefinition.FromSnapshot(installed.Snapshot);
        var targetAttempt = SaveInspector.Inspect(path, [targetDefinition], []).Attempts[0];
        Assert.True(targetAttempt.Succeeded, targetAttempt.Error);
        var usedStat = targetAttempt.AllItems.SelectMany(item => item.Stats).First();
        var sourceDirectory = CopyInstalledFixtureToCustomDirectory();
        var stat = Assert.Single(TableSchema.Extract(TableKind.ItemStatCost,
                TableSnapshot.Load(Path.Combine(sourceDirectory, "itemstatcost.txt"))),
            definition => definition.SaveId == usedStat.Id);
        SetTableCell(Path.Combine(sourceDirectory, "itemstatcost.txt"), "Stat", stat.Key, "Save Add", "2000000000");

        var sourceSnapshot = GameDataSnapshot.Load("Synthetic historical source", sourceDirectory);
        var source = SaveDataDefinition.WithAppendedStats(sourceSnapshot.Label, sourceSnapshot,
            installed.Snapshot, installed.Snapshot);
        var comparison = TableComparer.Compare(source.ItemTables, installed.Snapshot);
        var historicalAttempt = SaveInspector.Inspect(path, [source], [comparison]).Attempts[0];
        Assert.True(historicalAttempt.Succeeded, historicalAttempt.Error);
        var migrator = new SaveMigrator([source], targetDefinition, [comparison]);
        var service = new SaveConversionService(() => migrator, BackupRoot, () => _gameRunning,
            installedTarget: installed);

        var plan = service.Plan(path, Path.Combine(StagingDirectory, "historical"));

        Assert.Equal(SaveConversionState.AlreadyCompatible, plan.State);
        Assert.Equal(source.Label, plan.HistoricalAlternative);
        Assert.Equal(SaveForceKind.ChooseSourceLayout, plan.ForceOption?.Kind);
        Assert.Contains(source.Label, plan.ForceOption!.SourceLayouts);
        Assert.Contains("recoverable historical interpretation", plan.Operation);
        var row = new SaveConversionRowViewModel(plan, service.CheckRename);
        Assert.Null(row.SelectedSourceChoice?.Layout);
        Assert.Equal(source.Label, row.SourceChoices[1].Layout);

        var chosen = service.Plan(path, Path.Combine(StagingDirectory, "chosen"),
            new MigrationOverrides(Force: true, SourceLayout: source.Label));
        Assert.Equal(SaveConversionState.Experimental, chosen.State);
        Assert.Equal(source.Label, chosen.SourceLabel);
        Assert.NotNull(chosen.ConvertedBytes);
    }

    [Theory]
    [InlineData("WereSorc")]
    [InlineData("Frostbite")]
    public void NameMismatchIsCorrectedToTheChosenName(string chosenName)
    {
        var service = CreateService();
        var path = CopyFixture("WereSorc.d2s");
        File.WriteAllBytes(Path.Combine(SaveDirectory, "WereSorc.key"), [1, 2, 3]);
        var plan = service.Plan(path, StagingDirectory);

        Assert.True(plan.HasNameMismatch);
        Assert.Equal(("WereSorc", "SorceressTest"), (plan.FileStem, plan.EmbeddedName));
        Assert.Null(service.CheckRename(plan, chosenName));

        var outcome = service.Apply(plan, chosenName, service.CreateBackupBatchDirectory());
        testOutput.WriteLine(outcome.Message);
        Assert.True(outcome.Succeeded, outcome.Message);
        var written = File.ReadAllBytes(Path.Combine(SaveDirectory, chosenName + ".d2s"));
        Assert.True(ChecksumCalculator.Verify(written));
        var save = D2Save.Read(written, Migrator.Value.Target.CreateExternalData(SaveMigrator.TargetVersion));
        Assert.Equal(chosenName, save.Character.Preview.Name);
        Assert.Empty(save.TrailingData);
        // Only the checksum and the 96-byte v105 preview name field may differ from the verified conversion.
        Assert.All(Enumerable.Range(0, written.Length).Where(index => written[index] != plan.ConvertedBytes![index]),
            index => Assert.True(index is >= 12 and < 16 or >= 299 and < 395, $"byte {index} changed"));
        Assert.Equal(chosenName != "WereSorc", !File.Exists(path));
        Assert.True(File.Exists(Path.Combine(SaveDirectory, chosenName + ".key")));
        Assert.Equal(File.ReadAllBytes(FixturePath("WereSorc.d2s")),
            File.ReadAllBytes(Path.Combine(outcome.BackupDirectory!, "WereSorc.d2s")));

        var replan = service.Plan(Path.Combine(SaveDirectory, chosenName + ".d2s"),
            Path.Combine(StagingDirectory, "again"));
        Assert.Equal(SaveConversionState.AlreadyCompatible, replan.State);
        Assert.False(replan.HasNameMismatch);
    }

    [Theory]
    [InlineData("WereSorc", true)]
    [InlineData("Were-Sorc", true)]
    [InlineData("무병장수바바", true)]
    [InlineData("A", false)]
    [InlineData("SixteenLettersXy", false)]
    [InlineData("Were Sorc", false)]
    [InlineData("WereSorc2", false)]
    [InlineData("-WereSorc", false)]
    [InlineData("WereSorc_", false)]
    [InlineData("Were-So_rc", false)]
    public void CharacterNamesFollowTheGameRules(string name, bool valid)
    {
        Assert.Equal(valid, SaveConversionService.ValidateCharacterName(name) is null);
    }

    [Fact]
    public void RenameCollisionsSidecarsRunningGameAndChangedFilesChangeNothing()
    {
        var service = CreateService();
        var path = CopyFixture("WereSorc.d2s");
        CopyFixture("Popsicle.d2s");
        Assert.Contains("already", service.CheckRename(service.Plan(path, StagingDirectory), "Popsicle"));

        File.WriteAllBytes(Path.ChangeExtension(path, ".d2rl"), [7]);
        var plan = service.Plan(path, Path.Combine(StagingDirectory, "sidecar"));
        Assert.Contains("sidecar", service.CheckRename(plan, "Frostbite"));
        Assert.Null(service.CheckRename(plan, "WereSorc"));
        Assert.False(service.Apply(plan, "Frostbite", service.CreateBackupBatchDirectory()).Succeeded);

        _gameRunning = true;
        Assert.False(service.Apply(plan, null, service.CreateBackupBatchDirectory()).Succeeded);
        _gameRunning = false;

        File.AppendAllText(path, "x");
        var changed = File.ReadAllBytes(path);
        Assert.False(service.Apply(plan, null, service.CreateBackupBatchDirectory()).Succeeded);
        Assert.Equal(changed, File.ReadAllBytes(path));
        Assert.Empty(Directory.GetFiles(BackupRoot, "*", SearchOption.AllDirectories));
        Assert.False(File.Exists(Path.Combine(SaveDirectory, "Frostbite.d2s")));
    }

    [Fact]
    public void WarningsAreGroupedByCategoryWithoutBlockingTheConversion()
    {
        var plan = CreateService().Plan(CopyFixture("Popsicle.d2s"), StagingDirectory);
        Log(plan);

        Assert.Equal(SaveConversionState.VerifiedWithWarnings, plan.State);
        Assert.NotNull(plan.ConvertedBytes);
        Assert.Null(plan.BlockingReason);
        Assert.Null(plan.ForceOption);
        Assert.NotEmpty(plan.IssueGroups);
        Assert.All(plan.IssueGroups, group => Assert.False(group.Blocking));
        Assert.Contains(plan.IssueGroups, group => group.Category == SaveIssueCategory.UnknownReferences);
        Assert.Equal(plan.IssueGroups.Count, plan.IssueGroups.Select(group => group.Category).Distinct().Count());
    }

    [Fact]
    public void AnUnresolvedIdentityIsPreservedAsANonblockingWarning()
    {
        // Without the registered rename, BruhII's Life & Death keeps id 1349, which the installed target names Iniquity Razor.
        var registry = SaveMigrationFixtures.LoadRegistry() with { IdentityMappings = [] };
        var installed = InstalledTarget.Value;
        var service = new SaveConversionService(() => SaveMigrator.ForInstalledTarget(registry, installed), BackupRoot,
            () => _gameRunning,
            installedTarget: installed);
        var path = CopyFixture("BruhII.d2s");
        var original = File.ReadAllBytes(path);

        var plan = service.Plan(path, StagingDirectory);
        Log(plan);
        Assert.Equal(SaveConversionState.VerifiedWithWarnings, plan.State);
        Assert.NotNull(plan.ConvertedBytes);
        Assert.Null(plan.BlockingReason);
        Assert.Null(plan.ForceOption);
        var identity = Assert.Single(plan.IssueGroups, group => group.Category == SaveIssueCategory.ItemIdentity);
        Assert.False(identity.Blocking);
        Assert.Contains("1349", plan.Report);
        Assert.Contains("Life & Death", plan.Report);
        Assert.Contains("Iniquity Razor", plan.Report);
        Assert.Contains("preserved unchanged", plan.Report);
        Assert.Contains("[PASS] Item, socket and property counts", plan.Report);
        Assert.Contains("[PASS] Reparse with", plan.Report);

        var source = SaveDataDefinition.FromSnapshot(
            KnownSnapshots.Reimagined212.Load(SaveMigrationFixtures.DataDirectory));
        var target = SaveDataDefinition.FromSnapshot(installed.Snapshot);
        var input = D2Save.Read(original, source.CreateExternalData(BitConverter.ToUInt32(original, 4)));
        var converted = D2Save.Read(plan.ConvertedBytes!, target.CreateExternalData(SaveMigrator.TargetVersion));
        var inputItem = Assert.Single(input.Items, item => item.Quality == ItemQuality.Unique
                                                           && ((SetUniqueQualityData)item.QualityData!)
                                                           .SetUniqueFileIndex == 1349);
        var convertedItem = Assert.Single(converted.Items, item => item.Quality == ItemQuality.Unique
                                                                   && ((SetUniqueQualityData)item.QualityData!)
                                                                   .SetUniqueFileIndex == 1349);
        Assert.Equal(inputItem.Stats.Select(stat => (source.Stat((int)stat.Id)!.Key, stat.Layer, stat.Value)),
            convertedItem.Stats.Select(stat => (target.Stat((int)stat.Id)!.Key, stat.Layer, stat.Value)));

        var outcome = service.Apply(plan, null, service.CreateBackupBatchDirectory());
        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Equal(plan.ConvertedBytes, File.ReadAllBytes(path));
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(outcome.BackupDirectory!, "BruhII.d2s")));
    }

    [Fact]
    public void NormalCharacterConversionBacksUpExistingCompanionsByteForByte()
    {
        var service = CreateService();
        var path = CopyFixture("WereSorc.d2s");
        var extensions = new[]
            { ".d2rl", ".key", ".keyo", ".ctl", ".ctlo", ".map", ".ma0", ".ma1", ".ma2", ".ma3" };
        var companionBytes = extensions.ToDictionary(extension => extension,
            extension => System.Text.Encoding.ASCII.GetBytes($"companion:{extension}"));
        foreach (var (extension, bytes) in companionBytes)
        {
            File.WriteAllBytes(Path.Combine(SaveDirectory, "WereSorc" + extension), bytes);
        }

        var plan = service.Plan(path, StagingDirectory);
        Assert.NotNull(plan.ConvertedBytes);
        var outcome = service.Apply(plan, null, service.CreateBackupBatchDirectory());

        Assert.True(outcome.Succeeded, outcome.Message);
        var manifest = File.ReadAllText(Path.Combine(outcome.BackupDirectory!, "WereSorc.d2s.restore.txt"));
        foreach (var (extension, bytes) in companionBytes)
        {
            var companionName = "WereSorc" + extension;
            Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(SaveDirectory, companionName)));
            Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(outcome.BackupDirectory!, companionName)));
            Assert.Contains($"Companion file: {Path.Combine(SaveDirectory, companionName)} (left unchanged)", manifest);
        }
    }

    [Fact]
    public void FailedCompanionBackupLeavesOriginalSaveAndCompanionsUnchanged()
    {
        var service = CreateService();
        var path = CopyFixture("WereSorc.d2s");
        var original = File.ReadAllBytes(path);
        var sidecar = new byte[] { 1, 2, 3, 4 };
        var key = new byte[] { 5, 6, 7, 8 };
        File.WriteAllBytes(Path.ChangeExtension(path, ".d2rl"), sidecar);
        File.WriteAllBytes(Path.ChangeExtension(path, ".key"), key);
        var plan = service.Plan(path, StagingDirectory);
        var backupBatch = service.CreateBackupBatchDirectory();
        var collision = new byte[] { 9, 10, 11 };
        File.WriteAllBytes(Path.Combine(backupBatch, "WereSorc.key"), collision);

        var outcome = service.Apply(plan, null, backupBatch);

        Assert.False(outcome.Succeeded);
        Assert.Contains("original file was left in place", outcome.Message);
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Equal(sidecar, File.ReadAllBytes(Path.ChangeExtension(path, ".d2rl")));
        Assert.Equal(key, File.ReadAllBytes(Path.ChangeExtension(path, ".key")));
        Assert.Equal(collision, File.ReadAllBytes(Path.Combine(backupBatch, "WereSorc.key")));
    }

    [Fact]
    public void ForceConversionCannotOverrideStructuralFailures()
    {
        var service = CreateService();
        var path = CopyFixture("WereSorc.d2s");
        var corrupt = File.ReadAllBytes(path);
        corrupt[^1] ^= 0xFF;
        File.WriteAllBytes(path, corrupt);

        var plan = service.Plan(path, StagingDirectory);
        Assert.Equal(SaveConversionState.Blocked, plan.State);
        Assert.Null(plan.ForceOption);
        Assert.Contains(plan.IssueGroups, group => group is { Category: SaveIssueCategory.SaveFormat, Blocking: true });

        var forced = service.Plan(path, Path.Combine(StagingDirectory, "forced"),
            new MigrationOverrides(Force: true, SourceLayout: "2.1.2"));
        Assert.Equal(SaveConversionState.Blocked, forced.State);
        Assert.Null(forced.ConvertedBytes);
        Assert.False(service.Apply(forced, null, service.CreateBackupBatchDirectory()).Succeeded);
        Assert.Equal(corrupt, File.ReadAllBytes(path));

        // A manually chosen source layout must still parse the file completely.
        var unknown = service.Plan(CopyFixture("Popsicle.d2s"), Path.Combine(StagingDirectory, "unknown"),
            new MigrationOverrides(Force: true, SourceLayout: "not a registered layout"));
        Assert.Equal(SaveConversionState.Blocked, unknown.State);
        Assert.Null(unknown.ConvertedBytes);
        Assert.Empty(Directory.GetFiles(BackupRoot, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void TrailingExtensionDataIsCarriedOverByteForByte()
    {
        var service = CreateService();
        var path = CopyFixture("Popsicle.d2s");
        // Opaque bytes after the standard sections, like a loader's extension block; content and length are arbitrary.
        byte[] trailer =
            [.. "EXT1"u8, 1, 0, 2, 0, .. Enumerable.Range(0, 37).Select(value => (byte)(value * 7)), .. "ABCD"u8];
        File.WriteAllBytes(path, WithValidHeader([.. File.ReadAllBytes(path), .. trailer]));

        var plan = service.Plan(path, StagingDirectory);
        Log(plan);
        Assert.Contains(plan.State, new[] { SaveConversionState.Verified, SaveConversionState.VerifiedWithWarnings });
        Assert.Contains("opaque extension data", plan.Report);
        Assert.Contains("[PASS] Trailing extension data preserved byte-for-byte", plan.Report);
        var output = D2Save.Read(plan.ConvertedBytes!,
            Migrator.Value.Target.CreateExternalData(SaveMigrator.TargetVersion));
        Assert.Equal(trailer, output.TrailingData);

        Assert.True(service.Apply(plan, null, service.CreateBackupBatchDirectory()).Succeeded);
        Assert.Equal(SaveConversionState.AlreadyCompatible,
            service.Plan(path, Path.Combine(StagingDirectory, "again")).State);
    }

    [Fact]
    public void ADamagedChecksumIsRepairedOnlyByAnExperimentalForceConversion()
    {
        var service = CreateService();
        var path = CopyFixture("Popsicle.d2s");
        var damaged = File.ReadAllBytes(path);
        damaged[12] ^= 0x5A;
        File.WriteAllBytes(path, damaged);

        var plan = service.Plan(path, StagingDirectory);
        Log(plan);
        Assert.Equal((SaveConversionState.Unresolved, SaveForceKind.Force), (plan.State, plan.ForceOption?.Kind));
        Assert.Null(plan.ConvertedBytes);
        Assert.Contains(plan.ForceOption!.Risks, risk => risk.Contains("checksum mismatch"));
        Assert.Equal("Needs Force", new SaveConversionRowViewModel(plan, service.CheckRename).StatusLabel);

        var forced = service.Plan(path, Path.Combine(StagingDirectory, "forced"), new MigrationOverrides(Force: true));
        Log(forced);
        Assert.Equal(SaveConversionState.Experimental, forced.State);
        Assert.True(ChecksumCalculator.Verify(forced.ConvertedBytes!));
        Assert.Contains("does not prove the source data is uncorrupted", forced.Report);
        Assert.Contains("EXPERIMENTAL", forced.Report);
        var outcome = service.Apply(forced, null, service.CreateBackupBatchDirectory());
        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Equal(forced.ConvertedBytes, File.ReadAllBytes(path));
        Assert.Equal(damaged, File.ReadAllBytes(Path.Combine(outcome.BackupDirectory!, "Popsicle.d2s")));
    }

    [Fact]
    public void AForcedOutputThatFailsACriticalCheckIsKeptAsARecoveryCandidateAndNeverApplied()
    {
        var service = CreateService();
        var earlier = service.Plan(CopyFixture("BruhII.d2s"), Path.Combine(StagingDirectory, "earlier"));
        Assert.True(service.Apply(earlier, null, service.CreateBackupBatchDirectory()).Succeeded);
        var existingBackups = Directory.GetFiles(BackupRoot, "*", SearchOption.AllDirectories)
            .ToDictionary(file => file, File.ReadAllBytes);

        // A decodable save whose opaque trailer pushes it past the retail size limit: the source is recoverable, but any output that
        // preserves the trailer must fail the critical output header check.
        var path = CopyFixture("Popsicle.d2s");
        var trailer = Enumerable.Range(0, SaveMigrator.RetailCharacterSizeLimit + 1).Select(value => (byte)(value * 31))
            .ToArray();
        var oversized = WithValidHeader([.. File.ReadAllBytes(path), .. trailer]);
        File.WriteAllBytes(path, oversized);

        var forced = service.Plan(path, Path.Combine(StagingDirectory, "forced"), new MigrationOverrides(Force: true));
        Log(forced);
        var row = new SaveConversionRowViewModel(forced, service.CheckRename);
        Assert.Equal(SaveConversionState.Blocked, forced.State);
        Assert.Null(forced.ConvertedBytes);
        Assert.Equal((false, false, "Blocked"), (row.CanSelect, row.IsSelected, row.StatusLabel));
        Assert.Contains("[FAIL, critical] Header, declared size and checksum", forced.Report);
        Assert.Contains($"exceeds the retail {SaveMigrator.RetailCharacterSizeLimit}-byte limit",
            forced.BlockingReason);
        Assert.DoesNotContain("VERIFIED", forced.Report);
        Assert.DoesNotContain("EXPERIMENTAL -", forced.Report);

        var candidate = forced.RecoveryCandidatePath;
        Assert.NotNull(candidate);
        Assert.Equal(Path.Combine(BackupRoot, "RecoveryCandidates"),
            Path.GetDirectoryName(Path.GetDirectoryName(candidate)));
        Assert.Equal("Popsicle.d2s.recovery-candidate", Path.GetFileName(candidate));
        Assert.Contains(candidate, forced.BlockingReason);
        var candidateBytes = File.ReadAllBytes(candidate);
        Assert.True(candidateBytes.Length > SaveMigrator.RetailCharacterSizeLimit);
        Assert.Equal(trailer, candidateBytes[^trailer.Length..]);
        var candidateReport = File.ReadAllText(candidate + ".report.txt");
        Assert.StartsWith("EXPERIMENTAL RECOVERY CANDIDATE - FAILED A CRITICAL CHECK AND WAS NOT APPLIED.",
            candidateReport);
        Assert.Contains("[FAIL, critical] Header, declared size and checksum", candidateReport);

        Assert.False(service.Apply(forced, null, service.CreateBackupBatchDirectory()).Succeeded);
        Assert.Equal(oversized, File.ReadAllBytes(path));
        Assert.Equal(["BruhII.d2s", "Popsicle.d2s"],
            Directory.GetFiles(SaveDirectory).Select(Path.GetFileName).Order());
        foreach (var (file, bytes) in existingBackups)
        {
            Assert.Equal(bytes, File.ReadAllBytes(file));
        }

        Assert.Equal([candidate, candidate + ".report.txt"],
            Directory.GetFiles(BackupRoot, "*", SearchOption.AllDirectories).Except(existingBackups.Keys).Order());
    }

    [Fact]
    public void ConfidentSourcesOverrideThePreviousReleaseDefaultAndEligibleFilesArePreselected()
    {
        var service = new SaveConversionService(() => Migrator.Value, BackupRoot, () => _gameRunning,
            defaultSourceLayout: "3.0.12");
        var plans = new[] { "BruhII.d2s", "BonesMcPimp.d2s", "FuckBenFuckingF.d2s" }
            .Select(fixture => service.Plan(CopyFixture(fixture), Path.Combine(StagingDirectory, fixture)))
            .ToList();
        var rows = plans.Select(plan => new SaveConversionRowViewModel(plan, service.CheckRename)).ToList();
        foreach (var row in rows)
        {
            testOutput.WriteLine($"{row.FileName}: {row.StatusLabel} - {row.Explanation}");
        }

        Assert.Equal(["2.1.2", "3.0.12 with installed appended stats"], plans.Take(2).Select(plan => plan.SourceLabel));
        Assert.All(rows, row => Assert.False(row.IsSourceUncertain));
        Assert.Equal([true, true, false], rows.Select(row => row.IsSelected));
        Assert.Equal(("Parses with target", false), (rows[2].StatusLabel, rows[2].CanSelect));
        rows[2].MarkSkipped();
        Assert.Equal(("Skipped", "No conversion needed."), (rows[2].StatusLabel, rows[2].Explanation));
    }

    [Fact]
    public void AnUncertainSourceKeepsTheDefaultFirstAndWaitsForConfirmation()
    {
        var plan = new SaveConversionPlan
        {
            SourcePath = Path.Combine(SaveDirectory, "Ambiguous.d2s"),
            Kind = SaveFileKind.Character,
            State = SaveConversionState.Unresolved,
            Operation = "Not converted",
            Issues = [],
            Report = "",
            SourceSha256 = "",
            TargetLabel = "Current installed Reimagined",
            ForceOption = new SaveForceOption(SaveForceKind.ChooseSourceLayout, ["3.0.12", "2.1.2"], [],
                TargetAlsoFits: true)
        };
        var row = new SaveConversionRowViewModel(plan, (_, _) => null);

        Assert.Equal(["3.0.12", "2.1.2", null], row.SourceChoices.Select(choice => choice.Layout));
        Assert.Equal("3.0.12", row.SelectedSourceChoice?.Layout);
        Assert.Equal((false, false, "Source uncertain"), (row.IsSelected, row.IsProblem, row.StatusLabel));

        row.SelectedSourceChoice = row.SourceChoices[^1];
        Assert.Equal(("Parses with target", false), (row.StatusLabel, row.CanSelect));
    }

    [Fact]
    public void ForceConversionNeverUnblocksAZeroFilledSave()
    {
        var service = CreateService();
        Directory.CreateDirectory(SaveDirectory);
        var path = Path.Combine(SaveDirectory, "LilSorcy.d2s");
        File.WriteAllBytes(path, new byte[9008]);

        var forced = service.Plan(path, StagingDirectory, new MigrationOverrides(Force: true));
        var row = new SaveConversionRowViewModel(forced, service.CheckRename);
        testOutput.WriteLine($"{row.StatusLabel} - {row.Explanation}");

        Assert.Equal(SaveConversionState.Blocked, forced.State);
        Assert.Null(forced.ConvertedBytes);
        Assert.Equal((false, false, true, "Blocked"), (row.CanSelect, row.IsSelected, row.IsProblem, row.StatusLabel));
        Assert.StartsWith("Save file is invalid or cannot be read", row.Explanation);
        Assert.False(service.Apply(forced, null, service.CreateBackupBatchDirectory()).Succeeded);
        Assert.Equal(new byte[9008], File.ReadAllBytes(path));
        Assert.Empty(Directory.GetFiles(BackupRoot, "*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData(LaunchExperience.Offline)]
    [InlineData(LaunchExperience.Ladder)]
    public void OfflineSaveDirectoryIgnoresTheLadderRedirect(LaunchExperience experience)
    {
        var install = Path.Combine(_root, "install");
        var savedGames = Path.Combine(_root, "Saved Games");
        Directory.CreateDirectory(Path.Combine(savedGames, "Diablo II Resurrected", "mods"));
        WriteModInfo(Path.Combine(install, "mods", "Reimagined"),
            "ReimaginedThree-Season-0123456789abcdef0123456789abcdef/");
        WriteModInfo(NormalModInstallationService.NormalModRoot(install), "ReimaginedThree/");
        var profile = new InstallationProfile
        {
            Type = InstallationType.BattleNet,
            LaunchExperience = experience,
            InstallDirectory = install,
            SaveDirectory = Path.Combine(savedGames, "Diablo II Resurrected", "mods",
                "ReimaginedThree-Season-0123456789abcdef0123456789abcdef")
        };

        Assert.Equal(Path.Combine(savedGames, "Diablo II Resurrected", "mods", "ReimaginedThree"),
            SaveConversionService.ResolveOfflineSaveDirectory(profile, savedGames));
    }

    [Fact]
    public void InstalledTargetUsesTheManagedNormalModWhileLadderIsActive()
    {
        var install = Path.Combine(_root, "install");
        var activeMod = Path.Combine(install, "mods", "Reimagined");
        var managedMod = NormalModInstallationService.NormalModRoot(install);
        WriteModInfo(activeMod, "ReimaginedThree-Season-0123456789abcdef0123456789abcdef/");
        WriteModInfo(managedMod, "ReimaginedThree/");
        var ladderMarker = Path.Combine(activeMod, "d2rloader", "ladder-bundle-state.json");
        Directory.CreateDirectory(Path.GetDirectoryName(ladderMarker)!);
        File.WriteAllText(ladderMarker, "{}");

        var managedExcel = Path.Combine(managedMod, "Reimagined.mpq", "data", "global", "excel");
        SaveMigrationFixtures.CopyInstalledTables(managedExcel);
        var profile = new InstallationProfile { Type = InstallationType.BattleNet, InstallDirectory = install };

        var target = SaveConversionService.LoadInstalledTarget(profile, Path.Combine(_root, "copied-target"));

        Assert.Equal(Path.GetFullPath(managedExcel), target.SourceDirectory);
        Assert.NotEqual(Path.GetFullPath(Path.Combine(activeMod, "Reimagined.mpq", "data", "global", "excel")),
            target.SourceDirectory);
    }

    [Fact]
    public void MissingInstalledTxtFileReportsActionAndDoesNotChangeSaves()
    {
        var sourceDirectory = Path.Combine(_root, "incomplete-install");
        SaveMigrationFixtures.CopyInstalledTables(sourceDirectory);
        File.Delete(Path.Combine(sourceDirectory, "misc.txt"));
        var path = CopyFixture("BruhII.d2s");
        var original = File.ReadAllBytes(path);

        var error = Assert.Throws<InvalidDataException>(() =>
            InstalledGameData.Load(sourceDirectory, Path.Combine(_root, "copy"), "3.0.13"));

        Assert.Contains("misc.txt", error.Message);
        Assert.Contains("No saves were changed", error.Message);
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.False(Directory.Exists(BackupRoot));
    }

    [Fact]
    public void ChangedInstalledStatEncodingChangesTheSerializedMigration()
    {
        var path = CopyFixture("basetolabpr.d2s");
        var baseline = CreateService().Plan(path, Path.Combine(StagingDirectory, "baseline"));
        var sourceDirectory = CopyInstalledFixtureToCustomDirectory();
        SetTableCell(Path.Combine(sourceDirectory, "itemstatcost.txt"), "Stat", "hpregen_perlevel", "Save Bits", "9");
        var changedTarget = InstalledGameData.Load(sourceDirectory, Path.Combine(_root, "changed-copy"), "3.0.13");
        var changed = CreateService(changedTarget).Plan(path, Path.Combine(StagingDirectory, "changed"));

        Assert.NotNull(baseline.ConvertedBytes);
        Assert.NotNull(changed.ConvertedBytes);
        Assert.False(baseline.ConvertedBytes!.AsSpan().SequenceEqual(changed.ConvertedBytes));
        var statId = TableSchema.Extract(TableKind.ItemStatCost, changedTarget.Snapshot[TableKind.ItemStatCost])
            .Single(stat => stat.Key == "hpregen_perlevel").SaveId!.Value;
        Assert.Equal("9", changedTarget.Snapshot[TableKind.ItemStatCost].Rows[statId].Text("Save Bits"));
    }

    [Fact]
    public void ChangedInstalledUniqueTableMovesTheMappedTargetIdWithoutAProductionSnapshot()
    {
        var sourceDirectory = CopyInstalledFixtureToCustomDirectory();
        InsertUniqueBefore(Path.Combine(sourceDirectory, "uniqueitems.txt"), "Life and Death");
        var changedTarget = InstalledGameData.Load(sourceDirectory, Path.Combine(_root, "changed-copy"), "3.0.13");
        var path = CopyFixture("BruhII.d2s");
        var result = SaveMigrationFixtures.CreateMigrator(changedTarget)
            .Migrate(path, Path.Combine(StagingDirectory, "shifted-unique"));

        var lifeAndDeath = Assert.Single(result.References,
            reference => reference is { Kind: TableKind.UniqueItems, SourceId: 1349 });
        Assert.Equal(1387, lifeAndDeath.TargetId);
    }

    [Fact]
    public void InstalledTargetChangeAfterAnalysisRequiresReanalysisBeforeBackupOrWrite()
    {
        var sourceDirectory = CopyInstalledFixtureToCustomDirectory();
        var target = InstalledGameData.Load(sourceDirectory, Path.Combine(_root, "stale-copy"), "3.0.13");
        var service = CreateService(target);
        var path = CopyFixture("BruhII.d2s");
        var original = File.ReadAllBytes(path);
        var plan = service.Plan(path, StagingDirectory);

        File.AppendAllText(Path.Combine(sourceDirectory, "misc.txt"), "changed");
        var outcome = service.Apply(plan, null, service.CreateBackupBatchDirectory());

        Assert.False(outcome.Succeeded);
        Assert.Contains("Reanalyze", outcome.Message);
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Empty(Directory.GetFiles(BackupRoot, "*", SearchOption.AllDirectories));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private SaveConversionService CreateService(InstalledGameData? installedTarget = null) => new(
        () => installedTarget is null
            ? Migrator.Value
            : SaveMigrator.ForInstalledTarget(SaveMigrationFixtures.LoadRegistry(), installedTarget),
        BackupRoot, () => _gameRunning, installedTarget: installedTarget ?? InstalledTarget.Value);

    private string CopyFixture(string fixture)
    {
        Directory.CreateDirectory(SaveDirectory);
        var path = Path.Combine(SaveDirectory, fixture);
        File.Copy(FixturePath(fixture), path);
        return path;
    }

    private string CopyInstalledFixtureToCustomDirectory()
    {
        var directory = Path.Combine(_root, "installed", Guid.NewGuid().ToString("N"));
        SaveMigrationFixtures.CopyInstalledTables(directory);
        return directory;
    }

    private static void SetTableCell(string path, string keyColumn, string key, string valueColumn, string value)
    {
        var lines = File.ReadAllLines(path, System.Text.Encoding.Latin1);
        var headers = lines[0].Split('\t');
        var keyIndex = Array.IndexOf(headers, keyColumn);
        var valueIndex = Array.IndexOf(headers, valueColumn);
        var rowIndex = Array.FindIndex(lines, 1, line => line.Split('\t')[keyIndex] == key);
        Assert.True(keyIndex >= 0 && valueIndex >= 0 && rowIndex >= 1);
        var cells = lines[rowIndex].Split('\t');
        cells[valueIndex] = value;
        lines[rowIndex] = string.Join('\t', cells);
        File.WriteAllLines(path, lines, System.Text.Encoding.Latin1);
    }

    private static void InsertUniqueBefore(string path, string key)
    {
        var lines = File.ReadAllLines(path, System.Text.Encoding.Latin1);
        var keyIndex = Array.IndexOf(lines[0].Split('\t'), "index");
        var rowIndex = Array.FindIndex(lines, 1, line => line.Split('\t')[keyIndex] == key);
        Assert.True(keyIndex >= 0 && rowIndex >= 1);
        var cells = lines[rowIndex].Split('\t');
        cells[keyIndex] = "Development test insertion";
        var inserted = string.Join('\t', cells);
        File.WriteAllLines(path, lines.Take(rowIndex).Append(inserted).Concat(lines.Skip(rowIndex)),
            System.Text.Encoding.Latin1);
    }

    private static byte[] WithValidHeader(byte[] bytes)
    {
        BitConverter.GetBytes((uint)bytes.Length).CopyTo(bytes, 8);
        D2SaveLayoutV104.UpdateChecksum(bytes);
        return bytes;
    }

    private static string FixturePath(string fixture) =>
        Path.Combine(FindRepositoryRoot(), "ReimaginedLauncher.Tests", "TestFixtures", "SaveMigration", fixture);

    private static void WriteModInfo(string modRoot, string savePath)
    {
        var directory = Path.Combine(modRoot, "Reimagined.mpq");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "modinfo.json"),
            $$"""{ "name": "Reimagined", "savepath": "{{savePath}}" }""");
    }

    private void Log(SaveConversionPlan plan)
    {
        testOutput.WriteLine($"{plan.FileName}: {plan.State} - {plan.Operation}");
        foreach (var issue in plan.Issues)
        {
            testOutput.WriteLine("  " + issue);
        }

        testOutput.WriteLine(plan.Report.Split(Environment.NewLine)[0]);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ReimaginedLauncher.sln")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException(
            "Could not locate ReimaginedLauncher.sln above the test output directory.");
    }
}