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
        if (File.Exists(DefaultInstallPath))
        {
            return DefaultInstallPath;
        }

        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Fixed) continue;
            var possiblePath = Path.Combine(drive.RootDirectory.FullName, @"Program Files (x86)\Diablo II Resurrected\D2R.exe");
            if (File.Exists(possiblePath))
            {
                return possiblePath;
            }
        }

        return null;
    }

    public void LaunchGame()
    {
        if (string.IsNullOrWhiteSpace(_selectedExePath))
        {
            throw new Exception("Executable path not set. Please ensure the game is installed.");
        }

        const string launchParameters = "-mod Merged -txt";
            
#pragma warning disable CA1416
        Process.Start(new ProcessStartInfo(_selectedExePath)
        {
            UseShellExecute = true,
            Arguments = launchParameters
        });
#pragma warning restore CA1416
    }
}