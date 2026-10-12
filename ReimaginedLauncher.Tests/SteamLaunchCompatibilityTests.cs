using System.Diagnostics;
using ReimaginedLauncher.Utilities;
using Xunit;

namespace ReimaginedLauncher.Tests;

public sealed class SteamLaunchCompatibilityTests
{
    /// <summary>Verifies that Steam integration never captures Battle.net, D2RMM, or Lutris profiles.</summary>
    [Theory]
    [InlineData(InstallationType.BattleNet)]
    [InlineData(InstallationType.D2RMM)]
    [InlineData(InstallationType.Lutris)]
    public void NonSteamProfilesKeepTheirExistingLaunchRoute(InstallationType type)
    {
        const uint shortcut = 0xA1234567;
        var gameId = (((ulong)shortcut << 32) | 0x02000000).ToString();
        foreach (var experience in Enum.GetValues<LaunchExperience>())
        {
            var profile = new InstallationProfile { Type = type, LaunchExperience = experience };
            Assert.False(GameLauncherService.UsesSingleSteamSession(profile, "dedicated", "2536520", gameId, shortcut.ToString(), true));
            Assert.False(GameLauncherService.UsesSingleSteamSession(profile, "dedicated", "2536520", gameId, shortcut.ToString(), false));
            Assert.False(GameLauncherService.UsesSteamHandoff(profile, true, true, true));
            Assert.False(GameLauncherService.UsesSteamHandoff(profile, false, true, true));
        }
    }

    /// <summary>Verifies that Linux children recover the launcher environment before Steam session initialization.</summary>
    [Theory]
    [InlineData("123456", null, null)]
    [InlineData("123456", "original-value", "original-session")]
    [InlineData(null, null, null)]
    public void NormalLinuxChildRestoresOriginalIdentityAndPreservesWineAndLutrisSettings(string? appId, string? enabled, string? sessionId)
    {
        var info = new ProcessStartInfo("lutris", "lutris:rungameid/145") { WorkingDirectory = "/games/d2r" };
        info.Environment["SteamAppId"] = "2536520";
        info.Environment["SteamGameId"] = "registered-launcher-game-id";
        info.Environment["D2RR_SINGLE_STEAM_SESSION"] = "dedicated";
        info.Environment["D2RR_SESSION_APP_ID"] = "modified-session-id";
        info.Environment["LUTRIS_SKIP_INIT"] = "1";
        info.Environment["WINEPREFIX"] = "/original/wine-prefix";
        var original = new Dictionary<string, string?>
        {
            ["SteamAppId"] = appId,
            ["D2RR_SINGLE_STEAM_SESSION"] = enabled,
            ["D2RR_SESSION_APP_ID"] = sessionId
        };

        SteamGameHandoff.RestoreLauncherEnvironment(info, original, true);

        foreach (var (name, value) in original)
            if (value == null) Assert.False(info.Environment.ContainsKey(name));
            else Assert.Equal(value, info.Environment[name]);
        Assert.Equal("registered-launcher-game-id", info.Environment["SteamGameId"]);
        Assert.Equal("1", info.Environment["LUTRIS_SKIP_INIT"]);
        Assert.Equal("/original/wine-prefix", info.Environment["WINEPREFIX"]);
        Assert.Equal("lutris", info.FileName);
        Assert.Equal("lutris:rungameid/145", info.Arguments);
        Assert.Equal("/games/d2r", info.WorkingDirectory);
    }

    /// <summary>Verifies that Windows and uninitialized launchers retain their inherited environment.</summary>
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void WindowsAndOrdinaryLinuxLaunchesLeaveEnvironmentUnchanged(bool linux, bool initialized)
    {
        var info = new ProcessStartInfo("game") { UseShellExecute = !linux };
        info.Environment["SteamAppId"] = "original";
        var before = info.Environment.ToArray();
        var original = initialized ? new Dictionary<string, string?> { ["SteamAppId"] = "changed" } : null;

        SteamGameHandoff.RestoreLauncherEnvironment(info, original, linux);

        Assert.Equal(before, info.Environment.ToArray());
        Assert.Equal(!linux, info.UseShellExecute);
    }
}
