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

    [Fact]
    public void SanitizeSettingsPreservesSelectedDuplicateProfileAndRestoresMutatedSiblingInPlace()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "launcher-dup-profiles-" + Guid.NewGuid().ToString("N"));
        var bnetDir = Path.Combine(tempRoot, "BattleNetD2R");
        var steamDir = Path.Combine(tempRoot, "CustomSteamD2R");
        Directory.CreateDirectory(bnetDir);
        Directory.CreateDirectory(steamDir);
        try
        {
            File.WriteAllText(Path.Combine(bnetDir, "D2R.exe"), "bnet");
            File.WriteAllText(Path.Combine(steamDir, "D2R.exe"), "steam");
            File.WriteAllText(Path.Combine(steamDir, "steam_api64.dll"), "steam");

            var mutatedFirstProfile = new InstallationProfile
            {
                Type = InstallationType.Steam,
                InstallDirectory = bnetDir,
                PlayersCount = 5,
                RemoveSplashVfx = true
            };
            var selectedSecondSteamProfile = new InstallationProfile
            {
                Type = InstallationType.Steam,
                InstallDirectory = steamDir,
                PlayersCount = 8,
                WindowedMode = true,
                BackupAmount = 25
            };

            var settings = new AppSettings
            {
                Profiles =
                [
                    mutatedFirstProfile,
                    selectedSecondSteamProfile,
                    new InstallationProfile { Type = InstallationType.D2RMM }
                ],
                SelectedProfileIndex = 1
            };

            SettingsManager.SanitizeSettings(settings);

            Assert.Equal(3, settings.Profiles.Count);
            Assert.Same(mutatedFirstProfile, settings.Profiles[0]);
            Assert.Equal(InstallationType.BattleNet, settings.Profiles[0].Type);
            Assert.Equal(5, settings.Profiles[0].PlayersCount);
            Assert.True(settings.Profiles[0].RemoveSplashVfx);

            Assert.Same(selectedSecondSteamProfile, settings.Profiles[1]);
            Assert.Equal(InstallationType.Steam, settings.Profiles[1].Type);
            Assert.Equal(1, settings.SelectedProfileIndex);
            Assert.Same(selectedSecondSteamProfile, settings.CurrentProfile);
            Assert.Equal(8, settings.CurrentProfile.PlayersCount);
            Assert.True(settings.CurrentProfile.WindowedMode);
            Assert.Equal(25, settings.CurrentProfile.BackupAmount);

            var launcherService = new GameLauncherService();
            Assert.Equal(InstallationType.BattleNet, launcherService.DetectInstallationType(bnetDir));
            Assert.Equal(InstallationType.Steam, launcherService.DetectInstallationType(steamDir));

            // Also verify pure duplicate Steam profiles where the second Steam profile is selected:
            var firstDuplicateSteam = new InstallationProfile
            {
                Type = InstallationType.Steam,
                InstallDirectory = steamDir,
                PlayersCount = 1
            };
            var secondSelectedSteam = new InstallationProfile
            {
                Type = InstallationType.Steam,
                PlayersCount = 6,
                NoSound = true
            };
            var duplicateSettings = new AppSettings
            {
                Profiles =
                [
                    firstDuplicateSteam,
                    secondSelectedSteam,
                    new InstallationProfile { Type = InstallationType.D2RMM }
                ],
                SelectedProfileIndex = 1
            };

            SettingsManager.SanitizeSettings(duplicateSettings);

            Assert.Same(secondSelectedSteam, duplicateSettings.Profiles[1]);
            Assert.Equal(1, duplicateSettings.SelectedProfileIndex);
            Assert.Same(secondSelectedSteam, duplicateSettings.CurrentProfile);
            Assert.Equal(6, duplicateSettings.CurrentProfile.PlayersCount);
            Assert.True(duplicateSettings.CurrentProfile.NoSound);
            Assert.Equal(steamDir, duplicateSettings.CurrentProfile.InstallDirectory);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }
}
