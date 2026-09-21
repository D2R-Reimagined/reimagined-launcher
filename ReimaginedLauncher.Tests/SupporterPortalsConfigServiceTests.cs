using ReimaginedLauncher.Utilities;
using Xunit;

namespace ReimaginedLauncher.Tests;

public sealed class SupporterPortalsConfigServiceTests : IDisposable
{
    private const string ApiBaseUrl = "https://api.d2r-reimagined.com";
    private const string AccessToken = "token-abc-123";

    private readonly string _installDirectory = Path.Combine(
        Path.GetTempPath(),
        $"reimagined-supporter-portals-tests-{Guid.NewGuid():N}");

    private string NormalLoaderRoot => Path.Combine(_installDirectory, "mods", "Reimagined", "d2rloader");
    private string LadderLoaderRoot => Path.Combine(_installDirectory, "mods", "ReimaginedLadder", "d2rloader");
    private static string ConfigPathIn(string loaderRoot) => Path.Combine(loaderRoot, "config", "supporter-portals.toml");
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
        Assert.Equal(expected, SupporterPortalsConfigService.IsEligible(type, experience));
    }
    [Fact]
    public void InstallationIsDetectedInTheModFolderTheExperienceWillLoad()
    {
        InstallPlugin(NormalLoaderRoot);

        Assert.True(SupporterPortalsConfigService.IsPluginInstalled(_installDirectory, LaunchExperience.Online));
        Assert.False(SupporterPortalsConfigService.IsPluginInstalled(_installDirectory, LaunchExperience.Ladder));

        InstallPlugin(LadderLoaderRoot);
        Assert.True(SupporterPortalsConfigService.IsPluginInstalled(_installDirectory, LaunchExperience.Ladder));
    }

    [Theory]
    [InlineData(LaunchExperience.Online)]
    [InlineData(LaunchExperience.Ladder)]
    public async Task EnablingWritesTheApiAddressAndTokenForTheSelectedLaunch(LaunchExperience experience)
    {
        var loaderRoot = experience == LaunchExperience.Ladder ? LadderLoaderRoot : NormalLoaderRoot;
        InstallPlugin(loaderRoot);

        Assert.True(await SupporterPortalsConfigService.EnableAsync(
            _installDirectory,
            new SupporterPortalsLaunchSettings(ApiBaseUrl, AccessToken),
            experience));

        var toml = await File.ReadAllTextAsync(ConfigPathIn(loaderRoot));
        Assert.Contains($"api_base_url = \"{ApiBaseUrl}\"", toml, StringComparison.Ordinal);
        Assert.Contains($"access_token = \"{AccessToken}\"", toml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EnablingRefusesWithoutATokenBecauseTheEndpointIsAuthenticated()
    {
        InstallPlugin(NormalLoaderRoot);

        Assert.False(await SupporterPortalsConfigService.EnableAsync(
            _installDirectory,
            new SupporterPortalsLaunchSettings(ApiBaseUrl, "   "),
            LaunchExperience.Online));

        Assert.False(await SupporterPortalsConfigService.EnableAsync(
            _installDirectory,
            new SupporterPortalsLaunchSettings("  ", AccessToken),
            LaunchExperience.Online));
    }
    [Fact]
    public async Task EnablingRequiresThePluginToBeInstalledAndWritesNothingWithoutIt()
    {
        Assert.False(await SupporterPortalsConfigService.EnableAsync(
            _installDirectory,
            new SupporterPortalsLaunchSettings(ApiBaseUrl, AccessToken),
            LaunchExperience.Online));

        Assert.False(File.Exists(ConfigPathIn(NormalLoaderRoot)));
    }
    [Fact]
    public async Task EnablingDoesNotReachTheOtherModFolder()
    {
        InstallPlugin(LadderLoaderRoot);

        Assert.False(await SupporterPortalsConfigService.EnableAsync(
            _installDirectory,
            new SupporterPortalsLaunchSettings(ApiBaseUrl, AccessToken),
            LaunchExperience.Online));

        Assert.False(File.Exists(ConfigPathIn(NormalLoaderRoot)));
        Assert.False(File.Exists(ConfigPathIn(LadderLoaderRoot)));
    }
    [Fact]
    public async Task DisablingClearsTheTokenInBothModFolders()
    {
        InstallPlugin(NormalLoaderRoot);
        InstallPlugin(LadderLoaderRoot);
        await SupporterPortalsConfigService.EnableAsync(
            _installDirectory, new SupporterPortalsLaunchSettings(ApiBaseUrl, AccessToken), LaunchExperience.Online);
        await SupporterPortalsConfigService.EnableAsync(
            _installDirectory, new SupporterPortalsLaunchSettings(ApiBaseUrl, AccessToken), LaunchExperience.Ladder);

        Assert.True(await SupporterPortalsConfigService.DisableAsync(_installDirectory));

        foreach (var root in new[] { NormalLoaderRoot, LadderLoaderRoot })
        {
            var toml = await File.ReadAllTextAsync(ConfigPathIn(root));
            Assert.Contains("access_token = \"\"", toml, StringComparison.Ordinal);
            Assert.DoesNotContain(AccessToken, toml, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task DisablingAConfigThatWasNeverWrittenIsNotAFailure()
    {
        Assert.True(await SupporterPortalsConfigService.DisableAsync(_installDirectory));
    }
    [Fact]
    public async Task EnablingPreservesTheRestOfThePlayersConfig()
    {
        InstallPlugin(NormalLoaderRoot);
        var configPath = ConfigPathIn(NormalLoaderRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        await File.WriteAllTextAsync(configPath,
            "# my notes\ncustom_setting = \"preserved\"\ncustom_number = 9\naccess_token = \"stale\"\n");

        Assert.True(await SupporterPortalsConfigService.EnableAsync(
            _installDirectory,
            new SupporterPortalsLaunchSettings(ApiBaseUrl, AccessToken),
            LaunchExperience.Online));

        var toml = await File.ReadAllTextAsync(configPath);
        Assert.Contains("# my notes", toml, StringComparison.Ordinal);
        Assert.Contains("custom_setting = \"preserved\"", toml, StringComparison.Ordinal);
        Assert.Contains("custom_number = 9", toml, StringComparison.Ordinal);
        Assert.Contains($"access_token = \"{AccessToken}\"", toml, StringComparison.Ordinal);
        Assert.DoesNotContain("\"stale\"", toml, StringComparison.Ordinal);
    }

    private static void InstallPlugin(string loaderRoot)
    {
        var directory = Path.Combine(loaderRoot, "plugins");
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, SupporterPortalsConfigService.PluginFileName), [0x4D, 0x5A]);
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
