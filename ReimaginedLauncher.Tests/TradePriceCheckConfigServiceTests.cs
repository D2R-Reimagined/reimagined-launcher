using ReimaginedLauncher.Utilities;
using Xunit;

namespace ReimaginedLauncher.Tests;

public sealed class TradePriceCheckConfigServiceTests : IDisposable
{
    private const string ApiBaseUrl = "https://api.d2r-reimagined.com";
    private const string AccessToken = "token-abc-123";
    private const string LadderId = "6f9619ff-8b86-d011-b42d-00c04fc964ff";

    private readonly string _installDirectory = Path.Combine(
        Path.GetTempPath(),
        $"reimagined-trade-price-check-tests-{Guid.NewGuid():N}");

    private string NormalLoaderRoot => Path.Combine(_installDirectory, "mods", "Reimagined", "d2rloader");
    private string LadderLoaderRoot => Path.Combine(_installDirectory, "mods", "ReimaginedLadder", "d2rloader");
    private static string ConfigPathIn(string loaderRoot) => Path.Combine(loaderRoot, "config", "trade-price-check.toml");

    [Theory]
    [InlineData(InstallationType.BattleNet, LaunchExperience.Online, true)]
    [InlineData(InstallationType.BattleNet, LaunchExperience.Ladder, true)]
    [InlineData(InstallationType.Steam, LaunchExperience.Online, true)]
    [InlineData(InstallationType.BattleNet, LaunchExperience.Offline, false)]
    [InlineData(InstallationType.D2RMM, LaunchExperience.Online, false)]
    [InlineData(InstallationType.D2RMM, LaunchExperience.Ladder, false)]
    public void EligibilityCoversD2RLoaderLaunches(InstallationType type, LaunchExperience experience, bool expected)
    {
        Assert.Equal(expected, TradePriceCheckConfigService.IsEligible(type, experience));
    }

    [Fact]
    public void InstallationIsDetectedInTheModFolderTheExperienceWillLoad()
    {
        InstallPlugin(NormalLoaderRoot);

        Assert.True(TradePriceCheckConfigService.IsPluginInstalled(_installDirectory, LaunchExperience.Online));
        Assert.False(TradePriceCheckConfigService.IsPluginInstalled(_installDirectory, LaunchExperience.Ladder));

        InstallPlugin(LadderLoaderRoot);
        Assert.True(TradePriceCheckConfigService.IsPluginInstalled(_installDirectory, LaunchExperience.Ladder));
    }

    [Fact]
    public async Task AnOnlineLaunchWritesTheApiAndTokenAndSearchesEveryLadder()
    {
        InstallPlugin(NormalLoaderRoot);

        Assert.True(await TradePriceCheckConfigService.EnableAsync(
            _installDirectory,
            new TradePriceCheckLaunchSettings(ApiBaseUrl + "/", AccessToken, LadderId),
            LaunchExperience.Online));

        var toml = await File.ReadAllTextAsync(ConfigPathIn(NormalLoaderRoot));
        Assert.Contains($"api_base_url = \"{ApiBaseUrl}\"", toml, StringComparison.Ordinal);
        Assert.Contains("access_token = \"\"", toml, StringComparison.Ordinal);
        Assert.DoesNotContain(AccessToken, toml, StringComparison.Ordinal);
        Assert.Contains("ladder_id = \"\"", toml, StringComparison.Ordinal);
        Assert.DoesNotContain("enabled", toml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALadderLaunchNarrowsTheSearchToTheSelectedLadder()
    {
        InstallPlugin(LadderLoaderRoot);

        Assert.True(await TradePriceCheckConfigService.EnableAsync(
            _installDirectory,
            new TradePriceCheckLaunchSettings(ApiBaseUrl, AccessToken, LadderId),
            LaunchExperience.Ladder));

        var toml = await File.ReadAllTextAsync(ConfigPathIn(LadderLoaderRoot));
        Assert.Contains("access_token = \"\"", toml, StringComparison.Ordinal);
        Assert.DoesNotContain(AccessToken, toml, StringComparison.Ordinal);
        Assert.Contains($"ladder_id = \"{LadderId}\"", toml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoSignedInAccountStillConfiguresAnAnonymousSearch()
    {
        InstallPlugin(NormalLoaderRoot);

        Assert.True(await TradePriceCheckConfigService.EnableAsync(
            _installDirectory,
            new TradePriceCheckLaunchSettings(ApiBaseUrl, null),
            LaunchExperience.Online));

        var toml = await File.ReadAllTextAsync(ConfigPathIn(NormalLoaderRoot));
        Assert.Contains($"api_base_url = \"{ApiBaseUrl}\"", toml, StringComparison.Ordinal);
        Assert.Contains("access_token = \"\"", toml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EnablingRefusesWithoutAnApiAddress()
    {
        InstallPlugin(NormalLoaderRoot);

        Assert.False(await TradePriceCheckConfigService.EnableAsync(
            _installDirectory,
            new TradePriceCheckLaunchSettings("  ", AccessToken),
            LaunchExperience.Online));

        Assert.False(File.Exists(ConfigPathIn(NormalLoaderRoot)));
    }

    [Fact]
    public async Task EnablingRequiresThePluginInTheModFolderTheLaunchLoads()
    {
        InstallPlugin(LadderLoaderRoot);

        Assert.False(await TradePriceCheckConfigService.EnableAsync(
            _installDirectory,
            new TradePriceCheckLaunchSettings(ApiBaseUrl, AccessToken),
            LaunchExperience.Online));

        Assert.False(File.Exists(ConfigPathIn(NormalLoaderRoot)));
        Assert.False(File.Exists(ConfigPathIn(LadderLoaderRoot)));
    }

    [Fact]
    public async Task DisablingClearsTheTokenAndLadderInBothModFolders()
    {
        InstallPlugin(NormalLoaderRoot);
        InstallPlugin(LadderLoaderRoot);
        await TradePriceCheckConfigService.EnableAsync(
            _installDirectory, new TradePriceCheckLaunchSettings(ApiBaseUrl, AccessToken), LaunchExperience.Online);
        await TradePriceCheckConfigService.EnableAsync(
            _installDirectory, new TradePriceCheckLaunchSettings(ApiBaseUrl, AccessToken, LadderId), LaunchExperience.Ladder);

        Assert.True(await TradePriceCheckConfigService.DisableAsync(_installDirectory));

        foreach (var root in new[] { NormalLoaderRoot, LadderLoaderRoot })
        {
            var toml = await File.ReadAllTextAsync(ConfigPathIn(root));
            Assert.Contains("access_token = \"\"", toml, StringComparison.Ordinal);
            Assert.Contains("ladder_id = \"\"", toml, StringComparison.Ordinal);
            Assert.DoesNotContain(AccessToken, toml, StringComparison.Ordinal);
            Assert.DoesNotContain(LadderId, toml, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task DisablingAConfigThatWasNeverWrittenIsNotAFailure()
    {
        Assert.True(await TradePriceCheckConfigService.DisableAsync(_installDirectory));
    }

    [Fact]
    public async Task EnablingPreservesTheRestOfThePlayersConfig()
    {
        InstallPlugin(NormalLoaderRoot);
        var configPath = ConfigPathIn(NormalLoaderRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        await File.WriteAllTextAsync(configPath,
            "# my notes\nhotkey_vk = 80\nmax_results = 50\naccess_token = \"stale\"\n");

        Assert.True(await TradePriceCheckConfigService.EnableAsync(
            _installDirectory,
            new TradePriceCheckLaunchSettings(ApiBaseUrl, AccessToken),
            LaunchExperience.Online));

        var toml = await File.ReadAllTextAsync(configPath);
        Assert.Contains("# my notes", toml, StringComparison.Ordinal);
        Assert.Contains("hotkey_vk = 80", toml, StringComparison.Ordinal);
        Assert.Contains("max_results = 50", toml, StringComparison.Ordinal);
        Assert.Contains("access_token = \"\"", toml, StringComparison.Ordinal);
        Assert.DoesNotContain(AccessToken, toml, StringComparison.Ordinal);
        Assert.DoesNotContain("\"stale\"", toml, StringComparison.Ordinal);
    }

    private static void InstallPlugin(string loaderRoot)
    {
        var directory = Path.Combine(loaderRoot, "plugins");
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, TradePriceCheckConfigService.PluginFileName), [0x4D, 0x5A]);
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
