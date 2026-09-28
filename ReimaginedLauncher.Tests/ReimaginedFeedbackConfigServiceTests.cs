using ReimaginedLauncher.Utilities;
using Xunit;

namespace ReimaginedLauncher.Tests;

public sealed class ReimaginedFeedbackConfigServiceTests : IDisposable
{
    private const string ApiBaseUrl = "https://api.d2r-reimagined.com";
    private const string AccessToken = "token-abc-123";

    private readonly string _installDirectory = Path.Combine(
        Path.GetTempPath(),
        $"reimagined-feedback-tests-{Guid.NewGuid():N}");

    private string NormalLoaderRoot => Path.Combine(_installDirectory, "mods", "Reimagined", "d2rloader");
    private string LadderLoaderRoot => Path.Combine(_installDirectory, "mods", "ReimaginedLadder", "d2rloader");
    private static string ConfigPathIn(string loaderRoot) => Path.Combine(loaderRoot, "config", "reimagined-feedback.toml");
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
        Assert.Equal(expected, ReimaginedFeedbackConfigService.IsEligible(type, experience));
    }
    [Fact]
    public void InstallationIsDetectedInTheModFolderTheExperienceWillLoad()
    {
        InstallPlugin(NormalLoaderRoot);

        Assert.True(ReimaginedFeedbackConfigService.IsPluginInstalled(_installDirectory, LaunchExperience.Online));
        Assert.False(ReimaginedFeedbackConfigService.IsPluginInstalled(_installDirectory, LaunchExperience.Ladder));

        InstallPlugin(LadderLoaderRoot);
        Assert.True(ReimaginedFeedbackConfigService.IsPluginInstalled(_installDirectory, LaunchExperience.Ladder));
    }

    [Theory]
    [InlineData(LaunchExperience.Online)]
    [InlineData(LaunchExperience.Ladder)]
    public async Task EnablingWritesTheApiAddressAndToken(LaunchExperience experience)
    {
        var root = experience == LaunchExperience.Ladder ? LadderLoaderRoot : NormalLoaderRoot;
        InstallPlugin(root);

        Assert.True(await ReimaginedFeedbackConfigService.EnableAsync(
            _installDirectory,
            new ReimaginedFeedbackLaunchSettings(ApiBaseUrl + "/", AccessToken),
            experience));

        var toml = await File.ReadAllTextAsync(ConfigPathIn(root));
        Assert.DoesNotContain("enabled =", toml, StringComparison.Ordinal);
        Assert.Contains($"api_base_url = \"{ApiBaseUrl}\"", toml, StringComparison.Ordinal);
        Assert.Contains($"access_token = \"{AccessToken}\"", toml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EnablingRefusesWithoutATokenBecauseTheEndpointIsAuthenticated()
    {
        InstallPlugin(NormalLoaderRoot);

        Assert.False(await ReimaginedFeedbackConfigService.EnableAsync(
            _installDirectory,
            new ReimaginedFeedbackLaunchSettings(ApiBaseUrl, "   "),
            LaunchExperience.Online));

        Assert.False(await ReimaginedFeedbackConfigService.EnableAsync(
            _installDirectory,
            new ReimaginedFeedbackLaunchSettings("  ", AccessToken),
            LaunchExperience.Online));

        Assert.False(File.Exists(ConfigPathIn(NormalLoaderRoot)));
    }

    [Fact]
    public async Task EnablingRefusesARemotePlainHttpApiSoTheTokenIsNeverSentUnencrypted()
    {
        InstallPlugin(NormalLoaderRoot);

        Assert.False(await ReimaginedFeedbackConfigService.EnableAsync(
            _installDirectory,
            new ReimaginedFeedbackLaunchSettings("http://api.d2r-reimagined.com", AccessToken),
            LaunchExperience.Online));

        Assert.False(File.Exists(ConfigPathIn(NormalLoaderRoot)));
    }

    [Theory]
    [InlineData("http://localhost:5000")]
    [InlineData("http://127.0.0.1:5000")]
    public async Task EnablingAcceptsALocalHttpApi(string apiBaseUrl)
    {
        InstallPlugin(NormalLoaderRoot);

        Assert.True(await ReimaginedFeedbackConfigService.EnableAsync(
            _installDirectory,
            new ReimaginedFeedbackLaunchSettings(apiBaseUrl, AccessToken),
            LaunchExperience.Online));

        var toml = await File.ReadAllTextAsync(ConfigPathIn(NormalLoaderRoot));
        Assert.Contains($"api_base_url = \"{apiBaseUrl}\"", toml, StringComparison.Ordinal);
        Assert.Contains($"access_token = \"{AccessToken}\"", toml, StringComparison.Ordinal);
    }
    [Fact]
    public async Task EnablingRequiresThePluginToBeInstalledAndWritesNothingWithoutIt()
    {
        Assert.False(await ReimaginedFeedbackConfigService.EnableAsync(
            _installDirectory,
            new ReimaginedFeedbackLaunchSettings(ApiBaseUrl, AccessToken),
            LaunchExperience.Online));

        Assert.False(File.Exists(ConfigPathIn(NormalLoaderRoot)));
    }
    [Fact]
    public async Task DisablingClearsTheTokenInBothModFolders()
    {
        InstallPlugin(NormalLoaderRoot);
        InstallPlugin(LadderLoaderRoot);
        await ReimaginedFeedbackConfigService.EnableAsync(
            _installDirectory, new ReimaginedFeedbackLaunchSettings(ApiBaseUrl, AccessToken), LaunchExperience.Online);
        await ReimaginedFeedbackConfigService.EnableAsync(
            _installDirectory, new ReimaginedFeedbackLaunchSettings(ApiBaseUrl, AccessToken), LaunchExperience.Ladder);

        Assert.True(await ReimaginedFeedbackConfigService.DisableAsync(_installDirectory));

        foreach (var root in new[] { NormalLoaderRoot, LadderLoaderRoot })
        {
            var toml = await File.ReadAllTextAsync(ConfigPathIn(root));
            Assert.DoesNotContain("enabled =", toml, StringComparison.Ordinal);
            Assert.Contains("access_token = \"\"", toml, StringComparison.Ordinal);
            Assert.DoesNotContain(AccessToken, toml, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task DisablingAConfigThatWasNeverWrittenIsNotAFailure()
    {
        Assert.True(await ReimaginedFeedbackConfigService.DisableAsync(_installDirectory));
    }
    [Fact]
    public async Task EnablingPreservesTheRestOfThePlayersConfigAndReplacesAStaleOrigin()
    {
        InstallPlugin(NormalLoaderRoot);
        var configPath = ConfigPathIn(NormalLoaderRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        await File.WriteAllTextAsync(configPath,
            "# my notes\napi_base_url = \"https://api.d2rreimagined.com\"\nmax_length = 500\naccess_token = \"stale\"\n");

        Assert.True(await ReimaginedFeedbackConfigService.EnableAsync(
            _installDirectory,
            new ReimaginedFeedbackLaunchSettings(ApiBaseUrl, AccessToken),
            LaunchExperience.Online));

        var toml = await File.ReadAllTextAsync(configPath);
        Assert.Contains("# my notes", toml, StringComparison.Ordinal);
        Assert.Contains("max_length = 500", toml, StringComparison.Ordinal);
        Assert.Contains($"api_base_url = \"{ApiBaseUrl}\"", toml, StringComparison.Ordinal);
        Assert.Contains($"access_token = \"{AccessToken}\"", toml, StringComparison.Ordinal);
        Assert.DoesNotContain("d2rreimagined.com", toml, StringComparison.Ordinal);
        Assert.DoesNotContain("\"stale\"", toml, StringComparison.Ordinal);
    }

    private static void InstallPlugin(string loaderRoot)
    {
        var directory = Path.Combine(loaderRoot, "plugins");
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, ReimaginedFeedbackConfigService.PluginFileName), [0x4D, 0x5A]);
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
