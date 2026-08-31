using ReimaginedLauncher.Utilities;
using Xunit;

namespace ReimaginedLauncher.Tests;

public sealed class LadderSaveDirectoryServiceTests : IDisposable
{
    private static readonly Guid Ladder = Guid.Parse("d630a3fc-9da3-11f1-a936-c87f5404bb80");

    private readonly string _testDirectory = Path.Combine(
        Path.GetTempPath(),
        $"reimagined-ladder-savepath-tests-{Guid.NewGuid():N}");

    [Theory]
    // The apostrophe is dropped rather than replaced, so this reads as a word.
    [InlineData("Ben's Bitchin HC Ladder", "Bens-Bitchin-HC-Ladder")]
    [InlineData("Season 1", "Season-1")]
    [InlineData("  spaced   out  ", "spaced-out")]
    [InlineData("Slashes/And\\Colons:*?", "Slashes-And-Colons")]
    [InlineData("Ünïcôdé Lädder", "Unicode-Ladder")]
    [InlineData("!!!", "")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void SlugifyProducesSafeFolderFragments(string? name, string expected)
    {
        Assert.Equal(expected, LadderSaveDirectoryService.Slugify(name));
    }

    [Fact]
    public void SlugifyNeverEmitsPathSeparatorsOrTraversal()
    {
        var slug = LadderSaveDirectoryService.Slugify("../../etc/passwd");

        Assert.DoesNotContain('/', slug);
        Assert.DoesNotContain('\\', slug);
        Assert.DoesNotContain("..", slug);
    }

    [Fact]
    public void SlugifyIsLengthCappedSoPathsStayUsable()
    {
        var slug = LadderSaveDirectoryService.Slugify(new string('a', 500));

        Assert.True(slug.Length <= 40, $"slug was {slug.Length} characters");
    }

    [Fact]
    public void LadderSavePathCarriesTheNameAndTheLadderId()
    {
        var path = LadderSaveDirectoryService.BuildLadderSavePath("ReimaginedThree", Ladder, "Ben's Bitchin HC Ladder");

        Assert.Equal("ReimaginedThree-Bens-Bitchin-HC-Ladder-d630a3fc", path);
    }

    [Fact]
    public void TwoLaddersWithTheSameNameStillGetDifferentFolders()
    {
        var first = LadderSaveDirectoryService.BuildLadderSavePath("ReimaginedThree", Guid.NewGuid(), "Season 1");
        var second = LadderSaveDirectoryService.BuildLadderSavePath("ReimaginedThree", Guid.NewGuid(), "Season 1");

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void AnUnnamedLadderStillGetsAUniqueFolder()
    {
        var path = LadderSaveDirectoryService.BuildLadderSavePath("ReimaginedThree", Ladder, "!!!");

        Assert.Equal("ReimaginedThree-d630a3fc", path);
    }

    [Fact]
    public void ALadderFolderIsNeverTheBaseFolder()
    {
        // The whole point is that ladder characters and offline characters never
        // share a directory.
        var path = LadderSaveDirectoryService.BuildLadderSavePath("ReimaginedThree", Ladder, "Anything");

        Assert.NotEqual("ReimaginedThree", path);
        Assert.StartsWith("ReimaginedThree-", path);
    }

    [Fact]
    public void SeedingCarriesSettingsAndLootFiltersButNotCharacters()
    {
        var baseDirectory = Path.Combine(_testDirectory, "ReimaginedThree");
        var ladderDirectory = Path.Combine(_testDirectory, "ReimaginedThree-ladder");
        Directory.CreateDirectory(baseDirectory);
        Directory.CreateDirectory(ladderDirectory);

        File.WriteAllText(Path.Combine(baseDirectory, "Settings.json"), "{\"Gamma\":155}");
        File.WriteAllText(Path.Combine(baseDirectory, "lootfilter.json"), "{\"Version\":1}");
        File.WriteAllText(Path.Combine(baseDirectory, "Starter - Sorc.fltr"), "filter");
        File.WriteAllText(Path.Combine(baseDirectory, "Hero.d2s"), "save");
        File.WriteAllText(Path.Combine(baseDirectory, "ModernSharedStashSoftCoreV2.d2i"), "stash");

        var copied = LadderSaveDirectoryService.SeedPlayerPreferences(baseDirectory, ladderDirectory);

        Assert.Equal(3, copied);
        Assert.True(File.Exists(Path.Combine(ladderDirectory, "Settings.json")));
        Assert.True(File.Exists(Path.Combine(ladderDirectory, "lootfilter.json")));
        Assert.True(File.Exists(Path.Combine(ladderDirectory, "Starter - Sorc.fltr")));

        // Characters belong to the server, and the shared stash is deliberately
        // per-ladder so items cannot be laundered between them.
        Assert.False(File.Exists(Path.Combine(ladderDirectory, "Hero.d2s")));
        Assert.False(File.Exists(Path.Combine(ladderDirectory, "ModernSharedStashSoftCoreV2.d2i")));
    }

    [Fact]
    public void SeedingNeverOverwritesLadderSpecificTweaks()
    {
        var baseDirectory = Path.Combine(_testDirectory, "ReimaginedThree");
        var ladderDirectory = Path.Combine(_testDirectory, "ReimaginedThree-ladder");
        Directory.CreateDirectory(baseDirectory);
        Directory.CreateDirectory(ladderDirectory);

        File.WriteAllText(Path.Combine(baseDirectory, "Settings.json"), "base");
        File.WriteAllText(Path.Combine(ladderDirectory, "Settings.json"), "ladder");

        LadderSaveDirectoryService.SeedPlayerPreferences(baseDirectory, ladderDirectory);

        Assert.Equal("ladder", File.ReadAllText(Path.Combine(ladderDirectory, "Settings.json")));
    }

    [Fact]
    public void SeedingToleratesAMissingBaseFolder()
    {
        var ladderDirectory = Path.Combine(_testDirectory, "ladder-only");
        Directory.CreateDirectory(ladderDirectory);

        Assert.Equal(0, LadderSaveDirectoryService.SeedPlayerPreferences(
            Path.Combine(_testDirectory, "does-not-exist"),
            ladderDirectory));
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }
    }
}
