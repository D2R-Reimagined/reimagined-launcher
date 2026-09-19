using ReimaginedLauncher.Utilities;
using Xunit;

namespace ReimaginedLauncher.Tests;

public sealed class ServerSavesConfigServiceTests : IDisposable
{
    private static readonly Guid Ladder = Guid.Parse("3f2504e0-4f89-11d3-9a0c-0305e82c3301");

    private readonly string _installDirectory = Path.Combine(
        Path.GetTempPath(),
        $"reimagined-server-saves-tests-{Guid.NewGuid():N}");

    private string ModLoaderRoot => Path.Combine(_installDirectory, "mods", "ReimaginedLadder", "d2rloader");
    private string ModConfigPath => Path.Combine(ModLoaderRoot, "config", "server-saves.toml");
    private string InstalledPluginPath => Path.Combine(ModLoaderRoot, "plugins", ServerSavesConfigService.PluginFileName);

    [Fact]
    public async Task ConfiguringPreservesTheLadderPackageBinary()
    {
        InstallPlugin(ModLoaderRoot);
        byte[] bundleBytes = [0x4D, 0x5A, 0x42, 0x17];
        await File.WriteAllBytesAsync(InstalledPluginPath, bundleBytes);

        Assert.True(await ServerSavesConfigService.EnableAsync(
            _installDirectory,
            new ServerSavesLaunchSettings("http://localhost:5000", "token-abc", Ladder, "ticket-abc")));
        Assert.Equal(bundleBytes, await File.ReadAllBytesAsync(InstalledPluginPath));

        Assert.True(await ServerSavesConfigService.DisableAsync(_installDirectory));
        Assert.Equal(bundleBytes, await File.ReadAllBytesAsync(InstalledPluginPath));
    }

    [Fact]
    public async Task EnablingWritesTheLaunchSettingsWhenThePluginIsInstalled()
    {
        InstallPlugin(ModLoaderRoot);

        var enabled = await ServerSavesConfigService.EnableAsync(
            _installDirectory,
            new ServerSavesLaunchSettings("http://localhost:5000/", "token-abc", Ladder, "ticket-abc", "session-abc"));

        Assert.True(enabled);
        var toml = await File.ReadAllTextAsync(ModConfigPath);
        Assert.Contains("enabled = true", toml);
        Assert.Contains("api_base_url = \"http://localhost:5000\"", toml);
        Assert.Contains("access_token = \"token-abc\"", toml);
        Assert.Contains($"ladder_id = \"{Ladder}\"", toml);
        Assert.Contains("ladder_launch_ticket = \"ticket-abc\"", toml);
        Assert.Contains("status_session_id = \"session-abc\"", toml);
    }

    [Fact]
    public async Task EnablingIsRefusedWhenThePluginIsNotInstalled()
    {
        Assert.False(await ServerSavesConfigService.EnableAsync(
            _installDirectory,
            new ServerSavesLaunchSettings("http://localhost:5000", "token-abc", Ladder, "ticket-abc")));
        Assert.False(File.Exists(ModConfigPath));
    }

    [Fact]
    public async Task EnablingIsRefusedWithoutAnAccessToken()
    {
        InstallPlugin(ModLoaderRoot);

        Assert.False(await ServerSavesConfigService.EnableAsync(
            _installDirectory,
            new ServerSavesLaunchSettings("http://localhost:5000", "   ", Ladder, "ticket-abc")));
    }

    [Fact]
    public async Task EnablingIsRefusedWithoutALadderLaunchTicket()
    {
        InstallPlugin(ModLoaderRoot);

        Assert.False(await ServerSavesConfigService.EnableAsync(
            _installDirectory,
            new ServerSavesLaunchSettings("http://localhost:5000", "token-abc", Ladder, "")));
    }

    [Fact]
    public async Task DisablingClearsTheTokenSoLocalCharactersStayVisible()
    {
        InstallPlugin(ModLoaderRoot);
        await ServerSavesConfigService.EnableAsync(
            _installDirectory,
            new ServerSavesLaunchSettings("http://localhost:5000", "token-abc", Ladder, "ticket-abc"));

        Assert.True(await ServerSavesConfigService.DisableAsync(_installDirectory));

        var toml = await File.ReadAllTextAsync(ModConfigPath);
        Assert.Contains("enabled = false", toml);
        Assert.Contains("access_token = \"\"", toml);
        Assert.Contains("ladder_id = \"\"", toml);
        Assert.Contains("ladder_launch_ticket = \"\"", toml);
        Assert.DoesNotContain("token-abc", toml);
    }

    [Fact]
    public async Task DisablingSucceedsWhenNothingWasEverConfigured()
    {
        Assert.True(await ServerSavesConfigService.DisableAsync(_installDirectory));
        Assert.False(File.Exists(ModConfigPath));
    }

    [Fact]
    public async Task ExistingCommentsAndUnrelatedSettingsSurvive()
    {
        InstallPlugin(ModLoaderRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(ModConfigPath)!);
        await File.WriteAllTextAsync(
            ModConfigPath,
            "# a comment the player wrote\r\n"
            + "enabled = false\r\n"
            + "api_base_url = \"https://old.example\"\r\n"
            + "offline_policy = \"local\"\r\n"
            + "poll_interval_ms = 5000\r\n");

        await ServerSavesConfigService.EnableAsync(
            _installDirectory,
            new ServerSavesLaunchSettings("http://localhost:5000", "token-abc", Ladder, "ticket-abc"));

        var toml = await File.ReadAllTextAsync(ModConfigPath);
        Assert.Contains("# a comment the player wrote", toml);
        Assert.Contains("offline_policy = \"local\"", toml);
        Assert.Contains("poll_interval_ms = 5000", toml);
        Assert.Contains("enabled = true", toml);
        Assert.DoesNotContain("https://old.example", toml);
        Assert.Single(toml.Split('\n'), line => line.TrimStart().StartsWith("enabled =", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AGlobalScopeInstallIsConfiguredToo()
    {
        var globalRoot = Path.Combine(_installDirectory, "d2rloader");
        InstallPlugin(globalRoot);

        Assert.True(await ServerSavesConfigService.EnableAsync(
            _installDirectory,
            new ServerSavesLaunchSettings("http://localhost:5000", "token-abc", Ladder, "ticket-abc")));

        Assert.True(File.Exists(Path.Combine(globalRoot, "config", "server-saves.toml")));
        Assert.False(File.Exists(ModConfigPath));
    }

    [Fact]
    public async Task ANullLadderIdBecomesTheNoLadderBucket()
    {
        InstallPlugin(ModLoaderRoot);

        await ServerSavesConfigService.EnableAsync(
            _installDirectory,
            new ServerSavesLaunchSettings("http://localhost:5000", "token-abc", null, ""));

        Assert.Contains("ladder_id = \"\"", await File.ReadAllTextAsync(ModConfigPath));
    }

    [Theory]
    [InlineData("enabled = false\n")]
    [InlineData("  enabled   =   false  \n")]
    [InlineData("# enabled = false\n")]
    [InlineData("enabled_extra_setting = false\n")]
    [InlineData("")]
    public async Task EnablingLandsExactlyOneRealAssignment(string seed)
    {
        InstallPlugin(ModLoaderRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(ModConfigPath)!);
        await File.WriteAllTextAsync(ModConfigPath, seed);

        await ServerSavesConfigService.EnableAsync(
            _installDirectory,
            new ServerSavesLaunchSettings("http://localhost:5000", "token-abc", Ladder, "ticket-abc"));

        var assignments = (await File.ReadAllLinesAsync(ModConfigPath))
            .Where(line => line.TrimStart().StartsWith("enabled ", StringComparison.Ordinal)
                           || line.TrimStart().StartsWith("enabled=", StringComparison.Ordinal))
            .ToArray();
        Assert.Single(assignments);
        Assert.Equal("enabled = true", assignments[0].Trim());

        // A commented-out line is not an assignment and must be left alone.
        var toml = await File.ReadAllTextAsync(ModConfigPath);
        if (seed.StartsWith('#'))
        {
            Assert.Contains("# enabled = false", toml);
        }

        if (seed.StartsWith("enabled_extra", StringComparison.Ordinal))
        {
            Assert.Contains("enabled_extra_setting = false", toml);
        }
    }

    private static void InstallPlugin(string loaderRoot)
    {
        var pluginsDirectory = Path.Combine(loaderRoot, "plugins");
        Directory.CreateDirectory(pluginsDirectory);
        File.WriteAllBytes(Path.Combine(pluginsDirectory, ServerSavesConfigService.PluginFileName), [0x4D, 0x5A]);
    }

    public void Dispose()
    {
        if (Directory.Exists(_installDirectory))
        {
            Directory.Delete(_installDirectory, recursive: true);
        }
    }
}
