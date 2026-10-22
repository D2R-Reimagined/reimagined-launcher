using System.IO.Compression;
using ReimaginedLauncher.Utilities;
using Xunit;

namespace ReimaginedLauncher.Tests;

public sealed class LocalPluginInstallTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "launcher-plugin-tests-" + Guid.NewGuid().ToString("N"));
    private readonly InstallationProfile profile;
    public LocalPluginInstallTests()
    {
        Directory.CreateDirectory(Path.Combine(directory, "mods", "Reimagined"));
        File.WriteAllText(Path.Combine(directory, "D2R.exe"), "test fixture");
        profile = new InstallationProfile { InstallDirectory = directory, LaunchExperience = LaunchExperience.Online };
    }
    private string Zip(params (string Name, string Content)[] entries)
    {
        var path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var entry in entries) { using var writer = new StreamWriter(zip.CreateEntry(entry.Name).Open()); writer.Write(entry.Content); }
        return path;
    }
    [Fact]
    public void InstallsPackageAndPreservesConfigByDefault()
    {
        var root = LocalPluginInstallService.ResolveRoot(profile);
        Directory.CreateDirectory(Path.Combine(root, "plugins")); Directory.CreateDirectory(Path.Combine(root, "config"));
        File.WriteAllText(Path.Combine(root, "plugins", "example.dll"), "old DLL");
        File.WriteAllText(Path.Combine(root, "config", "example.toml"), "custom settings");
        var plan = LocalPluginInstallService.Preview(Zip(("release/example.dll", "new DLL"), ("release/example.mpq", "archive"),
            ("release/config/example.toml", "defaults"), ("release/config/presets/a.json", "{}"), ("README.md", "docs")), profile);
        var backup = LocalPluginInstallService.Install(plan, false, () => { });
        Assert.Equal("new DLL", File.ReadAllText(Path.Combine(root, "plugins", "example.dll")));
        Assert.Equal("custom settings", File.ReadAllText(Path.Combine(root, "config", "example.toml")));
        Assert.Equal("old DLL", File.ReadAllText(Path.Combine(backup, "plugins", "example.dll")));
        Assert.Equal("{}", File.ReadAllText(Path.Combine(root, "config", "presets", "a.json")));
        Assert.Single(plan.Skipped);
    }
    [Fact]
    public void ExplicitConfigReplacementIsBackedUp()
    {
        var root = LocalPluginInstallService.ResolveRoot(profile);
        Directory.CreateDirectory(Path.Combine(root, "config"));
        File.WriteAllText(Path.Combine(root, "config", "example.toml"), "custom");
        var plan = LocalPluginInstallService.Preview(Zip(("example.dll", "DLL"), ("config/example.toml", "new")), profile);
        var backup = LocalPluginInstallService.Install(plan, true, () => { });
        Assert.Equal("custom", File.ReadAllText(Path.Combine(backup, "config", "example.toml")));
        Assert.Equal("new", File.ReadAllText(Path.Combine(root, "config", "example.toml")));
    }
    [Theory]
    [InlineData("../escape.dll")]
    [InlineData("/absolute.dll")]
    [InlineData("config/../../escape.json")]
    [InlineData("C:/escape.dll")]
    [InlineData("config/CON.toml")]
    [InlineData("folder./a.dll")]
    public void RejectsUnsafeArchivePaths(string path) => Assert.Throws<InvalidDataException>(() => LocalPluginInstallService.Preview(Zip((path, "bad")), profile));
    [Fact]
    public void RejectsAmbiguousJsonAndDestinationCollisions()
    {
        Assert.Throws<InvalidDataException>(() => LocalPluginInstallService.Preview(Zip(("example.dll", "x"), ("settings.json", "{}")), profile));
        Assert.Throws<InvalidDataException>(() => LocalPluginInstallService.Preview(Zip(("one/example.dll", "x"), ("two/EXAMPLE.dll", "y")), profile));
    }
    [Fact]
    public void RejectsLadderBeforeResolvingOrWritingDestination()
    {
        profile.LaunchExperience = LaunchExperience.Ladder;
        Assert.Throws<InvalidOperationException>(() => LocalPluginInstallService.Preview("missing.zip", profile));
        Assert.False(Directory.Exists(Path.Combine(directory, "mods", "ReimaginedLadder")));
    }
    [Fact]
    public void ChangedDestinationStopsBeforeAnyReplacement()
    {
        var root = LocalPluginInstallService.ResolveRoot(profile);
        Directory.CreateDirectory(Path.Combine(root, "plugins"));
        var dll = Path.Combine(root, "plugins", "example.dll"); File.WriteAllText(dll, "old");
        var plan = LocalPluginInstallService.Preview(Zip(("example.dll", "new")), profile);
        File.WriteAllText(dll, "changed after preview");
        Assert.Throws<IOException>(() => LocalPluginInstallService.Install(plan, true, () => { }));
        Assert.Equal("changed after preview", File.ReadAllText(dll));
    }
    [Fact]
    public void FailureAfterFirstReplacementRollsBackEveryCommittedFile()
    {
        var root = LocalPluginInstallService.ResolveRoot(profile);
        Directory.CreateDirectory(Path.Combine(root, "plugins"));
        var first = Path.Combine(root, "plugins", "one.dll"); File.WriteAllText(first, "original");
        var files = new[] { new LocalInstallFile("plugins/one.dll", "replacement"u8.ToArray(), LocalPluginInstallService.Hash(first)),
            new LocalInstallFile("plugins/two.dll", "new"u8.ToArray(), null) };
        int calls = 0;
        Assert.Throws<IOException>(() => LocalPluginInstallService.Commit(root, files, () => { if (++calls == 4) throw new IOException("Simulated mode or process change"); }));
        Assert.Equal("original", File.ReadAllText(first));
        Assert.False(File.Exists(Path.Combine(root, "plugins", "two.dll")));
        Assert.Empty(Directory.GetFiles(root, "*.tmp", SearchOption.AllDirectories));
    }
    [Fact]
    public void SwitchingToLadderAfterPreviewStopsBeforeWriting()
    {
        var plan = LocalPluginInstallService.Preview(Zip(("example.dll", "new")), profile);
        profile.LaunchExperience = LaunchExperience.Ladder;
        Assert.Throws<InvalidOperationException>(() => LocalPluginInstallService.Install(plan, false,
            () => LocalPluginInstallService.RefuseLadder(profile.LaunchExperience)));
        Assert.False(File.Exists(Path.Combine(plan.Root, "plugins", "example.dll")));
    }
    [Fact]
    public void BackupsAreOutsideBothModPackages()
    {
        var normal = LocalPluginInstallService.ResolveRoot(profile);
        Assert.DoesNotContain(Path.Combine("mods", "Reimagined"), LocalPluginInstallService.BackupDirectory(normal, ".launcher-filter-backups"));
        var ladder = Path.Combine(directory, "mods", "ReimaginedLadder", "d2rloader");
        Assert.StartsWith(Path.Combine(directory, ".reimagined-launcher"), LocalPluginInstallService.BackupDirectory(ladder, ".launcher-filter-backups"));
        Assert.NotEqual(LocalPluginInstallService.BackupDirectory(normal, ".launcher-filter-backups"),
            LocalPluginInstallService.BackupDirectory(ladder, ".launcher-filter-backups"));
    }
    [Fact]
    public void StandaloneDllInstallsToModNotGlobalFolder()
    {
        var source = Path.Combine(directory, "test.dll"); File.WriteAllBytes(source, [0, 1, 2, 255]);
        var plan = LocalPluginInstallService.Preview(source, profile);
        LocalPluginInstallService.Install(plan, false, () => { });
        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(Path.Combine(plan.Root, "plugins", "test.dll")));
        Assert.False(Directory.Exists(Path.Combine(directory, "d2rloader")));
    }
    public void Dispose() => Directory.Delete(directory, true);
}
