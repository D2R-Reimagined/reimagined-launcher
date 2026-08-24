using System.Text.Json;
using System.Security.Cryptography;
using ReimaginedLauncher.Utilities;
using Xunit;

namespace ReimaginedLauncher.Tests;

public sealed class D2RLoaderServiceTests : IDisposable
{
    private readonly string _installDirectory = Path.Combine(
        Path.GetTempPath(),
        $"reimagined-launcher-tests-{Guid.NewGuid():N}");

    [Fact]
    public void DiscoverReturnsMissingStateWhenLoaderIsAbsent()
    {
        Directory.CreateDirectory(_installDirectory);

        var inventory = D2RLoaderService.Discover(_installDirectory);

        Assert.False(inventory.IsInstalled);
        Assert.Empty(inventory.Extensions);
        Assert.Equal(Path.Combine(_installDirectory, "D2RLoader.exe"), inventory.LoaderPath);
    }

    [Fact]
    public void DiscoverSeparatesGlobalAndModExtensionsAndReadsPolicies()
    {
        File.WriteAllBytes(CreatePath("D2RLoader.exe"), [0]);
        File.WriteAllText(
            CreatePath("d2rloader", "config", "d2rloader.toml"),
            "allow_global_extensions = false\nallow_mod_extensions = true\n");
        File.WriteAllBytes(CreatePath("d2rloader", "plugins", "d2rl-author-fast-stash.dll"), [0]);
        File.WriteAllText(
            CreatePath("mods", "Reimagined", "d2rloader", "patches", "level-cap.json"),
            JsonSerializer.Serialize(new
            {
                version = 2,
                name = "Level Cap",
                description = "Raises the level cap.",
                patches = new[] { new { op = "bytes" }, new { op = "bytes" } }
            }));

        var inventory = D2RLoaderService.Discover(_installDirectory);

        Assert.True(inventory.IsInstalled);
        Assert.False(inventory.AllowGlobalExtensions);
        Assert.True(inventory.AllowModExtensions);
        var plugin = Assert.Single(inventory.Plugins);
        Assert.Equal(D2RLoaderExtensionScope.Global, plugin.Scope);
        Assert.Equal("Fast Stash", plugin.Name);
        var patch = Assert.Single(inventory.Patches);
        Assert.Equal(D2RLoaderExtensionScope.Reimagined, patch.Scope);
        Assert.Equal("Level Cap", patch.Name);
        Assert.Equal(2, patch.PatchCount);
    }

    [Fact]
    public void DiscoverReportsInvalidPatchWithoutExecutingOrFailingInventory()
    {
        File.WriteAllBytes(CreatePath("D2RLoader.exe"), [0]);
        File.WriteAllText(CreatePath("d2rloader", "patches", "broken.json"), "not-json");

        var patch = Assert.Single(D2RLoaderService.Discover(_installDirectory).Patches);

        Assert.NotNull(patch.Error);
        Assert.Contains("Could not read manifest", patch.Error);
    }

    [Fact]
    public void OnlineLaunchParametersOmitOfflineOnlyOptions()
    {
        var profile = new InstallationProfile
        {
            LaunchExperience = LaunchExperience.Online,
            EnableRespec = true,
            ResetOfflineMaps = true,
            PlayersCount = 8,
            CustomMapSeedEnabled = true,
            CustomMapSeed = 123,
            NoSound = true
        };

        var parameters = GameLauncherService.BuildLaunchParameters(profile);

        Assert.Equal("-mod Reimagined -txt -nosound", parameters);
    }

    [Fact]
    public void OfflineLaunchParametersRetainOfflineOptions()
    {
        var profile = new InstallationProfile
        {
            LaunchExperience = LaunchExperience.Offline,
            EnableRespec = true,
            ResetOfflineMaps = true,
            PlayersCount = 3,
            CustomMapSeedEnabled = true,
            CustomMapSeed = 456
        };

        var parameters = GameLauncherService.BuildLaunchParameters(profile);

        Assert.Contains("-enablerespec", parameters);
        Assert.Contains("-resetofflinemaps", parameters);
        Assert.Contains("-players 3", parameters);
        Assert.Contains("-seed 456", parameters);
    }

    [Fact]
    public void LadderLaunchParametersOmitOfflineOnlyOptions()
    {
        var profile = new InstallationProfile
        {
            LaunchExperience = LaunchExperience.Ladder,
            EnableRespec = true,
            ResetOfflineMaps = true,
            PlayersCount = 8,
            CustomMapSeedEnabled = true,
            CustomMapSeed = 123,
            NoSound = true
        };

        var parameters = GameLauncherService.BuildLaunchParameters(profile);

        Assert.Equal("-mod Reimagined -txt -nosound", parameters);
    }

    [Fact]
    public async Task LadderPolicyKeepsSelectedApprovedExtensionsAndRestoresMovedFiles()
    {
        File.WriteAllBytes(CreatePath("D2RLoader.exe"), [0]);
        var approvedPluginPath = CreatePath("d2rloader", "plugins", "approved.dll");
        var unapprovedPluginPath = CreatePath("d2rloader", "plugins", "unapproved.dll");
        var approvedPatchPath = CreatePath("mods", "Reimagined", "d2rloader", "patches", "approved.json");
        File.WriteAllBytes(approvedPluginPath, [1, 2, 3]);
        File.WriteAllBytes(unapprovedPluginPath, [4, 5, 6]);
        File.WriteAllText(approvedPatchPath, "{\"name\":\"Approved Patch\",\"patches\":[]}");

        var pluginApprovalId = Guid.NewGuid();
        var patchApprovalId = Guid.NewGuid();
        var approvals = new[]
        {
            new LadderExtensionApproval(
                pluginApprovalId,
                "Approved Plugin",
                "approved.dll",
                ComputeSha256(approvedPluginPath),
                D2RLoaderExtensionKind.Plugin),
            new LadderExtensionApproval(
                patchApprovalId,
                "Approved Patch",
                "approved.json",
                ComputeSha256(approvedPatchPath),
                D2RLoaderExtensionKind.Patch),
            new LadderExtensionApproval(
                Guid.NewGuid(),
                "Wrong Hash Plugin",
                "unapproved.dll",
                new string('0', 64),
                D2RLoaderExtensionKind.Plugin)
        };

        var result = await D2RLoaderService.ApplyLadderPolicyAsync(
            _installDirectory,
            approvals,
            new HashSet<Guid> { pluginApprovalId });

        Assert.True(File.Exists(approvedPluginPath));
        Assert.False(File.Exists(unapprovedPluginPath));
        Assert.False(File.Exists(approvedPatchPath));
        Assert.True(File.Exists(CreatePath("d2rloader", "ladder-disabled", "plugins", "unapproved.dll")));
        Assert.True(File.Exists(CreatePath(
            "mods", "Reimagined", "d2rloader", "ladder-disabled", "patches", "approved.json")));
        Assert.Single(result.UnapprovedMoved);
        Assert.Single(result.UnselectedMoved);

        var preview = await D2RLoaderService.PreviewLadderPolicyAsync(_installDirectory, approvals);
        Assert.Contains(preview.ApprovedExtensions, state =>
            state.Approval.Id == patchApprovalId && state.IsInstalled && state.IsLadderDisabled);
        Assert.Contains(preview.UnapprovedExtensions, extension => extension.FileName == "unapproved.dll");

        var restored = D2RLoaderService.RestoreLadderDisabledExtensions(_installDirectory);

        Assert.Equal(2, restored);
        Assert.True(File.Exists(unapprovedPluginPath));
        Assert.True(File.Exists(approvedPatchPath));
    }

    private static string ComputeSha256(string path)
    {
        return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    }

    private string CreatePath(params string[] parts)
    {
        var path = parts.Aggregate(_installDirectory, Path.Combine);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_installDirectory))
        {
            Directory.Delete(_installDirectory, recursive: true);
        }
    }
}
