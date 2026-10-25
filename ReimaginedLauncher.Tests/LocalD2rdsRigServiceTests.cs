using ReimaginedLauncher.HttpClients.Models;
using ReimaginedLauncher.Utilities;
using Xunit;

namespace ReimaginedLauncher.Tests;

public sealed class LocalD2rdsRigServiceTests : IDisposable
{
    private readonly string _install = Path.Combine(Path.GetTempPath(), "reimagined-local-rig-tests", Guid.NewGuid().ToString("N"));

    public LocalD2rdsRigServiceTests()
    {
        Directory.CreateDirectory(_install);
    }

    private string Write(string relative, string text)
    {
        var path = Path.Combine(_install, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    private string Ladder(string relative) => Path.Combine(_install, "mods", "ReimaginedLadder", relative.Replace('/', Path.DirectorySeparatorChar));

    [Fact]
    public void Mirror_makes_the_ladder_folder_a_copy_of_the_local_mod_but_its_configs_and_logs()
    {
        Write("mods/Reimagined/d2rloader/plugins/d2rl-d2rds.dll", "server");
        Write("mods/Reimagined/d2rloader/plugins/d2rl-lobby-bridge.dll", "local-lobby");
        Write("mods/Reimagined/Reimagined.mpq/modinfo.json", "{\"savepath\":\"ReimaginedThree/\"}");
        Write("mods/Reimagined/Reimagined.mpq/data/global/excel/levels.txt", "levels");
        Write("mods/Reimagined/d2rloader/config/maps.toml", "player config");
        Write("mods/Reimagined/d2rloader/plugins/d2rl-maps.pdb", "symbols");
        Write("mods/ReimaginedLadder/d2rloader/plugins/d2rl-lobby-bridge.dll", "signed-lobby");
        Write("mods/ReimaginedLadder/d2rloader/plugins/d2rl-stale.dll", "not in the local mod");
        Write("mods/ReimaginedLadder/d2rloader/config/server-saves.toml", "launcher config");
        Write("mods/ReimaginedLadder/d2rloader/logs/d2rds.log", "log");

        var first = LocalD2rdsRigService.Mirror(_install);

        Assert.Equal(4, first.Copied);
        Assert.Equal(1, first.Removed);
        Assert.Equal("server", File.ReadAllText(Ladder("d2rloader/plugins/d2rl-d2rds.dll")));
        Assert.Equal("local-lobby", File.ReadAllText(Ladder("d2rloader/plugins/d2rl-lobby-bridge.dll")));
        Assert.True(File.Exists(Ladder("ReimaginedLadder.mpq/modinfo.json")));
        Assert.True(File.Exists(Ladder("ReimaginedLadder.mpq/data/global/excel/levels.txt")));
        Assert.False(Directory.Exists(Ladder("Reimagined.mpq")));
        Assert.False(File.Exists(Ladder("d2rloader/plugins/d2rl-stale.dll")));
        Assert.False(File.Exists(Ladder("d2rloader/plugins/d2rl-maps.pdb")));
        Assert.False(File.Exists(Ladder("d2rloader/config/maps.toml")));
        Assert.Equal("launcher config", File.ReadAllText(Ladder("d2rloader/config/server-saves.toml")));
        Assert.True(File.Exists(Ladder("d2rloader/logs/d2rds.log")));

        var again = LocalD2rdsRigService.Mirror(_install);
        Assert.Equal(0, again.Copied);
        Assert.Equal(4, again.Unchanged);
        Assert.Equal(0, again.Removed);
    }

    [Fact]
    public void Server_files_point_d2rds_at_the_api_and_keep_other_secrets()
    {
        Write("reimagined-secrets/server.toml", "api_token = \"control\"\nlobby_server_key = \"old\"\n");

        LocalD2rdsRigService.WriteServerConfig(_install, "http://localhost:5000");
        LocalD2rdsRigService.WriteServerSecret(_install, "ABCDEF0123");

        var config = File.ReadAllText(Ladder("d2rloader/config/d2rds.toml"));
        Assert.Contains("lobby_api_base_url = \"http://localhost:5000\"", config);
        Assert.Contains("authoritative_saves = true", config);
        Assert.Contains("enabled = false", config);
        Assert.Contains("lobby_server_key = \"\"", config);
        var secrets = File.ReadAllLines(Path.Combine(_install, "reimagined-secrets", "server.toml"));
        Assert.Contains("api_token = \"control\"", secrets);
        Assert.Single(secrets, line => line.StartsWith("lobby_server_key", StringComparison.Ordinal));
        Assert.Contains("lobby_server_key = \"ABCDEF0123\"", secrets);
        Assert.Throws<ArgumentException>(() => LocalD2rdsRigService.WriteServerSecret(_install, "not\"hex"));
        Assert.Throws<ArgumentException>(() => LocalD2rdsRigService.WriteServerConfig(_install, "http://x\"y"));
    }

    [Fact]
    public void Only_admins_and_moderators_with_d2rds_installed_get_the_rig()
    {
        static ReimaginedUserResponse User(params string[] roles) => new(Guid.NewGuid(), "a@b.c", "a", null, null, null, roles, DateTime.UtcNow);
        Assert.True(LocalD2rdsRigService.IsStaff(User("Admin")));
        Assert.True(LocalD2rdsRigService.IsStaff(User("moderator", "Tester")));
        Assert.False(LocalD2rdsRigService.IsStaff(User("Tester")));
        Assert.False(LocalD2rdsRigService.IsStaff(null));

        Assert.NotNull(LocalD2rdsRigService.Problem(_install));
        File.WriteAllBytes(Path.Combine(_install, "D2RLoader.exe"), [0x4D, 0x5A]);
        Write("mods/Reimagined/d2rloader/plugins/d2rl-server-saves.dll", "x");
        Write("mods/Reimagined/d2rloader/plugins/d2rl-lobby-bridge.dll", "x");
        Assert.Contains("d2rl-d2rds.dll", LocalD2rdsRigService.Problem(_install));
        Write("mods/Reimagined/d2rloader/plugins/d2rl-d2rds.dll", "x");
        Assert.Null(LocalD2rdsRigService.Problem(_install));
    }

    [Fact]
    public void Extension_detail_logs_are_turned_on_without_touching_other_settings()
    {
        var toml = "[d2rloader]\ndefault_mod = \"Reimagined\"\n\n[d2rloader.developer.logs]\nenabled = false\nnative_blizzard = true\n";

        var updated = D2RLoaderService.UpdateExtensionDetailLogs(toml);

        Assert.Contains("default_mod = \"Reimagined\"", updated);
        var logs = updated[updated.IndexOf("[d2rloader.developer.logs]", StringComparison.Ordinal)..];
        Assert.Contains("enabled = true", logs);
        Assert.DoesNotContain("enabled = false", logs);
        Assert.Contains("extension_details = true", logs);
        Assert.Contains("native_blizzard = true", updated);
        Assert.Equal(updated, D2RLoaderService.UpdateExtensionDetailLogs(updated));
        Assert.Contains("[d2rloader.developer.logs]", D2RLoaderService.UpdateExtensionDetailLogs(string.Empty));
    }

    public void Dispose()
    {
        if (Directory.Exists(_install)) Directory.Delete(_install, recursive: true);
    }
}
