using System.Text.Json;
using System.Text.Json.Nodes;
using ReimaginedLauncher.Utilities;
using Xunit;

namespace ReimaginedLauncher.Tests;

public class LadderToNormalCopyServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _ladderModRoot;
    private readonly string _normalModRoot;

    public LadderToNormalCopyServiceTests()
    {
        _tempDir = Path.Combine(
            Path.GetTempPath(),
            "reimagined-ladder-copy-tests-" + Guid.NewGuid().ToString("N"));
        _ladderModRoot = Path.Combine(_tempDir, "mods", ModInstallationPaths.LadderModName);
        _normalModRoot = Path.Combine(_tempDir, "mods", ModInstallationPaths.NormalModName);

        Directory.CreateDirectory(_tempDir);
        File.WriteAllText(Path.Combine(_tempDir, "D2R.exe"), "stub");
        File.WriteAllText(Path.Combine(_tempDir, "D2RLoader.exe"), "stub");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(_tempDir, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(entry, File.GetAttributes(entry) & ~FileAttributes.ReadOnly);
            }

            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Fact]
    public void Inspect_AppliesDefaultEnabledFlagsWarningsAndNormalizesLadderDisabled()
    {
        SeedMinimalLadderMod();

        WriteLadderPlugin("d2rl-server-saves.dll", "server-saves");
        WriteLadderPlugin("d2rl-lobby-bridge.dll", "lobby-bridge");
        WriteLadderPlugin("d2rl-d2rds.dll", "d2rds");
        WriteLadderPlugin("d2rl-ruffneckk-bnet-simulation.dll", "bnet-sim");
        WriteLadderPlugin("d2rl-ruffneckk-force-map-reset.dll", "force-map");
        WriteLadderPlugin("d2rl-ladder-droprates.dll", "droprates");
        WriteLadderPlugin("d2rl-maps.dll", "maps");
        WriteLadderPlugin("ignored.dll.disabled", "ignored");
        WriteLadderDisabledPlugin("policy-off.dll", "policy-off");

        WriteLadderPatch("ui-patch.json", "{\"active\":true}");
        WriteLadderDisabledPatch("ui-patch.json", "{\"active\":false}");
        WriteLadderDisabledPatch("policy-patch.json", "{\"policy\":true}");

        var plan = LadderToNormalCopyService.Inspect(_tempDir);

        Assert.True(LadderToNormalCopyService.HasInstalledLadderMod(_tempDir));
        Assert.Equal(LadderToNormalCopyService.DefaultSavePath, plan.TargetSavePath);

        Assert.DoesNotContain(plan.Extensions, item => item.FileName == "ignored.dll.disabled");

        var byFile = plan.Extensions.ToDictionary(
            item => $"{item.Kind}:{item.FileName}",
            StringComparer.OrdinalIgnoreCase);

        Assert.False(byFile["Plugin:d2rl-server-saves.dll"].DefaultEnabled);
        Assert.False(byFile["Plugin:d2rl-lobby-bridge.dll"].DefaultEnabled);
        Assert.False(byFile["Plugin:d2rl-d2rds.dll"].DefaultEnabled);
        Assert.False(byFile["Plugin:d2rl-ruffneckk-bnet-simulation.dll"].DefaultEnabled);

        Assert.True(byFile["Plugin:d2rl-ruffneckk-force-map-reset.dll"].DefaultEnabled);
        Assert.False(string.IsNullOrWhiteSpace(byFile["Plugin:d2rl-ruffneckk-force-map-reset.dll"].WarningNote));

        Assert.True(byFile["Plugin:d2rl-ladder-droprates.dll"].DefaultEnabled);
        Assert.False(string.IsNullOrWhiteSpace(byFile["Plugin:d2rl-ladder-droprates.dll"].WarningNote));

        Assert.True(byFile["Plugin:d2rl-maps.dll"].DefaultEnabled);
        Assert.Null(byFile["Plugin:d2rl-maps.dll"].WarningNote);

        Assert.True(byFile["Plugin:policy-off.dll"].DefaultEnabled);
        Assert.True(byFile["Patch:policy-patch.json"].DefaultEnabled);

        var uiPatch = byFile["Patch:ui-patch.json"];
        Assert.True(uiPatch.DefaultEnabled);
        Assert.Equal(
            Path.Combine(_ladderModRoot, "d2rloader", "patches", "ui-patch.json"),
            uiPatch.SourcePath);
    }

    [Fact]
    public async Task CopyAsync_RoutesExtensionsStripsLadderArtifactsScrubsSecretsAndPreservesSource()
    {
        SeedMinimalLadderMod(savePath: "ReimaginedLadder/");

        WriteLadderPlugin("d2rl-maps.dll", "maps-dll");
        WriteLadderPlugin("d2rl-maps.mpq", "maps-mpq");
        WriteLadderPlugin("d2rl-server-saves.dll", "server-saves");
        WriteLadderPlugin("ignored.dll.disabled", "ignored");
        WriteLadderDisabledPlugin("policy-off.dll", "policy-off");

        WriteLadderPatch("keep-patch.json", "{\"keep\":true}");
        WriteLadderPatch("uncheck-patch.json", "{\"uncheck\":true}");
        WriteLadderDisabledPatch("policy-patch.json", "{\"policy\":true}");

        WriteFile(Path.Combine(_ladderModRoot, "d2rloader", "logs", "session.log"), "secret log");
        WriteFile(Path.Combine(_ladderModRoot, "d2rloader", "ladder-bundle-state.json"), "{\"revision\":9}");
        WriteFile(Path.Combine(_ladderModRoot, "d2rloader", "launcher-disabled", "plugins", "stale.dll"), "stale");
        WriteFile(Path.Combine(_ladderModRoot, ".DISABLE_DEBUG"), "ladder");
        WriteFile(Path.Combine(_ladderModRoot, "ReimaginedLadder.mpq", ".DISABLE_DEBUG"), "nested");

        var layoutRelative = Path.Combine(
            "data", "global", "ui", "layouts", "characterselectpanelhd.json");
        var layoutJson = """
            {
              "name": "CharacterSelectPanel",
              "children": [
                { "type": "TextBoxWidget", "name": "CharacterNameText" },
                { "type": "TextBoxWidget", "name": "ReimaginedLadderNameInfo" },
                { "type": "TextBoxWidget", "name": "ReimaginedLadderRuntimeInfo" }
              ]
            }
            """;
        var sourceLayoutPath = Path.Combine(_ladderModRoot, "ReimaginedLadder.mpq", layoutRelative);
        WriteFile(sourceLayoutPath, layoutJson);

        var sourceConfigPath = Path.Combine(_ladderModRoot, "d2rloader", "config", "d2rl-trade.toml");
        WriteFile(sourceConfigPath, """
            enabled = true
            access_token = "tok_live_123"
            ladder_launch_ticket = "lt_live_456"
            status_session_id = "sid_live_789"
            """);

        var isolatedStatePath = Path.Combine(
            _tempDir, ".reimagined-launcher", "ladder-bundles", "isolated-ladder-bundle-state.json");
        WriteFile(isolatedStatePath, "{\"bundle\":\"v1\"}");

        var enabledKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            LadderToNormalCopyService.ExtensionKey(D2RLoaderExtensionKind.Plugin, "d2rl-maps.dll"),
            LadderToNormalCopyService.ExtensionKey(D2RLoaderExtensionKind.Plugin, "policy-off.dll"),
            LadderToNormalCopyService.ExtensionKey(D2RLoaderExtensionKind.Patch, "keep-patch.json"),
            LadderToNormalCopyService.ExtensionKey(D2RLoaderExtensionKind.Patch, "policy-patch.json")
        };

        var result = await LadderToNormalCopyService.CopyAsync(
            _tempDir,
            enabledKeys,
            keepExistingConfigs: false);

        Assert.Equal(_normalModRoot, result.DestinationModRoot);
        Assert.Equal(LadderToNormalCopyService.DefaultSavePath, result.ResolvedSavePath);
        Assert.Equal(4, result.EnabledCount);
        Assert.Equal(2, result.DisabledCount);

        Assert.True(Directory.Exists(Path.Combine(_normalModRoot, "Reimagined.mpq")));
        Assert.False(Directory.Exists(Path.Combine(_normalModRoot, "ReimaginedLadder.mpq")));

        Assert.True(File.Exists(Path.Combine(_normalModRoot, "d2rloader", "plugins", "d2rl-maps.dll")));
        Assert.True(File.Exists(Path.Combine(_normalModRoot, "d2rloader", "plugins", "d2rl-maps.mpq")));
        Assert.True(File.Exists(Path.Combine(_normalModRoot, "d2rloader", "plugins", "policy-off.dll")));
        Assert.True(File.Exists(Path.Combine(_normalModRoot, "d2rloader", "patches", "keep-patch.json")));
        Assert.True(File.Exists(Path.Combine(_normalModRoot, "d2rloader", "patches", "policy-patch.json")));

        Assert.True(File.Exists(Path.Combine(
            _normalModRoot, "d2rloader", "launcher-disabled", "plugins", "d2rl-server-saves.dll")));
        Assert.True(File.Exists(Path.Combine(
            _normalModRoot, "d2rloader", "launcher-disabled", "patches", "uncheck-patch.json")));

        Assert.False(Directory.Exists(Path.Combine(_normalModRoot, "d2rloader", "logs")));
        Assert.False(Directory.Exists(Path.Combine(_normalModRoot, "d2rloader", "ladder-disabled")));
        Assert.False(File.Exists(Path.Combine(_normalModRoot, "d2rloader", "ladder-bundle-state.json")));
        Assert.False(File.Exists(Path.Combine(_normalModRoot, "d2rloader", "plugins", "ignored.dll.disabled")));
        Assert.False(File.Exists(Path.Combine(
            _normalModRoot, "d2rloader", "launcher-disabled", "plugins", "stale.dll")));

        Assert.Empty(Directory.EnumerateFiles(_normalModRoot, ".DISABLE_DEBUG", SearchOption.AllDirectories));
        Assert.True(File.Exists(Path.Combine(_ladderModRoot, ".DISABLE_DEBUG")));
        Assert.True(File.Exists(Path.Combine(_ladderModRoot, "ReimaginedLadder.mpq", ".DISABLE_DEBUG")));

        var copiedLayout = JsonNode.Parse(
            await File.ReadAllTextAsync(Path.Combine(_normalModRoot, "Reimagined.mpq", layoutRelative)))!;
        var childNames = copiedLayout["children"]!.AsArray()
            .Select(node => node?["name"]?.GetValue<string>() ?? string.Empty)
            .ToArray();
        Assert.Equal(["CharacterNameText"], childNames);

        var copiedConfig = await File.ReadAllTextAsync(
            Path.Combine(_normalModRoot, "d2rloader", "config", "d2rl-trade.toml"));
        Assert.Contains("enabled = true", copiedConfig);
        Assert.DoesNotContain("tok_live_123", copiedConfig);
        Assert.DoesNotContain("lt_live_456", copiedConfig);
        Assert.DoesNotContain("sid_live_789", copiedConfig);

        // Source Ladder files and isolated state are untouched.
        Assert.Contains("tok_live_123", await File.ReadAllTextAsync(sourceConfigPath));
        Assert.Contains(LadderCharacterSelectService.GeneratedNameWidgetName, await File.ReadAllTextAsync(sourceLayoutPath));
        Assert.Equal("{\"bundle\":\"v1\"}", await File.ReadAllTextAsync(isolatedStatePath));

        // Verify launcher extension inventory & toggle round-trip work with the copied layout.
        var inventory = D2RLoaderService.Discover(
            _tempDir,
            LaunchExperience.Online,
            includeManualDisabled: true);
        var discoveredSaves = Assert.Single(inventory.Extensions,
            item => item.Kind == D2RLoaderExtensionKind.Plugin && item.FileName == "d2rl-server-saves.dll");
        Assert.False(discoveredSaves.IsEnabled);

        var profile = new InstallationProfile
        {
            InstallDirectory = _tempDir,
            LaunchExperience = LaunchExperience.Online
        };
        D2RLoaderExtensionToggleService.SetEnabled(profile, discoveredSaves, enabled: true, () => { });
        Assert.True(File.Exists(Path.Combine(_normalModRoot, "d2rloader", "plugins", "d2rl-server-saves.dll")));
        Assert.False(File.Exists(Path.Combine(
            _normalModRoot, "d2rloader", "launcher-disabled", "plugins", "d2rl-server-saves.dll")));
    }

    [Fact]
    public async Task CopyAsync_KeepsCompanionMpqInPluginsDirectoryWhenPluginIsUnchecked()
    {
        SeedMinimalLadderMod();
        WriteLadderPlugin("d2rl-maps.dll", "maps-dll");
        WriteLadderPlugin("d2rl-maps.mpq", "maps-mpq");

        var result = await LadderToNormalCopyService.CopyAsync(
            _tempDir,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            keepExistingConfigs: false);

        Assert.Equal(0, result.EnabledCount);
        Assert.Equal(1, result.DisabledCount);
        Assert.False(File.Exists(Path.Combine(_normalModRoot, "d2rloader", "plugins", "d2rl-maps.dll")));
        Assert.True(File.Exists(Path.Combine(
            _normalModRoot, "d2rloader", "launcher-disabled", "plugins", "d2rl-maps.dll")));
        Assert.True(File.Exists(Path.Combine(_normalModRoot, "d2rloader", "plugins", "d2rl-maps.mpq")));
    }

    [Fact]
    public async Task CopyAsync_UpdatesModInfoSavePathFallbackAndPreservesCustomSavePath()
    {
        SeedMinimalLadderMod(savePath: "ReimaginedLadder/");

        // Case 1: no existing Non-Ladder modinfo.json -> fallback to ReimaginedThree/
        await LadderToNormalCopyService.CopyAsync(
            _tempDir,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            keepExistingConfigs: false);

        var copiedModInfoPath = Path.Combine(_normalModRoot, "modinfo.json");
        var copiedMpqModInfoPath = Path.Combine(_normalModRoot, "Reimagined.mpq", "modinfo.json");
        Assert.Equal(LadderToNormalCopyService.DefaultSavePath, ReadSavePath(copiedModInfoPath));
        Assert.Equal(LadderToNormalCopyService.DefaultSavePath, ReadSavePath(copiedMpqModInfoPath));

        // Case 2: Non-Ladder had Ladder savepath -> fallback to ReimaginedThree/
        WriteFile(copiedModInfoPath, """{"name":"Reimagined","savepath":"ReimaginedLadder/"}""");
        WriteFile(copiedMpqModInfoPath, """{"name":"Reimagined","savepath":"ReimaginedLadder/"}""");
        Assert.Equal(LadderToNormalCopyService.DefaultSavePath, LadderToNormalCopyService.ResolveTargetSavePath(_tempDir));

        // Case 3: Non-Ladder has custom savepath without trailing slash -> preserved even if stale ladder-active exists
        WriteFile(Path.Combine(_tempDir, ".reimagined-launcher", "ladder-active"), "ladder");
        WriteFile(copiedModInfoPath, """{"name":"Reimagined","savepath":"CustomOffline"}""");
        WriteFile(copiedMpqModInfoPath, """{"name":"Reimagined","savepath":"CustomOffline"}""");
        Assert.Equal("CustomOffline/", LadderToNormalCopyService.ResolveTargetSavePath(_tempDir));

        var result = await LadderToNormalCopyService.CopyAsync(
            _tempDir,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            keepExistingConfigs: false);

        Assert.Equal("CustomOffline/", result.ResolvedSavePath);
        Assert.Equal("CustomOffline/", ReadSavePath(copiedModInfoPath));
        Assert.Equal("CustomOffline/", ReadSavePath(copiedMpqModInfoPath));
    }

    [Fact]
    public async Task CopyAsync_RetainsOnlyNewestTwoBackupsScrubsBackupSecretsAndHandlesReadOnlyFiles()
    {
        SeedMinimalLadderMod();
        File.SetAttributes(Path.Combine(_ladderModRoot, "modinfo.json"), FileAttributes.ReadOnly);
        File.SetAttributes(Path.Combine(_ladderModRoot, "ReimaginedLadder.mpq", "modinfo.json"), FileAttributes.ReadOnly);
        var enabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var iteration = 1; iteration <= 4; iteration++)
        {
            var markerPath = Path.Combine(_normalModRoot, "marker.txt");
            if (File.Exists(markerPath))
            {
                File.SetAttributes(markerPath, FileAttributes.Normal);
            }

            WriteFile(markerPath, $"v{iteration}");
            File.SetAttributes(markerPath, FileAttributes.ReadOnly);
            WriteFile(
                Path.Combine(_normalModRoot, "d2rloader", "config", "custom-plugin.toml"),
                $"access_token = \"tok_v{iteration}\"");

            await LadderToNormalCopyService.CopyAsync(
                _tempDir,
                enabled,
                keepExistingConfigs: false);
        }

        var backupRoot = Path.Combine(_tempDir, ".reimagined-launcher", LadderToNormalCopyService.BackupFolderName);
        var backupEntries = Directory.GetDirectories(backupRoot)
            .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(2, backupEntries.Length);

        var markers = backupEntries
            .Select(dir => File.ReadAllText(Path.Combine(dir, ModInstallationPaths.NormalModName, "marker.txt")))
            .ToArray();
        Assert.Equal(["v4", "v3"], markers);

        foreach (var entry in backupEntries)
        {
            var backupSecret = await File.ReadAllTextAsync(
                Path.Combine(entry, ModInstallationPaths.NormalModName, "d2rloader", "config", "custom-plugin.toml"));
            Assert.DoesNotContain("tok_v", backupSecret);

            WriteFile(
                Path.Combine(entry, ModInstallationPaths.NormalModName, "d2rloader", "config", "lobby-bridge.toml"),
                "access_token = \"seeded_after\"");
        }

        var scrubbedCount = await GameSessionSecretsService.ScrubPluginConfigsAsync(_tempDir);
        Assert.Equal(2, scrubbedCount);
        foreach (var entry in backupEntries)
        {
            var backupLobby = await File.ReadAllTextAsync(
                Path.Combine(entry, ModInstallationPaths.NormalModName, "d2rloader", "config", "lobby-bridge.toml"));
            Assert.DoesNotContain("seeded_after", backupLobby);
        }
    }

    [Fact]
    public async Task CopyAsync_KeepExistingConfigsPreservesNormalConfigsWhenTrueAndReplacesWhenFalse()
    {
        SeedMinimalLadderMod();
        WriteFile(
            Path.Combine(_ladderModRoot, "d2rloader", "config", "d2rl-shared.toml"),
            "mode = \"ladder\"");
        WriteFile(
            Path.Combine(_ladderModRoot, "d2rloader", "config", "ladder-only.toml"),
            "only_ladder = true");

        void SeedNormalConfigs()
        {
            WriteFile(
                Path.Combine(_normalModRoot, "d2rloader", "config", "d2rl-shared.toml"),
                "mode = \"normal\"");
            WriteFile(
                Path.Combine(_normalModRoot, "d2rloader", "config", "user-custom.toml"),
                "custom = true");
        }

        SeedNormalConfigs();
        await LadderToNormalCopyService.CopyAsync(
            _tempDir,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            keepExistingConfigs: true);

        Assert.Equal(
            "mode = \"normal\"",
            await File.ReadAllTextAsync(Path.Combine(_normalModRoot, "d2rloader", "config", "d2rl-shared.toml")));
        Assert.Equal(
            "custom = true",
            await File.ReadAllTextAsync(Path.Combine(_normalModRoot, "d2rloader", "config", "user-custom.toml")));
        Assert.True(File.Exists(Path.Combine(_normalModRoot, "d2rloader", "config", "ladder-only.toml")));

        SeedNormalConfigs();
        await LadderToNormalCopyService.CopyAsync(
            _tempDir,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            keepExistingConfigs: false);

        Assert.Equal(
            "mode = \"ladder\"",
            await File.ReadAllTextAsync(Path.Combine(_normalModRoot, "d2rloader", "config", "d2rl-shared.toml")));
        Assert.False(File.Exists(Path.Combine(_normalModRoot, "d2rloader", "config", "user-custom.toml")));
        Assert.True(File.Exists(Path.Combine(_normalModRoot, "d2rloader", "config", "ladder-only.toml")));
    }

    [Fact]
    public async Task CopyAsync_ScrubsD2rdsTokensAndSecretsInCopiedConfigs()
    {
        SeedMinimalLadderMod();
        WriteFile(
            Path.Combine(_ladderModRoot, "d2rloader", "config", "d2rds.toml"),
            "api_token = \"secret-api-tok\"\nlobby_server_key = \"secret-lobby-key\"\nhost = \"127.0.0.1\"\n");

        await LadderToNormalCopyService.CopyAsync(
            _tempDir,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            keepExistingConfigs: false);

        var copiedD2rds = await File.ReadAllTextAsync(
            Path.Combine(_normalModRoot, "d2rloader", "config", "d2rds.toml"));
        Assert.DoesNotContain("secret-api-tok", copiedD2rds);
        Assert.DoesNotContain("secret-lobby-key", copiedD2rds);
        Assert.Contains("api_token = \"\"", copiedD2rds);
        Assert.Contains("lobby_server_key = \"\"", copiedD2rds);
        Assert.Contains("host = \"127.0.0.1\"", copiedD2rds);
    }

    [Fact]
    public async Task CopyAsync_KeepExistingConfigsDeduplicatesAliasAndCaseVariantStemsAndScrubsMixedCaseToml()
    {
        SeedMinimalLadderMod();
        WriteLadderPlugin("Controller QOL Updates.dll", "qol");
        WriteFile(Path.Combine(_ladderModRoot, "d2rloader", "config", "controller-qol.toml"), "mode = \"ladder\"\n");
        WriteFile(
            Path.Combine(_ladderModRoot, "d2rloader", "config", "Global-Chat.TOML"),
            "access_token = \"tok_case_999\"\nport = 1\n");

        WriteFile(Path.Combine(_normalModRoot, "d2rloader", "config", "d2rl-controller-qol.toml"), "mode = \"normal\"\n");
        WriteFile(Path.Combine(_normalModRoot, "d2rloader", "config", "filter.json"), "{\"custom\":true}\n");

        await LadderToNormalCopyService.CopyAsync(
            _tempDir,
            new HashSet<string>([LadderToNormalCopyService.ExtensionKey(D2RLoaderExtensionKind.Plugin, "Controller QOL Updates.dll")], StringComparer.OrdinalIgnoreCase),
            keepExistingConfigs: true);

        Assert.False(File.Exists(Path.Combine(_normalModRoot, "d2rloader", "config", "controller-qol.toml")));
        Assert.True(File.Exists(Path.Combine(_normalModRoot, "d2rloader", "config", "d2rl-controller-qol.toml")));
        Assert.Equal("{\"custom\":true}\n", await File.ReadAllTextAsync(Path.Combine(_normalModRoot, "d2rloader", "config", "filter.json")));

        var profile = new InstallationProfile { InstallDirectory = _tempDir, LaunchExperience = LaunchExperience.Online };
        var inventory = D2RLoaderService.Discover(_tempDir, LaunchExperience.Online, includeManualDisabled: true);
        var qolEntry = Assert.Single(inventory.Extensions, ext => ext.FileName == "Controller QOL Updates.dll");
        Assert.True(D2RLoaderConfigService.HasExistingConfig(profile, qolEntry));

        var scrubbedChat = await File.ReadAllTextAsync(
            Path.Combine(_normalModRoot, "d2rloader", "config", "Global-Chat.TOML"));
        Assert.DoesNotContain("tok_case_999", scrubbedChat);
        Assert.Contains("port = 1", scrubbedChat);
    }

    private void SeedMinimalLadderMod(string savePath = "ReimaginedLadder/")
    {
        WriteFile(
            Path.Combine(_ladderModRoot, "modinfo.json"),
            $$"""{"name":"ReimaginedLadder","savepath":"{{savePath}}"}""");
        WriteFile(
            Path.Combine(_ladderModRoot, "ReimaginedLadder.mpq", "modinfo.json"),
            $$"""{"name":"ReimaginedLadder","savepath":"{{savePath}}"}""");
        WriteFile(
            Path.Combine(_ladderModRoot, "ReimaginedLadder.mpq", "data", "global", "excel", "misc.txt"),
            "name\n");
    }

    private void WriteLadderPlugin(string fileName, string content) =>
        WriteFile(Path.Combine(_ladderModRoot, "d2rloader", "plugins", fileName), content);

    private void WriteLadderDisabledPlugin(string fileName, string content) =>
        WriteFile(
            Path.Combine(_ladderModRoot, "d2rloader", "ladder-disabled", "plugins", fileName),
            content);

    private void WriteLadderPatch(string fileName, string content) =>
        WriteFile(Path.Combine(_ladderModRoot, "d2rloader", "patches", fileName), content);

    private void WriteLadderDisabledPatch(string fileName, string content) =>
        WriteFile(
            Path.Combine(_ladderModRoot, "d2rloader", "ladder-disabled", "patches", fileName),
            content);

    private static string? ReadSavePath(string modInfoPath)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(modInfoPath));
        return document.RootElement.TryGetProperty("savepath", out var value)
            ? value.GetString()
            : null;
    }

    private static void WriteFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
