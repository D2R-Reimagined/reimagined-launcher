using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using D2RReimaginedTools.TextFileParsers;

namespace ReimaginedLauncher.Utilities;

public static class ModTweaksService
{
    private const string ModDirectoryName = "Reimagined";
    private const string ExcelDirectoryName = "excel";
    private const string CleanExcelDirectoryName = "excel_launcher_clean";
    private const string CharStatsFileName = "charstats.txt";
    private const string DifficultyLevelsFileName = "DifficultyLevels.txt";

    public static async Task<bool> PrepareForLaunchAsync()
    {
        var excelDirectory = GetExcelDirectory();
        if (string.IsNullOrWhiteSpace(excelDirectory) || !Directory.Exists(excelDirectory))
        {
            Notifications.SendNotification("Excel folder not found in the Reimagined mod directory.", "Warning");
            return false;
        }

        var cleanExcelDirectory = GetCleanExcelDirectory(excelDirectory);
        var charStatsFilePath = Path.Combine(excelDirectory, CharStatsFileName);
        var difficultyLevelsFilePath = Path.Combine(excelDirectory, DifficultyLevelsFileName);
        if (!File.Exists(charStatsFilePath))
        {
            Notifications.SendNotification("charstats.txt was not found in the Reimagined excel folder.", "Warning");
            return false;
        }

        if (!File.Exists(difficultyLevelsFilePath))
        {
            Notifications.SendNotification("DifficultyLevels.txt was not found in the Reimagined excel folder.", "Warning");
            return false;
        }

        try
        {
            await EnsureCleanExcelCopyAsync(excelDirectory, cleanExcelDirectory);
            await CopyDirectoryAsync(cleanExcelDirectory, excelDirectory, overwrite: true);
            await ApplyCharStatsTweaksAsync(
                charStatsFilePath,
                MainWindow.Settings.SkillPointsPerLevel,
                MainWindow.Settings.AttributesPerLevel);
            await ApplyDifficultyLevelsTweaksAsync(
                difficultyLevelsFilePath,
                MainWindow.Settings.NormalResistPenalty,
                MainWindow.Settings.NightmareResistPenalty,
                MainWindow.Settings.HellResistPenalty);
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
