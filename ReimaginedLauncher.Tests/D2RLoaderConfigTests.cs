using ReimaginedLauncher.HttpClients;
using ReimaginedLauncher.HttpClients.Models;
using ReimaginedLauncher.Utilities;
using Xunit;

namespace ReimaginedLauncher.Tests;

public sealed class D2RLoaderConfigTests : IDisposable
{
    private readonly string game = Path.Combine(Path.GetTempPath(), "launcher-config-" + Guid.NewGuid().ToString("N"));
    private readonly InstallationProfile profile;
    public D2RLoaderConfigTests()
    {
        Write("D2R.exe"); Write("D2RLoader.exe");
        Directory.CreateDirectory(Path.Combine(game, "mods", "Reimagined"));
        profile = new() { InstallDirectory = game, LaunchExperience = LaunchExperience.Online };
    }
    private string Write(string relative, string content = "fixture")
    {
        var path = Path.Combine(game, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }
    private D2RLoaderExtensionInfo Entry(D2RLoaderExtensionScope scope = D2RLoaderExtensionScope.Reimagined)
        => D2RLoaderService.Discover(game, includeManualDisabled: true).Extensions.Single(entry => entry.Scope == scope);

    [Fact]
    public void MatchesFormattingAndPreservesScopeAndContents()
    {
        Write("mods/Reimagined/d2rloader/plugins/Controller QOL Updates.dll");
        var expected = Write("mods/Reimagined/d2rloader/config/controller-qol-updates.toml", "custom = true");
        Write("d2rloader/config/controller-qol-updates.toml", "global = true");
        Assert.Equal(expected, D2RLoaderConfigService.Resolve(profile, Entry()).File);
        Assert.Equal("custom = true", File.ReadAllText(expected));
    }
    [Fact]
    public void DisabledGlobalEntryFindsGlobalConfig()
    {
        Write("d2rloader/launcher-disabled/plugins/d2rl-example.dll");
        var expected = Write("d2rloader/config/example.toml");
        Assert.Equal(expected, D2RLoaderConfigService.Resolve(profile, Entry(D2RLoaderExtensionScope.Global)).File);
    }
    [Fact]
    public void AmbiguousOrUnmatchedNamesRequireChoosingAnExistingScopedFile()
    {
        Write("mods/Reimagined/d2rloader/plugins/example.dll");
        var toml = Write("mods/Reimagined/d2rloader/config/example.toml");
        Write("mods/Reimagined/d2rloader/config/example.json");
        var target = D2RLoaderConfigService.Resolve(profile, Entry());
        Assert.Null(target.File);
        Assert.Equal(toml, D2RLoaderConfigService.ValidateSelection(profile, target, toml));
        File.Delete(toml);
        Assert.Throws<FileNotFoundException>(() => D2RLoaderConfigService.ValidateSelection(profile, target, toml));
    }
    [Fact]
    public void PatchOpensItsActualJsonIncludingWhenDisabled()
    {
        var path = Write("mods/Reimagined/d2rloader/launcher-disabled/patches/example.json", "{\"name\":\"Example\",\"patches\":[]}");
        Assert.Equal(path, D2RLoaderConfigService.Resolve(profile, Entry()).File);
    }
    [Fact]
    public void LadderRefusesGlobalScopeResolutionAndPostPickerSelection()
    {
        Write("d2rloader/plugins/example.dll");
        var config = Write("d2rloader/config/example.toml");
        var entry = Entry(D2RLoaderExtensionScope.Global);
        var target = D2RLoaderConfigService.Resolve(profile, entry);
        profile.LaunchExperience = LaunchExperience.Ladder;
        Assert.Throws<InvalidOperationException>(() => D2RLoaderConfigService.Resolve(profile, entry));
        Assert.Throws<InvalidOperationException>(() => D2RLoaderConfigService.ValidateSelection(profile, target, config));
    }
    [Fact]
    public void LadderAllowsEditingUnlockedModPluginConfigsAndLocksRuleConfigs()
    {
        profile.LaunchExperience = LaunchExperience.Ladder;
        Write("mods/ReimaginedLadder/d2rloader/plugins/Controller QOL Updates.dll");
        var qolConfig = Write("mods/ReimaginedLadder/d2rloader/config/controller-qol.toml", "auto_verify = true");
        var qolEntry = D2RLoaderConfigService.CreateLadderExtensionInfo(
            profile, "Controller QOL Updates", "Controller QOL Updates.dll", D2RLoaderExtensionKind.Plugin, isLadderDisabled: false);
        Assert.False(D2RLoaderConfigService.IsLockedLadderExtension("Controller QOL Updates.dll", D2RLoaderExtensionKind.Plugin));
        Assert.True(D2RLoaderConfigService.HasExistingConfig(profile, qolEntry));
        var resolved = D2RLoaderConfigService.Resolve(profile, qolEntry);
        Assert.Equal(qolConfig, resolved.File);
        Assert.Equal(qolConfig, D2RLoaderConfigService.ValidateSelection(profile, resolved, qolConfig));

        // Optional Ladder plugin in ladder-disabled can still have its config edited before being enabled.
        Write("mods/ReimaginedLadder/d2rloader/ladder-disabled/plugins/d2rl-kill-tracker.dll");
        var killTrackerConfig = Write("mods/ReimaginedLadder/d2rloader/config/kill-tracker.toml", "enabled = true");
        var killTrackerEntry = D2RLoaderConfigService.CreateLadderExtensionInfo(
            profile, "Kill Tracker", "d2rl-kill-tracker.dll", D2RLoaderExtensionKind.Plugin, isLadderDisabled: true);
        Assert.True(D2RLoaderConfigService.HasExistingConfig(profile, killTrackerEntry));
        Assert.Equal(killTrackerConfig, D2RLoaderConfigService.Resolve(profile, killTrackerEntry).File);

        // Rule-enforced and server-managed Ladder plugins are locked.
        string[] lockedPlugins =
        [
            "d2rl-server-saves.dll",
            "d2rl-lobby-bridge.dll",
            "d2rl-chat-relay.dll",
            "d2rds.dll",
            "d2rl-maps.dll",
            "auto-waypoint.dll",
            "identify-on-click.dll",
            "bindandsummon.dll",
            "charm-inventory.dll",
            "celestialrayone.left-click-auras.dll",
            "d2rl-announcements.dll",
            "d2rl-area-level-display.dll",
            "d2rl-corruption-marker.dll",
            "d2rl-death-tracker.dll",
            "d2rl-global-chat.dll",
            "d2rl-hardcore-deaths.dll",
            "d2rl-monster-ai.dll",
            "d2rl-reimagined-feedback.dll",
            "d2rl-reimagined-online-count.dll",
            "d2rl-supporter-mtx.dll",
            "d2rl-supporter-portals.dll",
            "d2rl-trade-notifications.dll",
            "d2rl-trade-price-check.dll"
        ];
        foreach (var fileName in lockedPlugins)
        {
            Assert.True(D2RLoaderConfigService.IsLockedLadderExtension(fileName, D2RLoaderExtensionKind.Plugin));
            Write($"mods/ReimaginedLadder/d2rloader/plugins/{fileName}");
            var lockedEntry = D2RLoaderConfigService.CreateLadderExtensionInfo(
                profile, fileName, fileName, D2RLoaderExtensionKind.Plugin, isLadderDisabled: false);
            Assert.False(D2RLoaderConfigService.HasExistingConfig(profile, lockedEntry));
            Assert.Throws<InvalidOperationException>(() => D2RLoaderConfigService.Resolve(profile, lockedEntry));
        }

        // Patches and signed bundle config files (except filter.json) are locked on Ladder.
        Assert.True(D2RLoaderConfigService.IsLockedLadderExtension("level-cap-105.json", D2RLoaderExtensionKind.Patch));
        var lockedMapsConfig = Write("mods/ReimaginedLadder/d2rloader/config/maps.toml");
        Assert.Throws<InvalidOperationException>(() => D2RLoaderConfigService.ValidateSelection(profile, resolved, lockedMapsConfig));

        var bundle = new LadderBundleResponse(
            Guid.NewGuid(),
            Guid.NewGuid(),
            1,
            1,
            "published",
            new string('0', 64),
            new string('0', 64),
            "sig",
            "key",
            100,
            new LadderBundleCompatibility("0.19.0", "1.5.0", "", "", "2.1.0", "1.6"),
            [
                new LadderBundleManifestFile(
                    "mods/Reimagined/d2rloader/config/custom-rule.toml",
                    "mods/Reimagined/d2rloader/config/custom-rule.toml",
                    "custom-rule.toml",
                    10,
                    new string('a', 64)),
                new LadderBundleManifestFile(
                    "mods/Reimagined/d2rloader/config/filter.json",
                    "mods/Reimagined/d2rloader/config/filter.json",
                    "filter.json",
                    10,
                    new string('b', 64))
            ],
            [],
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            null,
            "/download");
        Assert.True(D2RLoaderConfigService.IsLockedLadderExtension("custom-rule.dll", D2RLoaderExtensionKind.Plugin, bundle));
        Assert.False(D2RLoaderConfigService.IsLockedLadderExtension("unhoarder.dll", D2RLoaderExtensionKind.Plugin, bundle));
    }
    [Fact]
    public void PickerCannotEscapeScopeOrOpenExecutable()
    {
        Write("mods/Reimagined/d2rloader/plugins/example.dll");
        Write("mods/Reimagined/d2rloader/config/other.toml");
        var target = D2RLoaderConfigService.Resolve(profile, Entry());
        var global = Write("d2rloader/config/other.toml");
        Assert.Throws<InvalidDataException>(() => D2RLoaderConfigService.ValidateSelection(profile, target, global));
        var exe = Write("mods/Reimagined/d2rloader/config/other.exe");
        Assert.Throws<InvalidDataException>(() => D2RLoaderConfigService.ValidateSelection(profile, target, exe));
    }
    [Fact]
    public void ForgedScopeAndMissingFolderAreRejectedWithoutCreatingFiles()
    {
        Write("mods/Reimagined/d2rloader/plugins/example.dll");
        var entry = Entry();
        Assert.Throws<DirectoryNotFoundException>(() => D2RLoaderConfigService.Resolve(profile, entry));
        var forged = new D2RLoaderExtensionInfo { Name = entry.Name, FileName = entry.FileName,
            FilePath = entry.FilePath, Kind = entry.Kind, Scope = D2RLoaderExtensionScope.Global };
        Assert.Throws<InvalidDataException>(() => D2RLoaderConfigService.Resolve(profile, forged));
        Assert.False(Directory.Exists(Path.Combine(game, "mods", "Reimagined", "d2rloader", "config")));
    }
    [Fact]
    public void EditAvailabilityRequiresAnExistingUniqueConfig()
    {
        Write("mods/Reimagined/d2rloader/plugins/example.dll");
        var entry = Entry();
        Assert.False(D2RLoaderConfigService.HasExistingConfig(profile, entry));
        Write("mods/Reimagined/d2rloader/config/unrelated.toml");
        Assert.False(D2RLoaderConfigService.HasExistingConfig(profile, entry));
        var config = Write("mods/Reimagined/d2rloader/config/example.toml");
        Assert.True(D2RLoaderConfigService.HasExistingConfig(profile, entry));
        var duplicate = Write("mods/Reimagined/d2rloader/config/example.json");
        Assert.False(D2RLoaderConfigService.HasExistingConfig(profile, entry));
        File.Delete(duplicate);
        Assert.True(D2RLoaderConfigService.HasExistingConfig(profile, entry));
        File.Delete(config);
        Assert.False(D2RLoaderConfigService.HasExistingConfig(profile, entry));
        Write("mods/Reimagined/d2rloader/config/example.toml");
        profile.LaunchExperience = LaunchExperience.Offline;
        Assert.False(D2RLoaderConfigService.HasExistingConfig(profile, entry));
    }

    [Theory]
    [InlineData("d2rl-announcements.dll", "announcements.toml")]
    [InlineData("d2rl-area-level-display.dll", "area-level-display.toml")]
    [InlineData("d2rl-corruption-marker.dll", "corruption-marker.toml")]
    [InlineData("d2rl-death-tracker.dll", "death-tracker.toml")]
    [InlineData("d2rl-global-chat.dll", "global-chat.toml")]
    [InlineData("d2rl-hardcore-deaths.dll", "hardcore-deaths.toml")]
    [InlineData("d2rl-monster-ai.dll", "monster-ai.toml")]
    [InlineData("d2rl-reimagined-feedback.dll", "reimagined-feedback.toml")]
    [InlineData("d2rl-reimagined-online-count.dll", "reimagined-online-count.toml")]
    [InlineData("d2rl-supporter-mtx.dll", "supporter-mtx.toml")]
    [InlineData("d2rl-supporter-portals.dll", "supporter-portals.toml")]
    [InlineData("d2rl-trade-notifications.dll", "trade-notifications.toml")]
    [InlineData("d2rl-trade-price-check.dll", "trade-price-check.toml")]
    public void OnlineModeBlocksNonEditablePluginConfigs(string pluginFile, string configFile)
    {
        Write($"mods/Reimagined/d2rloader/plugins/{pluginFile}");
        var configPath = Write($"mods/Reimagined/d2rloader/config/{configFile}", "enabled = true");
        var entry = Entry();
        Assert.True(D2RLoaderConfigService.IsNonEditableExtension(pluginFile));
        Assert.False(D2RLoaderConfigService.HasExistingConfig(profile, entry));
        Assert.Throws<InvalidOperationException>(() => D2RLoaderConfigService.Resolve(profile, entry));
        var target = new ExtensionConfigTarget(Path.Combine(game, "mods", "Reimagined", "d2rloader", "config"), null);
        Assert.Throws<InvalidOperationException>(() => D2RLoaderConfigService.ValidateSelection(profile, target, configPath));
    }

    public void Dispose() { if (Directory.Exists(game)) Directory.Delete(game, true); }
}
