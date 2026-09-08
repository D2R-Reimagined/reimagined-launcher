using ReimaginedLauncher.Utilities;
using Xunit;

namespace ReimaginedLauncher.Tests;

public sealed class IsolatedModLaunchTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "reimagined-isolated-launch-tests", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(LaunchExperience.Ladder, "ReimaginedLadder")]
    [InlineData(LaunchExperience.D2RLoader, "Reimagined")]
    [InlineData(LaunchExperience.Offline, "Reimagined")]
    public void DefaultModIsWrittenInTheLoaderTableAndPreservesOtherSettings(LaunchExperience experience, string mod)
    {
        const string original = "[other]\r\ndefault_mod = \"Unrelated\"\r\n[d2rloader] # settings\r\ndefault_mod = \"Old\"\r\nlaunch_arguments = \"-w\"\r\n[d2rloader.advanced]\r\nallow_global_extensions = false\r\n";
        var updated = D2RLoaderService.UpdateDefaultMod(original, experience);
        Assert.Equal(original.Replace("default_mod = \"Old\"", $"default_mod = \"{mod}\""), updated);
        Assert.Equal(updated, D2RLoaderService.UpdateDefaultMod(updated, experience));
    }

    [Theory]
    [InlineData("")]
    [InlineData("[d2rcore.items]\ndisplay_item_levels = true")]
    [InlineData("[d2rloader]\n# default_mod = \"Example\"\n[d2rloader.advanced]\nallow_mod_extensions = true\n")]
    public void MissingDefaultModIsAddedToTheCorrectTable(string original)
    {
        var updated = D2RLoaderService.UpdateDefaultMod(original, LaunchExperience.Ladder);
        var loader = updated.IndexOf("[d2rloader]", StringComparison.Ordinal);
        var setting = updated.IndexOf("\ndefault_mod = \"ReimaginedLadder\"", StringComparison.Ordinal);
        var next = updated.IndexOf("[d2rloader.advanced]", StringComparison.Ordinal);
        Assert.True(loader >= 0 && setting > loader);
        Assert.True(next < 0 || setting < next);
        Assert.Equal(updated, D2RLoaderService.UpdateDefaultMod(updated, LaunchExperience.Ladder));
    }

    [Fact]
    public void SwitchingModesPersistsDefaultWithoutTouchingLockedModFiles()
    {
        var normal = Path.Combine(_root, "mods", "Reimagined", "modinfo.json");
        var ladder = Path.Combine(_root, "mods", "ReimaginedLadder", "modinfo.json");
        Directory.CreateDirectory(Path.GetDirectoryName(normal)!);
        Directory.CreateDirectory(Path.GetDirectoryName(ladder)!);
        File.WriteAllText(normal, "{\"savepath\":\"Normal/\"}");
        File.WriteAllText(ladder, "{\"savepath\":\"Normal-Season-aaaaaaaa/\"}");
        using var normalLock = File.Open(normal, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var ladderLock = File.Open(ladder, FileMode.Open, FileAccess.Read, FileShare.Read);
        foreach (var experience in new[] { LaunchExperience.Ladder, LaunchExperience.D2RLoader, LaunchExperience.Ladder, LaunchExperience.Offline })
        {
            NormalModInstallationService.Restore(_root);
            D2RLoaderService.SetDefaultMod(_root, experience);
            var config = File.ReadAllText(Path.Combine(_root, "d2rloader", "config", "d2rloader.toml"));
            Assert.Contains($"default_mod = \"{ModInstallationPaths.ModName(experience)}\"", config);
        }
        Assert.False(Directory.Exists(Path.Combine(_root, ".reimagined-launcher", "mod-backups")));
        Assert.Contains("Normal/", File.ReadAllText(normal));
        Assert.Contains("Normal-Season-aaaaaaaa/", File.ReadAllText(ladder));
    }

    [Fact]
    public async Task LadderPolicyLeavesNormalExtensionsAlone()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllBytes(Path.Combine(_root, "D2RLoader.exe"), [0]);
        var normal = Path.Combine(_root, "mods", "Reimagined", "d2rloader", "plugins", "normal.dll");
        var ladder = Path.Combine(_root, "mods", "ReimaginedLadder", "d2rloader", "plugins", "unapproved.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(normal)!);
        Directory.CreateDirectory(Path.GetDirectoryName(ladder)!);
        File.WriteAllBytes(normal, [1]);
        File.WriteAllBytes(ladder, [2]);
        var result = await D2RLoaderService.ApplyLadderPolicyAsync(_root, [], new HashSet<Guid>());
        Assert.Single(result.UnapprovedMoved);
        Assert.True(File.Exists(normal));
        Assert.False(File.Exists(ladder));
        Assert.Equal(normal, Assert.Single(D2RLoaderService.Discover(_root).Extensions).FilePath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
