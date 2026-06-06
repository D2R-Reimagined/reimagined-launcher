using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ReimaginedLauncher.Utilities;

public class SaveFileService
{
    public static string? ResolveDirectoryCaseInsensitive(string parentDirectory, string targetName)
    {
        if (!Directory.Exists(parentDirectory))
        {
            return null;
        }

        var exactPath = Path.Combine(parentDirectory, targetName);
        if (Directory.Exists(exactPath))
        {
            return exactPath;
        }

        var actualName = Directory
            .EnumerateDirectories(parentDirectory)
            .Select(Path.GetFileName)
            .FirstOrDefault(d => d is not null && d.Equals(targetName, StringComparison.OrdinalIgnoreCase));

        return actualName == null ? null : Path.Combine(parentDirectory, actualName);
    }

    public static string GetSavedGamesPath()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var myDocuments = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        var candidates = new List<string>();

        // 1. Linux: Steam Proton prefix path (prioritize on Linux)
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            var steamCompatPath = Path.Combine(
                userProfile,
                ".local", "share", "Steam", "steamapps", "compatdata", "2536520", "pfx",
                "drive_c", "users", "steamuser", "Saved Games");
            candidates.Add(steamCompatPath);
        }

        // 2. Linux: Wine default path
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            var winePath = Path.Combine(
                userProfile,
                ".wine", "drive_c", "users", "steamuser", "Saved Games");
            candidates.Add(winePath);
        }

        // 3. Try the one derived from MyDocuments (handles OneDrive better as MyDocuments is often redirected to OneDrive)
        if (!string.IsNullOrWhiteSpace(myDocuments))
        {
            var parent = Path.GetDirectoryName(myDocuments);
            if (!string.IsNullOrWhiteSpace(parent))
            {
                candidates.Add(Path.Combine(parent, "Saved Games"));
            }
        }

        // 4. Try the standard one under UserProfile
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            candidates.Add(Path.Combine(userProfile, "Saved Games"));
        }

        // 5. Try inside MyDocuments (some systems might have it there)
        if (!string.IsNullOrWhiteSpace(myDocuments))
        {
            candidates.Add(Path.Combine(myDocuments, "Saved Games"));
        }

        // Return the first candidate that has a Diablo II Resurrected folder
        foreach (var candidate in candidates)
        {
            var d2rPath = ResolveDirectoryCaseInsensitive(candidate, "Diablo II Resurrected");
            if (d2rPath != null)
            {
                return candidate;
            }
        }

        // Fallback
        return candidates.FirstOrDefault(Directory.Exists)
               ?? candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c))
               ?? Path.Combine(userProfile, "Saved Games");
    }

    public static bool SaveFilesSafe()
    {
        var mySavedGamesPath = GetSavedGamesPath();
        if (string.IsNullOrEmpty(mySavedGamesPath) || !Directory.Exists(mySavedGamesPath))
        {
            Notifications.SendNotification("Failed to find save files", "Warning");
            return true; // Assume safe if not found? Or false? Current code returned true if no file found.
        }

        //Check all ds2 files and see if they are above 7kb in size
        var foundFile = false;
        foreach (var saveFile in GetSaveFiles())
        {
            var fileInfo = new FileInfo(saveFile);
            if (fileInfo.Length <= 7000) continue;
            foundFile = true;
            Notifications.SendNotification($"Found save file above 7kb - {fileInfo.Name}", "Warning");
        }

        return !foundFile;
    }
    
    private static string[] GetSaveFiles()
    {
        var mySavedGamesPath = GetSavedGamesPath();
        if (Directory.Exists(mySavedGamesPath))
        {
            var saveFiles = Directory.GetFiles(mySavedGamesPath, "*.d2s", SearchOption.AllDirectories);
            return saveFiles;
        }
        
        return [];
    }

    public static void MoveSaveFilesToBackupDirectory()
    {
        var files = GetSaveFiles();
        if (files.Length == 0)
        {
            Notifications.SendNotification("No save files found to move.");
            return;
        }
        
        var backupDirectory = MainWindow.Settings.CurrentProfile.BackupSaveDirectory;
        if (string.IsNullOrEmpty(backupDirectory))
        {
            Notifications.SendNotification("Backup directory not set.");
            return;
        }

        if (Directory.Exists(backupDirectory)) return;
        
        Directory.CreateDirectory(backupDirectory);
        Notifications.SendNotification($"Backup directory created: {backupDirectory}");
        // move files
        foreach (var file in files)
        {
            var fileName = Path.GetFileName(file);
            var destFile = Path.Combine(backupDirectory, fileName);
            File.Copy(file, destFile);
            Notifications.SendNotification($"Moved {fileName} to backup directory.");
        }
    }
}