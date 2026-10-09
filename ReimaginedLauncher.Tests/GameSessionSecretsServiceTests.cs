using System.Text;
using ReimaginedLauncher.Utilities;
using Xunit;

namespace ReimaginedLauncher.Tests;

public sealed class GameSessionSecretsServiceTests : IDisposable
{
    private static readonly DateTimeOffset Expiry = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

    private readonly string _installDirectory = Path.Combine(
        Path.GetTempPath(),
        $"reimagined-session-secrets-tests-{Guid.NewGuid():N}");

    private string SessionPath => Path.Combine(_installDirectory, "reimagined-secrets", "session.toml");

    [Fact]
    public void TheSessionFileLivesInTheInstallRootNeverUnderMods()
    {
        var path = GameSessionSecretsService.GetPath(_installDirectory);

        Assert.Equal(SessionPath, path);
        Assert.DoesNotContain($"{Path.DirectorySeparatorChar}mods{Path.DirectorySeparatorChar}", path!);
        Assert.Null(GameSessionSecretsService.GetPath(null));
    }

    [Fact]
    public void FormatWritesTheContractExactly()
    {
        var content = GameSessionSecretsService.Format(
            new GameSessionSecrets("tok", Expiry, "ticket", "status-id"));

        Assert.Equal(
            "# Reimagined launcher - secrets for the running game session.\n"
            + "# Rewritten before every launch and deleted when the game exits. Never share\n"
            + "# this file. It lives outside mods/ so D2RLoader never hands it to other players.\n"
            + "access_token = \"tok\"\n"
            + $"expires_at_unix = {Expiry.ToUnixTimeSeconds()}\n"
            + "ladder_launch_ticket = \"ticket\"\n"
            + "status_session_id = \"status-id\"\n",
            content);
        Assert.DoesNotContain("\r", content);
    }

    [Fact]
    public void FormatWritesEmptyStringsForAnOnlineLaunchWithoutTicketOrStatus()
    {
        var content = GameSessionSecretsService.Format(new GameSessionSecrets("tok", Expiry));

        Assert.Contains("ladder_launch_ticket = \"\"\n", content);
        Assert.Contains("status_session_id = \"\"\n", content);
    }

    [Fact]
    public void FormatEscapesBackslashesAndQuotes()
    {
        var content = GameSessionSecretsService.Format(
            new GameSessionSecrets("a\\b\"c", Expiry, "t\"x", "s\\y"));

        Assert.Contains("access_token = \"a\\\\b\\\"c\"\n", content);
        Assert.Contains("ladder_launch_ticket = \"t\\\"x\"\n", content);
        Assert.Contains("status_session_id = \"s\\\\y\"\n", content);
    }

    [Fact]
    public void FormatRefusesAValueThatCouldInjectAnotherLine()
    {
        Assert.Throws<ArgumentException>(() => GameSessionSecretsService.Format(
            new GameSessionSecrets("tok\nexpires_at_unix = 9999999999", Expiry)));
    }

    [Fact]
    public async Task WriteCreatesTheFolderAndWritesUtf8WithoutABom()
    {
        Assert.True(await GameSessionSecretsService.WriteAsync(
            _installDirectory, new GameSessionSecrets("tok", Expiry, "ticket", "status")));

        var bytes = await File.ReadAllBytesAsync(SessionPath);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
        Assert.Equal(
            GameSessionSecretsService.Format(new GameSessionSecrets("tok", Expiry, "ticket", "status")),
            Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public async Task OnWindowsTheFileIsRestrictedToTheCurrentUser()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.True(await GameSessionSecretsService.WriteAsync(
            _installDirectory, new GameSessionSecrets("tok", Expiry)));

        var security = new FileInfo(SessionPath).GetAccessControl();
        Assert.True(security.AreAccessRulesProtected);
        var rules = security.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier))
            .Cast<System.Security.AccessControl.FileSystemAccessRule>()
            .ToList();
        var rule = Assert.Single(rules);
        Assert.Equal(System.Security.Principal.WindowsIdentity.GetCurrent().User, rule.IdentityReference);
    }

    [Fact]
    public async Task WriteReplacesAnEarlierSessionAndLeavesNoTempFiles()
    {
        Assert.True(await GameSessionSecretsService.WriteAsync(
            _installDirectory, new GameSessionSecrets("first-token", Expiry, "first-ticket")));
        Assert.True(await GameSessionSecretsService.WriteAsync(
            _installDirectory, new GameSessionSecrets("second-token", Expiry.AddHours(1))));

        var content = await File.ReadAllTextAsync(SessionPath);
        Assert.Contains("access_token = \"second-token\"", content);
        Assert.DoesNotContain("first-token", content);
        Assert.DoesNotContain("first-ticket", content);
        Assert.Equal([SessionPath], Directory.GetFiles(Path.GetDirectoryName(SessionPath)!));
    }

    [Fact]
    public async Task WriteRefusesAnUnwritableValueWithoutTouchingTheDisk()
    {
        Assert.False(await GameSessionSecretsService.WriteAsync(
            _installDirectory, new GameSessionSecrets("bad\rtoken", Expiry)));

        Assert.False(File.Exists(SessionPath));
    }

    [Fact]
    public async Task DeleteRemovesTheFileAndLeftoverTempFiles()
    {
        Assert.True(await GameSessionSecretsService.WriteAsync(
            _installDirectory, new GameSessionSecrets("tok", Expiry)));
        var leftover = Path.Combine(Path.GetDirectoryName(SessionPath)!, "session.toml.0123.tmp");
        await File.WriteAllTextAsync(leftover, "access_token = \"half-written\"\n");

        Assert.True(GameSessionSecretsService.Delete(_installDirectory));

        Assert.False(File.Exists(SessionPath));
        Assert.False(File.Exists(leftover));
    }

    [Fact]
    public void DeletingAFileThatIsNotThereIsSuccess()
    {
        Assert.True(GameSessionSecretsService.Delete(_installDirectory));
        Assert.True(GameSessionSecretsService.Delete(null));
    }

    [Fact]
    public void ScrubBlanksEverySecretKeyAndKeepsEverythingElse()
    {
        const string toml =
            "# my notes\r\n"
            + "enabled = true\r\n"
            + "api_base_url = \"https://api.d2r-reimagined.com\"\r\n"
            + "  access_token = \"eyJ-admin\" # old launcher\r\n"
            + "ladder_launch_ticket=\"ticket\"\r\n"
            + "status_session_id = \"abc\"\r\n"
            + "access_token_hint = \"keep me\"\r\n"
            + "channel_tag = \"[World]\"";

        var scrubbed = GameSessionSecretsService.ScrubSecrets(toml);

        Assert.Equal(
            "# my notes\r\n"
            + "enabled = true\r\n"
            + "api_base_url = \"https://api.d2r-reimagined.com\"\r\n"
            + "access_token = \"\"\r\n"
            + "ladder_launch_ticket = \"\"\r\n"
            + "status_session_id = \"\"\r\n"
            + "access_token_hint = \"keep me\"\r\n"
            + "channel_tag = \"[World]\"",
            scrubbed);
    }

    [Fact]
    public void ScrubNeverAddsKeysThatAreNotThere()
    {
        const string toml = "enabled = true\nchannel_tag = \"[World]\"\n";

        Assert.Equal(toml, GameSessionSecretsService.ScrubSecrets(toml));
    }

    [Fact]
    public void ScrubBlanksRepeatedAssignmentsToo()
    {
        var scrubbed = GameSessionSecretsService.ScrubSecrets(
            "access_token = \"one\"\n[section]\naccess_token = \"two\"\n");

        Assert.DoesNotContain("one", scrubbed);
        Assert.DoesNotContain("two", scrubbed);
    }

    [Fact]
    public async Task ScrubCleansOurPluginConfigsInBothModsAndTheGlobalLoaderFolder()
    {
        var normal = ConfigPath("mods", "Reimagined", "d2rloader", "config", "server-saves.toml");
        var ladder = ConfigPath("mods", "ReimaginedLadder", "d2rloader", "config", "global-chat.toml");
        var lobby = ConfigPath("mods", "ReimaginedLadder", "d2rloader", "config", "lobby-bridge.toml");
        var global = ConfigPath("d2rloader", "config", "trade-price-check.toml");
        var foreign = ConfigPath("mods", "Reimagined", "d2rloader", "config", "someone-elses-plugin.toml");
        await WriteConfig(normal, "enabled = false\naccess_token = \"secret-1\"\nladder_launch_ticket = \"ticket-1\"\nstatus_session_id = \"status-1\"\n");
        await WriteConfig(ladder, "# disabled plugin\nenabled = false\naccess_token = \"secret-2\"\n");
        await WriteConfig(lobby, "capture = true\naccess_token = \"secret-3\"\n");
        await WriteConfig(global, "access_token = \"secret-4\"\nmax_results = 9\n");
        await WriteConfig(foreign, "access_token = \"theirs\"\n");

        var scrubbed = await GameSessionSecretsService.ScrubPluginConfigsAsync(_installDirectory);

        Assert.Equal(4, scrubbed);
        Assert.Equal("enabled = false\naccess_token = \"\"\nladder_launch_ticket = \"\"\nstatus_session_id = \"\"\n",
            await File.ReadAllTextAsync(normal));
        Assert.Equal("# disabled plugin\nenabled = false\naccess_token = \"\"\n", await File.ReadAllTextAsync(ladder));
        Assert.Equal("capture = true\naccess_token = \"\"\n", await File.ReadAllTextAsync(lobby));
        Assert.Equal("access_token = \"\"\nmax_results = 9\n", await File.ReadAllTextAsync(global));
        // Not one of ours: left exactly as it was.
        Assert.Equal("access_token = \"theirs\"\n", await File.ReadAllTextAsync(foreign));
    }

    [Fact]
    public async Task ScrubCleansTheLaunchersStashedCopiesOfTheNormalMod()
    {
        // Switching back from a ladder copies this stash over mods/Reimagined,
        // so a token left in it would come straight back.
        var stash = ConfigPath(".reimagined-launcher", "normal-mod", "Reimagined", "d2rloader", "config", "server-saves.toml");
        var swapBackup = ConfigPath(".reimagined-launcher", "mod-backups", "0123abcd", "previous", "d2rloader", "config", "global-chat.toml");
        await WriteConfig(stash, "access_token = \"stashed\"\nladder_launch_ticket = \"stashed-ticket\"\n");
        await WriteConfig(swapBackup, "access_token = \"backed-up\"\n");

        Assert.Equal(2, await GameSessionSecretsService.ScrubPluginConfigsAsync(_installDirectory));

        Assert.Equal("access_token = \"\"\nladder_launch_ticket = \"\"\n", await File.ReadAllTextAsync(stash));
        Assert.Equal("access_token = \"\"\n", await File.ReadAllTextAsync(swapBackup));
    }

    [Fact]
    public async Task ScrubLeavesAlreadyCleanConfigsUntouched()
    {
        var path = ConfigPath("mods", "Reimagined", "d2rloader", "config", "announcements.toml");
        await WriteConfig(path, "enabled = true\naccess_token = \"\"\n");
        var stamp = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, stamp);

        Assert.Equal(0, await GameSessionSecretsService.ScrubPluginConfigsAsync(_installDirectory));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public async Task ScrubCoversEveryPluginTheLauncherHasWrittenSecretsFor()
    {
        var ids = new[]
        {
            AnnouncementsConfigService.PluginId, ChatRelayConfigService.PluginId,
            GlobalChatConfigService.PluginId, HardcoreDeathsConfigService.PluginId,
            ReimaginedFeedbackConfigService.PluginId, ServerSavesConfigService.PluginId,
            SupporterPortalsConfigService.PluginId, TradeNotificationsConfigService.PluginId,
            TradePriceCheckConfigService.PluginId, "lobby-bridge"
        };
        foreach (var id in ids)
        {
            await WriteConfig(ConfigPath("mods", "ReimaginedLadder", "d2rloader", "config", $"{id}.toml"),
                $"access_token = \"secret-for-{id}\"\n");
        }

        Assert.Equal(ids.Length, await GameSessionSecretsService.ScrubPluginConfigsAsync(_installDirectory));
        foreach (var id in ids)
        {
            Assert.DoesNotContain("secret-for",
                await File.ReadAllTextAsync(ConfigPath("mods", "ReimaginedLadder", "d2rloader", "config", $"{id}.toml")));
        }
    }

    private string ConfigPath(params string[] parts) => Path.Combine([_installDirectory, .. parts]);

    private static async Task WriteConfig(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content);
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
        catch (UnauthorizedAccessException)
        {
        }
    }
}
