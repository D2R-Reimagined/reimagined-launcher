using ReimaginedLauncher.Utilities;
using System.Security.Cryptography;
using Xunit;

namespace ReimaginedLauncher.Tests;

public sealed class ServerSavesConfigServiceTests : IDisposable
{
    private static readonly Guid Ladder = Guid.Parse("3f2504e0-4f89-11d3-9a0c-0305e82c3301");

    private readonly string _installDirectory = Path.Combine(
        Path.GetTempPath(),
        $"reimagined-server-saves-tests-{Guid.NewGuid():N}");

    private string ModLoaderRoot => Path.Combine(_installDirectory, "mods", "Reimagined", "d2rloader");
    private string ModConfigPath => Path.Combine(ModLoaderRoot, "config", "server-saves.toml");
    private string InstalledPluginPath => Path.Combine(ModLoaderRoot, "plugins", ServerSavesConfigService.PluginFileName);

    [Fact]
    public async Task EnsureInstalledCopiesTheBundledPluginWhenNothingIsInstalled()
    {
        var bundled = CreateBundledPlugin([0x4D, 0x5A, 0x01]);

        Assert.True(await ServerSavesConfigService.EnsureInstalledAsync(_installDirectory, bundled));

        Assert.Equal(await File.ReadAllBytesAsync(bundled), await File.ReadAllBytesAsync(InstalledPluginPath));
    }

    [Fact]
    public async Task EnsureInstalledOverwritesAnOutdatedCopy()
    {
        var bundled = CreateBundledPlugin([0x4D, 0x5A, 0x02]);
        Directory.CreateDirectory(Path.GetDirectoryName(InstalledPluginPath)!);
        await File.WriteAllBytesAsync(InstalledPluginPath, [0x4D, 0x5A, 0x00, 0x00, 0x00]);

        Assert.True(await ServerSavesConfigService.EnsureInstalledAsync(_installDirectory, bundled));

        Assert.Equal(await File.ReadAllBytesAsync(bundled), await File.ReadAllBytesAsync(InstalledPluginPath));
    }

    [Fact]
    public async Task EnsureInstalledLeavesAMatchingCopyAlone()
    {
        var bundled = CreateBundledPlugin([0x4D, 0x5A, 0x03]);
        Directory.CreateDirectory(Path.GetDirectoryName(InstalledPluginPath)!);
        File.Copy(bundled, InstalledPluginPath);
        var writtenAtUtc = File.GetLastWriteTimeUtc(InstalledPluginPath);

        // A copy is not overwritten just because it is present - only a real
        // content difference should touch the file the game will load from.
        await Task.Delay(50);
        Assert.True(await ServerSavesConfigService.EnsureInstalledAsync(_installDirectory, bundled));

        Assert.Equal(writtenAtUtc, File.GetLastWriteTimeUtc(InstalledPluginPath));
    }

    [Fact]
    public async Task EnsureInstalledFailsWhenTheBundledAssetIsMissing()
    {
        var missing = Path.Combine(_installDirectory, "does-not-exist.dll");

        Assert.False(await ServerSavesConfigService.EnsureInstalledAsync(_installDirectory, missing));
        Assert.False(File.Exists(InstalledPluginPath));
    }

    [Fact]
    public async Task EnsureInstalledFailsWithoutAnInstallDirectory()
    {
        var bundled = CreateBundledPlugin([0x4D, 0x5A]);

        Assert.False(await ServerSavesConfigService.EnsureInstalledAsync(null, bundled));
    }

    [Fact]
    public async Task EnsureInstalledOnlyEverTargetsModScope()
    {
        var bundled = CreateBundledPlugin([0x4D, 0x5A, 0x04]);

        Assert.True(await ServerSavesConfigService.EnsureInstalledAsync(_installDirectory, bundled));

        Assert.True(File.Exists(InstalledPluginPath));
        Assert.False(File.Exists(Path.Combine(_installDirectory, "d2rloader", "plugins", ServerSavesConfigService.PluginFileName)));
    }

    [Fact]
    public void BundledPluginOnlySatisfiesAnApprovalWithTheExactFileNameAndHash()
    {
        var bundled = CreateBundledPlugin([0x4D, 0x5A, 0x05]);
        var sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(bundled)));

        Assert.True(ServerSavesConfigService.CanSupplyApprovedPlugin(
            ServerSavesConfigService.PluginFileName,
            sha256,
            bundled));
        Assert.False(ServerSavesConfigService.CanSupplyApprovedPlugin(
            "another-plugin.dll",
            sha256,
            bundled));
        Assert.False(ServerSavesConfigService.CanSupplyApprovedPlugin(
            ServerSavesConfigService.PluginFileName,
            new string('0', 64),
            bundled));
    }

    private string CreateBundledPlugin(byte[] content)
    {
        var path = Path.Combine(_installDirectory, $"bundled-{Guid.NewGuid():N}.dll");
        Directory.CreateDirectory(_installDirectory);
        File.WriteAllBytes(path, content);
        return path;
    }

    [Fact]
    public async Task EnablingWritesTheLaunchSettingsWhenThePluginIsInstalled()
    {
        InstallPlugin(ModLoaderRoot);

        var enabled = await ServerSavesConfigService.EnableAsync(
            _installDirectory,
            new ServerSavesLaunchSettings("http://localhost:5000/", "token-abc", Ladder, "ticket-abc"));

        Assert.True(enabled);
        var toml = await File.ReadAllTextAsync(ModConfigPath);
        Assert.Contains("enabled = true", toml);
        Assert.Contains("api_base_url = \"http://localhost:5000\"", toml);
        Assert.Contains("access_token = \"token-abc\"", toml);
        Assert.Contains($"ladder_id = \"{Ladder}\"", toml);
        Assert.Contains("ladder_launch_ticket = \"ticket-abc\"", toml);
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
