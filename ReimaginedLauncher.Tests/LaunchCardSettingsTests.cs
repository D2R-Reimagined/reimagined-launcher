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
}
