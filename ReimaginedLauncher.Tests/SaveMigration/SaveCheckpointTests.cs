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
        Assert.Equal(4, registry.IdentityMappingsUpTo(int.MaxValue).Count);
        Assert.Empty(registry.ValidateIdentityMappings());
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

        var releaseDirectory = registry.PathOf(registry.Get("reimagined-3.0.12").Directory);
        var releaseTarget =
            InstalledGameData.Load(releaseDirectory, SaveMigrationFixtures.NewCopyDirectory(), "3.0.12");
        var releaseMigrator = SaveMigrator.ForInstalledTarget(registry, releaseTarget);

        Assert.Equal(["2.1.2"], releaseMigrator.SourceLayouts);
    }
}