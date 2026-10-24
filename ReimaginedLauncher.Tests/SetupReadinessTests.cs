using System.Text.Json;
using ReimaginedLauncher.Utilities;
using Xunit;

namespace ReimaginedLauncher.Tests;

public sealed class SetupReadinessTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "launcher-setup-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void SetupFlagsRoundTripWithoutMarkingDeferredSetupComplete()
    {
        var legacy = JsonSerializer.Deserialize<AppSettings>("{}")!;
        Assert.False(legacy.FirstTimeSetupShown);
        legacy.FirstTimeSetupShown = true;
        var deferred = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(legacy))!;
        Assert.True(deferred.FirstTimeSetupShown);
        Assert.False(deferred.FirstTimeSetupCompleted);
    }

    [Fact]
    public void StaleValidationFlagCannotConfirmMissingInstallation()
    {
        Assert.NotNull(SetupReadiness.GetIssue(new() { Type = InstallationType.BattleNet, InstallDirectory = root, IsInstallDirectoryValidated = true }));
    }

    [Fact]
    public void SteamRequiresGameMarkerAndSteamExecutable()
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "D2R.exe"), "fixture");
        var profile = new InstallationProfile { Type = InstallationType.Steam, InstallDirectory = root };
        Assert.NotNull(SetupReadiness.GetIssue(profile));
        File.WriteAllText(Path.Combine(root, "steam_api64.dll"), "fixture");
        Assert.NotNull(SetupReadiness.GetIssue(profile));
        profile.SteamDirectory = Path.Combine(root, "steam.exe");
        File.WriteAllText(profile.SteamDirectory, "fixture");
        Assert.Null(SetupReadiness.GetIssue(profile));
    }

    [Fact]
    public void D2RmmRequiresItsExistingModsDirectoryAndExecutable()
    {
        Directory.CreateDirectory(root);
        var mods = Path.Combine(root, "mods");
        File.WriteAllText(Path.Combine(root, "D2RMM.exe"), "fixture");
        var profile = new InstallationProfile { Type = InstallationType.D2RMM, InstallDirectory = mods };
        Assert.NotNull(SetupReadiness.GetIssue(profile));
        Directory.CreateDirectory(mods);
        Assert.Null(SetupReadiness.GetIssue(profile));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void ExistingAccountAndInstallationSkipNewClientWalkthrough(bool signedIn, bool savedAccount)
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "D2R.exe"), "fixture");
        var settings = new AppSettings
        {
            Profiles = [new() { Type = InstallationType.BattleNet, InstallDirectory = root }],
            D2RReimaginedRefreshToken = savedAccount ? "fixture" : null
        };
        Assert.False(SetupReadiness.ShouldOfferSetup(settings, signedIn));
        settings.Profiles[0].InstallDirectory = Path.Combine(root, "missing");
        Assert.True(SetupReadiness.ShouldOfferSetup(settings, signedIn));
    }

    [Fact]
    public void SavedNexusAccountWorksOfflineAndAnyConfiguredProfileCounts()
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "D2R.exe"), "fixture");
        var settings = new AppSettings
        {
            NexusModsSSOApiKey = "fixture",
            Profiles = [new() { InstallDirectory = "" }, new() { Type = InstallationType.BattleNet, InstallDirectory = root }]
        };
        Assert.False(SetupReadiness.ShouldOfferSetup(settings, false));
        settings.NexusModsSSOApiKey = null;
        Assert.True(SetupReadiness.ShouldOfferSetup(settings, false));
        settings.FirstTimeSetupCompleted = true;
        Assert.False(SetupReadiness.ShouldOfferSetup(settings, false));
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
