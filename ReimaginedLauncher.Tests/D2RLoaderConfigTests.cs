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
    public void LadderRefusesBothResolutionAndPostPickerSelection()
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
    public void EditAvailabilityRequiresAnExistingUniqueConfigAndDisallowsLadder()
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
        profile.LaunchExperience = LaunchExperience.Ladder;
        Assert.False(D2RLoaderConfigService.HasExistingConfig(profile, entry));
    }

    public void Dispose() { if (Directory.Exists(game)) Directory.Delete(game, true); }
}
