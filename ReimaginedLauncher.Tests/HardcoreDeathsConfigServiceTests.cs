using ReimaginedLauncher.Utilities;
using Xunit;

namespace ReimaginedLauncher.Tests;

public sealed class HardcoreDeathsConfigServiceTests : IDisposable
{
    private const string ApiBaseUrl = "https://api.d2rreimagined.com";
    private const string AccessToken = "token-abc-123";

    private readonly string _installDirectory = Path.Combine(
        Path.GetTempPath(),
        $"reimagined-hardcore-deaths-tests-{Guid.NewGuid():N}");

    private string NormalLoaderRoot => Path.Combine(_installDirectory, "mods", "Reimagined", "d2rloader");
    private string LadderLoaderRoot => Path.Combine(_installDirectory, "mods", "ReimaginedLadder", "d2rloader");
    private static string ConfigPathIn(string loaderRoot) => Path.Combine(loaderRoot, "config", "hardcore-deaths.toml");

    // Online and Ladder, signed in, on a D2RLoader installation. Offline is the
    // one mode meant to need no account, and D2RMM has no loader to run the
    // plugin at all.
    [Theory]
    [InlineData(InstallationType.BattleNet, LaunchExperience.Online, true)]
    [InlineData(InstallationType.BattleNet, LaunchExperience.Ladder, true)]
    [InlineData(InstallationType.Steam, LaunchExperience.Online, true)]
    [InlineData(InstallationType.Steam, LaunchExperience.Ladder, true)]
    [InlineData(InstallationType.BattleNet, LaunchExperience.Offline, false)]
    [InlineData(InstallationType.D2RMM, LaunchExperience.Online, false)]
    [InlineData(InstallationType.D2RMM, LaunchExperience.Ladder, false)]
    public void EligibilityCoversSignedInD2RLoaderLaunches(
        InstallationType type, LaunchExperience experience, bool expected)
    {
        Assert.Equal(expected, HardcoreDeathsConfigService.IsEligible(type, experience));
    }

    // A ladder launch loads ReimaginedLadder and every other one loads
    // Reimagined, so detection has to be per mod folder.
    [Fact]
    public void InstallationIsDetectedInTheModFolderTheExperienceWillLoad()
    {
        InstallPlugin(NormalLoaderRoot);

        Assert.True(HardcoreDeathsConfigService.IsPluginInstalled(_installDirectory, LaunchExperience.Online));
        Assert.False(HardcoreDeathsConfigService.IsPluginInstalled(_installDirectory, LaunchExperience.Ladder));

        InstallPlugin(LadderLoaderRoot);
        Assert.True(HardcoreDeathsConfigService.IsPluginInstalled(_installDirectory, LaunchExperience.Ladder));
    }

    [Theory]
    [InlineData(LaunchExperience.Online)]
    [InlineData(LaunchExperience.Ladder)]
    public async Task EnablingWritesTheApiAddressAndTokenForBothEligibleExperiences(LaunchExperience experience)
    {
        var root = experience == LaunchExperience.Ladder ? LadderLoaderRoot : NormalLoaderRoot;
        InstallPlugin(root);

        Assert.True(await HardcoreDeathsConfigService.EnableAsync(
            _installDirectory,
            new HardcoreDeathsLaunchSettings(ApiBaseUrl, AccessToken),
            experience));

        var toml = await File.ReadAllTextAsync(ConfigPathIn(root));
        Assert.Contains("enabled = true", toml, StringComparison.Ordinal);
        Assert.Contains($"api_base_url = \"{ApiBaseUrl}\"", toml, StringComparison.Ordinal);
        Assert.Contains($"access_token = \"{AccessToken}\"", toml, StringComparison.Ordinal);
    }

    // Without a token the plugin would connect, listen, and be refused
    // "unauthenticated" the one time it mattered. Half-working is a confusing
    // thing to hand somebody deliberately.
    [Fact]
    public async Task EnablingRefusesWithoutATokenBecauseReportingIsAuthenticated()
    {
        InstallPlugin(NormalLoaderRoot);

        Assert.False(await HardcoreDeathsConfigService.EnableAsync(
            _installDirectory,
            new HardcoreDeathsLaunchSettings(ApiBaseUrl, "   "),
            LaunchExperience.Online));

        Assert.False(await HardcoreDeathsConfigService.EnableAsync(
            _installDirectory,
            new HardcoreDeathsLaunchSettings("  ", AccessToken),
            LaunchExperience.Online));

        Assert.False(File.Exists(ConfigPathIn(NormalLoaderRoot)));
    }

    // No plugin, no token written. The launcher must not create a config for a
    // plugin the player has not installed.
    [Fact]
    public async Task EnablingRequiresThePluginToBeInstalledAndWritesNothingWithoutIt()
    {
        Assert.False(await HardcoreDeathsConfigService.EnableAsync(
            _installDirectory,
            new HardcoreDeathsLaunchSettings(ApiBaseUrl, AccessToken),
            LaunchExperience.Online));

        Assert.False(File.Exists(ConfigPathIn(NormalLoaderRoot)));
    }

    [Fact]
    public async Task EnablingDoesNotReachTheOtherModFolder()
    {
        InstallPlugin(LadderLoaderRoot);

        Assert.False(await HardcoreDeathsConfigService.EnableAsync(
            _installDirectory,
            new HardcoreDeathsLaunchSettings(ApiBaseUrl, AccessToken),
            LaunchExperience.Online));

        Assert.False(File.Exists(ConfigPathIn(NormalLoaderRoot)));
        Assert.False(File.Exists(ConfigPathIn(LadderLoaderRoot)));
    }

    // Signing out must not leave a working token behind for the next launch to
    // report a death under the previous account's name.
    [Fact]
    public async Task DisablingClearsTheTokenInBothModFolders()
    {
        InstallPlugin(NormalLoaderRoot);
        InstallPlugin(LadderLoaderRoot);
        await HardcoreDeathsConfigService.EnableAsync(
            _installDirectory, new HardcoreDeathsLaunchSettings(ApiBaseUrl, AccessToken), LaunchExperience.Online);
        await HardcoreDeathsConfigService.EnableAsync(
            _installDirectory, new HardcoreDeathsLaunchSettings(ApiBaseUrl, AccessToken), LaunchExperience.Ladder);

        Assert.True(await HardcoreDeathsConfigService.DisableAsync(_installDirectory));

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
        Assert.True(await HardcoreDeathsConfigService.DisableAsync(_installDirectory));
    }

    // The launcher owns three settings. Everything else is the player's - and
    // death_ui_target especially, which is a discovered value the launcher has
    // no way to know and must never overwrite.
    [Fact]
    public async Task EnablingPreservesTheRestOfThePlayersConfigIncludingTheDeathSignal()
    {
        InstallPlugin(NormalLoaderRoot);
        var configPath = ConfigPathIn(NormalLoaderRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        await File.WriteAllTextAsync(configPath,
            "# my notes\ndeath_ui_target = \"DeathPanel\"\nkiller_prefix = \" slain by \"\n"
            + "channel_tag = \"[Fallen]\"\ndeath_color = 9\naccess_token = \"stale\"\n");

        Assert.True(await HardcoreDeathsConfigService.EnableAsync(
            _installDirectory,
            new HardcoreDeathsLaunchSettings(ApiBaseUrl, AccessToken),
            LaunchExperience.Online));

        var toml = await File.ReadAllTextAsync(configPath);
        Assert.Contains("# my notes", toml, StringComparison.Ordinal);
        Assert.Contains("death_ui_target = \"DeathPanel\"", toml, StringComparison.Ordinal);
        Assert.Contains("killer_prefix = \" slain by \"", toml, StringComparison.Ordinal);
        Assert.Contains("channel_tag = \"[Fallen]\"", toml, StringComparison.Ordinal);
        Assert.Contains("death_color = 9", toml, StringComparison.Ordinal);
        Assert.Contains($"access_token = \"{AccessToken}\"", toml, StringComparison.Ordinal);
        Assert.DoesNotContain("\"stale\"", toml, StringComparison.Ordinal);
    }

    // Disabling must not resurrect a death signal either: it clears the token
    // and the switch, and leaves the discovered value where the player put it.
    [Fact]
    public async Task DisablingLeavesTheDeathSignalAlone()
    {
        InstallPlugin(NormalLoaderRoot);
        var configPath = ConfigPathIn(NormalLoaderRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        await File.WriteAllTextAsync(configPath, "death_ui_target = \"DeathPanel\"\naccess_token = \"stale\"\n");

        Assert.True(await HardcoreDeathsConfigService.DisableAsync(_installDirectory));

        var toml = await File.ReadAllTextAsync(configPath);
        Assert.Contains("death_ui_target = \"DeathPanel\"", toml, StringComparison.Ordinal);
        Assert.Contains("access_token = \"\"", toml, StringComparison.Ordinal);
    }

    private static void InstallPlugin(string loaderRoot)
    {
        var directory = Path.Combine(loaderRoot, "plugins");
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, HardcoreDeathsConfigService.PluginFileName), [0x4D, 0x5A]);
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
