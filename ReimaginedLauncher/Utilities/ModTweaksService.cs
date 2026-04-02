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
    private const string MissilesDirectoryName = "missiles";
    private const string MissilesFileName = "missiles.json";
    private const string CleanMissilesFileName = "missiles_launcher_clean.json";
    private const string CharStatsFileName = "charstats.txt";
    private const string DifficultyLevelsFileName = "DifficultyLevels.txt";
    private const string SkillsFileName = "skills.txt";
    private const string StatesFileName = "states.txt";
    private const string GeneratedTweaksFolderName = "mod-tweaks";

    public static async Task<bool> PrepareForLaunchAsync()
    {
        var excelDirectory = GetExcelDirectory();
        if (string.IsNullOrWhiteSpace(excelDirectory) || !Directory.Exists(excelDirectory))
        {
            Notifications.SendNotification("Excel folder not found in the Reimagined mod directory.", "Warning");
            return false;
        }

        var missilesFilePath = GetMissilesFilePath();
        var cleanExcelDirectory = GetCleanExcelDirectory(excelDirectory);
        var cleanMissilesFilePath = GetCleanMissilesFilePath(missilesFilePath);
        var excelDirectories = GetExcelDirectories(excelDirectory).ToList();

        try
        {
            await EnsureCleanExcelCopyAsync(excelDirectory, cleanExcelDirectory);
            await EnsureCleanMissilesCopyAsync(missilesFilePath, cleanMissilesFilePath);

            foreach (var targetExcelDirectory in excelDirectories)
            {
                var sourceExcelDirectory = GetCleanVariantDirectory(targetExcelDirectory, excelDirectory, cleanExcelDirectory);
                await ValidateExcelFilesAsync(sourceExcelDirectory);
                await CopyDirectoryAsync(sourceExcelDirectory, targetExcelDirectory, overwrite: true);
                await ApplyTweaksAsync(targetExcelDirectory);
            }

            await RestoreMissilesFileAsync(cleanMissilesFilePath, missilesFilePath);
            await ApplyMissilesTweaksAsync(missilesFilePath, MainWindow.Settings.RemoveSplashVfx);

            return true;
        }
        catch (Exception ex)
        {
            Notifications.SendNotification($"Failed to prepare mod tweaks: {ex.Message}", "Warning");
            return false;
        }
    }

    private static string? GetExcelDirectory()
    {
        var installDirectory = InstallDirectoryValidator.NormalizeInstallDirectory(MainWindow.Settings.InstallDirectory);
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
            "global",
            ExcelDirectoryName);
    }

    private static string? GetMissilesFilePath()
    {
        var installDirectory = InstallDirectoryValidator.NormalizeInstallDirectory(MainWindow.Settings.InstallDirectory);
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
            MissilesDirectoryName,
            MissilesFileName);
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

    private static async Task ApplyTweaksAsync(string excelDirectory)
    {
        await ApplyCharStatsTweaksAsync(
            Path.Combine(excelDirectory, CharStatsFileName),
            MainWindow.Settings.SkillPointsPerLevel,
            MainWindow.Settings.AttributesPerLevel);
        await ApplySkillsTweaksAsync(
            Path.Combine(excelDirectory, SkillsFileName),
            MainWindow.Settings.MaxSkillLevel);
        await ApplyDifficultyLevelsTweaksAsync(
            Path.Combine(excelDirectory, DifficultyLevelsFileName),
            MainWindow.Settings.NormalResistPenalty,
            MainWindow.Settings.NightmareResistPenalty,
            MainWindow.Settings.HellResistPenalty);
        await ApplyStatesTweaksAsync(
            Path.Combine(excelDirectory, StatesFileName),
            MainWindow.Settings.RemovePaladinAuraSound);
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
            if (!int.TryParse(entry.MaxLvl, out var currentMaxLevel) || currentMaxLevel <= 0)
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
            if (string.IsNullOrWhiteSpace(entry.StateId) ||
                !entry.StateId.StartsWith("paladin_aura_", StringComparison.OrdinalIgnoreCase))
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
