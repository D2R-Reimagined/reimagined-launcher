using ReimaginedLauncher.Utilities;
using Xunit;

namespace ReimaginedLauncher.Tests;

public sealed class AnnouncementsConfigServiceTests : IDisposable
{
    private const string ApiBaseUrl = "https://api.d2r-reimagined.com";
    private const string AccessToken = "token-abc-123";

    private readonly string _installDirectory = Path.Combine(
        Path.GetTempPath(),
        $"reimagined-announcements-tests-{Guid.NewGuid():N}");

    private string NormalLoaderRoot => Path.Combine(_installDirectory, "mods", "Reimagined", "d2rloader");
    private string LadderLoaderRoot => Path.Combine(_installDirectory, "mods", "ReimaginedLadder", "d2rloader");
    private static string ConfigPathIn(string loaderRoot) => Path.Combine(loaderRoot, "config", "announcements.toml");
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
        Assert.Equal(expected, AnnouncementsConfigService.IsEligible(type, experience));
    }
    [Fact]
    public void InstallationIsDetectedInTheModFolderTheExperienceWillLoad()
    {
        InstallPlugin(NormalLoaderRoot);

        Assert.True(AnnouncementsConfigService.IsPluginInstalled(_installDirectory, LaunchExperience.Online));
        Assert.False(AnnouncementsConfigService.IsPluginInstalled(_installDirectory, LaunchExperience.Ladder));

        InstallPlugin(LadderLoaderRoot);
        Assert.True(AnnouncementsConfigService.IsPluginInstalled(_installDirectory, LaunchExperience.Ladder));
    }

    [Fact]
    public async Task EnablingWritesTheApiAddressAndTokenForAnOnlineLaunch()
    {
        InstallPlugin(NormalLoaderRoot);

        Assert.True(await AnnouncementsConfigService.EnableAsync(
            _installDirectory,
            new AnnouncementsLaunchSettings(ApiBaseUrl, AccessToken),
            LaunchExperience.Online));

        var toml = await File.ReadAllTextAsync(ConfigPathIn(NormalLoaderRoot));
        Assert.Contains("enabled = true", toml, StringComparison.Ordinal);
        Assert.Contains($"api_base_url = \"{ApiBaseUrl}\"", toml, StringComparison.Ordinal);
        Assert.Contains($"access_token = \"{AccessToken}\"", toml, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(LaunchExperience.Online)]
    [InlineData(LaunchExperience.Ladder)]
    public async Task SignedOutLaunchClearsStaleTokenAndStillReceivesPublicAnnouncements(LaunchExperience experience)
    {
        var root = experience == LaunchExperience.Ladder ? LadderLoaderRoot : NormalLoaderRoot;
        InstallPlugin(root);
        await AnnouncementsConfigService.EnableAsync(_installDirectory,
            new AnnouncementsLaunchSettings(ApiBaseUrl, AccessToken, "old-ladder"), experience);
        Assert.True(await AnnouncementsConfigService.EnableAsync(_installDirectory,
            new AnnouncementsLaunchSettings(ApiBaseUrl, null), experience));
        var toml = await File.ReadAllTextAsync(ConfigPathIn(root));
        Assert.Contains("enabled = true", toml);
        Assert.Contains("access_token = \"\"", toml);
        Assert.Contains("ladder_id = \"\"", toml);
        Assert.DoesNotContain(AccessToken, toml);
    }

    [Theory]
    [InlineData(LaunchExperience.Online, "")]
    [InlineData(LaunchExperience.Ladder, "selected-ladder")]
    public async Task LaunchUsesSelectedApiAndOnlyIncludesLadderForLadderMode(LaunchExperience experience, string expectedLadder)
    {
        var root = experience == LaunchExperience.Ladder ? LadderLoaderRoot : NormalLoaderRoot;
        InstallPlugin(root);
        Assert.True(await AnnouncementsConfigService.EnableAsync(_installDirectory,
            new AnnouncementsLaunchSettings("http://localhost:5000/", AccessToken, "selected-ladder"), experience));
        var toml = await File.ReadAllTextAsync(ConfigPathIn(root));
        Assert.Contains("api_base_url = \"http://localhost:5000\"", toml);
        Assert.Contains($"access_token = \"{AccessToken}\"", toml);
        Assert.Contains($"ladder_id = \"{expectedLadder}\"", toml);
    }
    [Fact]
    public async Task EnablingRequiresThePluginToBeInstalledAndWritesNothingWithoutIt()
    {
        Assert.False(await AnnouncementsConfigService.EnableAsync(
            _installDirectory,
            new AnnouncementsLaunchSettings(ApiBaseUrl, AccessToken),
            LaunchExperience.Online));

        Assert.False(File.Exists(ConfigPathIn(NormalLoaderRoot)));
    }
    [Fact]
    public async Task EnablingDoesNotReachTheOtherModFolder()
    {
        InstallPlugin(LadderLoaderRoot);

        Assert.False(await AnnouncementsConfigService.EnableAsync(
            _installDirectory,
            new AnnouncementsLaunchSettings(ApiBaseUrl, AccessToken),
            LaunchExperience.Online));

        Assert.False(File.Exists(ConfigPathIn(NormalLoaderRoot)));
        Assert.False(File.Exists(ConfigPathIn(LadderLoaderRoot)));
    }
    [Fact]
    public async Task DisablingClearsTheTokenInBothModFolders()
    {
        InstallPlugin(NormalLoaderRoot);
        InstallPlugin(LadderLoaderRoot);
        await AnnouncementsConfigService.EnableAsync(
            _installDirectory, new AnnouncementsLaunchSettings(ApiBaseUrl, AccessToken), LaunchExperience.Online);
        await AnnouncementsConfigService.EnableAsync(
            _installDirectory, new AnnouncementsLaunchSettings(ApiBaseUrl, AccessToken), LaunchExperience.Ladder);

        Assert.True(await AnnouncementsConfigService.DisableAsync(_installDirectory));

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
        Assert.True(await AnnouncementsConfigService.DisableAsync(_installDirectory));
    }
    [Fact]
    public async Task EnablingPreservesTheRestOfThePlayersConfig()
    {
        InstallPlugin(NormalLoaderRoot);
        var configPath = ConfigPathIn(NormalLoaderRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        await File.WriteAllTextAsync(configPath,
            "# my notes\nchannel_tag = \"[World]\"\nglobal_color = 9\naccess_token = \"stale\"\n");

        Assert.True(await AnnouncementsConfigService.EnableAsync(
            _installDirectory,
            new AnnouncementsLaunchSettings(ApiBaseUrl, AccessToken),
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
        File.WriteAllBytes(Path.Combine(directory, AnnouncementsConfigService.PluginFileName), [0x4D, 0x5A]);
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

