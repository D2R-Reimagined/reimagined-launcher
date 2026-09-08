using ReimaginedLauncher.Utilities;
using Xunit;

namespace ReimaginedLauncher.Tests;

public sealed class LutrisArgumentsTests : IDisposable
{
    private readonly string _testDirectory = Path.Combine(
        Path.GetTempPath(),
        $"reimagined-lutris-args-tests-{Guid.NewGuid():N}");

    private const string ConfigYaml = """
    game:
      args: --offline
      desktop_integration: false
      exe: /home/player/Games/D2R/D2RLoader.exe
      prefix: /home/player/Games/D2R/pfx
    system:
      env:
        DXVK_HUD: compiler
    wine:
      overrides:
        winhttp: n,b
      version: proton-cachyos-slr
    """;

    [Fact]
    public void BuildArgsKeepsForeignArgumentsAndAppendsTheEnabledOptions()
    {
        var profile = new InstallationProfile
        {
            EnableRespec = true,
            PlayersCount = 8
        };

        Assert.Equal(
            "--offline -mod Reimagined -txt -enablerespec -players 8",
            LutrisArgumentsService.BuildArgs("--offline -mod Reimagined -txt", profile));
    }

    [Fact]
    public void BuildArgsRemovesOptionsThatAreNoLongerEnabled()
    {
        var profile = new InstallationProfile();

        Assert.Equal(
            "--offline",
            LutrisArgumentsService.BuildArgs("--offline -enablerespec -players 8 -nosound", profile));
    }

    [Fact]
    public void BuildArgsRemovesTheValueBelongingToAFlag()
    {
        var profile = new InstallationProfile();

        Assert.Equal(
            "--offline --fullscreen",
            LutrisArgumentsService.BuildArgs("--offline -seed 12345 --fullscreen", profile));
    }

    [Fact]
    public void BuildArgsDoesNotSwallowANonNumericTokenAfterAValueFlag()
    {
        var profile = new InstallationProfile();

        Assert.Equal(
            "--offline",
            LutrisArgumentsService.BuildArgs("-players --offline", profile));
    }

    [Fact]
    public void BuildArgsReplacesAnExistingValueRatherThanDuplicatingIt()
    {
        var profile = new InstallationProfile { PlayersCount = 3 };

        Assert.Equal("-players 3", LutrisArgumentsService.BuildArgs("-players 8", profile));
    }

    [Fact]
    public void BuildArgsMatchesExistingFlagsCaseInsensitively()
    {
        var profile = new InstallationProfile();

        Assert.Equal(string.Empty, LutrisArgumentsService.BuildArgs("-EnableRespec -NOSOUND", profile));
    }

    [Fact]
    public void BuildArgsIgnoresAnOutOfRangePlayerCount()
    {
        var profile = new InstallationProfile { PlayersCount = 99 };

        Assert.Equal("--offline", LutrisArgumentsService.BuildArgs("--offline", profile));
    }

    [Fact]
    public void BuildArgsWritesTheSeedWhenItIsEnabled()
    {
        var profile = new InstallationProfile { CustomMapSeedEnabled = true, CustomMapSeed = 4242 };

        Assert.Equal("-seed 4242", LutrisArgumentsService.BuildArgs(null, profile));
    }

    [Fact]
    public void ImportReadsTheOptionsAlreadyPresent()
    {
        var profile = new InstallationProfile();

        LutrisArgumentsService.ImportInto(profile, "--offline -enablerespec -players 4 -seed 7 -nosound");

        Assert.True(profile.EnableRespec);
        Assert.True(profile.NoSound);
        Assert.Equal(4, profile.PlayersCount);
        Assert.True(profile.CustomMapSeedEnabled);
        Assert.Equal(7u, profile.CustomMapSeed);
        Assert.False(profile.ResetOfflineMaps);
        Assert.False(profile.NoRumble);
        Assert.False(profile.ForceDesktop);
    }

    [Fact]
    public void ImportClearsOptionsThatAreNotPresent()
    {
        var profile = new InstallationProfile
        {
            EnableRespec = true,
            PlayersCount = 8,
            CustomMapSeedEnabled = true,
            CustomMapSeed = 99
        };

        LutrisArgumentsService.ImportInto(profile, "--offline");

        Assert.False(profile.EnableRespec);
        Assert.Null(profile.PlayersCount);
        Assert.False(profile.CustomMapSeedEnabled);
    }

    [Fact]
    public void UpdateArgsLineReplacesOnlyTheArgsLine()
    {
        var updated = LutrisArgumentsService.UpdateArgsLine(ConfigYaml, "--offline -enablerespec");

        Assert.Contains("  args: '--offline -enablerespec'", updated);
        Assert.Contains("  exe: /home/player/Games/D2R/D2RLoader.exe", updated);
        Assert.Contains("  prefix: /home/player/Games/D2R/pfx", updated);
        Assert.Contains("    DXVK_HUD: compiler", updated);
        Assert.Contains("  version: proton-cachyos-slr", updated);
        Assert.DoesNotContain("args: --offline\n", updated);
    }

    [Fact]
    public void UpdateArgsLineAddsTheKeyWhenTheGameHasNoArguments()
    {
        const string yaml = "game:\n  exe: /home/player/Games/D2R/D2R.exe\n";

        var updated = LutrisArgumentsService.UpdateArgsLine(yaml, "-nosound");

        Assert.Equal("game:\n  args: '-nosound'\n  exe: /home/player/Games/D2R/D2R.exe\n", updated);
    }

    [Fact]
    public void UpdateArgsLineRemovesTheKeyWhenNothingIsLeft()
    {
        var updated = LutrisArgumentsService.UpdateArgsLine(ConfigYaml, string.Empty);

        Assert.DoesNotContain("args:", updated);
        Assert.Contains("  exe: /home/player/Games/D2R/D2RLoader.exe", updated);
    }

    [Fact]
    public void UpdateArgsLineReplacesAFoldedValue()
    {
        const string yaml = """
        game:
          args: --offline --a-very-long-argument-that-pyyaml-wrapped-at-eighty-columns
            --continued-here
          exe: /home/player/Games/D2R/D2R.exe
        """;

        var updated = LutrisArgumentsService.UpdateArgsLine(yaml, "-nosound");

        Assert.DoesNotContain("--continued-here", updated);
        Assert.Contains("  args: '-nosound'", updated);
        Assert.Contains("  exe: /home/player/Games/D2R/D2R.exe", updated);
    }

    [Fact]
    public void UpdateArgsLineIgnoresAnArgsKeyOutsideTheGameSection()
    {
        const string yaml = "game:\n  exe: /home/player/D2R.exe\nsystem:\n  args: not-the-game\n";

        var updated = LutrisArgumentsService.UpdateArgsLine(yaml, "-nosound");

        Assert.Contains("  args: not-the-game", updated);
        Assert.Contains("game:\n  args: '-nosound'", updated);
    }

    [Fact]
    public void UpdateArgsLineReturnsNullWithoutAGameSection()
    {
        Assert.Null(LutrisArgumentsService.UpdateArgsLine("wine:\n  version: proton\n", "-nosound"));
    }

    [Fact]
    public void ApplyToGameConfigRewritesTheNewestConfigForTheSlug()
    {
        var gamesDirectory = Path.Combine(_testDirectory, "games");
        Directory.CreateDirectory(gamesDirectory);
        var configPath = Path.Combine(gamesDirectory, "diablo-ii-resurrected-1788284282.yml");
        File.WriteAllText(configPath, ConfigYaml);

        var profile = new InstallationProfile { EnableRespec = true, PlayersCount = 8 };

        Assert.True(LutrisArgumentsService.ApplyToGameConfig(
            gamesDirectory, "diablo-ii-resurrected", profile));

        var written = File.ReadAllText(configPath);
        Assert.Equal(
            "--offline -enablerespec -players 8",
            LutrisService.ExtractGameArgs(written));
        Assert.Equal("/home/player/Games/D2R/D2RLoader.exe", LutrisService.ExtractGameExePath(written));
        Assert.Equal("/home/player/Games/D2R/pfx", LutrisService.ExtractGamePrefixPath(written));
        Assert.Contains("proton-cachyos-slr", written);
        Assert.False(File.Exists(configPath + ".launcher-tmp"));
    }

    [Fact]
    public void ApplyToGameConfigLeavesTheFileAloneWhenNothingChanges()
    {
        var gamesDirectory = Path.Combine(_testDirectory, "games");
        Directory.CreateDirectory(gamesDirectory);
        var configPath = Path.Combine(gamesDirectory, "diablo-ii-resurrected-1788284282.yml");
        File.WriteAllText(configPath, ConfigYaml);

        Assert.True(LutrisArgumentsService.ApplyToGameConfig(
            gamesDirectory, "diablo-ii-resurrected", new InstallationProfile()));

        Assert.Equal(ConfigYaml, File.ReadAllText(configPath));
    }

    [Fact]
    public void ApplyToGameConfigFailsWhenThereIsNoConfig()
    {
        var gamesDirectory = Path.Combine(_testDirectory, "games");
        Directory.CreateDirectory(gamesDirectory);

        Assert.False(LutrisArgumentsService.ApplyToGameConfig(
            gamesDirectory, "diablo-ii-resurrected", new InstallationProfile()));
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }
    }
}
