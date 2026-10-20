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

        Assert.Equal(["reimagined-2.1.2", "reimagined-3.0.12", "reimagined-3.0.13-pre-swap", "pr-1181"], registry.Checkpoints.Select(checkpoint => checkpoint.Id));
        var intermediate = registry.Get("reimagined-3.0.13-pre-swap");
        var pullRequest = registry.Get(SaveCheckpointRegistry.PullRequest1181Id);
        Assert.Equal((SaveCheckpointType.Intermediate, "D2RR 3.0.12", "D2RR 3.0.13", false),
            (intermediate.Type, intermediate.Directory, intermediate.AppendedStatsDirectory, intermediate.CanBeTarget));
        Assert.Equal(SaveMigrator.Intermediate3013Label, intermediate.Label);
        Assert.Equal(("D2RR 3.0.13", (string?)null, false, true, true),
            (pullRequest.Directory, pullRequest.AppendedStatsDirectory, pullRequest.DevelopmentTarget, pullRequest.CanBeTarget, pullRequest.IsSupportedTarget));
        Assert.False(intermediate.IsSupportedTarget);
        Assert.Equal(["reimagined-2.1.2", "reimagined-3.0.12", "reimagined-3.0.13-pre-swap"], registry.SourcesFor(pullRequest).Select(checkpoint => checkpoint.Id));
        Assert.Equal(["reimagined-2.1.2"], registry.SourcesFor(registry.Get("reimagined-3.0.12")).Select(checkpoint => checkpoint.Id));
        Assert.Empty(registry.ValidateIdentityMappings());
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
