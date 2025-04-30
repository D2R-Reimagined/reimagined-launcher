using System;
using System.IO;
using System.Linq;

namespace ReimaginedLauncher.Utilities;

public class SaveFileService
{
    public static bool SaveFilesSafe()
    {
        //Find default drive
        var drive = DriveInfo.GetDrives().FirstOrDefault(d => d.IsReady && d.DriveType == DriveType.Fixed);
        if (drive == null)
        {
            // Handle the case where no fixed drive is found
            Notifications.SendNotification("Failed to find drive info");
            return false;
        }
        
        //Check My Saved Games Folder
        var foundFile = false;
        var mySavedGamesPath = Path.Combine(drive.RootDirectory.FullName, "Users", Environment.UserName, "Saved Games");
        if (Directory.Exists(mySavedGamesPath))
        {
            //Check all ds2 files and see if they are above 7kb in size
            foreach (var saveFile in GetSaveFiles())
            {
                var fileInfo = new FileInfo(saveFile);
                if (fileInfo.Length <= 7000) continue;
                foundFile = true;
                // Handle the case where a save file is found
                Notifications.SendNotification($"Found save file above 7kb - {fileInfo.Name}", "Warning");
            }
        }
        else
        {
            Notifications.SendNotification("Failed to find save files", "Warning");
        }

        return !foundFile;
    }
    
    private static string[] GetSaveFiles()
    {
        //Find default drive
        var drive = DriveInfo.GetDrives().FirstOrDefault(d => d.IsReady && d.DriveType == DriveType.Fixed);
        if (drive == null)
        {
            // Handle the case where no fixed drive is found
            Notifications.SendNotification("Failed to find drive info");
            return [];
        }
        
        //Check My Saved Games Folder
        var mySavedGamesPath = Path.Combine(drive.RootDirectory.FullName, "Users", Environment.UserName, "Saved Games");
        if (Directory.Exists(mySavedGamesPath))
        {
            //Check all ds2 files and see if they are above 7kb in size
            var saveFiles = Directory.GetFiles(mySavedGamesPath, "*.d2s", SearchOption.AllDirectories);
            return saveFiles;
        }
        
        Notifications.SendNotification("Failed to find save files", "Warning");
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
        
        var backupDirectory = MainWindow.Settings.BackupSaveDirectory;
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