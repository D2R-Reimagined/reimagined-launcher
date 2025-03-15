using System.Diagnostics;

namespace ReimaginedLauncherMaui.Services;

public class GameLauncherService
{
    private const string ExePathPreferenceKey = "D2RExePath";
    private const string? DefaultInstallPath = @"C:\Program Files (x86)\Diablo II Resurrected\D2R.exe";
    private string? _selectedExePath;

    public GameLauncherService()
    {
        CheckForD2RExecutable();
    }

    private void CheckForD2RExecutable()
    {
        if (Preferences.ContainsKey(ExePathPreferenceKey))
        {
            _selectedExePath = Preferences.Get(ExePathPreferenceKey, null);
        }
        else
        {
            _selectedExePath = FindD2RExecutable();

            if (!string.IsNullOrEmpty(_selectedExePath))
            {
                Preferences.Set(ExePathPreferenceKey, _selectedExePath);
            }
            else
            {
                // Handle the case where the executable wasn't found
                throw new Exception("D2R.exe not found");
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


    public void LaunchGame()
    {
        if (string.IsNullOrWhiteSpace(_selectedExePath))
        {
            throw new Exception("Executable path not set. Please ensure the game is installed.");
        }

        const string launchParameters = "-mod Reimagined -txt";
            
#pragma warning disable CA1416
        Process.Start(new ProcessStartInfo(_selectedExePath)
        {
            UseShellExecute = true,
            Arguments = launchParameters
        });
#pragma warning restore CA1416
    }
}