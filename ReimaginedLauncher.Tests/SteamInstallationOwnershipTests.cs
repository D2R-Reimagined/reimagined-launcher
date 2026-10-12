using ReimaginedLauncher.Utilities;
using Xunit;

namespace ReimaginedLauncher.Tests;

public sealed class SteamInstallationOwnershipTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), $"steam-ownership-{Guid.NewGuid():N}");
    private string NativeRoot => Path.Combine(_home, ".local", "share", "Steam");
    private string FlatpakRoot => Path.Combine(_home, ".var", "app", "com.valvesoftware.Steam", "data", "Steam");

    private string CreateGame(string library)
    {
        var game = Path.Combine(library, "steamapps", "common", "Diablo II Resurrected");
        Directory.CreateDirectory(game);
        File.WriteAllText(Path.Combine(game, "D2R.exe"), "");
        return game;
    }

    private string? FindExecutable(string name) => name is "steam" or "flatpak" ? Path.Combine(_home, "bin", name) : null;

    [Fact]
    public void FlatpakGameSelectsFlatpakClientWhenNativeSteamAlsoExists()
    {
        CreateGame(NativeRoot);
        var game = CreateGame(FlatpakRoot);

        Assert.Equal(NativeRoot, GameLauncherService.GetSteamInstallPath(_home));
        Assert.Equal(FlatpakRoot, GameLauncherService.GetSteamInstallPath(_home, game));
        var executable = GameLauncherService.FindLinuxSteamExecutable(game, _home, FindExecutable);
        Assert.Equal(FindExecutable("flatpak"), executable);
        Assert.Equal("run com.valvesoftware.Steam ", GameLauncherService.GetSteamArgumentPrefix(executable!, isLinux: true));
        Assert.True(GameLauncherService.IsSteamFlatpakInstall(game, _home));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void NativeRegistrationDoesNotCaptureFlatpakOfflineProfile(bool gamingMode, bool registered)
    {
        CreateGame(NativeRoot);
        var profile = new InstallationProfile
        {
            Type = InstallationType.Steam,
            InstallDirectory = CreateGame(FlatpakRoot),
            LaunchExperience = LaunchExperience.Offline
        };

        Assert.False(GameLauncherService.UsesSteamHandoff(profile, isLinux: true, gamingMode, registered, _home));
    }

    [Fact]
    public void NativeGameKeepsNativeClientAndHandoffWhenFlatpakAlsoExists()
    {
        CreateGame(FlatpakRoot);
        var game = CreateGame(NativeRoot);
        var profile = new InstallationProfile { Type = InstallationType.Steam, InstallDirectory = game };

        Assert.Equal(NativeRoot, GameLauncherService.GetSteamInstallPath(_home, game));
        var executable = GameLauncherService.FindLinuxSteamExecutable(game, _home, FindExecutable);
        Assert.Equal(FindExecutable("steam"), executable);
        Assert.Empty(GameLauncherService.GetSteamArgumentPrefix(executable!, isLinux: true));
        Assert.True(GameLauncherService.UsesSteamHandoff(profile, isLinux: true, isGamingMode: false, hasRegistration: true, _home));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExternalLibraryUsesItsOwningSteamClient(bool flatpak)
    {
        CreateGame(NativeRoot);
        CreateGame(FlatpakRoot);
        var game = CreateGame(Path.Combine(_home, "External Library"));
        var owner = flatpak ? FlatpakRoot : NativeRoot;
        var libraryPath = Path.Combine(_home, "External Library").Replace("\\", "\\\\");
        File.WriteAllText(Path.Combine(owner, "steamapps", "libraryfolders.vdf"),
            $"\"libraryfolders\" {{ \"1\" {{ \"path\" \"{libraryPath}\" }} }}");

        Assert.Equal(owner, GameLauncherService.GetSteamInstallPath(_home, game));
        Assert.Equal(FindExecutable(flatpak ? "flatpak" : "steam"),
            GameLauncherService.FindLinuxSteamExecutable(game, _home, FindExecutable));
        Assert.Equal(flatpak, GameLauncherService.IsSteamFlatpakInstall(game, _home));
    }

    [Fact]
    public void MissingFlatpakExecutableDoesNotSelectUnrelatedNativeClient()
    {
        CreateGame(NativeRoot);
        var game = CreateGame(FlatpakRoot);
        Assert.Null(GameLauncherService.FindLinuxSteamExecutable(game, _home,
            name => name == "steam" ? FindExecutable(name) : null));
    }

    public void Dispose()
    {
        if (Directory.Exists(_home)) Directory.Delete(_home, true);
    }
}
