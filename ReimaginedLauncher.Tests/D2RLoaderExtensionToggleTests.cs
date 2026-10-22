using ReimaginedLauncher.Utilities;
using Xunit;

namespace ReimaginedLauncher.Tests;

public sealed class D2RLoaderExtensionToggleTests : IDisposable
{
    private readonly string game = Path.Combine(Path.GetTempPath(), "launcher-toggles-" + Guid.NewGuid().ToString("N"));
    private readonly InstallationProfile profile;
    public D2RLoaderExtensionToggleTests()
    {
        Directory.CreateDirectory(Path.Combine(game, "mods", "Reimagined"));
        File.WriteAllText(Path.Combine(game, "D2R.exe"), "fixture");
        File.WriteAllText(Path.Combine(game, "D2RLoader.exe"), "fixture");
        profile = new InstallationProfile { InstallDirectory = game, LaunchExperience = LaunchExperience.Online };
    }
    private string Write(string relative, string content)
    {
        var path = Path.Combine(game, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, content); return path;
    }
    private D2RLoaderInventory Managed() => D2RLoaderService.Discover(game, profile.LaunchExperience, includeManualDisabled: true);
    private void Toggle(D2RLoaderExtensionInfo item, bool enabled) => D2RLoaderExtensionToggleService.SetEnabled(profile, item, enabled, () => { });
    [Fact]
    public void RuntimeToggleLockPreservesConfigEditingAndConflictGuard()
    {
        Write("mods/Reimagined/d2rloader/plugins/example.dll", "fixture");
        var item = Managed().Plugins.Single();
        item.CanEditConfig = true;
        item.ToggleControlsEnabled = false;
        Assert.False(item.CanToggle);
        Assert.True(item.CanEditConfig);
        item.ToggleControlsEnabled = true;
        Assert.True(item.CanToggle);
        item.HasToggleConflict = true;
        item.ToggleControlsEnabled = false;
        item.ToggleControlsEnabled = true;
        Assert.False(item.CanToggle);
    }

    [Fact]
    public void ModCopyCanBeDisabledAndReenabledWithoutChangingGlobalCopyConfigsOrMpq()
    {
        Write("d2rloader/plugins/example.dll", "global binary");
        var dll = Write("mods/Reimagined/d2rloader/plugins/example.dll", "mod binary");
        var mpq = Write("mods/Reimagined/d2rloader/plugins/example.mpq", "companion");
        var config = Write("mods/Reimagined/d2rloader/config/example.toml", "custom settings");
        Toggle(Managed().Plugins.Single(item => item.Scope == D2RLoaderExtensionScope.Reimagined), false);
        Assert.False(File.Exists(dll));
        var management = Managed();
        var parked = management.Plugins.Single(item => !item.IsEnabled);
        Assert.Equal("example.dll", parked.FileName);
        Assert.Equal(D2RLoaderExtensionScope.Reimagined, parked.Scope);
        Assert.Equal("mod binary", File.ReadAllText(parked.FilePath));
        var loaderInventory = D2RLoaderService.Discover(game);
        Assert.Single(loaderInventory.Plugins); Assert.Equal(D2RLoaderExtensionScope.Global, loaderInventory.Plugins[0].Scope);
        Toggle(parked, true);
        Assert.Equal("mod binary", File.ReadAllText(dll));
        Assert.Equal("global binary", File.ReadAllText(Path.Combine(game, "d2rloader", "plugins", "example.dll")));
        Assert.Equal("companion", File.ReadAllText(mpq)); Assert.Equal("custom settings", File.ReadAllText(config));
        Assert.All(Managed().Plugins, item => Assert.True(item.IsEnabled));
    }
    [Fact]
    public void GlobalPatchRemainsListedWhenParkedAndKeepsItsMetadata()
    {
        var active = Write("d2rloader/patches/example.json", "{\"name\":\"Example patch\",\"patches\":[]}");
        Toggle(Assert.Single(Managed().Patches), false);
        Assert.False(File.Exists(active)); Assert.Empty(D2RLoaderService.Discover(game).Patches);
        var disabled = Assert.Single(Managed().Patches);
        Assert.False(disabled.IsEnabled); Assert.Equal("Example patch", disabled.Name);
        Assert.Equal(D2RLoaderExtensionScope.Global, disabled.Scope);
        Toggle(disabled, true); Assert.True(File.Exists(active));
    }
    [Fact]
    public void CollisionNeverOverwritesEitherCopy()
    {
        var active = Write("d2rloader/plugins/example.dll", "active");
        var disabled = Write("d2rloader/launcher-disabled/plugins/example.dll", "disabled");
        var copies = Managed().Plugins;
        Assert.Equal(2, copies.Count); Assert.All(copies, item => Assert.False(item.CanToggle));
        Assert.Throws<IOException>(() => Toggle(copies.Single(item => item.IsEnabled), false));
        Assert.Equal("active", File.ReadAllText(active)); Assert.Equal("disabled", File.ReadAllText(disabled));
    }
    [Fact]
    public void AllManualTogglesAreRefusedOnLadder()
    {
        var active = Write("d2rloader/plugins/example.dll", "global");
        var item = Assert.Single(Managed().Plugins); profile.LaunchExperience = LaunchExperience.Ladder;
        Assert.Throws<InvalidOperationException>(() => Toggle(item, false));
        Assert.Equal("global", File.ReadAllText(active));
        Assert.False(Directory.Exists(Path.Combine(game, "d2rloader", "launcher-disabled")));
    }
    [Fact]
    public void RunningGameGuardAndStaleScopeBothLeaveFileUntouched()
    {
        var active = Write("d2rloader/plugins/example.dll", "global");
        var item = Assert.Single(Managed().Plugins);
        Assert.Throws<InvalidOperationException>(() => D2RLoaderExtensionToggleService.SetEnabled(profile, item, false,
            () => throw new InvalidOperationException("Game is running")));
        Assert.True(File.Exists(active));
        var forged = new D2RLoaderExtensionInfo { Name = item.Name, FileName = item.FileName, FilePath = active,
            Kind = item.Kind, Scope = D2RLoaderExtensionScope.Reimagined };
        Assert.Throws<InvalidDataException>(() => Toggle(forged, false)); Assert.True(File.Exists(active));
    }
    [Fact]
    public void ReinstallingADisabledPluginKeepsItDisabledAndBacksUpItsPreviousBytes()
    {
        Write("mods/Reimagined/d2rloader/plugins/example.dll", "old version");
        Toggle(Assert.Single(Managed().Plugins), false);
        var source = Write("example.dll", "new version");
        var plan = LocalPluginInstallService.Preview(source, profile);
        Assert.StartsWith("launcher-disabled/plugins/", Assert.Single(plan.Files).RelativePath);
        var backup = LocalPluginInstallService.Install(plan, false, () => { });
        var item = Assert.Single(Managed().Plugins);
        Assert.False(item.IsEnabled); Assert.Equal("new version", File.ReadAllText(item.FilePath));
        Assert.Equal("old version", File.ReadAllText(Path.Combine(backup, "launcher-disabled", "plugins", "example.dll")));
        Assert.Empty(D2RLoaderService.Discover(game).Plugins);
    }
    [Fact]
    public void UppercaseExtensionsAreManagedOnEveryPlatform()
    {
        Write("d2rloader/plugins/EXAMPLE.DLL", "plugin");
        Toggle(Assert.Single(Managed().Plugins), false);
        Assert.False(Assert.Single(Managed().Plugins).IsEnabled);
        Toggle(Assert.Single(Managed().Plugins), true);
        Assert.True(Assert.Single(Managed().Plugins).IsEnabled);
    }
    [Fact]
    public void ExistingEntryUpdatesInPlaceAndCanBeToggledBackWithoutRediscovery()
    {
        Write("mods/Reimagined/d2rloader/plugins/example.dll", "binary");
        Write("mods/Reimagined/d2rloader/plugins/other.dll", "other binary");
        Write("mods/Reimagined/d2rloader/config/example.toml", "custom");
        var inventory = Managed();
        var item = inventory.Plugins.Single(entry => entry.FileName == "example.dll");
        var other = inventory.Plugins.Single(entry => entry.FileName == "other.dll");
        var events = new List<string?>();
        var otherEvents = new List<string?>();
        item.PropertyChanged += (_, e) => events.Add(e.PropertyName);
        other.PropertyChanged += (_, e) => otherEvents.Add(e.PropertyName);
        var parked = D2RLoaderExtensionToggleService.SetEnabled(profile, item, false, () => { });
        item.ApplyToggleState(parked, false);
        item.CanEditConfig = D2RLoaderConfigService.HasExistingConfig(profile, item);
        Assert.Same(item, inventory.Plugins.Single(entry => entry.FileName == "example.dll"));
        Assert.False(item.IsEnabled); Assert.True(item.CanEditConfig);
        Assert.Contains("Disabled", item.InstallationDetail);
        Assert.Contains(nameof(item.IsEnabled), events);
        Assert.Contains(nameof(item.FilePath), events);
        Assert.Contains(nameof(item.InstallationDetail), events);
        Assert.Empty(otherEvents); Assert.True(other.IsEnabled);
        Assert.Single(inventory.Plugins, entry => entry.IsEnabled);
        var active = D2RLoaderExtensionToggleService.SetEnabled(profile, item, true, () => { });
        item.ApplyToggleState(active, true);
        Assert.True(item.IsEnabled); Assert.DoesNotContain("Disabled", item.InstallationDetail);
        Assert.Equal(2, inventory.Plugins.Count(entry => entry.IsEnabled));
        Assert.True(File.Exists(active)); Assert.False(File.Exists(parked));
    }

    public void Dispose() => Directory.Delete(game, true);
}
