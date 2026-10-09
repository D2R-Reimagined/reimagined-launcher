using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ReimaginedLauncher.Utilities;

public class GameLauncherService
{
    private const string SteamAppId = "2536520";
    private const string SteamAppsDirName = "steamapps";
    private const string GameExecutableName = "D2R.exe";
    private static readonly string[] DefaultInstallPaths = GetDefaultInstallPaths();
    private CancellationTokenSource? _detectionCts;
    public bool IsDetecting { get; private set; }
    public string? GamePathOverride { get; set; } = string.Empty;
    public string LaunchParameters => BuildLaunchParameters();
    internal SteamGameHandoff? ActiveSteamHandoff { get; private set; }
    
    public string? InstallDirectory
    {
        get => InstallDirectoryValidator.NormalizeInstallDirectory(MainWindow.Settings.CurrentProfile.InstallDirectory) ?? string.Empty;
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
            if (IsSteamLibraryPath(path))
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
        if (IsSteamLibraryPath(path))
        {
            return InstallationType.Steam;
        }
        return InstallationType.BattleNet;
    }

    public string? FindSteamExecutable(string? d2rDir = null)
    {
        var targetD2rDir = d2rDir ?? MainWindow.Settings.CurrentProfile.InstallDirectory;

        if (OperatingSystem.IsLinux())
        {
            return FindLinuxSteamExecutable(targetD2rDir);
        }

        if (!string.IsNullOrEmpty(targetD2rDir) && IsSteamLibraryPath(targetD2rDir))
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

    /// <summary>
    /// Determines whether Steam installation is through Flatpak or native.
    /// </summary>
    /// <returns><c>true</c> if the installation is within the Flatpak sandbox; <c>false</c> otherwise.</returns>
    public static bool IsSteamFlatpakInstall(string? installDirectory = null, string? userHome = null)
    {
        return IsFlatpakInstall(installDirectory)
               || IsFlatpakInstall(GetSteamInstallPath(userHome, installDirectory));
    }

    internal static string? FindLinuxSteamExecutable(string? installDirectory, string? userHome = null,
        Func<string, string?>? findExecutable = null)
    {
        findExecutable ??= FindExecutableOnPath;
        var steamRoot = GetSteamInstallPath(userHome, installDirectory);
        if (IsFlatpakInstall(installDirectory) || IsFlatpakInstall(steamRoot))
            return findExecutable("flatpak");
        return findExecutable("steam") ?? (steamRoot is null ? findExecutable("flatpak") : null);
    }

    internal static bool UsesSteamHandoff(InstallationProfile profile)
        => UsesSteamHandoff(profile, OperatingSystem.IsLinux(), SteamGameHandoff.IsGamingMode,
            File.Exists(Path.Combine(SteamGameHandoff.StateDirectory, "registration.json")));

    internal static bool UsesSteamHandoff(InstallationProfile profile, bool isLinux, bool isGamingMode,
        bool hasRegistration, string? userHome = null)
        => isLinux && profile.Type == InstallationType.Steam
           && !IsSteamFlatpakInstall(profile.InstallDirectory, userHome)
           && (isGamingMode || hasRegistration);

    /// <summary>
    /// Determines whether the specified directory path indicates a Flatpak Steam installation.
    /// </summary>
    /// <param name="d2rDir">The directory path to check.</param>
    /// <returns><c>true</c> if the path is within the Flatpak Steam sandbox; <c>false</c> otherwise.</returns>
    private static bool IsFlatpakInstall(string? d2rDir)
    {
        return !string.IsNullOrWhiteSpace(d2rDir) &&
               (NormalizePathSeparators(d2rDir)
                    .Contains("/.var/app/com.valvesoftware.Steam/", StringComparison.Ordinal)
                || NormalizePathSeparators(SteamGameHandoff.ResolveFileSystemPath(d2rDir))
                    .Contains("/.var/app/com.valvesoftware.Steam/", StringComparison.Ordinal));
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

        if (!OperatingSystem.IsWindows())
        {
            return null;
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
        => BuildLaunchParameters(MainWindow.Settings.CurrentProfile);

    public static string BuildLaunchParameters(InstallationProfile profile)
    {
        var launchParameters = new List<string>
        {
            "-mod",
            ModInstallationPaths.ModName(profile.LaunchExperience),
            "-txt"
        };

        var isOfflineExperience = profile.LaunchExperience == LaunchExperience.Offline;

        if (isOfflineExperience && profile.EnableRespec)
        {
            launchParameters.Add("-enablerespec");
        }

        if (profile.LaunchExperience == LaunchExperience.Ladder ||
            (isOfflineExperience && profile.ResetOfflineMaps))
        {
            launchParameters.Add("-resetofflinemaps");
        }

        if (isOfflineExperience && profile.PlayersCount is >= 2 and <= 8)
        {
            launchParameters.Add("-players");
            launchParameters.Add(profile.PlayersCount.Value.ToString());
        }

        if (profile.NoRumble)
        {
            launchParameters.Add("-norumble");
        }

        if (profile.ForceDesktop)
        {
            launchParameters.Add("-forcedesktop");
        }

        if (profile.WindowedMode)
        {
            launchParameters.Add("-w");
        }

        if (profile.NoSound)
        {
            launchParameters.Add("-nosound");
        }

        if (isOfflineExperience && profile.CustomMapSeedEnabled)
        {
            launchParameters.Add("-seed");
            launchParameters.Add(profile.CustomMapSeed.ToString());
        }

        return string.Join(" ", launchParameters);
    }

    public string BuildLaunchCommand(string? launchParamOverride = null, string? gamePathOverride = null)
    {
        var profile = MainWindow.Settings.CurrentProfile;
        if (profile.Type == InstallationType.D2RMM)
        {
            return "D2RMM Install: No launch command. Clicking install mod will install Reimagined into D2RMM/mods.";
        }

        if (profile.Type == InstallationType.Lutris)
        {
            return BuildLutrisLaunchCommand(profile);
        }

        var launchParameters = string.IsNullOrWhiteSpace(launchParamOverride)
            ? LaunchParameters
            : launchParamOverride;

        if (UsesD2RLoader(profile))
        {
            if (!D2RLoaderService.CanUseOnlineExperience(profile, out var reason))
            {
                return $"D2RLoader unavailable: {reason}";
            }

            if (OperatingSystem.IsLinux() && profile.Type == InstallationType.Steam)
            {
                return IsSteamFlatpakInstall(profile.InstallDirectory)
                    ? "Flatpak Steam is not supported for D2RLoader. Only native Steam installation is supported."
                    : BuildOnlineNativeSteamLaunchCommand(profile, launchParameters);
            }

            var loaderPath = D2RLoaderService.GetLoaderPath(profile.InstallDirectory)!;
            if (OperatingSystem.IsLinux())
            {
                var winePath = FindExecutableOnPath("wine") ?? "wine";
                var winePrefix = FindWinePrefix(loaderPath);
                var prefix = winePrefix is null ? string.Empty : $"WINEPREFIX=\"{winePrefix}\" ";
                return $"{prefix}\"{winePath}\" \"{loaderPath}\" {launchParameters}";
            }

            return $"\"{loaderPath}\" {launchParameters}";
        }

        if (profile.Type == InstallationType.Steam)
        {
            if (UsesSteamHandoff(profile))
                return BuildSteamHandoffPreview(Path.Combine(profile.InstallDirectory ?? "", "D2R.exe"), launchParameters);
            var steamPath = profile.SteamDirectory ?? FindSteamExecutable(profile.InstallDirectory) ?? GetDefaultSteamCommand();
            var steamPrefix = GetSteamArgumentPrefix(steamPath);
            return $"\"{steamPath}\" {steamPrefix}-silent -applaunch {SteamAppId} {launchParameters}";
        }

        var executablePath = ResolveExecutablePath(gamePathOverride) ?? "D2R.exe";
        if (OperatingSystem.IsLinux())
        {
            var winePath = FindExecutableOnPath("wine") ?? "wine";
            var winePrefix = FindWinePrefix(executablePath);
            var prefix = winePrefix is null ? string.Empty : $"WINEPREFIX=\"{winePrefix}\" ";
            return $"{prefix}\"{winePath}\" \"{executablePath}\" {launchParameters}";
        }

        return $"\"{executablePath}\" {launchParameters}";
    }

    /// <summary>
    /// The URI takes no game arguments, so the launch options are written into
    /// the game's Lutris arguments just before this command runs.
    /// </summary>
    internal static string BuildLutrisLaunchCommand(InstallationProfile profile)
    {
        if (profile.LutrisGameId is not { } gameId)
        {
            return "Lutris: no game selected yet. Choose your Diablo II: Resurrected entry in the Installation section.";
        }

        var managedArguments = LutrisArgumentsService.BuildArgs(null, profile);

        return $"env LUTRIS_SKIP_INIT=1 lutris {LutrisService.BuildRunGameUri(gameId)}"
               + Environment.NewLine
               + Environment.NewLine
               + "Set in this game's Lutris arguments before launch: "
               + (managedArguments.Length == 0 ? "none" : managedArguments)
               + Environment.NewLine
               + "Other arguments in Lutris are kept.";
    }

    /// <summary>
    /// Builds the text to be displayed as advanced launch details.
    /// </summary>
    /// <param name="profile">The object containing information regarding the current installation type.</param>
    /// <returns>Text to be displayed as launch details. Can be either an executable command, or an error message.</returns>
    private static string BuildOnlineNativeSteamLaunchCommand(InstallationProfile profile, string launchParameters)
    {
        var loaderPath = D2RLoaderService.GetLoaderPath(profile.InstallDirectory);
        var steamInstallPath = GetSteamInstallPath(installDirectory: profile.InstallDirectory);
        if (!IsValidSteamProtonLaunch(profile, steamInstallPath, loaderPath, out var error))
        {
            return error;
        }

        if (UsesSteamHandoff(profile))
            return BuildSteamHandoffPreview(loaderPath!, launchParameters);

        var steamApps = FileService.FindAncestorDirectory(profile.InstallDirectory, SteamAppsDirName);
        var steamProtonEnvVars = new Dictionary<string, string>();
        PopulateSteamProtonEnvVars(steamProtonEnvVars, profile, steamInstallPath!, steamApps);
        var envVarsExports = string.Join("\n", steamProtonEnvVars.Select(kvp => $"export {kvp.Key}=\"{kvp.Value}\""));
        return $"{envVarsExports}\n \"{profile.ProtonExecutable}\" {BuildSteamProtonArguments(loaderPath!, launchParameters)}";
    }

    private static string BuildSteamHandoffPreview(string executable, string arguments)
        => $"Steam-owned game session; request prepared when Launch is pressed.{Environment.NewLine}\"{executable}\" {arguments}";

    public Process? LaunchGame(string? launchParamOverride = null, string? gamePathOverride = null)
        => LaunchGame(null, launchParamOverride, gamePathOverride);

    internal Process? LaunchGame(SteamGameHandoff? reservation, string? launchParamOverride = null, string? gamePathOverride = null)
    {
        var profile = MainWindow.Settings.CurrentProfile;
        
        // D2RMM handled separately in LaunchView
        if (profile.Type == InstallationType.D2RMM) return null;

        if (!string.IsNullOrWhiteSpace(gamePathOverride))
        {
            GamePathOverride = gamePathOverride;
        }

        var launchParameters = string.IsNullOrWhiteSpace(launchParamOverride)
            ? LaunchParameters
            : launchParamOverride;

        string executablePath;
        string finalArgs;
        string? winePrefix = null;
        string? workingDirectory = null;
        var environmentOverrides = new Dictionary<string, string>();

        if (UsesSteamHandoff(profile))
        {
            try
            {
                if (UsesD2RLoader(profile) && !D2RLoaderService.CanUseOnlineExperience(profile, out var reason))
                    throw new InvalidOperationException(reason ?? "D2RLoader is not available.");
                if (!File.Exists(Path.Combine(SteamGameHandoff.StateDirectory, "registration.json")))
                    throw new InvalidOperationException("Register the native launcher's Steam game session before playing in Gaming Mode. See LINUX.md for one-time setup.");
                if (D2RLoaderService.IsInstalled(profile.InstallDirectory))
                    D2RLoaderService.SetDefaultMod(profile.InstallDirectory!, profile.LaunchExperience);
                var handoff = SteamGameHandoff.Start(profile, launchParameters, reservation);
                ActiveSteamHandoff = handoff.Session;
                return handoff.Command;
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or UnauthorizedAccessException or ArgumentException)
            {
                Notifications.SendNotification(exception.Message, "Warning");
                LaunchDiagnostics.LogException("Steam game handoff failed", exception);
                return null;
            }
        }
        ActiveSteamHandoff = null;

        if (profile.Type == InstallationType.Lutris)
        {
            if (profile.LutrisGameId is not { } lutrisGameId)
            {
                Notifications.SendNotification(
                    "Select your Diablo II: Resurrected entry in the Installation section before launching.",
                    "Warning");
                return null;
            }

            executablePath = FindExecutableOnPath("lutris") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(executablePath))
            {
                Notifications.SendNotification("Lutris was not found on PATH.", "Warning");
                return null;
            }

            finalArgs = LutrisService.BuildRunGameUri(lutrisGameId);

            // Matches the command Lutris writes into its own desktop shortcuts.
            environmentOverrides["LUTRIS_SKIP_INIT"] = "1";
        }
        else if (UsesD2RLoader(profile))
        {
            if (!D2RLoaderService.CanUseOnlineExperience(profile, out var reason))
            {
                Notifications.SendNotification(reason ?? "D2RLoader is not available.", "Warning");
                return null;
            }

            var loaderPath = D2RLoaderService.GetLoaderPath(profile.InstallDirectory)!;
            if (OperatingSystem.IsLinux())
            {
                if (profile.Type == InstallationType.Steam)
                {
                    var steamInstallPath = GetSteamInstallPath(installDirectory: profile.InstallDirectory);
                    if (!IsValidSteamProtonLaunch(profile, steamInstallPath, loaderPath, out var error))
                    {
                        Notifications.SendNotification(error, "Warning");
                        return null;
                    }
                    
                    var steamApps = FileService.FindAncestorDirectory(profile.InstallDirectory, SteamAppsDirName);
                    PopulateSteamProtonEnvVars(environmentOverrides, profile, steamInstallPath!, steamApps);
                    executablePath = profile.ProtonExecutable!;
                    finalArgs = BuildSteamProtonArguments(loaderPath, launchParameters);
                }
                else
                {
                    executablePath = FindExecutableOnPath("wine") ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(executablePath))
                    {
                        Notifications.SendNotification("Wine was not found. Install Wine to use D2RLoader.", "Warning");
                        return null;
                    }

                    winePrefix = FindWinePrefix(loaderPath);
                    finalArgs = $"\"{loaderPath}\" {launchParameters}";
                }
            }
            else
            {
                executablePath = loaderPath;
                finalArgs = launchParameters;
            }

            workingDirectory = profile.InstallDirectory;
        }
        else if (profile.Type == InstallationType.Steam)
        {
            executablePath = profile.SteamDirectory ?? FindSteamExecutable(profile.InstallDirectory) ?? string.Empty;
            var steamPrefix = GetSteamArgumentPrefix(executablePath);
            finalArgs = $"{steamPrefix}-silent -applaunch {SteamAppId} {launchParameters}";
            
            if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
            {
                Notifications.SendNotification("Steam was not found. Please locate its executable in the Install Directory section.");
                return null;
            }
        }
        else
        {
            var gameExecutablePath = ResolveExecutablePath(GamePathOverride) ?? string.Empty;
            
            if (string.IsNullOrWhiteSpace(gameExecutablePath))
            {
                Notifications.SendNotification("No valid game path found. Please set the game path in settings.");
                return null;
            }

            if (OperatingSystem.IsLinux())
            {
                executablePath = FindExecutableOnPath("wine") ?? string.Empty;
                if (string.IsNullOrWhiteSpace(executablePath))
                {
                    Notifications.SendNotification("Wine was not found. Install Wine or use the Steam installation type.", "Warning");
                    return null;
                }

                winePrefix = FindWinePrefix(gameExecutablePath);
                finalArgs = $"\"{gameExecutablePath}\" {launchParameters}";
            }
            else
            {
                executablePath = gameExecutablePath;
                finalArgs = launchParameters;
            }
        }

        LaunchDiagnostics.Log($"Resolved executable path: {executablePath}");
        LaunchDiagnostics.Log($"Launch parameters: {finalArgs}");

        var processStartInfo = CreateProcessStartInfo(executablePath, finalArgs, workingDirectory);
        LaunchDiagnostics.Log($"Working directory: {processStartInfo.WorkingDirectory}");

        if (!string.IsNullOrWhiteSpace(winePrefix))
        {
            processStartInfo.Environment["WINEPREFIX"] = winePrefix;
        }

        foreach (var (name, value) in environmentOverrides)
        {
            processStartInfo.Environment[name] = value;
        }

        try
        {
            if (D2RLoaderService.IsInstalled(profile.InstallDirectory))
                D2RLoaderService.SetDefaultMod(profile.InstallDirectory!, profile.LaunchExperience);
            var process = Process.Start(processStartInfo);
            if (process == null)
            {
                LaunchDiagnostics.Log("Process.Start returned null.");
                Notifications.SendNotification($"Failed to start {Path.GetFileName(executablePath)}.", "Warning");
                return null;
            }

            LaunchDiagnostics.Log($"Process started with PID {process.Id}.");
            return process;
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException)
        {
            LaunchDiagnostics.LogException("Process.Start failed", ex);
            Notifications.SendNotification($"Failed to start {Path.GetFileName(executablePath)}: {ex.Message}", "Warning");
            return null;
        }
    }
    
    /// <summary>
    /// Validates whether the given profile and paths support a valid Steam Proton launch for D2RLoader.
    /// </summary>
    /// <param name="profile">The installation profile to validate.</param>
    /// <param name="steamInstallPath">The path to the Steam installation directory.</param>
    /// <param name="loaderPath">The path to the D2RLoader executable.</param>
    /// <param name="error">When validation fails, contains an error message explaining why; otherwise, <see cref="string.Empty"/>.</param>
    /// <returns><c>true</c>, if all validation checks pass; <c>false</c>, otherwise.</returns>
    internal static bool IsValidSteamProtonLaunch(
        InstallationProfile profile,
        string? steamInstallPath,
        string? loaderPath,
        out string error)
    {
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(profile.InstallDirectory) || !Directory.Exists(profile.InstallDirectory))
        {
            error = "Steam game directory could not be found.";
            return false;
        }

        if (IsFlatpakInstall(profile.InstallDirectory) || IsFlatpakInstall(steamInstallPath))
        {
            error = "Flatpak Steam is not supported for D2RLoader. Only native Steam installation is supported.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(profile.ProtonExecutable) || !File.Exists(profile.ProtonExecutable))
        {
            error = "Select the Proton executable used by your base Steam D2R installation.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(steamInstallPath) || !Directory.Exists(steamInstallPath))
        {
            error = "Steam install directory could not be found.";
            return false;
        }
                    
        if (string.IsNullOrWhiteSpace(loaderPath))
        {
            error = "D2RLoader executable could not be found.";
            return false;
        }

        var steamApps = FileService.FindAncestorDirectory(profile.InstallDirectory, SteamAppsDirName);
        if (steamApps is null || !File.Exists(Path.Combine(steamApps, $"appmanifest_{SteamAppId}.acf")))
        {
            error = "Select the official Steam Diablo II: Resurrected installation inside its Steam library.";
            return false;
        }

        if (!Directory.Exists(Path.Combine(steamApps, "compatdata", SteamAppId, "pfx")))
        {
            error = "Launch Diablo II: Resurrected from Steam and sign in once to initialize its authenticated Proton prefix.";
            return false;
        }

        return true;
    }

    internal static bool TryDetectSteamProtonExecutable(InstallationProfile profile)
    {
        if (profile.Type != InstallationType.Steam || !string.IsNullOrWhiteSpace(profile.ProtonExecutable)
            || string.IsNullOrWhiteSpace(profile.InstallDirectory))
            return false;

        try
        {
            var steamApps = FileService.FindAncestorDirectory(profile.InstallDirectory, SteamAppsDirName);
            if (steamApps is null || !File.Exists(Path.Combine(steamApps, $"appmanifest_{SteamAppId}.acf")))
                return false;

            var prefix = Path.Combine(steamApps, "compatdata", SteamAppId);
            var metadata = Path.Combine(prefix, "config_info");
            if (!Directory.Exists(Path.Combine(prefix, "pfx")) || !File.Exists(metadata))
                return false;

            string[] suffixes = [Path.Combine("files", "share", "default_pfx"),
                Path.Combine("files", "share", "fonts"), Path.Combine("files", "lib")];
            var candidates = new HashSet<string>(StringComparer.Ordinal);
            foreach (var line in File.ReadLines(metadata).Take(128))
            {
                var path = line.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (!Path.IsPathRooted(path)) continue;
                foreach (var suffix in suffixes)
                {
                    var tail = Path.DirectorySeparatorChar + suffix;
                    if (!path.EndsWith(tail, StringComparison.Ordinal)) continue;
                    var executable = Path.Combine(path[..^tail.Length], "proton");
                    if (File.Exists(executable)) candidates.Add(executable);
                }
            }

            if (candidates.Count != 1) return false;
            profile.ProtonExecutable = candidates.Single();
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    internal static string BuildSteamProtonArguments(string loaderPath, string launchParameters)
        => $"run \"{loaderPath}\" {launchParameters}";

    /// <summary>
    /// Populates the given <c>environment</c> with Steam Proton environment variables.
    /// </summary>
    /// <param name="environment">The dictionary to populate with environment variables.</param>
    /// <param name="profile">The installation profile.</param>
    /// <param name="steamInstallPath">The Steam installation path.</param>
    /// <param name="steamApps">The Steam apps directory path.</param>
    internal static void PopulateSteamProtonEnvVars(Dictionary<string, string> environment, InstallationProfile profile,
        string steamInstallPath, string? steamApps)
    {
        environment["SteamAppId"] = $"{SteamAppId}";
        environment["STEAM_COMPAT_APP_ID"] = $"{SteamAppId}";
        environment["STEAM_COMPAT_CLIENT_INSTALL_PATH"] = $"{steamInstallPath}";
        environment["STEAM_COMPAT_DATA_PATH"] = Path.Combine(steamApps!, "compatdata", SteamAppId);
        environment["STEAM_COMPAT_INSTALL_PATH"] = $"{profile.InstallDirectory}";
        environment["STEAM_COMPAT_LIBRARY_PATHS"] = $"{steamApps}";
    }

    internal static ProcessStartInfo CreateProcessStartInfo(
        string executablePath,
        string arguments,
        string? workingDirectory)
    {
        return new ProcessStartInfo(executablePath)
        {
            UseShellExecute = !OperatingSystem.IsLinux() && string.IsNullOrWhiteSpace(workingDirectory),
            Arguments = arguments,
            WorkingDirectory = workingDirectory ?? string.Empty
        };
    }

    private string? ResolveExecutablePath(string? gamePathOverride = null)
    {
        if (!string.IsNullOrWhiteSpace(gamePathOverride))
        {
            return Path.Combine(gamePathOverride, "D2R.exe");
        }

        return InstallDirectoryValidator.GetExecutablePath(MainWindow.Settings.CurrentProfile.InstallDirectory);
    }

    internal static bool UsesD2RLoader(InstallationProfile profile)
    {
        return profile.LaunchExperience is LaunchExperience.Online or LaunchExperience.Ladder;
    }

    public string? GetExpectedGameExecutablePath()
        => InstallDirectoryValidator.GetExecutablePath(MainWindow.Settings.CurrentProfile.InstallDirectory);

    private static string[] GetDefaultInstallPaths()
    {
        if (OperatingSystem.IsWindows())
        {
            return
            [
                @"C:\Program Files (x86)\Diablo II Resurrected\D2R.exe",
                @"C:\Program Files (x86)\Steam\steamapps\common\Diablo II Resurrected\D2R.exe"
            ];
        }

        if (!OperatingSystem.IsLinux())
        {
            return [];
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home))
        {
            return [];
        }

        var steamRoots = new HashSet<string>(StringComparer.Ordinal)
        {
            Path.Combine(home, ".local", "share", "Steam"),
            Path.Combine(home, ".steam", "steam"),
            Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", "data", "Steam")
        };

        foreach (var steamRoot in steamRoots.ToArray())
        {
            AddConfiguredSteamLibraries(steamRoot, steamRoots);
        }

        var paths = steamRoots
            .Select(root => Path.Combine(root, SteamAppsDirName, "common", "Diablo II Resurrected", GameExecutableName))
            .ToList();
        paths.Add(Path.Combine(
            home,
            ".wine", "drive_c", "Program Files (x86)", "Diablo II Resurrected", GameExecutableName));

        return paths.ToArray();
    }

    private static void AddConfiguredSteamLibraries(string steamRoot, ISet<string> steamRoots)
    {
        var libraryFoldersPath = Path.Combine(steamRoot, SteamAppsDirName, "libraryfolders.vdf");
        if (!File.Exists(libraryFoldersPath))
        {
            return;
        }

        try
        {
            var content = File.ReadAllText(libraryFoldersPath);
            foreach (Match match in Regex.Matches(content, "\\\"path\\\"\\s+\\\"(?<path>[^\\\"]+)\\\""))
            {
                var libraryPath = match.Groups["path"].Value.Replace("\\\\", "\\");
                if (!string.IsNullOrWhiteSpace(libraryPath))
                {
                    steamRoots.Add(libraryPath);
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static bool IsSteamLibraryPath(string path)
    {
        return NormalizePathSeparators(path)
            .Contains("/steamapps/common/", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePathSeparators(string path)
    {
        return path.Replace('\\', '/');
    }

    internal static string? FindExecutableOnPath(string executableName)
    {
        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathValue))
        {
            return null;
        }

        foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory, executableName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
            }
        }

        return null;
    }

    private static string GetDefaultSteamCommand()
    {
        return OperatingSystem.IsWindows()
            ? @"C:\Program Files (x86)\Steam\steam.exe"
            : "steam";
    }

    internal static string GetSteamArgumentPrefix(string steamExecutable, bool? isLinux = null)
    {
        if (!(isLinux ?? OperatingSystem.IsLinux()) ||
            !string.Equals(Path.GetFileName(steamExecutable), "flatpak", StringComparison.Ordinal))
        {
            return string.Empty;
        }

        return "run com.valvesoftware.Steam ";
    }

    /// <summary>
    /// Retrieves the selected library's Steam client root, or the first installed client when ownership is unknown.
    /// </summary>
    /// <returns>The path to the Steam installation directory, or <c>null</c> if none of the standard paths exist.</returns>
    internal static string? GetSteamInstallPath(string? userHome = null, string? installDirectory = null)
    {
        var homeDir = userHome ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!Directory.Exists(homeDir))
        {
            return null;
        }

        string[] candidates = [
            Path.Combine(homeDir, ".local", "share", "Steam"),
            Path.Combine(homeDir, ".steam", "steam"),
            Path.Combine(homeDir, ".steam", "Steam"),
            Path.Combine(homeDir, ".var", "app", "com.valvesoftware.Steam", "data", "Steam")
        ];

        var installedRoots = candidates.Where(Directory.Exists).ToArray();
        if (!string.IsNullOrWhiteSpace(installDirectory))
        {
            try
            {
                var steamApps = FileService.FindAncestorDirectory(installDirectory, SteamAppsDirName);
                if (steamApps is not null)
                {
                    foreach (var root in installedRoots)
                        if (SameSteamDirectory(steamApps, Path.Combine(root, SteamAppsDirName))) return root;
                    foreach (var root in installedRoots)
                    {
                        var libraries = new HashSet<string>(StringComparer.Ordinal);
                        AddConfiguredSteamLibraries(root, libraries);
                        if (libraries.Any(library => SameSteamDirectory(steamApps, Path.Combine(library, SteamAppsDirName))))
                            return root;
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
            }
        }
        return installedRoots.FirstOrDefault();
    }

    private static bool SameSteamDirectory(string first, string second)
        => string.Equals(SteamGameHandoff.ResolveFileSystemPath(first).TrimEnd(Path.DirectorySeparatorChar),
            SteamGameHandoff.ResolveFileSystemPath(second).TrimEnd(Path.DirectorySeparatorChar),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string? FindWinePrefix(string executablePath)
    {
        if (!OperatingSystem.IsLinux() || string.IsNullOrWhiteSpace(executablePath))
        {
            return null;
        }

        var dir = Path.GetDirectoryName(executablePath);
        if (string.IsNullOrWhiteSpace(dir))
        {
            return null;
        }

        var directory = new DirectoryInfo(dir);
        while (directory is not null)
        {
            if (string.Equals(directory.Name, "drive_c", StringComparison.OrdinalIgnoreCase))
            {
                return directory.Parent?.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
