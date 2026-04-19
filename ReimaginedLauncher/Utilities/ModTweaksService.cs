using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using D2RReimaginedTools.TextFileParsers;
using ReimaginedLauncher.Utilities.Json;

namespace ReimaginedLauncher.Utilities;

public static class ModTweaksService
{
    private const string ModDirectoryName = "Reimagined";
    private const string DataDirectoryName = "data";
    private const string ExcelDirectoryName = "excel";
    private const string BaseExcelDirectoryName = "base";
    private const string CleanExcelDirectoryName = "excel_launcher_clean";
    private const string HdDirectoryName = "hd";
    private const string ItemsDirectoryName = "items";
    private const string ArmorDirectoryName = "armor";
    private const string HelmetDirectoryName = "helmet";
    private const string CircletDirectoryName = "circlet";
    private const string PeltDirectoryName = "pelt";
    private const string GlobalDirectoryName = "global";
    private const string UiDirectoryName = "ui";
    private const string LayoutsDirectoryName = "layouts";
    private const string MissilesDirectoryName = "missiles";
    private const string MissilesFileName = "missiles.json";
    private const string CleanMissilesFileName = "missiles_launcher_clean.json";
    private const string LayoutsProfileHdFileName = "_profilehd.json";
    private const string CleanLayoutsProfileHdFileName = "layouts_profilehd_launcher_clean.json";
    private const string CleanArmorTweaksDirectoryName = "armor_launcher_clean";
    private const string TexturesDirectoryName = "textures";
    private const string VignetteDirectoryName = "vignette";
    private const string VignetteFileName = "vignette.texture";
    private const string BundledVignetteAssetPath = "Assets/Vignette/vignette.texture";
    private const string CharStatsFileName = "charstats.txt";
    private const string DifficultyLevelsFileName = "DifficultyLevels.txt";
    private const string SkillsFileName = "skills.txt";
    private const string StatesFileName = "states.txt";
    private const string TreasureClassExFileName = "treasureclassex.txt";
    private const string DesecratedZonesFileName = "desecratedzones.json";
    private const string CleanDesecratedZonesFileName = "desecratedzones_launcher_clean.json";
    private const string EnvDirectoryName = "env";
    private const string VisDirectoryName = "vis";
    private const string DesecratedFilePattern = "desecrated";
    private const string CleanVisDirectoryName = "vis_launcher_clean";
    private const string SfxDirectoryName = "sfx";
    private const string QuestDirectoryName = "quest";
    private const string DesecratedEnterHdFileName = "desecrated_enter_hd.flac";
    private const string CleanDesecratedEnterHdFileName = "desecrated_enter_hd_launcher_clean.flac";
    private const string GeneratedTweaksFolderName = "mod-tweaks";
    private static readonly string[] HelmetVisualRelativePaths =
    [
        Path.Combine(HelmetDirectoryName, "assault_helmet.json"),
        Path.Combine(HelmetDirectoryName, "avenger_guard.json"),
        Path.Combine(HelmetDirectoryName, "bone_helm.json"),
        Path.Combine(HelmetDirectoryName, "cap_hat.json"),
        Path.Combine(HelmetDirectoryName, "coif_of_glory.json"),
        Path.Combine(HelmetDirectoryName, "colbarbfrenzy_helm.json"),
        Path.Combine(HelmetDirectoryName, "colossal_summons_great_helm.json"),
        Path.Combine(HelmetDirectoryName, "crown.json"),
        Path.Combine(HelmetDirectoryName, "crown_of_thieves.json"),
        Path.Combine(HelmetDirectoryName, "duskdeep.json"),
        Path.Combine(HelmetDirectoryName, "fanged_helm.json"),
        Path.Combine(HelmetDirectoryName, "full_helm.json"),
        Path.Combine(HelmetDirectoryName, "great_helm.json"),
        Path.Combine(HelmetDirectoryName, "helm.json"),
        Path.Combine(HelmetDirectoryName, "horazons_countenance.json"),
        Path.Combine(HelmetDirectoryName, "horned_helm.json"),
        Path.Combine(HelmetDirectoryName, "jawbone_cap.json"),
        Path.Combine(HelmetDirectoryName, "mask.json"),
        Path.Combine(HelmetDirectoryName, "ondals_almighty.json"),
        Path.Combine(HelmetDirectoryName, "rockstopper.json"),
        Path.Combine(HelmetDirectoryName, "skull_cap.json"),
        Path.Combine(HelmetDirectoryName, "unique_warlock_helm.json"),
        Path.Combine(HelmetDirectoryName, "war_bonnet.json"),
        Path.Combine(HelmetDirectoryName, "wormskull.json"),
        Path.Combine(CircletDirectoryName, "circlet.json"),
        Path.Combine(CircletDirectoryName, "coronet.json"),
        Path.Combine(CircletDirectoryName, "diadem.json"),
        Path.Combine(CircletDirectoryName, "tiara.json"),
        Path.Combine(PeltDirectoryName, "antlers.json"),
        Path.Combine(PeltDirectoryName, "falcon_mask.json"),
        Path.Combine(PeltDirectoryName, "hawk_helm.json"),
        Path.Combine(PeltDirectoryName, "spirit_mask.json"),
        Path.Combine(PeltDirectoryName, "wolf_head.json")
    ];

    public static async Task<bool> PrepareForLaunchAsync(IProgress<string>? progress = null)
    {
        ReportProgress(progress, "Resolving mod directories...");
        var excelDirectory = GetExcelDirectory();
        LaunchDiagnostics.Log($"Resolved excel directory: {excelDirectory ?? "<null>"}");
        if (string.IsNullOrWhiteSpace(excelDirectory) || !Directory.Exists(excelDirectory))
        {
            Notifications.SendNotification("Excel folder not found in the Reimagined mod directory.", "Warning");
            return false;
        }

        var missilesFilePath = GetMissilesFilePath();
        var layoutsProfileHdFilePath = GetLayoutsProfileHdFilePath();
        var armorDirectory = GetArmorDirectory();
        var desecratedZonesFilePath = GetDesecratedZonesFilePath();
        var cleanExcelDirectory = GetCleanExcelDirectory(excelDirectory);
        var cleanMissilesFilePath = GetCleanMissilesFilePath(missilesFilePath);
        var cleanLayoutsProfileHdFilePath = GetCleanLayoutsProfileHdFilePath(layoutsProfileHdFilePath);
        var cleanArmorTweaksDirectory = GetCleanArmorTweaksDirectory(armorDirectory);
        var cleanDesecratedZonesFilePath = GetCleanDesecratedZonesFilePath(desecratedZonesFilePath);
        var excelDirectories = GetExcelDirectories(excelDirectory).ToList();
        LaunchDiagnostics.Log($"Resolved missiles file path: {missilesFilePath ?? "<null>"}");
        LaunchDiagnostics.Log($"Resolved layouts_profilehd.json path: {layoutsProfileHdFilePath ?? "<null>"}");
        LaunchDiagnostics.Log($"Resolved armor directory path: {armorDirectory ?? "<null>"}");
        LaunchDiagnostics.Log($"Resolved desecratedzones.json path: {desecratedZonesFilePath ?? "<null>"}");
        LaunchDiagnostics.Log($"Excel directories to process: {string.Join(", ", excelDirectories)}");

        try
        {
            ReportProgress(progress, "Preparing clean excel copy...");
            LaunchDiagnostics.Log("Ensuring clean excel copy.");
            await EnsureCleanExcelCopyAsync(excelDirectory, cleanExcelDirectory);
            ReportProgress(progress, "Preparing clean missiles copy...");
            LaunchDiagnostics.Log("Ensuring clean missiles copy.");
            await EnsureCleanMissilesCopyAsync(missilesFilePath, cleanMissilesFilePath);
            ReportProgress(progress, "Preparing clean tooltip layout copy...");
            LaunchDiagnostics.Log("Ensuring clean layouts_profilehd.json copy.");
            await EnsureCleanLayoutsProfileHdCopyAsync(layoutsProfileHdFilePath, cleanLayoutsProfileHdFilePath);
            ReportProgress(progress, "Preparing clean helmet visual copy...");
            LaunchDiagnostics.Log("Ensuring clean helmet and circlet JSON copy.");
            await EnsureCleanArmorTweaksCopyAsync(armorDirectory, cleanArmorTweaksDirectory);
            ReportProgress(progress, "Preparing clean desecrated zones copy...");
            LaunchDiagnostics.Log("Ensuring clean desecratedzones.json copy.");
            await EnsureCleanDesecratedZonesCopyAsync(desecratedZonesFilePath, cleanDesecratedZonesFilePath);
            ReportProgress(progress, "Preparing clean vis copy...");
            LaunchDiagnostics.Log("Ensuring clean vis directory copy.");
            await EnsureCleanVisCopyAsync();
            ReportProgress(progress, "Preparing clean terror zone fanfare copy...");
            LaunchDiagnostics.Log("Ensuring clean desecrated_enter_hd.flac copy.");
            EnsureCleanTerrorZoneFanfareCopy();

            foreach (var targetExcelDirectory in excelDirectories)
            {
                var sourceExcelDirectory = GetCleanVariantDirectory(targetExcelDirectory, excelDirectory, cleanExcelDirectory);
                var targetLabel = Path.GetFileName(targetExcelDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                ReportProgress(progress, $"Applying tweaks in {targetLabel}...");
                LaunchDiagnostics.Log($"Processing excel directory. Source={sourceExcelDirectory}, Target={targetExcelDirectory}");
                await ValidateExcelFilesAsync(sourceExcelDirectory);
                await CopyDirectoryAsync(sourceExcelDirectory, targetExcelDirectory, overwrite: true);
                await ApplyTweaksAsync(targetExcelDirectory, progress);
                ReportProgress(progress, $"Applying plugins in {targetLabel}...");
                LaunchDiagnostics.Log($"Applying plugins in {targetExcelDirectory}.");
                await PluginsService.ApplyEnabledPluginsAsync(targetExcelDirectory, progress);
            }

            ReportProgress(progress, "Restoring missiles.json...");
            LaunchDiagnostics.Log("Restoring missiles file from clean copy.");
            await RestoreMissilesFileAsync(cleanMissilesFilePath, missilesFilePath);
            ReportProgress(progress, "Applying missiles tweaks...");
            LaunchDiagnostics.Log("Applying missiles tweaks.");
            var profile = MainWindow.Settings.CurrentProfile;
            await ApplyMissilesTweaksAsync(missilesFilePath, profile.RemoveSplashVfx);
            ReportProgress(progress, "Restoring layouts_profilehd.json...");
            LaunchDiagnostics.Log("Restoring tooltip layout file from clean copy.");
            await RestoreLayoutsProfileHdFileAsync(cleanLayoutsProfileHdFilePath, layoutsProfileHdFilePath);
            ReportProgress(progress, "Applying tooltip layout tweaks...");
            LaunchDiagnostics.Log("Applying tooltip layout tweaks.");
            await ApplyTooltipLayoutTweaksAsync(layoutsProfileHdFilePath, profile.MakeTooltipBackgroundOpaque);
            ReportProgress(progress, "Restoring helmet visual files...");
            LaunchDiagnostics.Log("Restoring helmet and circlet visual files from clean copy.");
            await RestoreArmorTweaksAsync(armorDirectory, cleanArmorTweaksDirectory);
            ReportProgress(progress, "Applying helmet visual tweaks...");
            LaunchDiagnostics.Log("Applying helmet visual tweaks.");
            await ApplyHelmetVisualTweaksAsync(armorDirectory, profile.RemoveHelmetVisual);
            ReportProgress(progress, "Restoring desecratedzones.json...");
            LaunchDiagnostics.Log("Restoring desecrated zones file from clean copy.");
            await RestoreDesecratedZonesFileAsync(cleanDesecratedZonesFilePath, desecratedZonesFilePath);
            ReportProgress(progress, "Applying desecrated zones tweaks...");
            LaunchDiagnostics.Log("Applying desecrated zones tweaks.");
            await ApplyDesecratedZonesTweaksAsync(desecratedZonesFilePath, profile.TerrorizeAllZones);
            ReportProgress(progress, "Restoring desecrated vis files...");
            LaunchDiagnostics.Log("Restoring desecrated vis files from clean copy.");
            await RestoreVisFilesAsync();
            ReportProgress(progress, "Applying terror zone purple overlay tweaks...");
            LaunchDiagnostics.Log("Applying terror zone purple overlay tweaks.");
            ApplyTerrorZonePurpleOverlayTweak(profile.TerrorZonePurpleOverlay);
            ReportProgress(progress, "Restoring terror zone fanfare...");
            LaunchDiagnostics.Log("Restoring desecrated_enter_hd.flac from clean copy.");
            RestoreTerrorZoneFanfareFile();
            ReportProgress(progress, "Applying terror zone fanfare tweaks...");
            LaunchDiagnostics.Log("Applying terror zone fanfare tweaks.");
            ApplyTerrorZoneFanfareTweak(profile.RestoreTerrorZoneFanfare);
            ReportProgress(progress, "Applying vignette tweaks...");
            LaunchDiagnostics.Log("Applying vignette tweaks.");
            await ApplyVignetteTweakAsync(profile.RemoveVignette);
            LaunchDiagnostics.Log("Mod tweak preparation succeeded.");

            return true;
        }
        catch (Exception ex)
        {
            LaunchDiagnostics.LogException("Failed to prepare mod tweaks", ex);
            Notifications.SendNotification($"Failed to prepare mod tweaks: {ex.Message}", "Warning");
            return false;
        }
    }

    private static string? GetMpqBaseDirectory()
    {
        var profile = MainWindow.Settings.CurrentProfile;
        var installDirectory = profile.Type == InstallationType.D2RMM
            ? profile.InstallDirectory
            : InstallDirectoryValidator.NormalizeInstallDirectory(profile.InstallDirectory);
        if (string.IsNullOrWhiteSpace(installDirectory))
        {
            return null;
        }

        return profile.Type == InstallationType.D2RMM
            ? Path.Combine(installDirectory, $"{ModDirectoryName}.mpq")
            : Path.Combine(installDirectory, "mods", ModDirectoryName, $"{ModDirectoryName}.mpq");
    }

    private static string? GetExcelDirectory()
    {
        var mpqBase = GetMpqBaseDirectory();
        if (string.IsNullOrWhiteSpace(mpqBase))
        {
            return null;
        }

        return Path.Combine(
            mpqBase,
            DataDirectoryName,
            "global",
            ExcelDirectoryName);
    }

    private static string? GetMissilesFilePath()
    {
        var mpqBase = GetMpqBaseDirectory();
        if (string.IsNullOrWhiteSpace(mpqBase))
        {
            return null;
        }

        return Path.Combine(
            mpqBase,
            DataDirectoryName,
            HdDirectoryName,
            MissilesDirectoryName,
            MissilesFileName);
    }

    private static string? GetLayoutsProfileHdFilePath()
    {
        var mpqBase = GetMpqBaseDirectory();
        if (string.IsNullOrWhiteSpace(mpqBase))
        {
            return null;
        }

        return Path.Combine(
            mpqBase,
            DataDirectoryName,
            GlobalDirectoryName,
            UiDirectoryName,
            LayoutsDirectoryName,
            LayoutsProfileHdFileName);
    }

    private static string? GetDesecratedZonesFilePath()
    {
        var mpqBase = GetMpqBaseDirectory();
        if (string.IsNullOrWhiteSpace(mpqBase))
        {
            return null;
        }

        return Path.Combine(
            mpqBase,
            DataDirectoryName,
            HdDirectoryName,
            GlobalDirectoryName,
            ExcelDirectoryName,
            DesecratedZonesFileName);
    }

    private static string? GetArmorDirectory()
    {
        var installDirectory = InstallDirectoryValidator.NormalizeInstallDirectory(MainWindow.Settings.CurrentProfile.InstallDirectory);
        if (string.IsNullOrWhiteSpace(installDirectory))
        {
            return null;
        }

        return Path.Combine(
            installDirectory,
            "mods",
            ModDirectoryName,
            $"{ModDirectoryName}.mpq",
            DataDirectoryName,
            HdDirectoryName,
            ItemsDirectoryName,
            ArmorDirectoryName);
    }

    private static string GetCleanExcelDirectory(string excelDirectory)
    {
        var parentDirectory = Directory.GetParent(excelDirectory)?.FullName;
        if (string.IsNullOrWhiteSpace(parentDirectory))
        {
            throw new DirectoryNotFoundException("Excel folder parent directory could not be resolved.");
        }

        return Path.Combine(parentDirectory, CleanExcelDirectoryName);
    }

    private static async Task EnsureCleanExcelCopyAsync(string excelDirectory, string cleanExcelDirectory)
    {
        if (Directory.Exists(cleanExcelDirectory))
        {
            return;
        }

        await CopyDirectoryAsync(excelDirectory, cleanExcelDirectory, overwrite: true);
    }

    private static string GetCleanMissilesFilePath(string? missilesFilePath)
    {
        if (string.IsNullOrWhiteSpace(missilesFilePath))
        {
            throw new FileNotFoundException("missiles.json path could not be resolved.");
        }

        var missilesDirectory = Path.GetDirectoryName(missilesFilePath);
        if (string.IsNullOrWhiteSpace(missilesDirectory))
        {
            throw new DirectoryNotFoundException("Missiles folder could not be resolved.");
        }

        return Path.Combine(missilesDirectory, CleanMissilesFileName);
    }

    private static async Task EnsureCleanMissilesCopyAsync(string? missilesFilePath, string cleanMissilesFilePath)
    {
        if (File.Exists(cleanMissilesFilePath))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(missilesFilePath) || !File.Exists(missilesFilePath))
        {
            throw new FileNotFoundException("missiles.json was not found in the Reimagined hd missiles folder.");
        }

        await CopyFileAsync(missilesFilePath, cleanMissilesFilePath, overwrite: true);
    }

    private static string GetCleanLayoutsProfileHdFilePath(string? layoutsProfileHdFilePath)
    {
        if (string.IsNullOrWhiteSpace(layoutsProfileHdFilePath))
        {
            throw new FileNotFoundException("layouts_profilehd.json path could not be resolved.");
        }

        var layoutsDirectory = Path.GetDirectoryName(layoutsProfileHdFilePath);
        if (string.IsNullOrWhiteSpace(layoutsDirectory))
        {
            throw new DirectoryNotFoundException("UI layouts folder could not be resolved.");
        }

        return Path.Combine(layoutsDirectory, CleanLayoutsProfileHdFileName);
    }

    private static async Task EnsureCleanLayoutsProfileHdCopyAsync(string? layoutsProfileHdFilePath, string cleanLayoutsProfileHdFilePath)
    {
        if (File.Exists(cleanLayoutsProfileHdFilePath))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(layoutsProfileHdFilePath) || !File.Exists(layoutsProfileHdFilePath))
        {
            throw new FileNotFoundException("layouts_profilehd.json was not found in the Reimagined global ui folder.");
        }

        await CopyFileAsync(layoutsProfileHdFilePath, cleanLayoutsProfileHdFilePath, overwrite: true);
    }

    private static string GetCleanArmorTweaksDirectory(string? armorDirectory)
    {
        if (string.IsNullOrWhiteSpace(armorDirectory))
        {
            throw new DirectoryNotFoundException("Armor folder could not be resolved.");
        }

        return Path.Combine(armorDirectory, CleanArmorTweaksDirectoryName);
    }

    private static async Task EnsureCleanArmorTweaksCopyAsync(string? armorDirectory, string cleanArmorTweaksDirectory)
    {
        if (string.IsNullOrWhiteSpace(armorDirectory) || !Directory.Exists(armorDirectory))
        {
            throw new DirectoryNotFoundException("Armor folder was not found in the Reimagined hd items folder.");
        }

        if (Directory.Exists(cleanArmorTweaksDirectory))
        {
            return;
        }

        foreach (var relativePath in HelmetVisualRelativePaths)
        {
            var sourceFilePath = Path.Combine(armorDirectory, relativePath);
            var cleanFilePath = Path.Combine(cleanArmorTweaksDirectory, relativePath);
            var cleanDirectory = Path.GetDirectoryName(cleanFilePath);
            if (!string.IsNullOrWhiteSpace(cleanDirectory))
            {
                Directory.CreateDirectory(cleanDirectory);
            }

            if (File.Exists(sourceFilePath))
            {
                await CopyFileAsync(sourceFilePath, cleanFilePath, overwrite: true);
                continue;
            }

            await File.WriteAllTextAsync($"{cleanFilePath}.missing", string.Empty);
        }
    }

    private static string GetCleanDesecratedZonesFilePath(string? desecratedZonesFilePath)
    {
        if (string.IsNullOrWhiteSpace(desecratedZonesFilePath))
        {
            throw new FileNotFoundException("desecratedzones.json path could not be resolved.");
        }

        var desecratedZonesDirectory = Path.GetDirectoryName(desecratedZonesFilePath);
        if (string.IsNullOrWhiteSpace(desecratedZonesDirectory))
        {
            throw new DirectoryNotFoundException("Desecrated zones folder could not be resolved.");
        }

        return Path.Combine(desecratedZonesDirectory, CleanDesecratedZonesFileName);
    }

    private static async Task EnsureCleanDesecratedZonesCopyAsync(string? desecratedZonesFilePath, string cleanDesecratedZonesFilePath)
    {
        if (File.Exists(cleanDesecratedZonesFilePath))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(desecratedZonesFilePath) || !File.Exists(desecratedZonesFilePath))
        {
            throw new FileNotFoundException("desecratedzones.json was not found in the Reimagined hd global excel folder.");
        }

        await CopyFileAsync(desecratedZonesFilePath, cleanDesecratedZonesFilePath, overwrite: true);
    }

    private static async Task RestoreDesecratedZonesFileAsync(string cleanDesecratedZonesFilePath, string? desecratedZonesFilePath)
    {
        if (string.IsNullOrWhiteSpace(desecratedZonesFilePath))
        {
            throw new FileNotFoundException("desecratedzones.json path could not be resolved.");
        }

        if (!File.Exists(cleanDesecratedZonesFilePath))
        {
            throw new FileNotFoundException("Clean desecratedzones.json copy was not found.");
        }

        await CopyFileAsync(cleanDesecratedZonesFilePath, desecratedZonesFilePath, overwrite: true);
    }

    private static async Task ApplyDesecratedZonesTweaksAsync(string? desecratedZonesFilePath, bool terrorizeAllZones)
    {
        if (string.IsNullOrWhiteSpace(desecratedZonesFilePath) || !File.Exists(desecratedZonesFilePath))
        {
            throw new FileNotFoundException("desecratedzones.json was not found in the Reimagined hd global excel folder.");
        }

        if (!terrorizeAllZones)
        {
            return;
        }

        var updatedEntries = await DesecratedZonesJsonService.MergeActAutoZonesAsync(desecratedZonesFilePath);
        if (updatedEntries == 0)
        {
            throw new InvalidDataException("desecratedzones.json did not contain Act Auto zone boundaries to update.");
        }
    }

    private static IEnumerable<string> GetExcelDirectories(string excelDirectory)
    {
        yield return excelDirectory;

        var baseExcelDirectory = Path.Combine(excelDirectory, BaseExcelDirectoryName);
        if (Directory.Exists(baseExcelDirectory))
        {
            yield return baseExcelDirectory;
        }
    }

    private static string GetCleanVariantDirectory(string targetExcelDirectory, string excelDirectory, string cleanExcelDirectory)
    {
        var relativePath = Path.GetRelativePath(excelDirectory, targetExcelDirectory);
        return relativePath == "."
            ? cleanExcelDirectory
            : Path.Combine(cleanExcelDirectory, relativePath);
    }

    private static Task ValidateExcelFilesAsync(string excelDirectory)
    {
        var charStatsFilePath = Path.Combine(excelDirectory, CharStatsFileName);
        if (!File.Exists(charStatsFilePath))
        {
            throw new FileNotFoundException($"charstats.txt was not found in the Reimagined excel folder: {excelDirectory}");
        }

        var difficultyLevelsFilePath = Path.Combine(excelDirectory, DifficultyLevelsFileName);
        if (!File.Exists(difficultyLevelsFilePath))
        {
            throw new FileNotFoundException($"DifficultyLevels.txt was not found in the Reimagined excel folder: {excelDirectory}");
        }

        var skillsFilePath = Path.Combine(excelDirectory, SkillsFileName);
        if (!File.Exists(skillsFilePath))
        {
            throw new FileNotFoundException($"skills.txt was not found in the Reimagined excel folder: {excelDirectory}");
        }

        var statesFilePath = Path.Combine(excelDirectory, StatesFileName);
        if (!File.Exists(statesFilePath))
        {
            throw new FileNotFoundException($"states.txt was not found in the Reimagined excel folder: {excelDirectory}");
        }

        return Task.CompletedTask;
    }

    private static async Task ApplyTweaksAsync(string excelDirectory, IProgress<string>? progress)
    {
        var profile = MainWindow.Settings.CurrentProfile;
        ReportProgress(progress, "Updating charstats.txt...");
        LaunchDiagnostics.Log($"Applying charstats tweaks in {excelDirectory}.");
        await ApplyCharStatsTweaksAsync(
            Path.Combine(excelDirectory, CharStatsFileName),
            profile.SkillPointsPerLevel,
            profile.AttributesPerLevel);
        ReportProgress(progress, "Updating skills.txt...");
        LaunchDiagnostics.Log($"Applying skills tweaks in {excelDirectory}.");
        await ApplySkillsTweaksAsync(
            Path.Combine(excelDirectory, SkillsFileName),
            profile.MaxSkillLevel);
        ReportProgress(progress, "Updating DifficultyLevels.txt...");
        LaunchDiagnostics.Log($"Applying difficulty tweaks in {excelDirectory}.");
        await ApplyDifficultyLevelsTweaksAsync(
            Path.Combine(excelDirectory, DifficultyLevelsFileName),
            profile.NormalResistPenalty,
            profile.NightmareResistPenalty,
            profile.HellResistPenalty);
        ReportProgress(progress, "Updating states.txt...");
        LaunchDiagnostics.Log($"Applying states tweaks in {excelDirectory}.");
        await ApplyStatesTweaksAsync(
            Path.Combine(excelDirectory, StatesFileName),
            profile.RemovePaladinAuraSound);
        ReportProgress(progress, "Updating treasureclassex.txt...");
        LaunchDiagnostics.Log($"Applying treasure class tweaks in {excelDirectory}.");
        await ApplyTreasureClassExTweaksAsync(
            Path.Combine(excelDirectory, TreasureClassExFileName),
            profile.OrbStackDrops,
            profile.RuneStackDrops);
    }

    private static void ReportProgress(IProgress<string>? progress, string message)
    {
        progress?.Report(message);
        LaunchDiagnostics.Log($"STATUS: {message}");
    }

    private static async Task RestoreMissilesFileAsync(string cleanMissilesFilePath, string? missilesFilePath)
    {
        if (string.IsNullOrWhiteSpace(missilesFilePath))
        {
            throw new FileNotFoundException("missiles.json path could not be resolved.");
        }

        if (!File.Exists(cleanMissilesFilePath))
        {
            throw new FileNotFoundException("Clean missiles.json copy was not found.");
        }

        await CopyFileAsync(cleanMissilesFilePath, missilesFilePath, overwrite: true);
    }

    private static async Task ApplyMissilesTweaksAsync(string? missilesFilePath, bool removeSplashVfx)
    {
        if (string.IsNullOrWhiteSpace(missilesFilePath) || !File.Exists(missilesFilePath))
        {
            throw new FileNotFoundException("missiles.json was not found in the Reimagined hd missiles folder.");
        }

        if (!removeSplashVfx)
        {
            return;
        }

        var updatedEntries = await MissilesJsonService.ClearProcSplashExplodeAsync(missilesFilePath);
        if (updatedEntries == 0)
        {
            throw new InvalidDataException("missiles.json did not contain a proc_splash_explode entry to update.");
        }
    }

    private static async Task RestoreLayoutsProfileHdFileAsync(string cleanLayoutsProfileHdFilePath, string? layoutsProfileHdFilePath)
    {
        if (string.IsNullOrWhiteSpace(layoutsProfileHdFilePath))
        {
            throw new FileNotFoundException("layouts_profilehd.json path could not be resolved.");
        }

        if (!File.Exists(cleanLayoutsProfileHdFilePath))
        {
            throw new FileNotFoundException("Clean layouts_profilehd.json copy was not found.");
        }

        await CopyFileAsync(cleanLayoutsProfileHdFilePath, layoutsProfileHdFilePath, overwrite: true);
    }

    private static async Task ApplyTooltipLayoutTweaksAsync(string? layoutsProfileHdFilePath, bool makeTooltipBackgroundOpaque)
    {
        if (string.IsNullOrWhiteSpace(layoutsProfileHdFilePath) || !File.Exists(layoutsProfileHdFilePath))
        {
            throw new FileNotFoundException("layouts_profilehd.json was not found in the Reimagined global ui folder.");
        }

        if (!makeTooltipBackgroundOpaque)
        {
            return;
        }

        var updatedValues = await TooltipStyleJsonService.MakeTooltipBackgroundOpaqueAsync(layoutsProfileHdFilePath);
        if (updatedValues == 0)
        {
            throw new InvalidDataException("layouts_profilehd.json did not contain TooltipStyle background colors to update.");
        }
    }

    private static async Task RestoreArmorTweaksAsync(string? armorDirectory, string cleanArmorTweaksDirectory)
    {
        if (string.IsNullOrWhiteSpace(armorDirectory) || !Directory.Exists(armorDirectory))
        {
            throw new DirectoryNotFoundException("Armor folder was not found in the Reimagined hd items folder.");
        }

        if (!Directory.Exists(cleanArmorTweaksDirectory))
        {
            throw new DirectoryNotFoundException("Clean helmet and circlet JSON copy was not found.");
        }

        foreach (var relativePath in HelmetVisualRelativePaths)
        {
            var targetFilePath = Path.Combine(armorDirectory, relativePath);
            var cleanFilePath = Path.Combine(cleanArmorTweaksDirectory, relativePath);
            var missingMarkerPath = $"{cleanFilePath}.missing";

            if (File.Exists(cleanFilePath))
            {
                await CopyFileAsync(cleanFilePath, targetFilePath, overwrite: true);
                continue;
            }

            if (File.Exists(missingMarkerPath))
            {
                if (File.Exists(targetFilePath))
                {
                    File.Delete(targetFilePath);
                }

                continue;
            }

            throw new FileNotFoundException($"Clean helmet visual state was missing for {relativePath}.");
        }
    }

    private static async Task ApplyHelmetVisualTweaksAsync(string? armorDirectory, bool removeHelmetVisual)
    {
        if (string.IsNullOrWhiteSpace(armorDirectory) || !Directory.Exists(armorDirectory))
        {
            throw new DirectoryNotFoundException("Armor folder was not found in the Reimagined hd items folder.");
        }

        if (!removeHelmetVisual)
        {
            return;
        }

        foreach (var relativePath in HelmetVisualRelativePaths)
        {
            var filePath = Path.Combine(armorDirectory, relativePath);
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllTextAsync(filePath, string.Empty);
        }
    }

    private static async Task ApplyVignetteTweakAsync(bool removeVignette)
    {
        var mpqBase = GetMpqBaseDirectory();
        if (string.IsNullOrWhiteSpace(mpqBase))
        {
            return;
        }

        var vignetteDirectory = Path.Combine(
            mpqBase,
            DataDirectoryName,
            HdDirectoryName,
            GlobalDirectoryName,
            TexturesDirectoryName,
            VignetteDirectoryName);
        var vignetteFilePath = Path.Combine(vignetteDirectory, VignetteFileName);

        if (!removeVignette)
        {
            if (File.Exists(vignetteFilePath))
            {
                File.Delete(vignetteFilePath);
            }

            return;
        }

        var bundledVignetteFile = Path.Combine(AppContext.BaseDirectory, BundledVignetteAssetPath);
        if (!File.Exists(bundledVignetteFile))
        {
            throw new FileNotFoundException("Bundled vignette.texture asset was not found.");
        }

        Directory.CreateDirectory(vignetteDirectory);
        await CopyFileAsync(bundledVignetteFile, vignetteFilePath, overwrite: true);
    }

    private static async Task ApplyCharStatsTweaksAsync(
        string charStatsFilePath,
        int skillPointsPerLevel,
        int attributesPerLevel)
    {
        var entries = (await CharStatsParser.GetEntries(charStatsFilePath)).ToList();
        if (entries.Count == 0)
        {
            throw new InvalidDataException("charstats.txt did not contain any editable rows.");
        }

        foreach (var entry in entries)
        {
            entry.StatPerLevel = attributesPerLevel;
            entry.SkillsPerLevel = skillPointsPerLevel;
        }

        var outputDirectory = Path.Combine(
            Path.GetTempPath(),
            "d2r-reimagined-launcher",
            GeneratedTweaksFolderName,
            Guid.NewGuid().ToString("N"));
        var generatedFile = await CharStatsParser.SaveEntries(entries, charStatsFilePath, outputDirectory);
        File.Copy(generatedFile.FullName, charStatsFilePath, overwrite: true);
    }

    private static async Task ApplyDifficultyLevelsTweaksAsync(
        string difficultyLevelsFilePath,
        int normalResistPenalty,
        int nightmareResistPenalty,
        int hellResistPenalty)
    {
        var entries = (await DifficultyLevelParser.GetEntries(difficultyLevelsFilePath)).ToList();
        if (entries.Count == 0)
        {
            throw new InvalidDataException("DifficultyLevels.txt did not contain any editable rows.");
        }

        var normalFound = false;
        var nightmareFound = false;
        var hellFound = false;

        foreach (var entry in entries)
        {
            switch (entry.Name)
            {
                case "Normal":
                    entry.ResistPenalty = normalResistPenalty;
                    normalFound = true;
                    break;
                case "Nightmare":
                    entry.ResistPenalty = nightmareResistPenalty;
                    nightmareFound = true;
                    break;
                case "Hell":
                    entry.ResistPenalty = hellResistPenalty;
                    hellFound = true;
                    break;
                default:
                    continue;
            }
        }

        if (!normalFound || !nightmareFound || !hellFound)
        {
            throw new InvalidDataException("DifficultyLevels.txt did not contain Normal, Nightmare, and Hell rows.");
        }

        await SaveGeneratedEntriesAsync(
            entries,
            difficultyLevelsFilePath,
            (updatedEntries, filePath, outputDirectory, cancellationToken) =>
                DifficultyLevelParser.SaveEntries(updatedEntries, filePath, outputDirectory, cancellationToken));
    }

    private static async Task ApplySkillsTweaksAsync(string skillsFilePath, int maxSkillLevel)
    {
        var entries = (await SkillsParser.GetEntries(skillsFilePath)).ToList();
        if (entries.Count == 0)
        {
            throw new InvalidDataException("skills.txt did not contain any editable rows.");
        }

        var updatedRows = 0;
        var updatedEntries = new List<D2RReimaginedTools.Models.Skills>(entries.Count);

        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.CharClass) ||
                !int.TryParse(entry.MaxLvl, out var currentMaxLevel) ||
                currentMaxLevel <= 0)
            {
                updatedEntries.Add(entry);
                continue;
            }

            updatedEntries.Add(entry with { MaxLvl = maxSkillLevel.ToString() });
            updatedRows++;
        }

        if (updatedRows == 0)
        {
            throw new InvalidDataException("skills.txt did not contain any rows with maxlvl values.");
        }

        await SaveGeneratedEntriesAsync(
            updatedEntries,
            skillsFilePath,
            (updatedEntriesList, filePath, outputDirectory, cancellationToken) =>
                SkillsParser.SaveEntries(updatedEntriesList, filePath, outputDirectory, cancellationToken));
    }

    private static async Task ApplyStatesTweaksAsync(string statesFilePath, bool removePaladinAuraSound)
    {
        if (!removePaladinAuraSound)
        {
            return;
        }

        var entries = (await StatesParser.GetEntries(statesFilePath)).ToList();
        if (entries.Count == 0)
        {
            throw new InvalidDataException("states.txt did not contain any editable rows.");
        }

        var updatedRows = 0;
        var updatedEntries = new List<D2RReimaginedTools.Models.States>(entries.Count);

        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.OnSound) ||
                !entry.OnSound.StartsWith("paladin_aura_", StringComparison.OrdinalIgnoreCase))
            {
                updatedEntries.Add(entry);
                continue;
            }

            updatedEntries.Add(entry with { OnSound = string.Empty });
            updatedRows++;
        }

        if (updatedRows == 0)
        {
            throw new InvalidDataException("states.txt did not contain any paladin_aura_ rows to update.");
        }

        await SaveGeneratedEntriesAsync(
            updatedEntries,
            statesFilePath,
            (updatedEntriesList, filePath, outputDirectory, cancellationToken) =>
                StatesParser.SaveEntries(updatedEntriesList, filePath, outputDirectory, cancellationToken));
    }

    private static readonly (string Unstacked, string Stacked)[] OrbReplacementPairs =
    [
        ("ooi", "1oi"),
        ("ooa", "1oa"),
        ("ooc", "1oc"),
        ("ka3", "1ka"),
        ("oos", "1os"),
        ("ooe", "1oe"),
        ("oor", "1or")
    ];

    private static async Task ApplyTreasureClassExTweaksAsync(
        string treasureClassExFilePath,
        StackDropOption orbStackDrops,
        StackDropOption runeStackDrops)
    {
        if (orbStackDrops == StackDropOption.Default && runeStackDrops == StackDropOption.Default)
        {
            return;
        }

        if (!File.Exists(treasureClassExFilePath))
        {
            return;
        }

        var entries = (await TreasureClassParser.GetEntries(treasureClassExFilePath)).ToList();
        if (entries.Count == 0)
        {
            return;
        }

        var updatedEntries = new List<D2RReimaginedTools.Models.TreasureClass>(entries.Count);

        foreach (var entry in entries)
        {
            var updated = entry with
            {
                Item1 = ReplaceDropCode(entry.Item1, orbStackDrops, runeStackDrops),
                Item2 = ReplaceDropCode(entry.Item2, orbStackDrops, runeStackDrops),
                Item3 = ReplaceDropCode(entry.Item3, orbStackDrops, runeStackDrops),
                Item4 = ReplaceDropCode(entry.Item4, orbStackDrops, runeStackDrops),
                Item5 = ReplaceDropCode(entry.Item5, orbStackDrops, runeStackDrops),
                Item6 = ReplaceDropCode(entry.Item6, orbStackDrops, runeStackDrops),
                Item7 = ReplaceDropCode(entry.Item7, orbStackDrops, runeStackDrops),
                Item8 = ReplaceDropCode(entry.Item8, orbStackDrops, runeStackDrops),
                Item9 = ReplaceDropCode(entry.Item9, orbStackDrops, runeStackDrops),
                Item10 = ReplaceDropCode(entry.Item10, orbStackDrops, runeStackDrops)
            };
            updatedEntries.Add(updated);
        }

        await SaveGeneratedEntriesAsync(
            updatedEntries,
            treasureClassExFilePath,
            (updatedEntriesList, filePath, outputDirectory, cancellationToken) =>
                TreasureClassParser.SaveEntries(updatedEntriesList, filePath, outputDirectory, cancellationToken));
    }

    private static string? ReplaceDropCode(
        string? value,
        StackDropOption orbStackDrops,
        StackDropOption runeStackDrops)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        if (orbStackDrops != StackDropOption.Default)
        {
            foreach (var (unstacked, stacked) in OrbReplacementPairs)
            {
                var from = orbStackDrops == StackDropOption.Unstacked ? stacked : unstacked;
                var to = orbStackDrops == StackDropOption.Unstacked ? unstacked : stacked;

                if (string.Equals(value, from, StringComparison.Ordinal))
                {
                    return to;
                }
            }
        }

        if (runeStackDrops != StackDropOption.Default && value.Length == 3)
        {
            var prefix = value[0];
            var suffix = value.Substring(1);

            if (runeStackDrops == StackDropOption.Unstacked
                && prefix == 's'
                && int.TryParse(suffix, out _))
            {
                return "r" + suffix;
            }

            if (runeStackDrops == StackDropOption.Stacked
                && prefix == 'r'
                && int.TryParse(suffix, out _))
            {
                return "s" + suffix;
            }
        }

        return value;
    }

    private static async Task SaveGeneratedEntriesAsync<TEntry>(
        IList<TEntry> entries,
        string sourceFilePath,
        Func<IList<TEntry>, string, string, CancellationToken, Task<FileInfo>> saveEntriesAsync)
    {
        var outputDirectory = Path.Combine(
            Path.GetTempPath(),
            "d2r-reimagined-launcher",
            GeneratedTweaksFolderName,
            Guid.NewGuid().ToString("N"));
        var generatedFile = await saveEntriesAsync(entries, sourceFilePath, outputDirectory, CancellationToken.None);
        File.Copy(generatedFile.FullName, sourceFilePath, overwrite: true);
    }

    private static async Task CopyDirectoryAsync(string sourceDirectory, string destinationDirectory, bool overwrite)
    {
        Directory.CreateDirectory(destinationDirectory);

        foreach (var filePath in Directory.GetFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, filePath);
            var destinationFilePath = Path.Combine(destinationDirectory, relativePath);
            var destinationFolder = Path.GetDirectoryName(destinationFilePath);

            if (!string.IsNullOrWhiteSpace(destinationFolder))
            {
                Directory.CreateDirectory(destinationFolder);
            }

            await using var sourceStream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            await using var destinationStream = File.Open(
                destinationFilePath,
                overwrite ? FileMode.Create : FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None);
            await sourceStream.CopyToAsync(destinationStream);
        }
    }

    private static string? GetVisDirectory()
    {
        var mpqBase = GetMpqBaseDirectory();
        if (string.IsNullOrWhiteSpace(mpqBase))
        {
            return null;
        }

        return Path.Combine(
            mpqBase,
            DataDirectoryName,
            HdDirectoryName,
            EnvDirectoryName,
            VisDirectoryName);
    }

    private static string GetCleanVisDirectory(string visDirectory)
    {
        var parentDirectory = Directory.GetParent(visDirectory)?.FullName;
        if (string.IsNullOrWhiteSpace(parentDirectory))
        {
            throw new DirectoryNotFoundException("Vis folder parent directory could not be resolved.");
        }

        return Path.Combine(parentDirectory, CleanVisDirectoryName);
    }

    private static async Task EnsureCleanVisCopyAsync()
    {
        var visDirectory = GetVisDirectory();
        if (string.IsNullOrWhiteSpace(visDirectory) || !Directory.Exists(visDirectory))
        {
            LaunchDiagnostics.Log($"Vis directory not found, skipping clean copy: {visDirectory ?? "<null>"}");
            return;
        }

        var cleanVisDirectory = GetCleanVisDirectory(visDirectory);
        if (Directory.Exists(cleanVisDirectory))
        {
            return;
        }

        var desecratedFiles = Directory.GetFiles(visDirectory, "*.json")
            .Where(f => Path.GetFileName(f).Contains(DesecratedFilePattern, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (desecratedFiles.Count == 0)
        {
            LaunchDiagnostics.Log("No desecrated vis files found to back up.");
            return;
        }

        Directory.CreateDirectory(cleanVisDirectory);

        foreach (var file in desecratedFiles)
        {
            var fileName = Path.GetFileName(file);
            var cleanFilePath = Path.Combine(cleanVisDirectory, fileName);
            await CopyFileAsync(file, cleanFilePath, overwrite: true);
            LaunchDiagnostics.Log($"Backed up desecrated vis file: {fileName}");
        }
    }

    private static async Task RestoreVisFilesAsync()
    {
        var visDirectory = GetVisDirectory();
        if (string.IsNullOrWhiteSpace(visDirectory) || !Directory.Exists(visDirectory))
        {
            LaunchDiagnostics.Log($"Vis directory not found, skipping restore: {visDirectory ?? "<null>"}");
            return;
        }

        var cleanVisDirectory = GetCleanVisDirectory(visDirectory);
        if (!Directory.Exists(cleanVisDirectory))
        {
            LaunchDiagnostics.Log("Clean vis directory not found, skipping restore.");
            return;
        }

        foreach (var cleanFile in Directory.GetFiles(cleanVisDirectory, "*.json"))
        {
            var fileName = Path.GetFileName(cleanFile);
            var targetFilePath = Path.Combine(visDirectory, fileName);
            await CopyFileAsync(cleanFile, targetFilePath, overwrite: true);
            LaunchDiagnostics.Log($"Restored desecrated vis file: {fileName}");
        }
    }

    private static void ApplyTerrorZonePurpleOverlayTweak(bool terrorZonePurpleOverlay)
    {
        if (!terrorZonePurpleOverlay)
        {
            return;
        }

        var visDirectory = GetVisDirectory();
        if (string.IsNullOrWhiteSpace(visDirectory) || !Directory.Exists(visDirectory))
        {
            LaunchDiagnostics.Log($"Vis directory not found: {visDirectory ?? "<null>"}");
            return;
        }

        var files = Directory.GetFiles(visDirectory, "*.json")
            .Where(f => Path.GetFileName(f).Contains(DesecratedFilePattern, StringComparison.OrdinalIgnoreCase))
            .ToList();

        LaunchDiagnostics.Log($"Found {files.Count} desecrated vis file(s) to delete.");

        foreach (var file in files)
        {
            File.Delete(file);
            LaunchDiagnostics.Log($"Deleted desecrated vis file: {file}");
        }
    }

    private static string? GetTerrorZoneFanfareFilePath()
    {
        var mpqBase = GetMpqBaseDirectory();
        if (string.IsNullOrWhiteSpace(mpqBase))
        {
            return null;
        }

        return Path.Combine(
            mpqBase,
            DataDirectoryName,
            HdDirectoryName,
            GlobalDirectoryName,
            SfxDirectoryName,
            QuestDirectoryName,
            DesecratedEnterHdFileName);
    }

    private static string? GetCleanTerrorZoneFanfareFilePath()
    {
        var fanfareFilePath = GetTerrorZoneFanfareFilePath();
        if (string.IsNullOrWhiteSpace(fanfareFilePath))
        {
            return null;
        }

        var parentDirectory = Path.GetDirectoryName(fanfareFilePath);
        if (string.IsNullOrWhiteSpace(parentDirectory))
        {
            return null;
        }

        return Path.Combine(parentDirectory, CleanDesecratedEnterHdFileName);
    }

    private static void EnsureCleanTerrorZoneFanfareCopy()
    {
        var fanfareFilePath = GetTerrorZoneFanfareFilePath();
        if (string.IsNullOrWhiteSpace(fanfareFilePath) || !File.Exists(fanfareFilePath))
        {
            LaunchDiagnostics.Log($"Terror zone fanfare file not found, skipping clean copy: {fanfareFilePath ?? "<null>"}");
            return;
        }

        var cleanFilePath = GetCleanTerrorZoneFanfareFilePath();
        if (string.IsNullOrWhiteSpace(cleanFilePath))
        {
            return;
        }

        if (File.Exists(cleanFilePath))
        {
            return;
        }

        File.Copy(fanfareFilePath, cleanFilePath);
        LaunchDiagnostics.Log($"Backed up terror zone fanfare file: {DesecratedEnterHdFileName}");
    }

    private static void RestoreTerrorZoneFanfareFile()
    {
        var fanfareFilePath = GetTerrorZoneFanfareFilePath();
        var cleanFilePath = GetCleanTerrorZoneFanfareFilePath();
        if (string.IsNullOrWhiteSpace(fanfareFilePath) || string.IsNullOrWhiteSpace(cleanFilePath))
        {
            LaunchDiagnostics.Log("Terror zone fanfare paths could not be resolved, skipping restore.");
            return;
        }

        if (!File.Exists(cleanFilePath))
        {
            LaunchDiagnostics.Log("Clean terror zone fanfare file not found, skipping restore.");
            return;
        }

        File.Copy(cleanFilePath, fanfareFilePath, overwrite: true);
        LaunchDiagnostics.Log($"Restored terror zone fanfare file: {DesecratedEnterHdFileName}");
    }

    private static void ApplyTerrorZoneFanfareTweak(bool restoreTerrorZoneFanfare)
    {
        if (restoreTerrorZoneFanfare)
        {
            return;
        }

        var fanfareFilePath = GetTerrorZoneFanfareFilePath();
        if (string.IsNullOrWhiteSpace(fanfareFilePath) || !File.Exists(fanfareFilePath))
        {
            LaunchDiagnostics.Log($"Terror zone fanfare file not found: {fanfareFilePath ?? "<null>"}");
            return;
        }

        File.Delete(fanfareFilePath);
        LaunchDiagnostics.Log($"Deleted terror zone fanfare file: {fanfareFilePath}");
    }

    private static async Task CopyFileAsync(string sourceFilePath, string destinationFilePath, bool overwrite)
    {
        var destinationFolder = Path.GetDirectoryName(destinationFilePath);
        if (!string.IsNullOrWhiteSpace(destinationFolder))
        {
            Directory.CreateDirectory(destinationFolder);
        }

        await using var sourceStream = File.Open(sourceFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        await using var destinationStream = File.Open(
            destinationFilePath,
            overwrite ? FileMode.Create : FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None);
        await sourceStream.CopyToAsync(destinationStream);
    }
}
