using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ReimaginedLauncher.Utilities;

public class GameLauncherService
{
    private static readonly string[] DefaultInstallPaths =
    [
        @"C:\Program Files (x86)\Diablo II Resurrected\D2R.exe",
        @"C:\Program Files (x86)\Steam\steamapps\common\Diablo II Resurrected\D2R.exe"
    ];
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

        var settings = MainWindow.Settings;
        var currentProfile = settings.CurrentProfile;

        // If current profile already has a valid directory, just normalise and return
        if (InstallDirectoryValidator.IsValidInstallDirectory(currentProfile.InstallDirectory))
        {
            currentProfile.InstallDirectory =
                InstallDirectoryValidator.NormalizeInstallDirectory(currentProfile.InstallDirectory);
            currentProfile.IsInstallDirectoryValidated = true;

            if (currentProfile.Type == InstallationType.BattleNet)
            {
                var detectedType = DetectInstallationType(currentProfile.InstallDirectory!);
                if (detectedType != InstallationType.BattleNet)
                {
                    currentProfile.Type = detectedType;
                    if (detectedType == InstallationType.Steam)
                        currentProfile.SteamDirectory = FindSteamExecutable(currentProfile.InstallDirectory);
                }
            }

            // Still check the other default path to populate the sibling profile
            PopulateDefaultPaths(settings);
            _ = SettingsManager.SaveAsync(settings);

            onComplete?.Invoke();
            return;
        }

        IsDetecting = true;
        try
        {
            // ── Phase 1: check both well-known default locations ──
            var foundPaths = await Task.Run(() => CheckDefaultPaths(token), token);
            if (token.IsCancellationRequested) return;

            bool anyFound = false;

            if (foundPaths.BattleNetPath is not null)
            {
                var bnetProfile = GetProfileByType(settings, InstallationType.BattleNet);
                if (!bnetProfile.IsInstallDirectoryValidated)
                {
                    bnetProfile.InstallDirectory = InstallDirectoryValidator.NormalizeInstallDirectory(foundPaths.BattleNetPath);
                    bnetProfile.IsInstallDirectoryValidated = true;
                    anyFound = true;
                }
            }

            if (foundPaths.SteamPath is not null)
            {
                var steamProfile = GetProfileByType(settings, InstallationType.Steam);
                if (!steamProfile.IsInstallDirectoryValidated)
                {
                    steamProfile.InstallDirectory = InstallDirectoryValidator.NormalizeInstallDirectory(foundPaths.SteamPath);
                    steamProfile.IsInstallDirectoryValidated = true;
                    steamProfile.SteamDirectory = FindSteamExecutable(steamProfile.InstallDirectory);
                    anyFound = true;
                }
            }

            // If the current profile was populated by the default-path check, we're done
            if (currentProfile.IsInstallDirectoryValidated)
            {
                if (anyFound)
                {
                    NotifyDetectionResults(foundPaths);
                    _ = SettingsManager.SaveAsync(settings);
                }
                onComplete?.Invoke();
                return;
            }

            // ── Phase 2: fall back to full-disk search ──
            if (!anyFound)
            {
                var detectedExecutablePath = await Task.Run(() => FindD2RExecutable(token), token);
                if (token.IsCancellationRequested) return;

                if (!string.IsNullOrEmpty(detectedExecutablePath))
                {
                    var normalised = InstallDirectoryValidator.NormalizeInstallDirectory(detectedExecutablePath);
                    var detectedType = DetectInstallationType(normalised!);
                    var targetProfile = GetProfileByType(settings, detectedType);

                    targetProfile.InstallDirectory = normalised;
                    targetProfile.IsInstallDirectoryValidated = true;
                    if (detectedType == InstallationType.Steam)
                        targetProfile.SteamDirectory = FindSteamExecutable(targetProfile.InstallDirectory);

                    // Make sure the current profile points to the one we just found
                    settings.SelectedProfileIndex = settings.Profiles.IndexOf(targetProfile);

                    _ = SettingsManager.SaveAsync(settings);
                }
                else
                {
                    currentProfile.IsInstallDirectoryValidated = false;
                    Notifications.SendNotification("D2R.exe not found");
                }
            }
            else
            {
                // Default paths found something — select the first validated non-D2RMM profile
                if (!currentProfile.IsInstallDirectoryValidated)
                {
                    for (int i = 0; i < settings.Profiles.Count; i++)
                    {
                        if (settings.Profiles[i].IsInstallDirectoryValidated && settings.Profiles[i].Type != InstallationType.D2RMM)
                        {
                            settings.SelectedProfileIndex = i;
                            break;
                        }
                    }
                }
                NotifyDetectionResults(foundPaths);
                _ = SettingsManager.SaveAsync(settings);
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

    /// <summary>
    /// Checks the two well-known default D2R install locations.
    /// </summary>
    private static (string? BattleNetPath, string? SteamPath) CheckDefaultPaths(CancellationToken token)
    {
        string? bnet = null;
        string? steam = null;

        foreach (var path in DefaultInstallPaths)
        {
            if (token.IsCancellationRequested) break;
            if (!File.Exists(path)) continue;

            var dir = Path.GetDirectoryName(path);
            if (path.Contains("steamapps\\common", StringComparison.OrdinalIgnoreCase))
                steam = dir;
            else
                bnet = dir;
        }

        return (bnet, steam);
    }

    /// <summary>
    /// Ensures both B.Net and Steam profiles are populated from default paths when possible.
    /// Called when the current profile is already valid, to discover the other installation.
    /// </summary>
    private void PopulateDefaultPaths(AppSettings settings)
    {
        foreach (var path in DefaultInstallPaths)
        {
            if (!File.Exists(path)) continue;
            var dir = InstallDirectoryValidator.NormalizeInstallDirectory(Path.GetDirectoryName(path));
            var type = DetectInstallationType(dir!);
            var profile = GetProfileByType(settings, type);
            if (profile.IsInstallDirectoryValidated) continue;

            profile.InstallDirectory = dir;
            profile.IsInstallDirectoryValidated = true;
            if (type == InstallationType.Steam)
                profile.SteamDirectory = FindSteamExecutable(profile.InstallDirectory);
        }
    }

    private static InstallationProfile GetProfileByType(AppSettings settings, InstallationType type)
    {
        // Ensure profiles exist
        _ = settings.CurrentProfile;
        foreach (var p in settings.Profiles)
        {
            if (p.Type == type) return p;
        }
        // Should not happen with default 3 profiles, but just in case
        var newProfile = new InstallationProfile { Type = type };
        settings.Profiles.Add(newProfile);
        return newProfile;
    }

    private static void NotifyDetectionResults((string? BattleNetPath, string? SteamPath) found)
    {
        if (found.BattleNetPath is not null && found.SteamPath is not null)
        {
            Notifications.SendNotification(
                "Dual installation detected",
                "Both Battle.Net and Steam installations of D2R were found. Use the dropdown to switch between them.");
        }
        else if (found.BattleNetPath is not null)
        {
            Notifications.SendNotification("Battle.Net installation detected", "D2R found in the default Battle.Net location.");
        }
        else if (found.SteamPath is not null)
        {
            Notifications.SendNotification("Steam installation detected", "D2R found in the default Steam location.");
        }
    }

    public InstallationType DetectInstallationType(string path)
    {
        if (path.Contains("steamapps\\common", StringComparison.OrdinalIgnoreCase))
        {
            return InstallationType.Steam;
        }
        return InstallationType.BattleNet;
    }

    public string? FindSteamExecutable(string? d2rDir = null)
    {
        var targetD2rDir = d2rDir ?? MainWindow.Settings.InstallDirectory;
        if (!string.IsNullOrEmpty(targetD2rDir) && targetD2rDir.Contains("steamapps\\common", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var steamDir = Path.GetFullPath(Path.Combine(targetD2rDir, "..", "..", ".."));
                var steamExe = Path.Combine(steamDir, "Steam.exe");
                if (File.Exists(steamExe)) return steamExe;
            }
            catch { /* ignore path errors */ }
        }

        var defaultPath = @"C:\Program Files (x86)\Steam\steam.exe";
        if (File.Exists(defaultPath)) return defaultPath;

        return null;
    }

    private void CopyDirectory(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);
        foreach (var file in Directory.GetFiles(sourceDir))
        {
            File.Copy(file, Path.Combine(targetDir, Path.GetFileName(file)), true);
        }
        foreach (var directory in Directory.GetDirectories(sourceDir))
        {
            CopyDirectory(directory, Path.Combine(targetDir, Path.GetFileName(directory)));
        }
    }

    public void CancelDetection()
    {
        _detectionCts?.Cancel();
        IsDetecting = false;
    }

    private string? FindD2RExecutable(CancellationToken token)
    {
        // Check the default installation paths first
        foreach (var path in DefaultInstallPaths)
        {
            if (File.Exists(path))
            {
                return path;
            }
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

        if (MainWindow.Settings.ForceDesktop)
        {
            launchParameters.Add("-forcedesktop");
        }

        if (MainWindow.Settings.NoSound)
        {
            launchParameters.Add("-nosound");
        }

        return string.Join(" ", launchParameters);
    }

    public string BuildLaunchCommand(string? launchParamOverride = null, string? gamePathOverride = null)
    {
        var profile = MainWindow.Settings.CurrentProfile;
        if (profile.Type == InstallationType.D2RMM)
        {
            return "D2RMM Install: No launch command. Clicking install mod will install reimagined.mpq into D2RMM/mods.";
        }

        var launchParameters = string.IsNullOrWhiteSpace(launchParamOverride)
            ? LaunchParameters
            : launchParamOverride;

        if (profile.Type == InstallationType.Steam)
        {
            var steamPath = profile.SteamDirectory ?? @"C:\Program Files (x86)\Steam\steam.exe";
            return $"\"{steamPath}\" -silent -applaunch 2536520 {launchParameters}";
        }

        var executablePath = ResolveExecutablePath(gamePathOverride) ?? "D2R.exe";
        return $"\"{executablePath}\" {launchParameters}";
    }

    public void LaunchGame(string? launchParamOverride = null, string? gamePathOverride = null)
    {
        var profile = MainWindow.Settings.CurrentProfile;
        
        // D2RMM handled separately in LaunchView
        if (profile.Type == InstallationType.D2RMM) return;

        if (!string.IsNullOrWhiteSpace(gamePathOverride))
        {
            GamePathOverride = gamePathOverride;
        }

        var launchParameters = string.IsNullOrWhiteSpace(launchParamOverride)
            ? LaunchParameters
            : launchParamOverride;

        string executablePath;
        string finalArgs;

        if (profile.Type == InstallationType.Steam)
        {
            executablePath = profile.SteamDirectory ?? @"C:\Program Files (x86)\Steam\steam.exe";
            finalArgs = $"-silent -applaunch 2536520 {launchParameters}";
            
            if (!File.Exists(executablePath))
            {
                Notifications.SendNotification($"Steam.exe not found at {executablePath}. Please locate it in the Install Directory section.");
                return;
            }
        }
        else
        {
            executablePath = ResolveExecutablePath(GamePathOverride) ?? string.Empty;
            finalArgs = launchParameters;
            
            if (string.IsNullOrWhiteSpace(executablePath))
            {
                Notifications.SendNotification("No valid game path found. Please set the game path in settings.");
                return;
            }
        }

        LaunchDiagnostics.Log($"Resolved executable path: {executablePath}");
        LaunchDiagnostics.Log($"Launch parameters: {finalArgs}");
            
        if (!OperatingSystem.IsWindows())
        {
            Notifications.SendNotification("This only works on Windows");
            return;
        }

        var processStartInfo = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = true,
            Arguments = finalArgs
        };

        try
        {
            var process = Process.Start(processStartInfo);
            if (process == null)
            {
                LaunchDiagnostics.Log("Process.Start returned null.");
                Notifications.SendNotification($"Failed to start {Path.GetFileName(executablePath)}.", "Warning");
                return;
            }

            LaunchDiagnostics.Log($"Process started with PID {process.Id}.");
        }
        catch (Win32Exception ex)
        {
            LaunchDiagnostics.LogException("Process.Start failed", ex);
            Notifications.SendNotification($"Failed to start {Path.GetFileName(executablePath)}: {ex.Message}", "Warning");
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
