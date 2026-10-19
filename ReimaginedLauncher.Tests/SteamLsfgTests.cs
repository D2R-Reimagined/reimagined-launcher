using System.Text.Json;
using ReimaginedLauncher.Utilities;
using Xunit;

namespace ReimaginedLauncher.Tests;

public sealed class SteamLsfgTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "lsfg test " + Guid.NewGuid().ToString("N"));

    private SteamHandoffRequest Request(LaunchExperience experience, bool enabled)
    {
        var steam = Path.Combine(_home, "Steam");
        var apps = Path.Combine(steam, "steamapps");
        var game = Path.Combine(apps, "common", "Diablo II Resurrected");
        Directory.CreateDirectory(game);
        Directory.CreateDirectory(Path.Combine(apps, "compatdata", "2536520", "pfx"));
        File.WriteAllText(Path.Combine(apps, "appmanifest_2536520.acf"), "");
        var proton = Path.Combine(_home, "Proton Tool", "proton");
        Directory.CreateDirectory(Path.GetDirectoryName(proton)!);
        File.WriteAllText(proton, "");
        var profile = new InstallationProfile { LaunchExperience = experience, ForceDesktop = true, ResetOfflineMaps = true };
        return new SteamHandoffRequest("test", DateTimeOffset.UtcNow, steam, game, proton,
            Path.Combine(game, experience == LaunchExperience.Offline ? "D2R.exe" : "D2RLoader.exe"),
            SteamGameHandoff.SplitArguments(GameLauncherService.BuildLaunchParameters(profile)), enabled);
    }

    [Theory]
    [InlineData(LaunchExperience.Online, false)]
    [InlineData(LaunchExperience.Offline, false)]
    [InlineData(LaunchExperience.Ladder, false)]
    [InlineData(LaunchExperience.Online, true)]
    [InlineData(LaunchExperience.Offline, true)]
    [InlineData(LaunchExperience.Ladder, true)]
    public void LsfgWrapsProtonWithoutChangingGameArgumentsOrAuthentication(LaunchExperience experience, bool enabled)
    {
        var request = Request(experience, enabled);
        var wrapper = Path.Combine(_home, "lsfg");
        if (enabled)
        {
            File.WriteAllText(wrapper, "#!/bin/sh\nexec \"$@\"\n");
            if (OperatingSystem.IsLinux()) File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        }
        var original = SteamGameHandoff.CreateGameStartInfo(request with { EnableLsfg = false }, _home);
        var info = SteamGameHandoff.CreateGameStartInfo(request, _home);
        Assert.Equal(enabled ? wrapper : request.ProtonExecutable, info.FileName);
        Assert.Equal(enabled ? new[] { request.ProtonExecutable }.Concat(original.ArgumentList) : original.ArgumentList,
            info.ArgumentList);
        Assert.Equal(original.Environment["SteamAppId"], info.Environment["SteamAppId"]);
        Assert.Equal(original.Environment["STEAM_COMPAT_DATA_PATH"], info.Environment["STEAM_COMPAT_DATA_PATH"]);
        original.Environment.TryGetValue("WINEDLLOVERRIDES", out var originalOverrides);
        info.Environment.TryGetValue("WINEDLLOVERRIDES", out var overrides);
        Assert.Equal(originalOverrides, overrides);
        Assert.Equal(original.WorkingDirectory, info.WorkingDirectory);
        Assert.False(info.UseShellExecute);
        Assert.DoesNotContain("%command%", info.ArgumentList);
    }

    [Fact]
    public void MissingWrapperIsAnActionableFailureInsteadOfSilentlyIgnoringLsfg()
    {
        var request = Request(LaunchExperience.Online, true);
        var error = Assert.Throws<InvalidOperationException>(() => SteamGameHandoff.CreateGameStartInfo(request, _home));
        Assert.Contains("~/lsfg", error.Message);
        Assert.Contains("turn off LSFG", error.Message);
    }

    [Fact]
    public void OlderProfilesAndRequestsDefaultToDisabled()
    {
        Assert.False(JsonSerializer.Deserialize<InstallationProfile>("{}")!.EnableLsfg);
        var request = Request(LaunchExperience.Online, true);
        var legacy = JsonSerializer.SerializeToNode(request)!.AsObject();
        legacy.Remove(nameof(SteamHandoffRequest.EnableLsfg));
        Assert.False(legacy.Deserialize<SteamHandoffRequest>()!.EnableLsfg);
        var profile = JsonSerializer.Deserialize<InstallationProfile>(JsonSerializer.Serialize(new InstallationProfile { EnableLsfg = true }));
        Assert.True(profile!.EnableLsfg);
    }

    public void Dispose()
    {
        if (Directory.Exists(_home)) Directory.Delete(_home, true);
    }
}
