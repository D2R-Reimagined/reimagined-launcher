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
}
