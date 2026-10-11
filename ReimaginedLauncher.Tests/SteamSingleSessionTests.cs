using ReimaginedLauncher.Utilities;
using Xunit;

namespace ReimaginedLauncher.Tests;

public sealed class SteamSingleSessionTests
{
    [Theory]
    [InlineData(LaunchExperience.Online)]
    [InlineData(LaunchExperience.Offline)]
    [InlineData(LaunchExperience.Ladder)]
    public void DedicatedRouteRequiresMatchingSteamShortcutIdentity(LaunchExperience experience)
    {
        var profile = new InstallationProfile { Type = InstallationType.Steam, LaunchExperience = experience };
        const uint shortcut = 0xA1234567;
        var game = (((ulong)shortcut << 32) | 0x02000000).ToString();
        Assert.True(GameLauncherService.UsesSingleSteamSession(profile, "dedicated", "2536520", game, shortcut.ToString(), true));
        Assert.False(GameLauncherService.UsesSingleSteamSession(profile, "dedicated", "2536520", "2536520", shortcut.ToString(), true));
        Assert.False(GameLauncherService.UsesSingleSteamSession(profile, "dedicated", "wrong", game, shortcut.ToString(), true));
        Assert.False(GameLauncherService.UsesSingleSteamSession(profile, null, "2536520", game, shortcut.ToString(), true));
        Assert.False(GameLauncherService.UsesSingleSteamSession(profile, "1", "2536520", "2536520", shortcut.ToString(), true));
        Assert.False(GameLauncherService.UsesSingleSteamSession(profile, "dedicated", "2536520", game, shortcut.ToString(), false));
        profile.UseSteamHelperFallback = true;
        Assert.False(GameLauncherService.UsesSingleSteamSession(profile, "dedicated", "2536520", game, shortcut.ToString(), true));
        profile.UseSteamHelperFallback = false;
        profile.Type = InstallationType.Lutris;
        Assert.False(GameLauncherService.UsesSingleSteamSession(profile, "dedicated", "2536520", game, shortcut.ToString(), true));
    }
    [Fact]
    public void SetupSeedsCombinedLayoutAndPreservesUnrelatedControllerEntries()
    {
        const string original = "\"controller_config\"\n{\n\"other game\" { \"template\" \"custom.vdf\" }\n}\n";
        var updated = SteamControllerLayout.CarryForward(original, "D2R Reimagined (Native Launcher)",
            "Diablo II: Reimagined (Native Launcher)", true, true, "controller_neptune_reimagined.vdf");
        Assert.Contains("controller_neptune_reimagined.vdf", updated);
        Assert.Contains("\"other game\" { \"template\" \"custom.vdf\" }", updated);
        Assert.Equal(updated, SteamControllerLayout.CarryForward(updated, "D2R Reimagined (Native Launcher)",
            "Diablo II: Reimagined (Native Launcher)", true, true, "controller_neptune_reimagined.vdf"));
    }

    [Fact]
    public void HelperFallbackDefaultsOffAndPersistsWhenSelected()
    {
        Assert.False(System.Text.Json.JsonSerializer.Deserialize<InstallationProfile>("{}")!.UseSteamHelperFallback);
        var profile = new InstallationProfile { UseSteamHelperFallback = true };
        Assert.True(System.Text.Json.JsonSerializer.Deserialize<InstallationProfile>(
            System.Text.Json.JsonSerializer.Serialize(profile))!.UseSteamHelperFallback);
    }

    [Fact]
    public void AutomaticSessionInitializationOnlyMatchesTheInstalledOwnedLauncher()
    {
        var path = Path.Combine(Path.GetTempPath(), "owned launcher", "Launcher.AppImage");
        var launcher = new LauncherSteamShortcut("Diablo II: Reimagined (Native Launcher)",
            "\"" + Path.GetFullPath(path) + "\"", "", "D2R Reimagined (Native Launcher)");
        var registration = new SteamHandoffRegistration(123, "/steam", path);
        Assert.True(SteamGameHandoff.MatchesLauncherSession(registration, path, launcher.AppId.ToString(), launcher.GameId.ToString()));
        Assert.False(SteamGameHandoff.MatchesLauncherSession(registration, path, "2536520", "2536520"));
        Assert.False(SteamGameHandoff.MatchesLauncherSession(registration, path + ".other", launcher.AppId.ToString(), launcher.GameId.ToString()));
        Assert.False(SteamGameHandoff.MatchesLauncherSession(registration, path, "123", launcher.GameId.ToString()));
        Assert.False(SteamGameHandoff.MatchesLauncherSession(registration, null, launcher.AppId.ToString(), launcher.GameId.ToString()));
    }

    [Fact]
    public void CombinedTemplateOnlyChangesMouseTrackpadPress()
    {
        const string source = "\"controller_mappings\" { \"group\" { \"mode\" \"joystick_move\" \"binding\" \"xinput_button JOYSTICK_RIGHT\" } \"group\" { \"mode\" \"absolute_mouse\" \"inputs\" { \"click\" { \"binding\" \"xinput_button JOYSTICK_RIGHT\" } } } }";
        var updated = SteamControllerLayout.BuildCombinedTemplate(source);
        Assert.Contains("\"mode\" \"joystick_move\" \"binding\" \"xinput_button JOYSTICK_RIGHT\"", updated);
        Assert.Contains("\"mode\" \"absolute_mouse\" \"inputs\" { \"click\" { \"binding\" \"mouse_button LEFT\"", updated);
        Assert.Equal(source, updated.Replace("\"binding\" \"mouse_button LEFT\"", "\"binding\" \"xinput_button JOYSTICK_RIGHT\""));
        Assert.Throws<InvalidDataException>(() => SteamControllerLayout.BuildCombinedTemplate(source.Replace("absolute_mouse", "joystick_move")));
        Assert.Throws<InvalidDataException>(() => SteamControllerLayout.BuildCombinedTemplate(source + "garbage"));
    }
}
