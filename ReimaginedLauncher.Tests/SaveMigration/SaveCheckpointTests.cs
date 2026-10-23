using D2SSharp.Data;
using D2SSharp.Enums;
using ReimaginedLauncher.SaveMigration;
using Xunit;

namespace ReimaginedLauncher.Tests.SaveMigration;

public sealed class SaveCheckpointTests
{
    private static readonly Lazy<string> RepositoryRoot = new(FindRepositoryRoot);

    private static string DataDirectory => Path.Combine(RepositoryRoot.Value, "ReimaginedLauncher", "Assets", "SaveMigrationData");

    [Fact]
    public void RegistryKeepsExplicitOrderAndTheIntermediateLayoutDistinct()
    {
        var registry = SaveCheckpointRegistry.Load(DataDirectory);

        Assert.Equal(["reimagined-2.1.2", "reimagined-3.0.12", "reimagined-3.0.13-pre-swap", "reimagined-3.0.13-new-items-pre-swap-stats", "pr-1181"],
            registry.Checkpoints.Select(checkpoint => checkpoint.Id));
        var intermediate = registry.Get("reimagined-3.0.13-pre-swap");
        var pullRequest = registry.Get(SaveCheckpointRegistry.PullRequest1181Id);
        Assert.Equal((SaveCheckpointType.Intermediate, "D2RR 3.0.12", "D2RR 3.0.13", false),
            (intermediate.Type, intermediate.Directory, intermediate.AppendedStatsDirectory, intermediate.CanBeTarget));
        Assert.Equal(SaveMigrator.Intermediate3013Label, intermediate.Label);
        Assert.Equal(("D2RR 3.0.13", (string?)null, false, true, true),
            (pullRequest.Directory, pullRequest.AppendedStatsDirectory, pullRequest.DevelopmentTarget, pullRequest.CanBeTarget, pullRequest.IsSupportedTarget));
        Assert.False(intermediate.IsSupportedTarget);
        Assert.Equal(["reimagined-2.1.2", "reimagined-3.0.12", "reimagined-3.0.13-pre-swap", "reimagined-3.0.13-new-items-pre-swap-stats"],
            registry.SourcesFor(pullRequest).Select(checkpoint => checkpoint.Id));
        Assert.Equal(["reimagined-2.1.2"], registry.SourcesFor(registry.Get("reimagined-3.0.12")).Select(checkpoint => checkpoint.Id));
        Assert.Empty(registry.ValidateIdentityMappings());

        // New items with the pre-swap stat encoding: PR #1181 item tables, 3.0.12 stats plus the appended lab stats. Source only.
        var newItems = registry.Get("reimagined-3.0.13-new-items-pre-swap-stats");
        Assert.Equal((SaveCheckpointType.Intermediate, "D2RR 3.0.12", "D2RR 3.0.13", "D2RR 3.0.13", false, false),
            (newItems.Type, newItems.Directory, newItems.AppendedStatsDirectory, newItems.ItemTablesDirectory, newItems.CanBeTarget, newItems.IsSupportedTarget));
        Assert.True(newItems.Order > intermediate.Order && newItems.Order < pullRequest.Order);
        Assert.Equal("PR #1181", registry.SnapshotLabel("D2RR 3.0.13"));
        Assert.Throws<InvalidDataException>(() => SaveCheckpointRegistry.Parse(
            """{ "checkpoints": [ { "id": "x", "label": "x", "type": "intermediate", "order": 1, "directory": "D2RR 3.0.12", "itemTablesDirectory": "D2RR 3.0.13" } ] }""",
            DataDirectory));
    }

    [Fact]
    public void TheNewItemsPreSwapStatsLayoutReadsPr1181ItemTablesWith3012StatEncoding()
    {
        var registry = SaveCheckpointRegistry.Load(DataDirectory);
        var migrator = SaveMigrator.ForTarget(registry, SaveCheckpointRegistry.PullRequest1181Id);
        var reimagined3012 = KnownSnapshots.Reimagined3012.Load(DataDirectory);
        var pullRequest = KnownSnapshots.PullRequest1181.Load(DataDirectory);
        var label = registry.Get("reimagined-3.0.13-new-items-pre-swap-stats").Label;
        var source = SaveDataDefinition.WithAppendedStats(label, reimagined3012, pullRequest, pullRequest);

        Assert.Equal((label, pullRequest.Directory), (source.ItemTables.Label, source.ItemTables.Directory));
        foreach (var kind in TableSchema.AllKinds.Where(kind => kind != TableKind.ItemStatCost))
        {
            Assert.Same(pullRequest[kind], source.ItemTables[kind]);
        }

        Assert.Same(reimagined3012[TableKind.ItemStatCost], source.ItemTables[TableKind.ItemStatCost]);
        // The existing ids keep 3.0.12's positions; only the stats past its end come from PR #1181.
        var baseCount = TableSchema.Extract(TableKind.ItemStatCost, reimagined3012[TableKind.ItemStatCost]).Count;
        Assert.Equal(("hpregen_perlevel", "passive_dmg_pierce"), (source.Stat(366)!.Key, source.Stat(432)!.Key));
        Assert.Equal(source.Stats.Count - baseCount, source.AppendedStats.Count);
        Assert.Contains(source.AppendedStats, stat => stat.Key == "lab_boss_kill");

        var pr1181Data = migrator.Target.CreateExternalData(SaveMigrator.TargetVersion);
        var sourceData = source.CreateExternalData(SaveMigrator.TargetVersion);
        var preSwapData = SaveDataDefinition.WithAppendedStats(SaveMigrator.Intermediate3013Label, reimagined3012, pullRequest)
            .CreateExternalData(SaveMigrator.TargetVersion);
        var brak = ItemTypeCodes.MakeCode("r34");
        Assert.Equal(-1, preSwapData.GetItemIndex(brak, SaveMigrator.TargetVersion));
        Assert.Equal(pr1181Data.GetItemIndex(brak, SaveMigrator.TargetVersion), sourceData.GetItemIndex(brak, SaveMigrator.TargetVersion));
        Assert.NotEqual(-1, sourceData.GetItemIndex(brak, SaveMigrator.TargetVersion));
        // item_maxhp_percent keeps 3.0.12's Save Bits and Save Add, not PR #1181's.
        var maxHpPercent = (StatId)source.Stat("item_maxhp_percent")!.SaveId!.Value;
        var reimagined3012Data = SaveDataDefinition.FromSnapshot(reimagined3012).CreateExternalData(SaveMigrator.TargetVersion);
        Assert.Equal(reimagined3012Data.GetStatInfo(maxHpPercent, SaveMigrator.TargetVersion), sourceData.GetStatInfo(maxHpPercent, SaveMigrator.TargetVersion));
        Assert.NotEqual(pr1181Data.GetStatInfo(maxHpPercent, SaveMigrator.TargetVersion), sourceData.GetStatInfo(maxHpPercent, SaveMigrator.TargetVersion));
    }

    [Theory]
    [InlineData("3.0.12", true)]
    [InlineData(" 2.1.2 ", true)]
    [InlineData("3.0.13", false)]
    [InlineData("9.9.9", false)]
    [InlineData(null, false)]
    public void TheLatestSupportedTargetIsSelectedAndAnOlderInstallOnlyWarns(string? installed, bool warns)
    {
        var registry = SaveCheckpointRegistry.Load(DataDirectory);
        var selection = registry.SelectTarget(installed);

        // Neither 3.0.12 nor the pre-swap 3.0.13 layout may become the target.
        Assert.Equal(SaveCheckpointRegistry.PullRequest1181Id, selection.Target?.Id);
        Assert.Equal(SaveCheckpointRegistry.PullRequest1181Id, registry.DefaultTarget?.Id);
        Assert.False(string.IsNullOrWhiteSpace(selection.Explanation));
        Assert.Equal(warns, selection.Warning is not null);
        Assert.Equal("reimagined-3.0.12", registry.PreviousRelease(selection.Target!)?.Id);
    }

    [Fact]
    public void AFutureCheckpointRegisteredOnlyInJsonMigratesDirectlyFromAnOlderRelease()
    {
        // A 3.0.14 entry reusing the PR #1181 tables stands in for a future snapshot folder; it is listed first to prove order wins.
        var json = File.ReadAllText(Path.Combine(DataDirectory, SaveCheckpointRegistry.FileName)).Replace(
            "\"checkpoints\": [",
            """
            "checkpoints": [
                { "id": "reimagined-3.0.14", "label": "3.0.14", "build": "Reimagined 3.0.14", "type": "release", "order": 50,
                  "directory": "D2RR 3.0.13", "installedVersions": [ "3.0.14" ] },
            """);
        var registry = SaveCheckpointRegistry.Parse(json, DataDirectory);
        Assert.Equal("reimagined-3.0.14", registry.Checkpoints[^1].Id);
        Assert.Equal("reimagined-3.0.14", registry.SelectTarget("3.0.14").Target?.Id);
        Assert.Equal("reimagined-3.0.14", registry.SelectTarget("3.0.12").Target?.Id);
        // PR #1181 and the pre-swap layout are intermediate, so the previous release is still 3.0.12.
        Assert.Equal("reimagined-3.0.12", registry.PreviousRelease(registry.DefaultTarget!)?.Id);

        var withDevelopmentBuild = SaveCheckpointRegistry.Parse(json.Replace(
            "\"checkpoints\": [",
            """
            "checkpoints": [
                { "id": "dev-build", "label": "Dev", "type": "intermediate", "order": 60, "directory": "D2RR 3.0.13", "developmentTarget": true },
            """), DataDirectory);
        Assert.Equal("reimagined-3.0.14", withDevelopmentBuild.DefaultTarget?.Id);
        Assert.Contains(withDevelopmentBuild.Targets, checkpoint => checkpoint.Id == "dev-build");

        var output = Path.Combine(Path.GetTempPath(), "reimagined-checkpoint-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var result = SaveMigrator.ForTarget(registry, "reimagined-3.0.14").Migrate(
                Path.Combine(RepositoryRoot.Value, "ReimaginedLauncher.Tests", "TestFixtures", "SaveMigration", "BruhII.d2s"), output);

            Assert.Equal(("2.1.2", "3.0.14"), (result.SourceLayout, result.TargetLayout));
            Assert.NotEqual(MigrationStatus.Failed, result.Status);
            Assert.All(result.Checks, check => Assert.True(check.Passed, $"{check.Name}: {check.Detail}"));
            Assert.Contains(result.References, reference =>
                reference is { Kind: TableKind.UniqueItems, SourceId: 1349, TargetId: 1386, Outcome: ReferenceOutcome.Mapped });
        }
        finally
        {
            if (Directory.Exists(output))
            {
                Directory.Delete(output, recursive: true);
            }
        }
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
