using ReimaginedLauncher.Utilities;
using Xunit;

namespace ReimaginedLauncher.Tests;

public sealed class GlobalChatConfigServiceTests : IDisposable
{
    private const string ApiBaseUrl = "https://api.d2rreimagined.com";
    private const string AccessToken = "token-abc-123";

    private readonly string _installDirectory = Path.Combine(
        Path.GetTempPath(),
        $"reimagined-global-chat-tests-{Guid.NewGuid():N}");

    private string NormalLoaderRoot => Path.Combine(_installDirectory, "mods", "Reimagined", "d2rloader");
    private string LadderLoaderRoot => Path.Combine(_installDirectory, "mods", "ReimaginedLadder", "d2rloader");
    private static string ConfigPathIn(string loaderRoot) => Path.Combine(loaderRoot, "config", "global-chat.toml");

    // The reason this plugin exists as a separate service from chat-relay: it is
    // offered for an ordinary D2RLoader launch, not only a ladder one.
    [Theory]
    [InlineData(InstallationType.BattleNet, LaunchExperience.Online, true)]
    [InlineData(InstallationType.BattleNet, LaunchExperience.Ladder, true)]
    [InlineData(InstallationType.Steam, LaunchExperience.Online, true)]
    [InlineData(InstallationType.BattleNet, LaunchExperience.Offline, false)]
    [InlineData(InstallationType.D2RMM, LaunchExperience.Online, false)]
    [InlineData(InstallationType.D2RMM, LaunchExperience.Ladder, false)]
    public void EligibilityCoversSignedInD2RLoaderLaunches(
        InstallationType type, LaunchExperience experience, bool expected)
    {
        Assert.Equal(expected, GlobalChatConfigService.IsEligible(type, experience));
    }

    // The launcher never installs this plugin - it arrives in the ladder bundle,
    // as an approved extension, or from the website by the player's own hand.
    // Detection is per mod folder, because a ladder launch loads
    // ReimaginedLadder and every other one loads Reimagined.
    [Fact]
    public void InstallationIsDetectedInTheModFolderTheExperienceWillLoad()
    {
        InstallPlugin(NormalLoaderRoot);

        Assert.True(GlobalChatConfigService.IsPluginInstalled(_installDirectory, LaunchExperience.Online));
        Assert.False(GlobalChatConfigService.IsPluginInstalled(_installDirectory, LaunchExperience.Ladder));

        InstallPlugin(LadderLoaderRoot);
        Assert.True(GlobalChatConfigService.IsPluginInstalled(_installDirectory, LaunchExperience.Ladder));
    }

    [Fact]
    public async Task EnablingWritesTheApiAddressAndTokenForAnOnlineLaunch()
    {
        InstallPlugin(NormalLoaderRoot);

        Assert.True(await GlobalChatConfigService.EnableAsync(
            _installDirectory,
            new GlobalChatLaunchSettings(ApiBaseUrl, AccessToken),
            LaunchExperience.Online));

        var toml = await File.ReadAllTextAsync(ConfigPathIn(NormalLoaderRoot));
        Assert.Contains("enabled = true", toml, StringComparison.Ordinal);
        Assert.Contains($"api_base_url = \"{ApiBaseUrl}\"", toml, StringComparison.Ordinal);
        Assert.Contains($"access_token = \"{AccessToken}\"", toml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EnablingRefusesWithoutATokenBecauseTheEndpointIsAuthenticated()
    {
        InstallPlugin(NormalLoaderRoot);

        Assert.False(await GlobalChatConfigService.EnableAsync(
            _installDirectory,
            new GlobalChatLaunchSettings(ApiBaseUrl, "   "),
            LaunchExperience.Online));

        Assert.False(await GlobalChatConfigService.EnableAsync(
            _installDirectory,
            new GlobalChatLaunchSettings("  ", AccessToken),
            LaunchExperience.Online));
    }

    // The core of the arrangement: no plugin, no token written. The launcher
    // must not create a config for a plugin the player has not installed.
    [Fact]
    public async Task EnablingRequiresThePluginToBeInstalledAndWritesNothingWithoutIt()
    {
        Assert.False(await GlobalChatConfigService.EnableAsync(
            _installDirectory,
            new GlobalChatLaunchSettings(ApiBaseUrl, AccessToken),
            LaunchExperience.Online));

        Assert.False(File.Exists(ConfigPathIn(NormalLoaderRoot)));
    }

    // A plugin installed for one experience must not get a token written for the
    // other, or a ladder-only install would be configured by a D2RLoader launch.
    [Fact]
    public async Task EnablingDoesNotReachTheOtherModFolder()
    {
        InstallPlugin(LadderLoaderRoot);

        Assert.False(await GlobalChatConfigService.EnableAsync(
            _installDirectory,
            new GlobalChatLaunchSettings(ApiBaseUrl, AccessToken),
            LaunchExperience.Online));

        Assert.False(File.Exists(ConfigPathIn(NormalLoaderRoot)));
        Assert.False(File.Exists(ConfigPathIn(LadderLoaderRoot)));
    }

    // Signing out and launching offline must not leave a working token behind in
    // the other mod's config for the next launch to pick up.
    [Fact]
    public async Task DisablingClearsTheTokenInBothModFolders()
    {
        InstallPlugin(NormalLoaderRoot);
        InstallPlugin(LadderLoaderRoot);
        await GlobalChatConfigService.EnableAsync(
            _installDirectory, new GlobalChatLaunchSettings(ApiBaseUrl, AccessToken), LaunchExperience.Online);
        await GlobalChatConfigService.EnableAsync(
            _installDirectory, new GlobalChatLaunchSettings(ApiBaseUrl, AccessToken), LaunchExperience.Ladder);

        Assert.True(await GlobalChatConfigService.DisableAsync(_installDirectory));

        foreach (var root in new[] { NormalLoaderRoot, LadderLoaderRoot })
        {
            var toml = await File.ReadAllTextAsync(ConfigPathIn(root));
            Assert.Contains("enabled = false", toml, StringComparison.Ordinal);
            Assert.Contains("access_token = \"\"", toml, StringComparison.Ordinal);
            Assert.DoesNotContain(AccessToken, toml, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task DisablingAConfigThatWasNeverWrittenIsNotAFailure()
    {
        Assert.True(await GlobalChatConfigService.DisableAsync(_installDirectory));
    }

    // The launcher owns three settings; everything else in the file is the
    // player's, including the colour bytes they may have swept by hand.
    [Fact]
    public async Task EnablingPreservesTheRestOfThePlayersConfig()
    {
        InstallPlugin(NormalLoaderRoot);
        var configPath = ConfigPathIn(NormalLoaderRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        await File.WriteAllTextAsync(configPath,
            "# my notes\nchannel_tag = \"[World]\"\nglobal_color = 9\naccess_token = \"stale\"\n");

        Assert.True(await GlobalChatConfigService.EnableAsync(
            _installDirectory,
            new GlobalChatLaunchSettings(ApiBaseUrl, AccessToken),
            LaunchExperience.Online));

        var toml = await File.ReadAllTextAsync(configPath);
        Assert.Contains("# my notes", toml, StringComparison.Ordinal);
        Assert.Contains("channel_tag = \"[World]\"", toml, StringComparison.Ordinal);
        Assert.Contains("global_color = 9", toml, StringComparison.Ordinal);
        Assert.Contains($"access_token = \"{AccessToken}\"", toml, StringComparison.Ordinal);
        Assert.DoesNotContain("\"stale\"", toml, StringComparison.Ordinal);
    }

    private static void InstallPlugin(string loaderRoot)
    {
        var directory = Path.Combine(loaderRoot, "plugins");
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, GlobalChatConfigService.PluginFileName), [0x4D, 0x5A]);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_installDirectory))
            {
                Directory.Delete(_installDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}
