using System.Text.RegularExpressions;
using ReimaginedLauncher.Utilities;
using Xunit;

namespace ReimaginedLauncher.Tests;

public sealed class LadderCharacterSelectServiceTests : IDisposable
{
    private const string NameWidgetName = "ReimaginedLadderNameInfo";
    private const string RuntimeWidgetName = "ReimaginedLadderRuntimeInfo";

    private readonly string _testDirectory = Path.Combine(
        Path.GetTempPath(),
        $"reimagined-ladder-screen-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task LadderPreparationAddsOneCurrentBannerToKeyboardAndControllerLayouts()
    {
        var keyboardLayout = CreateLayout("characterselectpanelhd.json");
        var controllerLayout = CreateLayout(Path.Combine("controller", "characterselectpanelhd.json"));
        var firstLadder = new LadderDisplayInfo(
            "Ben's Bitchin HC Ladder",
            new DateTimeOffset(2026, 8, 21, 5, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 28, 5, 0, 0, TimeSpan.Zero));

        var prepared = await LadderCharacterSelectService.PrepareAsync(
            [keyboardLayout, controllerLayout],
            firstLadder,
            _testDirectory);

        Assert.Equal(2, prepared);
        AssertBanner(keyboardLayout, firstLadder.Name);
        AssertBanner(controllerLayout, firstLadder.Name);
        AssertBannerIsAFullScreenOverlay(keyboardLayout);
        AssertBannerIsAFullScreenOverlay(controllerLayout);

        var secondLadder = firstLadder with { Name = "Second Ladder" };
        await LadderCharacterSelectService.PrepareAsync(
            [keyboardLayout, controllerLayout],
            secondLadder,
            _testDirectory);

        AssertBanner(keyboardLayout, secondLadder.Name);
        AssertBanner(controllerLayout, secondLadder.Name);
        Assert.DoesNotContain(firstLadder.Name, await File.ReadAllTextAsync(keyboardLayout));
    }

    [Fact]
    public async Task NonLadderPreparationRestoresTheExactCleanCharacterSelectLayouts()
    {
        var layoutPath = CreateLayout("characterselectpanelhd.json");
        var original = await File.ReadAllTextAsync(layoutPath);
        var ladder = new LadderDisplayInfo(
            "Temporary Ladder",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddDays(7));

        await LadderCharacterSelectService.PrepareAsync([layoutPath], ladder, _testDirectory);
        Assert.Contains(NameWidgetName, await File.ReadAllTextAsync(layoutPath));

        await LadderCharacterSelectService.PrepareAsync(
            [layoutPath],
            ladder: null,
            installDirectory: _testDirectory);

        Assert.Equal(original, await File.ReadAllTextAsync(layoutPath));
        Assert.DoesNotContain(NameWidgetName, await File.ReadAllTextAsync(layoutPath));
        Assert.DoesNotContain(RuntimeWidgetName, await File.ReadAllTextAsync(layoutPath));
        Assert.False(File.Exists(Path.Combine(
            Path.GetDirectoryName(layoutPath)!,
            "characterselectpanelhd_launcher_clean.json")));
    }

    private string CreateLayout(string relativePath)
    {
        var path = Path.Combine(_testDirectory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """
                                {
                                    "type": "CharacterSelectPanel", "name": "CharacterSelectPanel",
                                    "children": [
                                        // Preserve D2R-compatible comments and duplicate keys verbatim.
                                        {
                                            "type": "ImageWidget", "name": "BackgroundCover",
                                            "fields": { "style": "first", "style": "second" },
                                            "children": [
                                                {
                                                    "type": "TextBoxWidget", "name": "D2RReimaginedModInfo",
                                                    "fields": { "text": "D2R Reimagined", },
                                                }
                                            ]
                                        }
                                    ]
                                }
                                """);
        return path;
    }

    private static void AssertBanner(string layoutPath, string ladderName)
    {
        var layout = File.ReadAllText(layoutPath);
        Assert.Single(Regex.Matches(layout, NameWidgetName).Cast<Match>());
        Assert.Single(Regex.Matches(layout, RuntimeWidgetName).Cast<Match>());
        Assert.Contains(ladderName, layout);
        Assert.Contains("Runs ", layout);
        Assert.Contains(" local", layout);
        Assert.DoesNotContain("\\nRuns", layout);
    }

    private static void AssertBannerIsAFullScreenOverlay(string layoutPath)
    {
        var layout = File.ReadAllText(layoutPath);
        Assert.True(
            layout.IndexOf(NameWidgetName, StringComparison.Ordinal)
            > layout.IndexOf("D2RReimaginedModInfo", StringComparison.Ordinal));
        Assert.Contains("\"x\": -2690, \"y\": 160, \"width\": 1600, \"height\": 80", layout);
        Assert.Contains("\"x\": -2690, \"y\": 240, \"width\": 1600, \"height\": 60", layout);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }
    }
}
