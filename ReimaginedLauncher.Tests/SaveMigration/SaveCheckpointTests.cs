using ReimaginedLauncher.SaveMigration;
using Xunit;

namespace ReimaginedLauncher.Tests.SaveMigration;

public sealed class SaveCheckpointTests
{
    [Fact]
    public void RegistryContainsOnlyFrozenReleaseSources()
    {
        var registry = SaveMigrationFixtures.LoadRegistry();

        Assert.Equal(["reimagined-2.1.2", "reimagined-3.0.12"],
            registry.Checkpoints.Select(checkpoint => checkpoint.Id));
        Assert.All(registry.Checkpoints, checkpoint => Assert.Equal(SaveCheckpointType.Release, checkpoint.Type));
        Assert.Equal("3.0.12", registry.LatestCheckpoint?.Label);
        var encoding = Assert.Single(registry.StatEncodings);
        Assert.Equal(("reimagined-3.0.13-dev-pre-swap", "3.0.13 development pre-swap", 30),
            (encoding.Id, encoding.Label, encoding.Order));
        Assert.Equal(4, registry.IdentityMappingsUpTo(int.MaxValue).Count);
        Assert.Empty(registry.ValidateIdentityMappings());
    }

    [Fact]
    public void PreSwapEncodingIsTheAuthoritativeDevelopmentTableWithOnlyTheSwappedRowsRestored()
    {
        var registry = SaveMigrationFixtures.LoadRegistry();
        var encoding = Assert.Single(registry.StatEncodings);
        var path = registry.PathOf(encoding);
        var fixturePath = Path.Combine(SaveMigrationFixtures.InstalledDirectory, "itemstatcost.txt");
        var expected = File.ReadAllLines(fixturePath);
        (expected[367], expected[433]) = (expected[433], expected[367]);
        Assert.Equal(expected, File.ReadAllLines(path));

        var stats = TableSchema.Extract(TableKind.ItemStatCost, TableSnapshot.Load(path));
        Assert.Equal(442, stats.Count);
        Assert.Equal(("hpregen_perlevel", "passive_dmg_pierce"), (stats[366].Key, stats[432].Key));
        Assert.Equal(("8", "100"), (stats[76].Field("Save Bits"), stats[76].Field("Save Add")));
        Assert.Equal(("8", "100"), (stats[77].Field("Save Bits"), stats[77].Field("Save Add")));
        Assert.Equal("16", stats[78].Field("Save Bits"));
        Assert.Equal(["lab_boss_kill", "lab_kill_dummy", "lab_kill_display", "extra_stbounce"],
            stats.Skip(438).Select(stat => stat.Key));
    }

    [Fact]
    public void InstalledTargetBuildsPreSwapSourcesAndARecognizedFrozenTargetDoesNot()
    {
        var registry = SaveMigrationFixtures.LoadRegistry();
        var installed = SaveMigrationFixtures.InstalledTarget;
        var migrator = SaveMigrator.ForInstalledTarget(registry, installed);

        Assert.Equal(installed.Snapshot.Label, migrator.Target.Label);
        Assert.Contains("3.0.12 with installed appended stats", migrator.SourceLayouts);
        Assert.Contains("Installed items with 3.0.12 stat encoding", migrator.SourceLayouts);
        Assert.Contains("Installed items with 3.0.13 development pre-swap stat encoding", migrator.SourceLayouts);

        var releaseDirectory = registry.PathOf(registry.Get("reimagined-3.0.12").Directory);
        var releaseTarget =
            InstalledGameData.Load(releaseDirectory, SaveMigrationFixtures.NewCopyDirectory(), "3.0.12");
        var releaseMigrator = SaveMigrator.ForInstalledTarget(registry, releaseTarget);

        Assert.Equal(["2.1.2"], releaseMigrator.SourceLayouts);
    }

    [Fact]
    public void CheckpointsRecordTheCharacterVersionsTheirSavesUse()
    {
        var registry = SaveMigrationFixtures.LoadRegistry();

        Assert.Equal([99u], registry.Get("reimagined-2.1.2").CharacterVersions);
        Assert.Equal([105u], registry.Get("reimagined-3.0.12").CharacterVersions);
        Assert.Equal([105u], Assert.Single(registry.StatEncodings).CharacterVersions);
    }

    [Fact]
    public void UnrecognizedInstalledDataIsOnlyAsNewAsTheStatsItDefines()
    {
        var registry = SaveMigrationFixtures.LoadRegistry();
        var releaseDirectory = SaveMigrationFixtures.ReleaseDirectory("reimagined-3.0.12");
        string[] newer =
        [
            "Installed items with 3.0.13 development pre-swap stat encoding",
            "3.0.12 with 3.0.13 development pre-swap appended stats"
        ];

        var release = SaveMigrator.ForInstalledTarget(registry,
            InstalledGameData.Load(releaseDirectory, SaveMigrationFixtures.NewCopyDirectory(), "3.0.12"));
        Assert.Equal(["2.1.2"], release.SourceLayouts);
        Assert.Equal(newer, release.NewerLayouts);

        // Modified 3.0.12 data defines no development stat, so it is not newer than 3.0.12 and gets no development source.
        var modifiedRelease = SaveMigrator.ForInstalledTarget(registry,
            SaveMigrationFixtures.LoadModified(releaseDirectory, "3.0.12"));
        Assert.Equal(["2.1.2", "3.0.12"], modifiedRelease.SourceLayouts);
        Assert.Equal(newer, modifiedRelease.NewerLayouts);

        // Development data whose modinfo version still reads 3.0.12 defines the development stats and keeps every upward source.
        var development = SaveMigrationFixtures.CreateMigrator();
        var outdatedVersion = SaveMigrator.ForInstalledTarget(registry,
            InstalledGameData.Load(SaveMigrationFixtures.InstalledDirectory, SaveMigrationFixtures.NewCopyDirectory(),
                "3.0.12"));
        var modifiedDevelopment = SaveMigrator.ForInstalledTarget(registry,
            SaveMigrationFixtures.LoadModified(SaveMigrationFixtures.InstalledDirectory, "3.0.12"));
        Assert.Equal(
        [
            "2.1.2", "3.0.12", "3.0.12 with installed appended stats", "Installed items with 3.0.12 stat encoding",
            "Installed items with 3.0.13 development pre-swap stat encoding"
        ], development.SourceLayouts);
        Assert.Equal(development.SourceLayouts, outdatedVersion.SourceLayouts);
        Assert.Equal(development.SourceLayouts, modifiedDevelopment.SourceLayouts);
        Assert.All(new[] { development, outdatedVersion, modifiedDevelopment },
            migrator => Assert.Empty(migrator.NewerLayouts));
    }
}