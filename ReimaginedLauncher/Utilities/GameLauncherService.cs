using System;
using System.Diagnostics;
using System.IO;

namespace ReimaginedLauncher.Utilities;

public class GameLauncherService
{
    private const string? DefaultInstallPath = @"C:\Program Files (x86)\Diablo II Resurrected\D2R.exe";
    public string? GamePathOverride { get; set; } = string.Empty;
    public string LaunchParameters = "-mod Reimagined -txt";
    
    public string? InstallDirectory
    {
        get => InstallDirectoryValidator.NormalizeInstallDirectory(MainWindow.Settings.InstallDirectory) ?? string.Empty;
        set => throw new NotImplementedException();
    }

    public GameLauncherService()
    {
#if OS_WINDOWS
        CheckForD2RExecutable();
#endif
    }

    private void CheckForD2RExecutable()
    {
        if (InstallDirectoryValidator.IsValidInstallDirectory(MainWindow.Settings.InstallDirectory))
        {
            MainWindow.Settings.InstallDirectory =
                InstallDirectoryValidator.NormalizeInstallDirectory(MainWindow.Settings.InstallDirectory);
            MainWindow.Settings.IsInstallDirectoryValidated = true;
        }
        else
        {
            var detectedExecutablePath = FindD2RExecutable();

            if (!string.IsNullOrEmpty(detectedExecutablePath))
            {
                MainWindow.Settings.InstallDirectory =
                    InstallDirectoryValidator.NormalizeInstallDirectory(detectedExecutablePath);
                MainWindow.Settings.IsInstallDirectoryValidated = true;
                _ = SettingsManager.SaveAsync(MainWindow.Settings);
            }
            else
            {
                MainWindow.Settings.IsInstallDirectoryValidated = false;
                // Handle the case where the executable wasn't found
                Notifications.SendNotification("D2R.exe not found");
            }
        }
    }

    private string? FindD2RExecutable()
    {
        // Check the default installation path first
        if (File.Exists(DefaultInstallPath))
        {
            return DefaultInstallPath;
        }

        // Iterate through all fixed drives
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Fixed) continue;

            try
            {
                // Start a recursive search in the root directory of each fixed drive
                var executablePath = FindFileRecursively(drive.RootDirectory.FullName, "D2R.exe");
                if (!string.IsNullOrEmpty(executablePath))
                {
                    return executablePath;
                }
            }
            catch (UnauthorizedAccessException)
            {
                // Handle unauthorized access (skip to the next drive)
                continue;
            }
        }

        // Return null if not found
        return null;
    }

    // Helper method to search for a file recursively
    private string? FindFileRecursively(string rootDirectory, string fileName)
    {
        try
        {
            // Search in the current directory for the file
            var files = Directory.GetFiles(rootDirectory, fileName, SearchOption.TopDirectoryOnly);
            if (files.Length > 0)
            {
                return files[0]; // Return the first found instance
            }

            // Recurse into subdirectories
            foreach (var directory in Directory.GetDirectories(rootDirectory))
            {
                var result = FindFileRecursively(directory, fileName);
                if (result != null)
                {
                    return result;
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            // Skip directories we do not have permission to access
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            // Skip directories that may have been deleted or moved
            return null;
        }

        // Return null if not found
        return null;
    }


    public void LaunchGame(string? launchParamOverride = null, string? gamePathOverride = null)
    {
        if (!string.IsNullOrWhiteSpace(launchParamOverride))
        {
            LaunchParameters = launchParamOverride;
        }

        if (!string.IsNullOrWhiteSpace(gamePathOverride))
        {
            GamePathOverride = gamePathOverride;
        }

        var executablePath = InstallDirectoryValidator.GetExecutablePath(MainWindow.Settings.InstallDirectory);

        // Validate the selected executable path and game path override
        if (string.IsNullOrWhiteSpace(executablePath) && string.IsNullOrWhiteSpace(GamePathOverride))
        {
            Notifications.SendNotification("No valid game path found. Please set the game path in settings.");
            return;
        }
            
#if OS_WINDOWS
        Process.Start(new ProcessStartInfo((!string.IsNullOrWhiteSpace(GamePathOverride) ? GamePathOverride + "\\D2R.exe" : executablePath) ?? throw new InvalidOperationException())
        {
            UseShellExecute = true,
            Arguments = LaunchParameters
        });
#else
        Notifications.SendNotification("This only works on Windows");
#endif
    }
}
