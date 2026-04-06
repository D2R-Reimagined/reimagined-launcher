using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Threading;
using ReimaginedLauncher.Generators;

namespace ReimaginedLauncher.Utilities;

public sealed class BackupEntry
{
    public required string Name { get; init; }
    public required string BackupPath { get; init; }
    public required DateTime CreatedAt { get; init; }
    public int FileCount { get; init; }
    public bool IsArchive { get; init; }
    public long SizeBytes { get; init; }
    public string Summary => $"{CreatedAt:g} · {FileCount} files · {FormatSize(SizeBytes)} · {(IsArchive ? "zip" : "folder")}";

    private static string FormatSize(long sizeBytes)
    {
        string[] suffixes = ["B", "KB", "MB", "GB", "TB"];
        var size = (double)Math.Max(sizeBytes, 0);
        var suffixIndex = 0;

        while (size >= 1024 && suffixIndex < suffixes.Length - 1)
        {
            size /= 1024;
            suffixIndex++;
        }

        return suffixIndex == 0
            ? $"{size:0} {suffixes[suffixIndex]}"
            : $"{size:0.##} {suffixes[suffixIndex]}";
    }
}

public static class BackupService
{
    private const int DefaultBackupIntervalMinutes = 60;
    private const int DefaultBackupAmount = 10;
    private const string DefaultBackupDirectoryName = "ReimaginedLauncherBackups";
    private static readonly DispatcherTimer BackupTimer = new();
    private static bool _isCreatingBackup;

    static BackupService()
    {
        BackupTimer.Tick += async (_, _) => await CreateBackupAsync("scheduled");
    }

    public static bool ApplyDefaultSettings()
    {
        var settingsChanged = false;

        if (MainWindow.Settings.BackupIntervalMinutes <= 0)
        {
            MainWindow.Settings.BackupIntervalMinutes = DefaultBackupIntervalMinutes;
            settingsChanged = true;
        }

        if (MainWindow.Settings.BackupAmount <= 0)
        {
            MainWindow.Settings.BackupAmount = DefaultBackupAmount;
            settingsChanged = true;
        }

        if (string.IsNullOrWhiteSpace(MainWindow.Settings.BackupSaveDirectory))
        {
            var defaultBackupDirectory = GetDefaultBackupDirectory();
            if (!string.IsNullOrWhiteSpace(defaultBackupDirectory))
            {
                MainWindow.Settings.BackupSaveDirectory = defaultBackupDirectory;
                settingsChanged = true;
            }
        }

        return settingsChanged;
    }

    public static void UpdateSchedule()
    {
        BackupTimer.Stop();
        TrimBackups();

        if (!CanRunAutomaticBackups())
        {
            return;
        }

        BackupTimer.Interval = TimeSpan.FromMinutes(MainWindow.Settings.BackupIntervalMinutes);
        BackupTimer.Start();
    }

    public static IReadOnlyList<BackupEntry> GetBackups()
    {
        var backupRoot = MainWindow.Settings.BackupSaveDirectory;
        if (string.IsNullOrWhiteSpace(backupRoot) || !Directory.Exists(backupRoot))
        {
            return [];
        }

        var directoryBackups = Directory.GetDirectories(backupRoot, "*-backup", SearchOption.TopDirectoryOnly)
            .Select(directoryPath =>
            {
                var directoryInfo = new DirectoryInfo(directoryPath);
                return new BackupEntry
                {
                    Name = directoryInfo.Name,
                    BackupPath = directoryPath,
                    CreatedAt = directoryInfo.CreationTime,
                    FileCount = Directory.GetFiles(directoryPath, "*", SearchOption.AllDirectories).Length,
                    SizeBytes = GetDirectorySize(directoryPath),
                    IsArchive = false
                };
            });

        var archiveBackups = Directory.GetFiles(backupRoot, "*-backup.zip", SearchOption.TopDirectoryOnly)
            .Select(archivePath =>
            {
                var fileInfo = new FileInfo(archivePath);
                return new BackupEntry
                {
                    Name = Path.GetFileNameWithoutExtension(fileInfo.Name),
                    BackupPath = archivePath,
                    CreatedAt = fileInfo.CreationTime,
                    FileCount = GetArchiveFileCount(archivePath),
                    SizeBytes = fileInfo.Length,
                    IsArchive = true
                };
            });

        return directoryBackups
            .Concat(archiveBackups)
            .OrderByDescending(entry => entry.CreatedAt)
            .ToList();
    }

    public static string GetResolvedSaveDirectory()
    {
        var savePath = GetSavePathFromModInfo();
        if (string.IsNullOrWhiteSpace(savePath))
        {
            return string.Empty;
        }

        var trimmedSavePath = savePath.Trim().Trim('/', '\\');
        var savedGamesPath = SaveFileService.GetSavedGamesPath();
        if (string.IsNullOrWhiteSpace(savedGamesPath))
        {
            return string.Empty;
        }

        return Path.Combine(savedGamesPath, "Diablo II Resurrected", "mods", trimmedSavePath);
    }

    public static Task<bool> CreateLaunchBackupAsync()
    {
        return CreateBackupAsync("launch");
    }

    public static async Task<bool> CreateBackupAsync(string? backupReason = null)
    {
        if (_isCreatingBackup)
        {
            return false;
        }

        var sourceDirectory = GetResolvedSaveDirectory();
        if (string.IsNullOrWhiteSpace(sourceDirectory) || !Directory.Exists(sourceDirectory))
        {
            Notifications.SendNotification("Save directory not found. Check your install and modinfo.json.", "Warning");
            return false;
        }

        if (!Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories).Any())
        {
            Notifications.SendNotification("No save files found to back up.", "Warning");
            return false;
        }

        var backupRoot = MainWindow.Settings.BackupSaveDirectory;
        if (string.IsNullOrWhiteSpace(backupRoot))
        {
            Notifications.SendNotification("Backup directory not set.", "Warning");
            return false;
        }

        if (MainWindow.Settings.BackupAmount <= 0)
        {
            Notifications.SendNotification("Backup amount must be greater than 0.", "Warning");
            return false;
        }

        Directory.CreateDirectory(backupRoot);

        _isCreatingBackup = true;
        try
        {
            var backupName = BuildBackupName(backupReason);
            var backupFilePath = Path.Combine(backupRoot, $"{backupName}.zip");
            
            var excludedDirectories = new List<string>();
            var excludedFiles = new List<string>();
            
            var normalizedSource = NormalizeDirectoryPath(sourceDirectory);
            var normalizedBackupRoot = NormalizeDirectoryPath(backupRoot);

            // If the backup root is a subdirectory of the source, we must exclude the entire root to avoid recursion.
            if (normalizedBackupRoot.StartsWith(normalizedSource + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                excludedDirectories.Add(backupRoot);
            }
            else if (string.Equals(normalizedSource, normalizedBackupRoot, StringComparison.OrdinalIgnoreCase))
            {
                // Exclude existing backups when they are in the same folder.
                foreach (var backup in GetBackups())
                {
                    if (backup.IsArchive)
                    {
                        excludedFiles.Add(backup.BackupPath);
                    }
                    else
                    {
                        excludedDirectories.Add(backup.BackupPath);
                    }
                }
            }

            await CreateZipBackupAsync(
                sourceDirectory,
                backupFilePath,
                excludedDirectories: excludedDirectories,
                excludedFiles: excludedFiles);
            TrimBackups();

            Notifications.SendNotification($"Backup created: {backupName}", "Success");
            return true;
        }
        catch (Exception ex)
        {
            Notifications.SendNotification($"Backup failed: {ex.Message}", "Warning");
            return false;
        }
        finally
        {
            _isCreatingBackup = false;
        }
    }

    public static async Task<bool> RestoreBackupAsync(BackupEntry? backupEntry)
    {
        if (backupEntry == null || !BackupExists(backupEntry))
        {
            Notifications.SendNotification("Select a backup to restore.", "Warning");
            return false;
        }

        var destinationDirectory = GetResolvedSaveDirectory();
        if (string.IsNullOrWhiteSpace(destinationDirectory))
        {
            Notifications.SendNotification("Save directory could not be resolved from modinfo.json.", "Warning");
            return false;
        }

        try
        {
            Directory.CreateDirectory(destinationDirectory);
            List<string> errors;
            if (backupEntry.IsArchive)
            {
                errors = await ExtractArchiveAsync(backupEntry.BackupPath, destinationDirectory);
            }
            else
            {
                errors = await CopyDirectoryAsync(backupEntry.BackupPath, destinationDirectory, overwrite: true);
            }

            if (errors.Count > 0)
            {
                var errorSummary = string.Join(Environment.NewLine, errors.Take(3));
                if (errors.Count > 3)
                {
                    errorSummary += $"{Environment.NewLine}...and {errors.Count - 3} more files failed.";
                }

                Notifications.SendNotification($"Restored backup with errors:{Environment.NewLine}{errorSummary}", "Warning");
                return true;
            }

            Notifications.SendNotification($"Restored backup: {backupEntry.Name}", "Success");
            return true;
        }
        catch (Exception ex)
        {
            Notifications.SendNotification($"Restore failed: {ex.Message}", "Warning");
            return false;
        }
    }

    public static void EnforceBackupLimit()
    {
        TrimBackups();
    }

    private static bool CanRunAutomaticBackups()
    {
        return MainWindow.Settings.AutomaticBackupsEnabled
               && MainWindow.Settings.BackupIntervalMinutes > 0
               && MainWindow.Settings.BackupAmount > 0
               && !string.IsNullOrWhiteSpace(MainWindow.Settings.BackupSaveDirectory)
               && !string.IsNullOrWhiteSpace(GetResolvedSaveDirectory());
    }

    private static string BuildBackupName(string? backupReason)
    {
        var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");

        return backupReason switch
        {
            "launch" => $"{timestamp}-launch-backup",
            "scheduled" => $"{timestamp}-scheduled-backup",
            _ => $"{timestamp}-backup"
        };
    }

    private static string GetDefaultBackupDirectory()
    {
        var savedGamesPath = SaveFileService.GetSavedGamesPath();
        if (string.IsNullOrWhiteSpace(savedGamesPath))
        {
            return string.Empty;
        }

        return Path.Combine(
            savedGamesPath,
            "Diablo II Resurrected",
            DefaultBackupDirectoryName);
    }

    private static void TrimBackups()
    {
        var maxBackups = MainWindow.Settings.BackupAmount;
        if (maxBackups <= 0)
        {
            return;
        }

        foreach (var backup in GetBackups().Skip(maxBackups))
        {
            DeleteBackup(backup);
        }
    }

    private static bool BackupExists(BackupEntry backupEntry)
    {
        return backupEntry.IsArchive
            ? File.Exists(backupEntry.BackupPath)
            : Directory.Exists(backupEntry.BackupPath);
    }

    private static void DeleteBackup(BackupEntry backupEntry)
    {
        if (backupEntry.IsArchive)
        {
            if (File.Exists(backupEntry.BackupPath))
            {
                File.Delete(backupEntry.BackupPath);
            }

            return;
        }

        if (Directory.Exists(backupEntry.BackupPath))
        {
            Directory.Delete(backupEntry.BackupPath, recursive: true);
        }
    }

    private static async Task<List<string>> CopyDirectoryAsync(
        string sourceDirectory,
        string destinationDirectory,
        bool overwrite,
        IEnumerable<string>? excludedDirectories = null,
        IEnumerable<string>? excludedFiles = null)
    {
        var errors = new List<string>();
        Directory.CreateDirectory(destinationDirectory);
        var excludedDirectoryPaths = excludedDirectories?
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(NormalizeDirectoryPath)
            .ToArray() ?? [];

        var excludedFilePaths = excludedFiles?
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];

        foreach (var filePath in Directory.GetFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var fullPath = Path.GetFullPath(filePath);
            if (excludedFilePaths.Contains(fullPath))
            {
                continue;
            }

            if (excludedDirectoryPaths.Any(excludedDirectory => IsPathWithinDirectory(fullPath, excludedDirectory)))
            {
                continue;
            }

            var relativePath = Path.GetRelativePath(sourceDirectory, filePath);
            var destinationFilePath = Path.Combine(destinationDirectory, relativePath);
            var destinationFolder = Path.GetDirectoryName(destinationFilePath);

            if (!string.IsNullOrWhiteSpace(destinationFolder))
            {
                Directory.CreateDirectory(destinationFolder);
            }

            try
            {
                await using var sourceStream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                await using var destinationStream = File.Open(destinationFilePath, overwrite ? FileMode.Create : FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await sourceStream.CopyToAsync(destinationStream);
            }
            catch (Exception ex)
            {
                var fileName = Path.GetFileName(destinationFilePath);
                var message = ex.Message.Replace(destinationFilePath, fileName, StringComparison.OrdinalIgnoreCase);
                errors.Add($"{destinationFilePath}:{Environment.NewLine}{message}");
            }
        }

        return errors;
    }

    private static Task CreateZipBackupAsync(
        string sourceDirectory,
        string destinationArchivePath,
        IEnumerable<string>? excludedDirectories = null,
        IEnumerable<string>? excludedFiles = null)
    {
        return Task.Run(async () =>
        {
            var excludedDirectoryPaths = excludedDirectories?
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(NormalizeDirectoryPath)
                .ToArray() ?? [];

            var excludedFilePaths = excludedFiles?
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(Path.GetFullPath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];

            var fullDestinationPath = Path.GetFullPath(destinationArchivePath);

            await using var destinationStream = File.Open(destinationArchivePath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            using var archive = new ZipArchive(destinationStream, ZipArchiveMode.Create);

            foreach (var filePath in Directory.GetFiles(sourceDirectory, "*", SearchOption.AllDirectories))
            {
                var fullPath = Path.GetFullPath(filePath);

                if (string.Equals(fullPath, fullDestinationPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (excludedFilePaths.Contains(fullPath))
                {
                    continue;
                }

                // Exclude files that match the backup naming pattern to avoid recursive backups or including other backups.
                if (filePath.EndsWith("-backup.zip", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (excludedDirectoryPaths.Any(excludedDirectory => IsPathWithinDirectory(fullPath, excludedDirectory)))
                {
                    continue;
                }

                var relativePath = Path.GetRelativePath(sourceDirectory, filePath)
                    .Replace(Path.DirectorySeparatorChar, '/');
                var entry = archive.CreateEntry(relativePath, CompressionLevel.Optimal);

                await using var sourceStream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                await using var entryStream = entry.Open();
                await sourceStream.CopyToAsync(entryStream);
            }
        });
    }

    private static Task<List<string>> ExtractArchiveAsync(string archivePath, string destinationDirectory)
    {
        return Task.Run(() =>
        {
            var errors = new List<string>();
            using var archive = ZipFile.OpenRead(archivePath);
            foreach (var entry in archive.Entries.Where(entry => !string.IsNullOrEmpty(entry.Name)))
            {
                var destinationPath = Path.Combine(destinationDirectory, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
                var destinationFolder = Path.GetDirectoryName(destinationPath);

                if (!string.IsNullOrWhiteSpace(destinationFolder))
                {
                    Directory.CreateDirectory(destinationFolder);
                }

                try
                {
                    entry.ExtractToFile(destinationPath, overwrite: true);
                }
                catch (Exception ex)
                {
                    var fileName = Path.GetFileName(destinationPath);
                    var message = ex.Message.Replace(destinationPath, fileName, StringComparison.OrdinalIgnoreCase);
                    errors.Add($"{destinationPath}:{Environment.NewLine}{message}");
                }
            }

            return errors;
        });
    }

    private static int GetArchiveFileCount(string archivePath)
    {
        try
        {
            using var archive = ZipFile.OpenRead(archivePath);
            return archive.Entries.Count(entry => !string.IsNullOrEmpty(entry.Name));
        }
        catch
        {
            return 0;
        }
    }

    private static long GetDirectorySize(string directoryPath)
    {
        try
        {
            return Directory.GetFiles(directoryPath, "*", SearchOption.AllDirectories)
                .Select(filePath => new FileInfo(filePath).Length)
                .Sum();
        }
        catch
        {
            return 0;
        }
    }

    private static string NormalizeDirectoryPath(string path)
    {
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static bool IsPathWithinDirectory(string path, string directoryPath)
    {
        var normalizedPath = Path.GetFullPath(path);
        return normalizedPath.StartsWith(
            directoryPath + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetSavePathFromModInfo()
    {
        var modInfoPath = GetModInfoPath();
        if (string.IsNullOrWhiteSpace(modInfoPath) || !File.Exists(modInfoPath))
        {
            return null;
        }

        try
        {
            var modInfo = JsonSerializer.Deserialize<ModInfo>(
                File.ReadAllText(modInfoPath),
                SerializerOptions.PropertyNameCaseInsensitive);
            return modInfo?.SavePath;
        }
        catch
        {
            return null;
        }
    }

    private static string? GetModInfoPath()
    {
        var installDirectory = MainWindow.Settings.InstallDirectory;
        if (string.IsNullOrWhiteSpace(installDirectory))
        {
            return null;
        }

        var expectedPath = Path.Combine(installDirectory, "mods", "Reimagined", "modinfo.json");
        if (File.Exists(expectedPath))
        {
            return expectedPath;
        }

        var modsDirectory = Path.Combine(installDirectory, "mods");
        if (!Directory.Exists(modsDirectory))
        {
            return null;
        }

        return Directory.GetFiles(modsDirectory, "modinfo.json", SearchOption.AllDirectories).FirstOrDefault();
    }

    private sealed class ModInfo
    {
        public string? SavePath { get; init; }
    }
}
