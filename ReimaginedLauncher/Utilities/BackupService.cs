using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Threading;
using ReimaginedLauncher.Generators;

namespace ReimaginedLauncher.Utilities;

public sealed class BackupEntry
{
    public required string Name { get; init; }
    public required string DirectoryPath { get; init; }
    public required DateTime CreatedAt { get; init; }
    public int FileCount { get; init; }
    public string Summary => $"{CreatedAt:g} · {FileCount} files";
}

public static class BackupService
{
    private static readonly DispatcherTimer BackupTimer = new();
    private static bool _isCreatingBackup;

    static BackupService()
    {
        BackupTimer.Tick += async (_, _) => await CreateBackupAsync();
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

        return Directory.GetDirectories(backupRoot, "*-backup", SearchOption.TopDirectoryOnly)
            .Select(directoryPath =>
            {
                var directoryInfo = new DirectoryInfo(directoryPath);
                return new BackupEntry
                {
                    Name = directoryInfo.Name,
                    DirectoryPath = directoryPath,
                    CreatedAt = directoryInfo.CreationTime,
                    FileCount = Directory.GetFiles(directoryPath, "*", SearchOption.AllDirectories).Length
                };
            })
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
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(userProfile))
        {
            return string.Empty;
        }

        return Path.Combine(userProfile, "Saved Games", "Diablo II Resurrected", "mods", trimmedSavePath);
    }

    public static async Task<bool> CreateBackupAsync()
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
            var backupName = $"{DateTime.Now:yyyyMMdd-HHmmss}-backup";
            var backupDirectory = Path.Combine(backupRoot, backupName);
            Directory.CreateDirectory(backupDirectory);

            await CopyDirectoryAsync(sourceDirectory, backupDirectory, overwrite: true);
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
        if (backupEntry == null || !Directory.Exists(backupEntry.DirectoryPath))
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
            await CopyDirectoryAsync(backupEntry.DirectoryPath, destinationDirectory, overwrite: true);
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
        return MainWindow.Settings.BackupIntervalMinutes > 0
               && MainWindow.Settings.BackupAmount > 0
               && !string.IsNullOrWhiteSpace(MainWindow.Settings.BackupSaveDirectory)
               && !string.IsNullOrWhiteSpace(GetResolvedSaveDirectory());
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
            Directory.Delete(backup.DirectoryPath, recursive: true);
        }
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
            await using var destinationStream = File.Open(destinationFilePath, overwrite ? FileMode.Create : FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await sourceStream.CopyToAsync(destinationStream);
        }
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
