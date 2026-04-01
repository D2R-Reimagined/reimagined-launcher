using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using D2RReimaginedTools.TextFileParsers;

namespace ReimaginedLauncher.Utilities;

public static class ModTweaksService
{
    private const string ModDirectoryName = "Reimagined";
    private const string ExcelDirectoryName = "excel";
    private const string BaseExcelDirectoryName = "base";
    private const string CleanExcelDirectoryName = "excel_launcher_clean";
    private const string CharStatsFileName = "charstats.txt";
    private const string DifficultyLevelsFileName = "DifficultyLevels.txt";
    private const string SkillsFileName = "skills.txt";

    public static async Task<bool> PrepareForLaunchAsync()
    {
        var excelDirectory = GetExcelDirectory();
        if (string.IsNullOrWhiteSpace(excelDirectory) || !Directory.Exists(excelDirectory))
        {
            Notifications.SendNotification("Excel folder not found in the Reimagined mod directory.", "Warning");
            return false;
        }

        var cleanExcelDirectory = GetCleanExcelDirectory(excelDirectory);
        var excelDirectories = GetExcelDirectories(excelDirectory).ToList();

        try
        {
            await EnsureCleanExcelCopyAsync(excelDirectory, cleanExcelDirectory);

            foreach (var targetExcelDirectory in excelDirectories)
            {
                var sourceExcelDirectory = GetCleanVariantDirectory(targetExcelDirectory, excelDirectory, cleanExcelDirectory);
                await ValidateExcelFilesAsync(sourceExcelDirectory);
                await CopyDirectoryAsync(sourceExcelDirectory, targetExcelDirectory, overwrite: true);
                await ApplyTweaksAsync(targetExcelDirectory);
            }

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
            "data",
            "global",
            ExcelDirectoryName);
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
            "mod-tweaks",
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
        var lines = await File.ReadAllLinesAsync(difficultyLevelsFilePath);
        if (lines.Length == 0)
        {
            throw new InvalidDataException("DifficultyLevels.txt did not contain any rows.");
        }

        var headers = lines[0].Split('\t');
        var nameIndex = Array.FindIndex(headers, header => header.Equals("Name", StringComparison.OrdinalIgnoreCase));
        var resistPenaltyIndex = Array.FindIndex(headers, header => header.Equals("ResistPenalty", StringComparison.OrdinalIgnoreCase));
        if (nameIndex < 0 || resistPenaltyIndex < 0)
        {
            throw new InvalidDataException("DifficultyLevels.txt is missing Name or ResistPenalty columns.");
        }

        var normalFound = false;
        var nightmareFound = false;
        var hellFound = false;

        for (var i = 1; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i]))
            {
                continue;
            }

            var columns = lines[i].Split('\t');
            if (columns.Length <= Math.Max(nameIndex, resistPenaltyIndex))
            {
                continue;
            }

            switch (columns[nameIndex])
            {
                case "Normal":
                    columns[resistPenaltyIndex] = normalResistPenalty.ToString();
                    normalFound = true;
                    break;
                case "Nightmare":
                    columns[resistPenaltyIndex] = nightmareResistPenalty.ToString();
                    nightmareFound = true;
                    break;
                case "Hell":
                    columns[resistPenaltyIndex] = hellResistPenalty.ToString();
                    hellFound = true;
                    break;
                default:
                    continue;
            }

            lines[i] = string.Join('\t', columns);
        }

        if (!normalFound || !nightmareFound || !hellFound)
        {
            throw new InvalidDataException("DifficultyLevels.txt did not contain Normal, Nightmare, and Hell rows.");
        }

        await File.WriteAllLinesAsync(difficultyLevelsFilePath, lines);
    }

    private static async Task ApplySkillsTweaksAsync(string skillsFilePath, int maxSkillLevel)
    {
        var lines = await File.ReadAllLinesAsync(skillsFilePath);
        if (lines.Length == 0)
        {
            throw new InvalidDataException("skills.txt did not contain any rows.");
        }

        var headers = lines[0].Split('\t');
        var maxLevelIndex = Array.FindIndex(headers, header => header.Equals("maxlvl", StringComparison.OrdinalIgnoreCase));
        if (maxLevelIndex < 0)
        {
            throw new InvalidDataException("skills.txt is missing the maxlvl column.");
        }

        var updatedRows = 0;

        for (var i = 1; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i]))
            {
                continue;
            }

            var columns = lines[i].Split('\t');
            if (columns.Length <= maxLevelIndex || string.IsNullOrWhiteSpace(columns[maxLevelIndex]))
            {
                continue;
            }

            columns[maxLevelIndex] = maxSkillLevel.ToString();
            lines[i] = string.Join('\t', columns);
            updatedRows++;
        }

        if (updatedRows == 0)
        {
            throw new InvalidDataException("skills.txt did not contain any rows with maxlvl values.");
        }

        await File.WriteAllLinesAsync(skillsFilePath, lines);
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
}
