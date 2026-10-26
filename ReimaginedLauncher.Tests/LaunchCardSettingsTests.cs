using System.Text.Json;
using ReimaginedLauncher.Utilities;
using Xunit;

namespace ReimaginedLauncher.Tests;

public sealed class LaunchCardSettingsTests
{
    [Fact]
    public void ExistingSettingsWithoutCardStatesUseDefaults()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("{\"UiScale\":1.25}")!;
        Assert.Empty(settings.LaunchCardExpansion);
        Assert.Equal(1.25, settings.UiScale);
    }

    [Fact]
    public void CardChoicesRoundTripAlongsideExistingSettings()
    {
        var settings = new AppSettings
        {
            MinimizeToTrayOnClose = true,
            LaunchCardExpansion = new() { ["Plugins"] = false, ["LootFilter"] = true, ["InstallDirectory"] = false }
        };
        var restored = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
        Assert.True(restored.MinimizeToTrayOnClose);
        Assert.False(restored.LaunchCardExpansion["Plugins"]);
        Assert.True(restored.LaunchCardExpansion["LootFilter"]);
        Assert.False(restored.LaunchCardExpansion["InstallDirectory"]);
    }

    [Fact]
    public void SanitizeSettingsResetsMinimizedOrTinyWindowGeometryAndNullCollections()
    {
        var settings = new AppSettings
        {
            WindowWidth = 0,
            WindowHeight = 200,
            WindowX = -32000,
            WindowY = -32000,
            LastNavigationTag = "",
            Profiles = null!,
            LaunchCardExpansion = null!,
            InstalledLootFilters = null!
        };

        SettingsManager.SanitizeSettings(settings);

        Assert.Null(settings.WindowWidth);
        Assert.Null(settings.WindowHeight);
        Assert.Null(settings.WindowX);
        Assert.Null(settings.WindowY);
        Assert.Equal("Launch", settings.LastNavigationTag);
        Assert.NotNull(settings.Profiles);
        Assert.NotNull(settings.LaunchCardExpansion);
        Assert.NotNull(settings.InstalledLootFilters);
    }

    [Fact]
    public void SanitizeSettingsRestoresMissingProfileTypesSoBattleNetAndSteamCanSwitch()
    {
        var settings = new AppSettings
        {
            Profiles =
            [
                new InstallationProfile
                {
                    Type = InstallationType.Steam,
                    InstallDirectory = @"C:\Games\Steam\steamapps\common\Diablo II Resurrected\"
                }
            ],
            SelectedProfileIndex = 0
        };

        SettingsManager.SanitizeSettings(settings);

        Assert.Equal(3, settings.Profiles.Count);
        Assert.Equal(InstallationType.BattleNet, settings.Profiles[0].Type);
        Assert.Equal(InstallationType.Steam, settings.Profiles[1].Type);
        Assert.Equal(InstallationType.D2RMM, settings.Profiles[2].Type);
        Assert.Equal(InstallationType.Steam, settings.CurrentProfile.Type);

        var bnet = settings.SelectProfile(InstallationType.BattleNet);
        Assert.Equal(InstallationType.BattleNet, bnet.Type);
        Assert.Equal(InstallationType.BattleNet, settings.CurrentProfile.Type);
        Assert.Equal(0, settings.SelectedProfileIndex);

        var steam = settings.SelectProfile(InstallationType.Steam);
        Assert.Equal(InstallationType.Steam, steam.Type);
        Assert.Equal(InstallationType.Steam, settings.CurrentProfile.Type);
        Assert.Equal(1, settings.SelectedProfileIndex);
    }
}
