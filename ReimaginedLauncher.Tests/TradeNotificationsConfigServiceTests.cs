using ReimaginedLauncher.Utilities;
using Xunit;

namespace ReimaginedLauncher.Tests;

public sealed class TradeNotificationsConfigServiceTests : IDisposable
{
    private const string ApiBaseUrl = "https://api.d2rreimagined.com";
    private const string AccessToken = "token-abc-123";

    private readonly string _installDirectory = Path.Combine(
        Path.GetTempPath(),
        $"reimagined-trade-notifications-tests-{Guid.NewGuid():N}");

    private string NormalLoaderRoot => Path.Combine(_installDirectory, "mods", "Reimagined", "d2rloader");
    private string LadderLoaderRoot => Path.Combine(_installDirectory, "mods", "ReimaginedLadder", "d2rloader");
    private static string ConfigPathIn(string loaderRoot) => Path.Combine(loaderRoot, "config", "trade-notifications.toml");
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
        Assert.Equal(expected, TradeNotificationsConfigService.IsEligible(type, experience));
    }
    [Fact]
    public void InstallationIsDetectedInTheModFolderTheExperienceWillLoad()
    {
        InstallPlugin(NormalLoaderRoot);

        Assert.True(TradeNotificationsConfigService.IsPluginInstalled(_installDirectory, LaunchExperience.Online));
        Assert.False(TradeNotificationsConfigService.IsPluginInstalled(_installDirectory, LaunchExperience.Ladder));

        InstallPlugin(LadderLoaderRoot);
        Assert.True(TradeNotificationsConfigService.IsPluginInstalled(_installDirectory, LaunchExperience.Ladder));
    }

    [Fact]
    public async Task EnablingWritesTheApiAddressAndTokenForAnOnlineLaunch()
    {
        InstallPlugin(NormalLoaderRoot);

        Assert.True(await TradeNotificationsConfigService.EnableAsync(
            _installDirectory,
            new TradeNotificationsLaunchSettings(ApiBaseUrl, AccessToken),
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

        Assert.False(await TradeNotificationsConfigService.EnableAsync(
            _installDirectory,
            new TradeNotificationsLaunchSettings(ApiBaseUrl, "   "),
            LaunchExperience.Online));

        Assert.False(await TradeNotificationsConfigService.EnableAsync(
            _installDirectory,
            new TradeNotificationsLaunchSettings("  ", AccessToken),
            LaunchExperience.Online));
    }
    [Fact]
    public async Task EnablingRequiresThePluginToBeInstalledAndWritesNothingWithoutIt()
    {
        Assert.False(await TradeNotificationsConfigService.EnableAsync(
            _installDirectory,
            new TradeNotificationsLaunchSettings(ApiBaseUrl, AccessToken),
            LaunchExperience.Online));

        Assert.False(File.Exists(ConfigPathIn(NormalLoaderRoot)));
    }
    [Fact]
    public async Task EnablingDoesNotReachTheOtherModFolder()
    {
        InstallPlugin(LadderLoaderRoot);

        Assert.False(await TradeNotificationsConfigService.EnableAsync(
            _installDirectory,
            new TradeNotificationsLaunchSettings(ApiBaseUrl, AccessToken),
            LaunchExperience.Online));

        Assert.False(File.Exists(ConfigPathIn(NormalLoaderRoot)));
        Assert.False(File.Exists(ConfigPathIn(LadderLoaderRoot)));
    }
    [Fact]
    public async Task DisablingClearsTheTokenInBothModFolders()
    {
        InstallPlugin(NormalLoaderRoot);
        InstallPlugin(LadderLoaderRoot);
        await TradeNotificationsConfigService.EnableAsync(
            _installDirectory, new TradeNotificationsLaunchSettings(ApiBaseUrl, AccessToken), LaunchExperience.Online);
        await TradeNotificationsConfigService.EnableAsync(
            _installDirectory, new TradeNotificationsLaunchSettings(ApiBaseUrl, AccessToken), LaunchExperience.Ladder);

        Assert.True(await TradeNotificationsConfigService.DisableAsync(_installDirectory));

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
        Assert.True(await TradeNotificationsConfigService.DisableAsync(_installDirectory));
    }
    [Fact]
    public async Task EnablingPreservesTheRestOfThePlayersConfig()
    {
        InstallPlugin(NormalLoaderRoot);
        var configPath = ConfigPathIn(NormalLoaderRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        await File.WriteAllTextAsync(configPath,
            "# my notes\nchannel_tag = \"[World]\"\nglobal_color = 9\naccess_token = \"stale\"\n");

        Assert.True(await TradeNotificationsConfigService.EnableAsync(
            _installDirectory,
            new TradeNotificationsLaunchSettings(ApiBaseUrl, AccessToken),
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
        File.WriteAllBytes(Path.Combine(directory, TradeNotificationsConfigService.PluginFileName), [0x4D, 0x5A]);
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
