using ReimaginedLauncher.Utilities;
using Xunit;

namespace ReimaginedLauncher.Tests;

public sealed class SteamProtonLaunchTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"steam-proton-{Guid.NewGuid():N}");

    private InstallationProfile CreateProfile(bool initializePrefix)
    {
        var steam = Path.Combine(_root, "Steam");
        var game = Path.Combine(steam, "steamapps", "common", "Diablo II Resurrected");
        Directory.CreateDirectory(game);
        File.WriteAllText(Path.Combine(steam, "proton"), "");
        File.WriteAllText(Path.Combine(game, "D2RLoader.exe"), "");
        File.WriteAllText(Path.Combine(steam, "steamapps", "appmanifest_2536520.acf"), "");
        if (initializePrefix)
            Directory.CreateDirectory(Path.Combine(steam, "steamapps", "compatdata", "2536520", "pfx"));
        return new InstallationProfile { Type = InstallationType.Steam, InstallDirectory = game,
            ProtonExecutable = Path.Combine(steam, "proton"), LaunchExperience = LaunchExperience.Online };
    }

    [Theory]
    [InlineData(LaunchExperience.Online)]
    [InlineData(LaunchExperience.Ladder)]
    public void ProtonRetainsConfiguredLaunchOptions(LaunchExperience experience)
    {
        var profile = new InstallationProfile { LaunchExperience = experience, ForceDesktop = true,
            NoRumble = true, WindowedMode = true, NoSound = true };
        var parameters = GameLauncherService.BuildLaunchParameters(profile);
        var arguments = GameLauncherService.BuildSteamProtonArguments("/Games with spaces/D2RLoader.exe", parameters);
        Assert.Contains("-forcedesktop", arguments);
        Assert.Contains("-norumble", arguments);
        Assert.Contains("-w", arguments);
        Assert.Contains("-nosound", arguments);
        Assert.Contains(experience == LaunchExperience.Ladder ? "-mod ReimaginedLadder" : "-mod Reimagined", arguments);
        Assert.Equal(experience == LaunchExperience.Ladder, arguments.Contains("-resetofflinemaps"));
        Assert.StartsWith("run \"/Games with spaces/D2RLoader.exe\" ", arguments);
    }

    [Fact]
    public void ProtonRetainsExplicitArgumentOverrides()
    {
        const string arguments = "-mod CustomMod -txt -forcedesktop -ip 127.0.0.1";
        Assert.EndsWith(arguments, GameLauncherService.BuildSteamProtonArguments("/Games/D2RLoader.exe", arguments));
    }

    [Fact]
    public void MissingPrefixIsRejectedWithoutCreatingIt()
    {
        var profile = CreateProfile(false);
        Assert.False(GameLauncherService.IsValidSteamProtonLaunch(profile, Path.Combine(_root, "Steam"),
            Path.Combine(profile.InstallDirectory!, "D2RLoader.exe"), out var error));
        Assert.Contains("sign in once", error);
        Assert.False(Directory.Exists(Path.Combine(_root, "Steam", "steamapps", "compatdata")));
    }

    [Fact]
    public void ExistingOfficialPrefixIsAccepted()
    {
        var profile = CreateProfile(true);
        Assert.True(GameLauncherService.IsValidSteamProtonLaunch(profile, Path.Combine(_root, "Steam"),
            Path.Combine(profile.InstallDirectory!, "D2RLoader.exe"), out var error));
        Assert.Empty(error);
    }

    [Fact]
    public void GameOutsideSteamLibraryIsRejected()
    {
        var profile = CreateProfile(true);
        profile.InstallDirectory = Path.Combine(_root, "Copied Game");
        Directory.CreateDirectory(profile.InstallDirectory);
        Assert.False(GameLauncherService.IsValidSteamProtonLaunch(profile, Path.Combine(_root, "Steam"),
            Path.Combine(profile.InstallDirectory, "D2RLoader.exe"), out var error));
        Assert.Contains("official Steam", error);
    }

    [Fact]
    public void SteamRootSupportsLowercaseSteamSymlinkLocation()
    {
        var steam = Path.Combine(_root, ".steam", "steam");
        Directory.CreateDirectory(steam);
        Assert.Equal(steam, GameLauncherService.GetSteamInstallPath(_root));
    }

    [Fact]
    public void WindowsSteamPreviewKeepsNativeLoaderCommand()
    {
        if (!OperatingSystem.IsWindows()) return;
        var previous = MainWindow.Settings;
        try
        {
            var profile = CreateProfile(true);
            profile.ForceDesktop = true;
            File.WriteAllText(Path.Combine(profile.InstallDirectory!, "D2R.exe"), "");
            MainWindow.Settings = new AppSettings { Profiles = [profile], SelectedProfileIndex = 0 };
            var command = new GameLauncherService().BuildLaunchCommand();
            Assert.StartsWith("\"" + Path.Combine(profile.InstallDirectory!, "D2RLoader.exe") + "\"", command);
            Assert.Contains("-forcedesktop", command);
        }
        finally
        {
            MainWindow.Settings = previous;
        }
    }

    [Fact]
    public void ProtonDetectionUsesInitializedPrefixAcrossDifferentToolLibrary()
    {
        var profile = CreateProfile(true);
        profile.ProtonExecutable = null;
        var tool = Path.Combine(_root, "Other Library", "Custom Proton");
        Directory.CreateDirectory(tool);
        File.WriteAllText(Path.Combine(tool, "proton"), "");
        WriteProtonMetadata(profile, tool);
        Assert.True(GameLauncherService.TryDetectSteamProtonExecutable(profile));
        Assert.Equal(Path.Combine(tool, "proton"), profile.ProtonExecutable);
    }

    [Fact]
    public void ProtonDetectionPreservesManualOverride()
    {
        var profile = CreateProfile(true);
        var selected = profile.ProtonExecutable;
        WriteProtonMetadata(profile, Path.Combine(_root, "Other Proton"));
        Assert.False(GameLauncherService.TryDetectSteamProtonExecutable(profile));
        Assert.Equal(selected, profile.ProtonExecutable);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProtonDetectionDoesNotGuessMissingOrStaleMetadata(bool stale)
    {
        var profile = CreateProfile(true);
        profile.ProtonExecutable = null;
        if (stale) WriteProtonMetadata(profile, Path.Combine(_root, "Removed Proton"));
        Assert.False(GameLauncherService.TryDetectSteamProtonExecutable(profile));
        Assert.Null(profile.ProtonExecutable);
    }

    [Fact]
    public void ProtonDetectionRejectsAmbiguousTools()
    {
        var profile = CreateProfile(true);
        profile.ProtonExecutable = null;
        var tools = new[] { Path.Combine(_root, "One"), Path.Combine(_root, "Two") };
        foreach (var tool in tools)
        {
            Directory.CreateDirectory(tool);
            File.WriteAllText(Path.Combine(tool, "proton"), "");
        }
        WriteProtonMetadata(profile, tools);
        Assert.False(GameLauncherService.TryDetectSteamProtonExecutable(profile));
        Assert.Null(profile.ProtonExecutable);
    }

    private void WriteProtonMetadata(InstallationProfile profile, params string[] tools)
    {
        var steamApps = Path.Combine(_root, "Steam", "steamapps");
        File.WriteAllLines(Path.Combine(steamApps, "compatdata", "2536520", "config_info"),
            tools.SelectMany(tool => new[] { Path.Combine(tool, "files", "share", "fonts") + Path.DirectorySeparatorChar,
                Path.Combine(tool, "files", "lib") + Path.DirectorySeparatorChar,
                Path.Combine(tool, "files", "share", "default_pfx") + Path.DirectorySeparatorChar }));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
