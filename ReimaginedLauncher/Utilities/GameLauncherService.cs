using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ReimaginedLauncher.Utilities;

public class GameLauncherService
{
    private const string? DefaultInstallPath = @"C:\Program Files (x86)\Diablo II Resurrected\D2R.exe";
    private CancellationTokenSource? _detectionCts;
    public bool IsDetecting { get; private set; }
    public string? GamePathOverride { get; set; } = string.Empty;
    public string LaunchParameters => BuildLaunchParameters();
    
    public string? InstallDirectory
    {
        get => InstallDirectoryValidator.NormalizeInstallDirectory(MainWindow.Settings.InstallDirectory) ?? string.Empty;
        set => throw new NotImplementedException();
    }

    public GameLauncherService()
    {
    }

    public async Task CheckForD2RExecutableAsync(Action? onComplete = null)
    {
        _detectionCts?.Cancel();
        _detectionCts = new CancellationTokenSource();
        var token = _detectionCts.Token;

        if (InstallDirectoryValidator.IsValidInstallDirectory(MainWindow.Settings.InstallDirectory))
        {
            MainWindow.Settings.InstallDirectory =
                InstallDirectoryValidator.NormalizeInstallDirectory(MainWindow.Settings.InstallDirectory);
            MainWindow.Settings.IsInstallDirectoryValidated = true;
            onComplete?.Invoke();
            return;
        }

        IsDetecting = true;
        try
        {
            var detectedExecutablePath = await Task.Run(() => FindD2RExecutable(token), token);

            if (token.IsCancellationRequested) return;

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
                Notifications.SendNotification("D2R.exe not found");
            }
        }
        catch (OperationCanceledException)
        {
            // Search was cancelled, do nothing
        }
        finally
        {
            IsDetecting = false;
            onComplete?.Invoke();
        }
    }

    public void CancelDetection()
    {
        _detectionCts?.Cancel();
        IsDetecting = false;
    }

    private string? FindD2RExecutable(CancellationToken token)
    {
        // Check the default installation path first
        if (File.Exists(DefaultInstallPath))
        {
            return DefaultInstallPath;
        }

        // Iterate through all fixed drives
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (token.IsCancellationRequested) return null;
            if (drive.DriveType != DriveType.Fixed) continue;

            try
            {
                // Start a recursive search in the root directory of each fixed drive
                var executablePath = FindFileRecursively(drive.RootDirectory.FullName, "D2R.exe", token);
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
    private string? FindFileRecursively(string rootDirectory, string fileName, CancellationToken token)
    {
        if (token.IsCancellationRequested) return null;

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
                if (token.IsCancellationRequested) return null;

                var result = FindFileRecursively(directory, fileName, token);
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


    public string BuildLaunchParameters()
    {
        var launchParameters = new List<string>
        {
            "-mod",
            "Reimagined",
            "-txt"
        };

        if (MainWindow.Settings.EnableRespec)
        {
            launchParameters.Add("-enablerespec");
        }

        if (MainWindow.Settings.ResetOfflineMaps)
        {
            launchParameters.Add("-resetofflinemaps");
        }

        if (MainWindow.Settings.PlayersCount is >= 2 and <= 8)
        {
            launchParameters.Add("-players");
            launchParameters.Add(MainWindow.Settings.PlayersCount.Value.ToString());
        }

        if (MainWindow.Settings.NoRumble)
        {
            launchParameters.Add("-norumble");
        }

        if (MainWindow.Settings.NoSound)
        {
            launchParameters.Add("-nosound");
        }

        return string.Join(" ", launchParameters);
    }

    public string BuildLaunchCommand(string? launchParamOverride = null, string? gamePathOverride = null)
    {
        var launchParameters = string.IsNullOrWhiteSpace(launchParamOverride)
            ? LaunchParameters
            : launchParamOverride;
        var executablePath = ResolveExecutablePath(gamePathOverride) ?? "D2R.exe";

        return $"\"{executablePath}\" {launchParameters}";
    }

    public void LaunchGame(string? launchParamOverride = null, string? gamePathOverride = null)
    {
        if (!string.IsNullOrWhiteSpace(gamePathOverride))
        {
            GamePathOverride = gamePathOverride;
        }

        var executablePath = ResolveExecutablePath(GamePathOverride);
        LaunchDiagnostics.Log($"Resolved executable path: {executablePath ?? "<null>"}");
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            Notifications.SendNotification("No valid game path found. Please set the game path in settings.");
            return;
        }

        var launchParameters = string.IsNullOrWhiteSpace(launchParamOverride)
            ? LaunchParameters
            : launchParamOverride;
        LaunchDiagnostics.Log($"Launch parameters: {launchParameters}");
            
        if (!OperatingSystem.IsWindows())
        {
            Notifications.SendNotification("This only works on Windows");
            return;
        }

        var processStartInfo = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = true,
            Arguments = launchParameters
        };

        try
        {
            var process = Process.Start(processStartInfo);
            if (process == null)
            {
                LaunchDiagnostics.Log("Process.Start returned null.");
                Notifications.SendNotification("Failed to start D2R.exe.", "Warning");
                return;
            }

            LaunchDiagnostics.Log($"Process started with PID {process.Id}.");
        }
        catch (Win32Exception ex)
        {
            LaunchDiagnostics.LogException("Process.Start failed", ex);
            Notifications.SendNotification($"Failed to start D2R.exe: {ex.Message}", "Warning");
        }
    }

    private string? ResolveExecutablePath(string? gamePathOverride = null)
    {
        if (!string.IsNullOrWhiteSpace(gamePathOverride))
        {
            return Path.Combine(gamePathOverride, "D2R.exe");
        }

        return InstallDirectoryValidator.GetExecutablePath(MainWindow.Settings.InstallDirectory);
    }
}
