using System.Security.Cryptography;
using D2SSharp;
using D2SSharp.Model;
using ReimaginedLauncher.SaveMigration;
using ReimaginedLauncher.Utilities;
using ReimaginedLauncher.Utilities.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace ReimaginedLauncher.Tests.SaveMigration;

public sealed class SaveConversionServiceTests(ITestOutputHelper testOutput) : IDisposable
{
    // The deployed asset location, so these tests also prove the definitions are copied to the build output.
    private static readonly Lazy<SaveMigrator> Migrator = new(() =>
        SaveMigrator.ForTarget(SaveCheckpointRegistry.Load(SaveConversionService.DefaultDataDirectory), SaveCheckpointRegistry.PullRequest1181Id));

    private readonly string _root = Path.Combine(Path.GetTempPath(), "reimagined-save-conversion-tests", Guid.NewGuid().ToString("N"));
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
    [InlineData("ModernSharedStashSoftCoreV2.d2i", true)]
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
            Assert.Contains(plan.State, new[] { SaveConversionState.Verified, SaveConversionState.VerifiedWithWarnings });
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
        Assert.Equal(alreadyCompatible ? 0 : 2, Directory.GetFiles(BackupRoot, "*", SearchOption.AllDirectories).Length);
        Assert.Equal(SHA256.HashData(original), SHA256.HashData(File.ReadAllBytes(FixturePath(fixture))));
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
        Assert.Equal(File.ReadAllBytes(FixturePath("WereSorc.d2s")), File.ReadAllBytes(Path.Combine(outcome.BackupDirectory!, "WereSorc.d2s")));

        var replan = service.Plan(Path.Combine(SaveDirectory, chosenName + ".d2s"), Path.Combine(StagingDirectory, "again"));
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
    public void AnUnresolvedIdentityBlocksSeparatelyAndOnlyAConfirmedForceConversionWritesIt()
    {
        // Without the registered rename, BruhII's Life & Death keeps id 1349, which PR #1181 names Iniquity Razor.
        var registry = SaveCheckpointRegistry.Load(SaveConversionService.DefaultDataDirectory) with { IdentityMappings = [] };
        var service = new SaveConversionService(() => SaveMigrator.ForTarget(registry, SaveCheckpointRegistry.PullRequest1181Id), BackupRoot, () => _gameRunning);
        var path = CopyFixture("BruhII.d2s");
        var original = File.ReadAllBytes(path);

        var plan = service.Plan(path, StagingDirectory);
        Log(plan);
        Assert.Equal(SaveConversionState.Unresolved, plan.State);
        Assert.Null(plan.ConvertedBytes);
        Assert.Contains("'Life & Death'", plan.BlockingReason);
        Assert.Contains("'Iniquity Razor'", plan.BlockingReason);
        Assert.Contains("verification checks passed", plan.BlockingReason);
        var blocking = Assert.Single(plan.IssueGroups, group => group.Blocking);
        Assert.Equal((SaveIssueCategory.ItemIdentity, 1, 1), (blocking.Category, blocking.Count, blocking.Items));
        Assert.Equal(SaveForceKind.AcceptUnresolvedIdentities, plan.ForceOption?.Kind);
        Assert.Contains("Iniquity Razor", Assert.Single(plan.ForceOption!.Risks));
        var unforcedRow = new SaveConversionRowViewModel(plan, service.CheckRename);
        Assert.Equal((false, false, "Needs Force"), (unforcedRow.CanSelect, unforcedRow.IsSelected, unforcedRow.StatusLabel));
        Assert.False(service.Apply(plan, null, service.CreateBackupBatchDirectory()).Succeeded);
        Assert.Equal(original, File.ReadAllBytes(path));

        var forced = service.Plan(path, Path.Combine(StagingDirectory, "forced"), new MigrationOverrides(AcceptUnresolvedIdentities: true));
        Log(forced);
        Assert.Equal(SaveConversionState.Experimental, forced.State);
        Assert.NotNull(forced.ConvertedBytes);
        Assert.Contains("EXPERIMENTAL", forced.Report);
        Assert.DoesNotContain(forced.IssueGroups, group => group.Blocking);
        var forcedRow = new SaveConversionRowViewModel(forced, service.CheckRename);
        Assert.Equal((true, "Experimental", "1 item identity cannot be confirmed."), (forcedRow.IsSelected, forcedRow.StatusLabel, forcedRow.Explanation));

        var outcome = service.Apply(forced, null, service.CreateBackupBatchDirectory());
        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Contains("experimentally", outcome.Message);
        Assert.Equal(forced.ConvertedBytes, File.ReadAllBytes(path));
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(outcome.BackupDirectory!, "BruhII.d2s")));
        Assert.Contains("EXPERIMENTAL", File.ReadAllText(Path.Combine(outcome.BackupDirectory!, "BruhII.d2s.restore.txt")));
        Assert.True(File.Exists(Path.Combine(outcome.BackupDirectory!, "BruhII.d2s.experimental-report.txt")));
        Assert.NotEqual(SaveConversionState.Experimental, service.Plan(path, Path.Combine(StagingDirectory, "again")).State);
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

        var forced = service.Plan(path, Path.Combine(StagingDirectory, "forced"), new MigrationOverrides(AcceptUnresolvedIdentities: true, SourceLayout: "2.1.2"));
        Assert.Equal(SaveConversionState.Blocked, forced.State);
        Assert.Null(forced.ConvertedBytes);
        Assert.False(service.Apply(forced, null, service.CreateBackupBatchDirectory()).Succeeded);
        Assert.Equal(corrupt, File.ReadAllBytes(path));

        // A manually chosen source layout must still parse the file completely.
        var unknown = service.Plan(CopyFixture("Popsicle.d2s"), Path.Combine(StagingDirectory, "unknown"),
            new MigrationOverrides(AcceptUnresolvedIdentities: true, SourceLayout: "not a registered layout"));
        Assert.Equal(SaveConversionState.Blocked, unknown.State);
        Assert.Null(unknown.ConvertedBytes);
        Assert.Empty(Directory.GetFiles(BackupRoot, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void ConfidentSourcesOverrideThePreviousReleaseDefaultAndEligibleFilesArePreselected()
    {
        var service = new SaveConversionService(() => Migrator.Value, BackupRoot, () => _gameRunning, defaultSourceLayout: "3.0.12");
        var plans = new[] { "BruhII.d2s", "BonesMcPimp.d2s", "FuckBenFuckingF.d2s" }
            .Select(fixture => service.Plan(CopyFixture(fixture), Path.Combine(StagingDirectory, fixture)))
            .ToList();
        var rows = plans.Select(plan => new SaveConversionRowViewModel(plan, service.CheckRename)).ToList();
        foreach (var row in rows)
        {
            testOutput.WriteLine($"{row.FileName}: {row.StatusLabel} - {row.Explanation}");
        }

        Assert.Equal(["2.1.2", SaveMigrator.Intermediate3013Label], plans.Take(2).Select(plan => plan.SourceLabel));
        Assert.All(rows, row => Assert.False(row.IsSourceUncertain));
        Assert.Equal([true, true, false], rows.Select(row => row.IsSelected));
        Assert.Equal(("Already compatible", false), (rows[2].StatusLabel, rows[2].CanSelect));
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
            TargetLabel = "PR #1181",
            ForceOption = new SaveForceOption(SaveForceKind.ChooseSourceLayout, ["3.0.12", "2.1.2"], [], TargetAlsoFits: true)
        };
        var row = new SaveConversionRowViewModel(plan, (_, _) => null);

        Assert.Equal(["3.0.12", "2.1.2", null], row.SourceChoices.Select(choice => choice.Layout));
        Assert.Equal("3.0.12", row.SelectedSourceChoice?.Layout);
        Assert.Equal((false, false, "Source uncertain"), (row.IsSelected, row.IsProblem, row.StatusLabel));

        row.SelectedSourceChoice = row.SourceChoices[^1];
        Assert.Equal(("Already compatible", false), (row.StatusLabel, row.CanSelect));
    }

    [Fact]
    public void ForceConversionNeverUnblocksAZeroFilledSave()
    {
        var service = CreateService();
        Directory.CreateDirectory(SaveDirectory);
        var path = Path.Combine(SaveDirectory, "LilSorcy.d2s");
        File.WriteAllBytes(path, new byte[9008]);

        var forced = service.Plan(path, StagingDirectory, new MigrationOverrides(AcceptUnresolvedIdentities: true));
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
        WriteModInfo(Path.Combine(install, "mods", "Reimagined"), "ReimaginedThree-Season-0123456789abcdef0123456789abcdef/");
        WriteModInfo(NormalModInstallationService.NormalModRoot(install), "ReimaginedThree/");
        var profile = new InstallationProfile
        {
            Type = InstallationType.BattleNet,
            LaunchExperience = experience,
            InstallDirectory = install,
            SaveDirectory = Path.Combine(savedGames, "Diablo II Resurrected", "mods", "ReimaginedThree-Season-0123456789abcdef0123456789abcdef")
        };

        Assert.Equal(Path.Combine(savedGames, "Diablo II Resurrected", "mods", "ReimaginedThree"),
            SaveConversionService.ResolveOfflineSaveDirectory(profile, savedGames));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private SaveConversionService CreateService() => new(() => Migrator.Value, BackupRoot, () => _gameRunning);

    private string CopyFixture(string fixture)
    {
        Directory.CreateDirectory(SaveDirectory);
        var path = Path.Combine(SaveDirectory, fixture);
        File.Copy(FixturePath(fixture), path);
        return path;
    }

    private static string FixturePath(string fixture) =>
        Path.Combine(FindRepositoryRoot(), "ReimaginedLauncher.Tests", "TestFixtures", "SaveMigration", fixture);

    private static void WriteModInfo(string modRoot, string savePath)
    {
        var directory = Path.Combine(modRoot, "Reimagined.mpq");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "modinfo.json"), $$"""{ "name": "Reimagined", "savepath": "{{savePath}}" }""");
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
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ReimaginedLauncher.sln")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate ReimaginedLauncher.sln above the test output directory.");
    }
}
