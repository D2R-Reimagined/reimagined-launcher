using ReimaginedLauncher.Utilities;
using Xunit;

namespace ReimaginedLauncher.Tests;

[Collection("Ladder bundle signing")]
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
    public void LadderSaveResolutionCreatesTheSavedGamesHierarchyOnFirstUse()
    {
        var savedGamesDirectory = Path.Combine(_testDirectory, "Saved Games");

        var resolved = LadderSaveDirectoryService.ResolveSaveDirectory(
            "ReimaginedThree-ladder",
            savedGamesDirectory,
            createMissingDirectories: true);

        var expectedModsDirectory = Path.Combine(
            savedGamesDirectory,
            "Diablo II Resurrected",
            "mods");
        Assert.Equal(Path.Combine(expectedModsDirectory, "ReimaginedThree-ladder"), resolved);
        Assert.True(Directory.Exists(expectedModsDirectory));
    }

    [Fact]
    public void ReadOnlySaveResolutionDoesNotCreateMissingDirectories()
    {
        var savedGamesDirectory = Path.Combine(_testDirectory, "Saved Games");

        var resolved = LadderSaveDirectoryService.ResolveSaveDirectory(
            "ReimaginedThree-ladder",
            savedGamesDirectory,
            createMissingDirectories: false);

        Assert.Null(resolved);
        Assert.False(Directory.Exists(savedGamesDirectory));
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
        File.WriteAllText(Path.Combine(baseDirectory, "lootfilter.json"), "base filter");
        File.WriteAllText(Path.Combine(ladderDirectory, "Settings.json"), "ladder");

        var copied = LadderSaveDirectoryService.SeedPlayerPreferences(baseDirectory, ladderDirectory);

        Assert.Equal(1, copied);
        Assert.Equal("ladder", File.ReadAllText(Path.Combine(ladderDirectory, "Settings.json")));
        Assert.Equal("base filter", File.ReadAllText(Path.Combine(ladderDirectory, "lootfilter.json")));
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

    /// <summary>
    /// A fake install with optional metadata preserved from the normal download.
    /// </summary>
    private string CreateInstall(string currentSavePath, string? baselineSavePath)
    {
        var installDirectory = Path.Combine(_testDirectory, $"install-{Guid.NewGuid():N}");
        var modInfoPath = Path.Combine(installDirectory, "mods", "Reimagined", "Reimagined.mpq", "modinfo.json");
        Directory.CreateDirectory(Path.GetDirectoryName(modInfoPath)!);
        File.WriteAllText(modInfoPath, $"{{\"name\":\"Reimagined\",\"savepath\":\"{currentSavePath}/\"}}");

        if (baselineSavePath is not null)
        {
            var baselinePath = Path.Combine(
                NormalModInstallationService.NormalModRoot(installDirectory), "Reimagined.mpq", "modinfo.json");
            Directory.CreateDirectory(Path.GetDirectoryName(baselinePath)!);
            File.WriteAllText(baselinePath, $"{{\"name\":\"Reimagined\",\"savepath\":\"{baselineSavePath}/\"}}");
        }

        return installDirectory;
    }

    private static string ReadCurrentSavePath(string installDirectory)
    {
        var modInfoPath = Path.Combine(installDirectory, "mods", "Reimagined", "Reimagined.mpq", "modinfo.json");
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(modInfoPath));
        return json!["savepath"]!.GetValue<string>().Trim('/');
    }

    [Fact]
    public async Task ASessionThatEndedRedirectedIsPutBackOnTheNormalFolder()
    {
        var installDirectory = CreateInstall(
            currentSavePath: "ReimaginedThree-Season-1-d630a3fc",
            baselineSavePath: "ReimaginedThree");

        Assert.True(await LadderSaveDirectoryService.RestoreIfRedirectedAsync(installDirectory));
        Assert.Equal("ReimaginedThree", ReadCurrentSavePath(installDirectory));
    }

    [Fact]
    public async Task AnInstallThatIsAlreadyOnTheNormalFolderIsLeftAlone()
    {
        var installDirectory = CreateInstall(
            currentSavePath: "ReimaginedThree",
            baselineSavePath: "ReimaginedThree");
        var modInfoPath = Path.Combine(installDirectory, "mods", "Reimagined", "Reimagined.mpq", "modinfo.json");
        var before = File.ReadAllText(modInfoPath);

        Assert.True(await LadderSaveDirectoryService.RestoreIfRedirectedAsync(installDirectory));
        Assert.Equal(before, File.ReadAllText(modInfoPath));
    }

    [Fact]
    public async Task WithNoNormalCopyRestoreFailsWithoutCapturingTheRedirect()
    {
        // Capturing here would enshrine whatever savepath is current as the
        // pristine one, and every later restore would return the player to it.
        var installDirectory = CreateInstall(
            currentSavePath: "ReimaginedThree-Season-1-d630a3fc",
            baselineSavePath: null);

        Assert.False(await LadderSaveDirectoryService.RestoreIfRedirectedAsync(installDirectory));
        Assert.Equal("ReimaginedThree-Season-1-d630a3fc", ReadCurrentSavePath(installDirectory));
        Assert.False(Directory.Exists(Path.Combine(installDirectory, ".reimagined-launcher", "ladder-runtime")));
    }

    [Fact]
    public async Task RestoringIsIdempotent()
    {
        var installDirectory = CreateInstall(
            currentSavePath: "ReimaginedThree-Season-1-d630a3fc",
            baselineSavePath: "ReimaginedThree");

        Assert.True(await LadderSaveDirectoryService.RestoreIfRedirectedAsync(installDirectory));
        Assert.True(await LadderSaveDirectoryService.RestoreIfRedirectedAsync(installDirectory));
        Assert.Equal("ReimaginedThree", ReadCurrentSavePath(installDirectory));
    }

    [Fact]
    public async Task AnInstallWithoutTheModIsNotAFailure()
    {
        var installDirectory = Path.Combine(_testDirectory, "no-mod");
        Directory.CreateDirectory(installDirectory);

        Assert.True(await LadderSaveDirectoryService.RestoreIfRedirectedAsync(installDirectory));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task LegacyLadderPreparationRecoversWithoutPromotingThePackageToNormal(
        bool hasRuntimeBaseline, bool usesLegacyStateLocation)
    {
        var installDirectory = CreateInstall(
            hasRuntimeBaseline ? "ReimaginedThree-Season-1-d630a3fc" : "ReimaginedThree", null);
        var statePath = usesLegacyStateLocation
            ? Path.Combine(installDirectory, "mods", "Reimagined", "d2rloader", "ladder-bundle-state.json")
            : NormalModInstallationService.BundleStatePath(installDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
        await File.WriteAllTextAsync(statePath, "{}");
        if (hasRuntimeBaseline)
        {
            var modInfo = Path.Combine(installDirectory, "mods", "Reimagined", "Reimagined.mpq", "modinfo.json");
            var baseline = LadderRuntimeFileService.GetBaselinePath(installDirectory, modInfo);
            Directory.CreateDirectory(Path.GetDirectoryName(baseline)!);
            await File.WriteAllTextAsync(baseline, "{\"savepath\":\"ReimaginedThree/\"}");
        }

        var savedGames = Path.Combine(_testDirectory, "Saved Games");
        var normalSaves = Path.Combine(savedGames, "Diablo II Resurrected", "mods", "ReimaginedThree");
        Directory.CreateDirectory(normalSaves);
        await File.WriteAllTextAsync(Path.Combine(normalSaves, "Hero.d2s"), "local character");
        await File.WriteAllTextAsync(Path.Combine(normalSaves, "Settings.json"), "normal settings");

        NormalModInstallationService.PreserveBeforeLadderInstall(installDirectory);
        var result = await LadderSaveDirectoryService.PrepareAsync(installDirectory, Ladder, "Season 1", savedGames);

        var expectedPath = Path.Combine(savedGames, "Diablo II Resurrected", "mods", "ReimaginedThree-Season-1-d630a3fc");
        Assert.Null(result.ErrorMessage);
        Assert.Equal(expectedPath, result.DirectoryPath);
        Assert.True(Directory.Exists(expectedPath));
        Assert.Equal("ReimaginedThree-Season-1-d630a3fc", ReadCurrentSavePath(installDirectory));
        Assert.Equal("normal settings", await File.ReadAllTextAsync(Path.Combine(expectedPath, "Settings.json")));
        Assert.False(File.Exists(Path.Combine(expectedPath, "Hero.d2s")));
        Assert.Equal("local character", await File.ReadAllTextAsync(Path.Combine(normalSaves, "Hero.d2s")));
        Assert.True(File.Exists(statePath));
        Assert.False(Directory.Exists(NormalModInstallationService.NormalModRoot(installDirectory)));
        Assert.True(NormalModInstallationService.RequiresRecovery(installDirectory));
        Assert.False(await LadderSaveDirectoryService.RestoreAsync(installDirectory));

        await File.WriteAllTextAsync(Path.Combine(expectedPath, "Settings.json"), "ladder settings");
        var repeated = await LadderSaveDirectoryService.PrepareAsync(installDirectory, Ladder, "Season 1", savedGames);
        Assert.Equal(expectedPath, repeated.DirectoryPath);
        Assert.Equal("ladder settings", await File.ReadAllTextAsync(Path.Combine(expectedPath, "Settings.json")));
    }

    [Fact]
    public async Task PreparationKeepsThePreservedNormalSavePathWhenThePackageHasAnotherPath()
    {
        var installDirectory = CreateInstall("PackageSaves", "NexusSaves");
        var result = await LadderSaveDirectoryService.PrepareAsync(
            installDirectory, Ladder, "Season 1", Path.Combine(_testDirectory, "Saved Games"));

        Assert.Null(result.ErrorMessage);
        Assert.EndsWith("NexusSaves-Season-1-d630a3fc", result.DirectoryPath);
        Assert.True(await LadderSaveDirectoryService.RestoreAsync(installDirectory));
        Assert.Equal("NexusSaves", ReadCurrentSavePath(installDirectory));
        Assert.False(NormalModInstallationService.RequiresRecovery(installDirectory));
    }

    [Theory]
    [InlineData("ReimaginedThree-Old-Ladder-aaaaaaaa")]
    [InlineData("../../outside")]
    [InlineData("")]
    public async Task PreparationDoesNotGuessAnOriginalPathFromUnusableMetadata(string savePath)
    {
        var installDirectory = CreateInstall(savePath, null);
        var savedGames = Path.Combine(_testDirectory, "Saved Games");
        var result = await LadderSaveDirectoryService.PrepareAsync(installDirectory, Ladder, "Season 1", savedGames);

        Assert.Null(result.DirectoryPath);
        Assert.Contains("Reinstall", result.ErrorMessage);
        Assert.False(Directory.Exists(savedGames));
        Assert.False(Directory.Exists(NormalModInstallationService.NormalModRoot(installDirectory)));
    }

    [Fact]
    public async Task PreparationReportsTheUnderlyingFolderFailureWithoutApplyingALadderRedirect()
    {
        var installDirectory = CreateInstall("ReimaginedThree", null);
        var savedGames = Path.Combine(_testDirectory, "Saved Games");
        await File.WriteAllTextAsync(savedGames, "a file blocks directory creation");

        var result = await LadderSaveDirectoryService.PrepareAsync(installDirectory, Ladder, "Season 1", savedGames);

        Assert.Null(result.DirectoryPath);
        Assert.Contains("The ladder save folder could not be prepared:", result.ErrorMessage);
        Assert.Contains("Saved Games", result.ErrorMessage);
        Assert.Equal("ReimaginedThree", ReadCurrentSavePath(installDirectory));
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }
    }
}
