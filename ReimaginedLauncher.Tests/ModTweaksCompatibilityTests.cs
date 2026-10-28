using ReimaginedLauncher.Utilities;
using Xunit;

namespace ReimaginedLauncher.Tests;

public sealed class ModTweaksCompatibilityTests
{
    [Fact]
    public async Task ExistingArmorAssetsAreTweakedAndRestoredFromCleanCopy()
    {
        var root = CreateTempDirectory();
        try
        {
            var armorDirectory = Path.Combine(root, "armor");
            var cleanDirectory = Path.Combine(armorDirectory, "armor_launcher_clean");
            var helmetFile = Path.Combine(armorDirectory, "helmet", "helm.json");
            const string originalContent = "{\"armor\":\"original\"}";
            Directory.CreateDirectory(Path.GetDirectoryName(helmetFile)!);
            await File.WriteAllTextAsync(helmetFile, originalContent);

            await ModTweaksService.EnsureCleanArmorTweaksCopyAsync(armorDirectory, cleanDirectory);
            Assert.True(File.Exists(Path.Combine(cleanDirectory, "helmet", "helm.json")));
            Assert.True(File.Exists(Path.Combine(cleanDirectory, "circlet", "circlet.json.missing")));

            await ModTweaksService.ApplyHelmetVisualTweaksAsync(armorDirectory, removeHelmetVisual: true);
            Assert.Equal(string.Empty, await File.ReadAllTextAsync(helmetFile));

            await ModTweaksService.RestoreArmorTweaksAsync(armorDirectory, cleanDirectory);
            Assert.Equal(originalContent, await File.ReadAllTextAsync(helmetFile));
            Assert.False(File.Exists(Path.Combine(armorDirectory, "circlet", "circlet.json")));
        }
        finally
        {
            DeleteTempDirectory(root);
        }
    }

    [Fact]
    public async Task MissingArmorWithDisabledTweakDoesNotCreateAssetsOrReportEnabledSkip()
    {
        var root = CreateTempDirectory();
        try
        {
            var armorDirectory = Path.Combine(root, "items", "armor");
            var report = new ModTweaksService.OptionalTweakReport();

            var prepared = await ModTweaksService.TryRunOptionalTweakStepAsync(
                "Remove Helmet Visual",
                tweakEnabled: false,
                () => ModTweaksService.EnsureCleanArmorTweaksCopyAsync(
                    armorDirectory,
                    Path.Combine(armorDirectory, "armor_launcher_clean")),
                report);
            await ModTweaksService.ApplyHelmetVisualTweaksAsync(armorDirectory, removeHelmetVisual: false);

            Assert.False(prepared);
            Assert.False(report.HasEnabledSkippedTweaks);
            Assert.False(Directory.Exists(armorDirectory));
        }
        finally
        {
            DeleteTempDirectory(root);
        }
    }

    [Fact]
    public async Task MissingArmorWithEnabledTweakIsSkippedAndReportedOnce()
    {
        var root = CreateTempDirectory();
        try
        {
            var armorDirectory = Path.Combine(root, "items", "armor");
            var report = new ModTweaksService.OptionalTweakReport();
            var ensure = () => ModTweaksService.EnsureCleanArmorTweaksCopyAsync(
                armorDirectory,
                Path.Combine(armorDirectory, "armor_launcher_clean"));

            Assert.False(await ModTweaksService.TryRunOptionalTweakStepAsync(
                "Remove Helmet Visual", true, ensure, report));
            Assert.False(await ModTweaksService.TryRunOptionalTweakStepAsync(
                "Remove Helmet Visual", true, ensure, report));
            Assert.False(await ModTweaksService.TryRunOptionalTweakStepAsync(
                "Remove Helmet Visual",
                tweakEnabled: true,
                () => ModTweaksService.ApplyHelmetVisualTweaksAsync(armorDirectory, removeHelmetVisual: true),
                report));

            Assert.True(report.HasEnabledSkippedTweaks);
            Assert.Single(report.EnabledSkippedTweaks);
            Assert.False(Directory.Exists(armorDirectory));
        }
        finally
        {
            DeleteTempDirectory(root);
        }
    }

    [Fact]
    public async Task MissingLegacyTweakAssetsAreSkippedWithoutCreatingFolders()
    {
        var root = CreateTempDirectory();
        try
        {
            var report = new ModTweaksService.OptionalTweakReport();
            var missingPath = Path.Combine(root, "legacy", "missing.json");
            Func<Task>[] unavailableSteps =
            [
                () => ModTweaksService.EnsureCleanMissilesCopyAsync(missingPath,
                    Path.Combine(root, "missiles-clean.json")),
                () => ModTweaksService.EnsureCleanMonstersCopyAsync(missingPath,
                    Path.Combine(root, "monsters-clean.json")),
                () => ModTweaksService.EnsureCleanStringsCopyAsync(Path.Combine(root, "strings"),
                    Path.Combine(root, "strings-clean")),
                () => ModTweaksService.EnsureCleanLayoutsProfileHdCopyAsync(missingPath,
                    Path.Combine(root, "layouts-clean.json")),
                () => ModTweaksService.EnsureCleanDesecratedZonesCopyAsync(missingPath,
                    Path.Combine(root, "zones-clean.json"))
            ];

            foreach (var unavailableStep in unavailableSteps)
            {
                Assert.False(await ModTweaksService.TryRunOptionalTweakStepAsync(
                    "Legacy optional asset", true, unavailableStep, report));
            }

            Assert.Single(report.EnabledSkippedTweaks);
            Assert.False(Directory.Exists(Path.Combine(root, "legacy")));
            Assert.False(Directory.Exists(Path.Combine(root, "strings")));
            Assert.False(Directory.Exists(Path.Combine(root, "strings-clean")));
            Assert.Empty(Directory.GetFileSystemEntries(root));
        }
        finally
        {
            DeleteTempDirectory(root);
        }
    }

    [Fact]
    public async Task SupportedTooltipAssetKeepsExistingTweakAndRestorationBehavior()
    {
        var root = CreateTempDirectory();
        try
        {
            var layoutsFile = Path.Combine(root, "_profilehd.json");
            var cleanFile = Path.Combine(root, "layouts_profilehd_launcher_clean.json");
            const string originalContent =
                "{\"TooltipStyle\":{\"backgroundColor\":[1,2,3,0.5],\"inGameBackgroundColor\":[4,5,6,0.5]}}";
            await File.WriteAllTextAsync(layoutsFile, originalContent);

            await ModTweaksService.EnsureCleanLayoutsProfileHdCopyAsync(layoutsFile, cleanFile);
            await ModTweaksService.ApplyTooltipLayoutTweaksAsync(layoutsFile, makeTooltipBackgroundOpaque: true);
            var tweakedContent = await File.ReadAllTextAsync(layoutsFile);
            Assert.Contains("[ 0, 0, 0, 1 ]", tweakedContent);

            await ModTweaksService.RestoreLayoutsProfileHdFileAsync(cleanFile, layoutsFile);
            Assert.Equal(originalContent, await File.ReadAllTextAsync(layoutsFile));
        }
        finally
        {
            DeleteTempDirectory(root);
        }
    }

    [Fact]
    public async Task AlreadyCorrectTerrorZoneDurationIsSuccessfulNoOp()
    {
        var root = CreateTempDirectory();
        try
        {
            var zonesFile = Path.Combine(root, "desecratedzones.json");
            const string content = "{\"zone_duration_minutes\": 90}";
            await File.WriteAllTextAsync(zonesFile, content);
            var oldWriteTime = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(zonesFile, oldWriteTime);
            var report = new ModTweaksService.OptionalTweakReport();

            var completed = await ModTweaksService.TryRunOptionalTweakStepAsync(
                "Zone duration",
                tweakEnabled: true,
                () => ModTweaksService.ApplyZoneDurationTweakAsync(zonesFile, 90),
                report);

            Assert.True(completed);
            Assert.False(report.HasEnabledSkippedTweaks);
            Assert.Equal(content, await File.ReadAllTextAsync(zonesFile));
            Assert.Equal(oldWriteTime, File.GetLastWriteTimeUtc(zonesFile));
        }
        finally
        {
            DeleteTempDirectory(root);
        }
    }

    [Fact]
    public async Task TerrorZoneDurationIsUpdatedWhenRequestedValueDiffers()
    {
        var root = CreateTempDirectory();
        try
        {
            var zonesFile = Path.Combine(root, "desecratedzones.json");
            await File.WriteAllTextAsync(zonesFile, "{\"zone_duration_minutes\": 60}");

            await ModTweaksService.ApplyZoneDurationTweakAsync(zonesFile, 120);

            Assert.Equal("{\"zone_duration_minutes\": 120}", await File.ReadAllTextAsync(zonesFile));
        }
        finally
        {
            DeleteTempDirectory(root);
        }
    }

    [Fact]
    public async Task UnsupportedTerrorZoneOperationsDoNotPreventTheOtherSupportedOperation()
    {
        var root = CreateTempDirectory();
        try
        {
            var zonesWithoutDuration = Path.Combine(root, "zones-without-duration.json");
            const string actAutoContent = "[{\"outer\":[{}]}, {\"type\":\"DesecratedZone\",\"name\":\"desecratedzones_desecrated_zones_0_zones_1\",\"id\":\"Act2-Auto\",\"levels\":[]}]";
            await File.WriteAllTextAsync(zonesWithoutDuration, actAutoContent);
            var durationReport = new ModTweaksService.OptionalTweakReport();

            Assert.False(await ModTweaksService.TryRunOptionalTweakStepAsync(
                "Zone duration",
                tweakEnabled: true,
                () => ModTweaksService.ApplyZoneDurationTweakAsync(zonesWithoutDuration, 90),
                durationReport));
            Assert.True(await ModTweaksService.TryRunOptionalTweakStepAsync(
                "Terrorize all zones",
                tweakEnabled: true,
                () => ModTweaksService.ApplyTerrorizeAllZonesTweakAsync(zonesWithoutDuration, terrorizeAllZones: true),
                durationReport));
            Assert.Single(durationReport.EnabledSkippedTweaks);
            Assert.NotEqual(actAutoContent, await File.ReadAllTextAsync(zonesWithoutDuration));

            var zonesWithoutActAuto = Path.Combine(root, "zones-without-act-auto.json");
            await File.WriteAllTextAsync(zonesWithoutActAuto, "{\"zone_duration_minutes\": 60}");
            var actAutoReport = new ModTweaksService.OptionalTweakReport();

            Assert.False(await ModTweaksService.TryRunOptionalTweakStepAsync(
                "Terrorize all zones",
                tweakEnabled: true,
                () => ModTweaksService.ApplyTerrorizeAllZonesTweakAsync(zonesWithoutActAuto, terrorizeAllZones: true),
                actAutoReport));
            Assert.True(await ModTweaksService.TryRunOptionalTweakStepAsync(
                "Zone duration",
                tweakEnabled: true,
                () => ModTweaksService.ApplyZoneDurationTweakAsync(zonesWithoutActAuto, 90),
                actAutoReport));
            Assert.Single(actAutoReport.EnabledSkippedTweaks);
            Assert.Equal("{\"zone_duration_minutes\": 90}", await File.ReadAllTextAsync(zonesWithoutActAuto));
        }
        finally
        {
            DeleteTempDirectory(root);
        }
    }

    [Fact]
    public void SkippedTweakNotificationIsDeduplicatedPerModAndSkipSet()
    {
        var report = new ModTweaksService.OptionalTweakReport();
        report.RecordEnabledSkip("Terror zone sound");

        Assert.True(ModTweaksService.ShouldNotifySkippedTweaks("mod-root-a", report));
        Assert.False(ModTweaksService.ShouldNotifySkippedTweaks("mod-root-a", report));
        Assert.True(ModTweaksService.ShouldNotifySkippedTweaks("mod-root-b", report));

        var differentSkipSet = new ModTweaksService.OptionalTweakReport();
        differentSkipSet.RecordEnabledSkip("Zone duration");
        Assert.True(ModTweaksService.ShouldNotifySkippedTweaks("mod-root-a", differentSkipSet));
        Assert.False(ModTweaksService.ShouldNotifySkippedTweaks("mod-root-a", new ModTweaksService.OptionalTweakReport()));
    }

    [Fact]
    public async Task DefaultedSettingsDoNotTurnDisabledMissingTweaksIntoLaunchBlockers()
    {
        var root = CreateTempDirectory();
        try
        {
            var report = new ModTweaksService.OptionalTweakReport();
            var armorDirectory = Path.Combine(root, "items", "armor");
            var defaultProfile = new AppSettings().CurrentProfile;
            Assert.False(defaultProfile.RemoveHelmetVisual);

            var completed = await ModTweaksService.TryRunOptionalTweakStepAsync(
                "Remove Helmet Visual",
                defaultProfile.RemoveHelmetVisual,
                () => ModTweaksService.EnsureCleanArmorTweaksCopyAsync(
                    armorDirectory,
                    Path.Combine(armorDirectory, "armor_launcher_clean")),
                report);

            Assert.False(completed);
            Assert.False(report.HasEnabledSkippedTweaks);
        }
        finally
        {
            DeleteTempDirectory(root);
        }
    }

    [Fact]
    public async Task CriticalIoFailuresAreNotSuppressedAndRemainActionable()
    {
        var report = new ModTweaksService.OptionalTweakReport();
        var unauthorized = new UnauthorizedAccessException("denied");
        var io = new IOException("locked");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => ModTweaksService.TryRunOptionalTweakStepAsync(
            "Critical write", true, () => Task.FromException(unauthorized), report));
        await Assert.ThrowsAsync<IOException>(() => ModTweaksService.TryRunOptionalTweakStepAsync(
            "Critical write", true, () => Task.FromException(io), report));

        Assert.Contains("permissions", ModTweaksService.DescribePreparationFailure(unauthorized));
        Assert.Contains("Close other programs", ModTweaksService.DescribePreparationFailure(io));
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "ReimaginedLauncher.ModTweaksTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTempDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
}