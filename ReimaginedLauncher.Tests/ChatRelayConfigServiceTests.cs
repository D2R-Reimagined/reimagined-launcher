using ReimaginedLauncher.Utilities;
using System.Security.Cryptography;
using Xunit;

namespace ReimaginedLauncher.Tests;

public sealed class ChatRelayConfigServiceTests : IDisposable
{
    private const string ApiBaseUrl = "https://api.d2rreimagined.com";
    private const string AccessToken = "token-abc-123";

    private readonly string _installDirectory = Path.Combine(
        Path.GetTempPath(),
        $"reimagined-chat-relay-tests-{Guid.NewGuid():N}");

    private string ModLoaderRoot => Path.Combine(_installDirectory, "mods", "Reimagined", "d2rloader");
    private string ModConfigPath => Path.Combine(ModLoaderRoot, "config", "chat-relay.toml");
    private string InstalledPluginPath => Path.Combine(ModLoaderRoot, "plugins", ChatRelayConfigService.PluginFileName);

    [Fact]
    public async Task EnsureInstalledCopiesTheBundledPlugin()
    {
        var bundled = CreateBundledPlugin([0x4D, 0x5A, 0x11]);

        Assert.True(await ChatRelayConfigService.EnsureInstalledAsync(_installDirectory, bundled));

        Assert.Equal(await File.ReadAllBytesAsync(bundled), await File.ReadAllBytesAsync(InstalledPluginPath));
    }

    [Fact]
    public async Task EnsureInstalledOnlyEverTargetsModScope()
    {
        var bundled = CreateBundledPlugin([0x4D, 0x5A, 0x12]);

        Assert.True(await ChatRelayConfigService.EnsureInstalledAsync(_installDirectory, bundled));

        // The plugin's plugin.json fixes its scope to the mod, so a copy in the
        // global loader folder would never be loaded and would only confuse the
        // ladder's extension policy.
        Assert.False(File.Exists(
            Path.Combine(_installDirectory, "d2rloader", "plugins", ChatRelayConfigService.PluginFileName)));
    }

    [Fact]
    public void TheBundledPluginOnlySatisfiesAnApprovalWithTheExactFileNameAndHash()
    {
        var bundled = CreateBundledPlugin([0x4D, 0x5A, 0x13]);
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(bundled)));

        Assert.True(ChatRelayConfigService.CanSupplyApprovedPlugin(
            ChatRelayConfigService.PluginFileName, hash, bundled));

        // A rebuilt DLL is an unapproved DLL until the ladder's row is updated.
        Assert.False(ChatRelayConfigService.CanSupplyApprovedPlugin(
            ChatRelayConfigService.PluginFileName, new string('A', 64), bundled));

        // And the server-saves approval must never be satisfied by this one.
        Assert.False(ChatRelayConfigService.CanSupplyApprovedPlugin(
            ServerSavesConfigService.PluginFileName, hash, bundled));
    }

    [Fact]
    public async Task EnablingWritesTheApiAddressAndToken()
    {
        InstallPlugin(ModLoaderRoot);

        Assert.True(await ChatRelayConfigService.EnableAsync(
            _installDirectory,
            new ChatRelayLaunchSettings(ApiBaseUrl, AccessToken)));

        var toml = await File.ReadAllTextAsync(ModConfigPath);
        Assert.Contains("enabled = true", toml);
        Assert.Contains($"api_base_url = \"{ApiBaseUrl}\"", toml);
        Assert.Contains($"access_token = \"{AccessToken}\"", toml);
    }

    [Fact]
    public async Task EnablingTrimsATrailingSlashFromTheApiAddress()
    {
        InstallPlugin(ModLoaderRoot);

        Assert.True(await ChatRelayConfigService.EnableAsync(
            _installDirectory,
            new ChatRelayLaunchSettings(ApiBaseUrl + "/", AccessToken)));

        // The plugin appends the endpoint path itself, so a trailing slash would
        // produce a double slash in the request target.
        Assert.Contains($"api_base_url = \"{ApiBaseUrl}\"", await File.ReadAllTextAsync(ModConfigPath));
    }

    [Fact]
    public async Task EnablingIsRefusedWhenThePluginIsNotInstalled()
    {
        Assert.False(await ChatRelayConfigService.EnableAsync(
            _installDirectory,
            new ChatRelayLaunchSettings(ApiBaseUrl, AccessToken)));

        Assert.False(File.Exists(ModConfigPath));
    }

    [Fact]
    public async Task EnablingIsRefusedWithoutAnAccessToken()
    {
        InstallPlugin(ModLoaderRoot);

        // Capturing what a player types with nowhere to send it would read their
        // chat for nothing.
        Assert.False(await ChatRelayConfigService.EnableAsync(
            _installDirectory,
            new ChatRelayLaunchSettings(ApiBaseUrl, "   ")));
    }

    [Fact]
    public async Task DisablingTurnsCaptureOffAndClearsTheToken()
    {
        InstallPlugin(ModLoaderRoot);
        await ChatRelayConfigService.EnableAsync(
            _installDirectory,
            new ChatRelayLaunchSettings(ApiBaseUrl, AccessToken));

        Assert.True(await ChatRelayConfigService.DisableAsync(_installDirectory));

        var toml = await File.ReadAllTextAsync(ModConfigPath);
        Assert.Contains("enabled = false", toml);
        Assert.Contains("access_token = \"\"", toml);
        Assert.DoesNotContain(AccessToken, toml);
    }

    [Fact]
    public async Task DisablingSucceedsWhenNothingWasEverConfigured()
    {
        // Every non-ladder launch calls this. A player who has never touched the
        // ladder must not see it fail.
        Assert.True(await ChatRelayConfigService.DisableAsync(_installDirectory));
    }

    [Fact]
    public async Task WhisperPolicyAndOtherPlayerSettingsSurviveALaunch()
    {
        InstallPlugin(ModLoaderRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(ModConfigPath)!);
        await File.WriteAllTextAsync(
            ModConfigPath,
            "# my notes\nrelay_whispers = true\nverbose = true\nenabled = false\naccess_token = \"stale\"\n");

        Assert.True(await ChatRelayConfigService.EnableAsync(
            _installDirectory,
            new ChatRelayLaunchSettings(ApiBaseUrl, AccessToken)));

        var toml = await File.ReadAllTextAsync(ModConfigPath);

        // The launcher owns three settings. Everything else is the player's,
        // including a whisper policy they deliberately changed.
        Assert.Contains("# my notes", toml);
        Assert.Contains("relay_whispers = true", toml);
        Assert.Contains("verbose = true", toml);
        Assert.Contains("enabled = true", toml);
        Assert.Contains($"access_token = \"{AccessToken}\"", toml);
        Assert.DoesNotContain("stale", toml);
    }

    [Fact]
    public async Task EnablingLandsExactlyOneRealAssignmentPerKey()
    {
        InstallPlugin(ModLoaderRoot);

        await ChatRelayConfigService.EnableAsync(
            _installDirectory,
            new ChatRelayLaunchSettings(ApiBaseUrl, AccessToken));
        await ChatRelayConfigService.EnableAsync(
            _installDirectory,
            new ChatRelayLaunchSettings(ApiBaseUrl, "second-token"));

        var lines = (await File.ReadAllLinesAsync(ModConfigPath))
            .Select(line => line.TrimStart())
            .ToArray();

        // Rewriting must replace, not append, or the plugin's first-match read
        // would keep seeing the original value.
        Assert.Single(lines, line => line.StartsWith("access_token =", StringComparison.Ordinal));
        Assert.Single(lines, line => line.StartsWith("enabled =", StringComparison.Ordinal));
        Assert.Contains("access_token = \"second-token\"", await File.ReadAllTextAsync(ModConfigPath));
    }

    [Fact]
    public async Task AGlobalScopeConfigIsRewrittenToo()
    {
        var globalRoot = Path.Combine(_installDirectory, "d2rloader");
        InstallPlugin(ModLoaderRoot);
        InstallPlugin(globalRoot);

        Assert.True(await ChatRelayConfigService.EnableAsync(
            _installDirectory,
            new ChatRelayLaunchSettings(ApiBaseUrl, AccessToken)));

        // A stale config in the global scope would otherwise keep an old token
        // alive somewhere the player cannot see.
        Assert.Contains(
            $"access_token = \"{AccessToken}\"",
            await File.ReadAllTextAsync(Path.Combine(globalRoot, "config", "chat-relay.toml")));
    }

    private string CreateBundledPlugin(byte[] contents)
    {
        var path = Path.Combine(_installDirectory, "bundled", ChatRelayConfigService.PluginFileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, contents);
        return path;
    }

    private static void InstallPlugin(string loaderRoot)
    {
        var pluginsDirectory = Path.Combine(loaderRoot, "plugins");
        Directory.CreateDirectory(pluginsDirectory);
        File.WriteAllBytes(Path.Combine(pluginsDirectory, ChatRelayConfigService.PluginFileName), [0x4D, 0x5A]);
    }

    public void Dispose()
    {
        if (Directory.Exists(_installDirectory))
        {
            Directory.Delete(_installDirectory, recursive: true);
        }
    }
}
