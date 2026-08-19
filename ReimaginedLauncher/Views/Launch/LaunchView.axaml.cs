using System.Threading.Tasks;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ReimaginedLauncher.Utilities;

namespace ReimaginedLauncher.Views.Launch;

public partial class LaunchView : UserControl
{
    public GameLauncherService LauncherService = new();
    private bool _isLaunching;
    private D2RLoaderInventory? _loaderInventory;

    public LaunchView()
    {
        InitializeComponent();

        RefreshInstallDirectoryState();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        if (MainWindow.Settings is not null && !LauncherService.IsDetecting)
        {
            // Skip D2R.exe detection entirely when the active profile is D2RMM —
            // it doesn't need a game executable.
            var currentType = MainWindow.Settings.CurrentProfile.Type;
            var needsDetection = false;

            if (currentType != InstallationType.D2RMM)
            {
                // Run detection if the current profile isn't validated, or if any
                // non-D2RMM profile is still missing its install directory (dual-install check).
                needsDetection = !MainWindow.Settings.CurrentProfile.IsInstallDirectoryValidated;
                if (!needsDetection)
                {
                    foreach (var p in MainWindow.Settings.Profiles)
                    {
                        if (p.Type != InstallationType.D2RMM && !p.IsInstallDirectoryValidated)
                        {
                            needsDetection = true;
                            break;
                        }
                    }
                }
            }

            if (needsDetection)
            {
                _ = LauncherService.CheckForD2RExecutableAsync(async () =>
                {
                    await Dispatcher.UIThread.InvokeAsync(async () =>
                    {
                        RefreshInstallDirectoryState();
                        if (TopLevel.GetTopLevel(this) is MainWindow mw)
                        {
                            mw.RefreshLocalModState();
                            await mw.RefreshUpdateStateAsync();
                        }
                    });
                });
            }

            RefreshInstallDirectoryState();
        }
    }

    public void RefreshInstallDirectoryState()
    {
        var settings = MainWindow.Settings;
        var profile = settings.CurrentProfile;
        var isOnlineExperience = profile.LaunchExperience == LaunchExperience.Online;

        InstallationTypeComboBox.SelectedIndex = (int)profile.Type;
        DirectoryTextBox.Text = profile.InstallDirectory ?? string.Empty;
        DetectionLoadingIndicator.IsVisible = LauncherService.IsDetecting;

        SteamExtraPanel.IsVisible = profile.Type == InstallationType.Steam;
        SteamPathTextBox.Text = profile.SteamDirectory ?? string.Empty;
        SteamPathTextBox.PlaceholderText = OperatingSystem.IsLinux() ? "Steam or Flatpak executable" : "Steam.exe Path";
        LocateSteamButton.Content = OperatingSystem.IsLinux() ? "Locate Steam" : "Locate Steam.exe";

        // Auto-detect Steam path if not set or if it's currently Steam type
        if (profile.Type == InstallationType.Steam)
        {
            var detectedSteam = LauncherService.FindSteamExecutable(profile.InstallDirectory);
            if (!string.IsNullOrEmpty(detectedSteam) && File.Exists(detectedSteam))
            {
                if (profile.SteamDirectory != detectedSteam)
                {
                    profile.SteamDirectory = detectedSteam;
                    SteamPathTextBox.Text = detectedSteam;
                }
                LocateSteamButton.IsEnabled = false;
            }
            else
            {
                LocateSteamButton.IsEnabled = true;
            }
        }


        bool isValidated;
        bool isModDetected;

        if (profile.Type == InstallationType.D2RMM)
        {
            InstallDirectoryTitle.Text = "D2RMM Mods Folder";
            InstallDirectoryDescription.Text = "Select your D2RMM mods folder where Reimagined will be installed.";
            
            isValidated = InstallDirectoryValidator.IsValidD2RmmModsDirectory(profile.InstallDirectory) && Directory.Exists(profile.InstallDirectory);
            
            // For D2RMM, check if Reimagined or Reimagined.mpq exists in the mods folder
            isModDetected = isValidated && InstallDirectoryValidator.ResolveD2RmmModFolder(profile.InstallDirectory) != null;
        }
        else
        {
            InstallDirectoryTitle.Text = "Install Directory";
            InstallDirectoryDescription.Text = "Select the Diablo II: Resurrected folder that contains your local mod installation (Folder with .exe in it)";
            isValidated = profile.Type == InstallationType.Steam
                ? InstallDirectoryValidator.IsValidSteamInstallDirectory(profile.InstallDirectory)
                : InstallDirectoryValidator.IsValidInstallDirectory(profile.InstallDirectory);
            isModDetected = MainWindow.IsLocalModDetected;
        }

        profile.IsInstallDirectoryValidated = isValidated;

        OfflineExperienceButton.Classes.Set("selected", !isOnlineExperience);
        OnlineExperienceButton.Classes.Set("selected", isOnlineExperience);
        OnlineExperienceButton.IsEnabled = profile.Type != InstallationType.D2RMM;
        OnlineExperiencePanel.IsVisible = isOnlineExperience && profile.Type != InstallationType.D2RMM;

        _loaderInventory = D2RLoaderService.Discover(profile.InstallDirectory);
        RefreshD2RLoaderState(profile, _loaderInventory);
        var onlineAvailable = D2RLoaderService.CanUseOnlineExperience(profile, out var onlineUnavailableReason);

        if (profile.Type == InstallationType.D2RMM)
        {
            StartGameButton.Content = "Install Tweaks";
            StartGameDescription.Text = "Clicking 'Install Tweaks' will apply tweaks and adjustments to the files in your D2RMM/mods/Reimagined/data directory.";
            StartGameButton.IsEnabled = !_isLaunching && isValidated && isModDetected;
        }
        else
        {
            StartGameButton.Content = isOnlineExperience ? "Start Online" : "Start Offline";
            StartGameDescription.Text = isOnlineExperience
                ? "Starts D2RLoader with Reimagined selected. Choose TCP/IP in-game to host or join; this does not connect to Battle.net."
                : "Starts the standard Reimagined offline experience with your saved launch options.";
            StartGameButton.IsEnabled = !_isLaunching
                                        && isValidated
                                        && isModDetected
                                        && (!isOnlineExperience || onlineAvailable);

            if (!isOnlineExperience
                && profile.Type == InstallationType.Steam
                && string.IsNullOrWhiteSpace(profile.SteamDirectory))
            {
                StartGameButton.IsEnabled = false;
            }
        }

        ValidationBanner.IsVisible = !isValidated
                                     || !isModDetected
                                     || isOnlineExperience && !onlineAvailable;
        
        if (profile.Type == InstallationType.D2RMM)
        {
            ValidationBannerText.Text = string.IsNullOrWhiteSpace(profile.InstallDirectory)
                ? "Select your D2RMM mods folder."
                : !InstallDirectoryValidator.IsValidD2RmmModsDirectory(profile.InstallDirectory)
                    ? InstallDirectoryValidator.GetD2RmmValidationMessage(profile.InstallDirectory)
                    : !isModDetected && isValidated
                        ? "Reimagined not yet installed in this mods folder."
                        : "The selected folder could not be found.";
        }
        else
        {
            ValidationBannerText.Text = isOnlineExperience && isValidated && isModDetected && !onlineAvailable
                ? onlineUnavailableReason ?? "D2RLoader is unavailable for this profile."
                : !isValidated
                ? string.IsNullOrWhiteSpace(profile.InstallDirectory)
                    ? "Enter your Diablo II: Resurrected install directory before using the launcher."
                    : profile.Type == InstallationType.Steam
                        && InstallDirectoryValidator.IsValidInstallDirectory(profile.InstallDirectory)
                        ? "The selected directory does not contain steam_*.dll. Please select a valid Steam installation or switch to Battle.Net."
                        : "The selected install directory has not been validated. Choose the folder that contains D2R.exe."
                : "D2R Reimagined mod not detected in this directory. Install the mod before launching.";
        }
        
        LaunchCommandText.Text = LauncherService.BuildLaunchCommand();

        BackupOnLaunchSummary.Text = $"Backup on Launch: {(profile.AutomaticBackupsEnabled ? "Yes" : "No")}";
        BackupIntervalSummary.Text = profile.AutomaticBackupsEnabled
            ? $"Auto-Backup Interval: {profile.BackupIntervalMinutes} min"
            : "Auto-Backup Interval: N/A";
    }

    private void RefreshD2RLoaderState(InstallationProfile profile, D2RLoaderInventory inventory)
    {
        LoaderPluginsItemsControl.ItemsSource = inventory.Plugins;
        LoaderPatchesItemsControl.ItemsSource = inventory.Patches;
        LoaderPluginCountText.Text = inventory.Plugins.Count.ToString();
        LoaderPatchCountText.Text = inventory.Patches.Count.ToString();
        NoLoaderPluginsText.IsVisible = inventory.Plugins.Count == 0;
        NoLoaderPatchesText.IsVisible = inventory.Patches.Count == 0;

        LoaderStatusBadge.Background = new SolidColorBrush(Color.Parse(inventory.IsInstalled ? "#17351D" : "#3A1818"));
        LoaderStatusBadgeText.Foreground = new SolidColorBrush(Color.Parse(inventory.IsInstalled ? "#86D88F" : "#E98B91"));
        LoaderStatusBadgeText.Text = inventory.IsInstalled ? "READY" : "NOT FOUND";
        LoaderStatusText.Text = inventory.IsInstalled
            ? $"D2RLoader {inventory.Version ?? "unknown version"} detected beside D2R.exe. "
              + $"Found {inventory.Plugins.Count} plugin{(inventory.Plugins.Count == 1 ? string.Empty : "s")} and "
              + $"{inventory.Patches.Count} patch manifest{(inventory.Patches.Count == 1 ? string.Empty : "s")}."
            : "Place D2RLoader.exe in the same folder as D2R.exe to enable this experience.";

        var disabledScopes = new[]
            {
                inventory.AllowGlobalExtensions ? null : "global extensions",
                inventory.AllowModExtensions ? null : "Reimagined extensions"
            }
            .Where(value => value is not null)
            .ToArray();
        LoaderExtensionPolicyText.Text = disabledScopes.Length == 0
            ? "Global and Reimagined extension loading are enabled in d2rloader.toml."
            : $"Disabled by d2rloader.toml: {string.Join(", ", disabledScopes!)}.";

        var canUseOnline = D2RLoaderService.CanUseOnlineExperience(profile, out var reason);
        LoaderWarningBanner.IsVisible = !canUseOnline;
        LoaderWarningText.Text = reason ?? string.Empty;
        OpenLoaderFolderButton.IsEnabled = Directory.Exists(inventory.GlobalRoot);
        OpenModLoaderFolderButton.IsEnabled = Directory.Exists(inventory.ModRoot)
                                                || Directory.Exists(Path.GetDirectoryName(inventory.ModRoot));
    }

    private async void OnOfflineExperienceClick(object? sender, RoutedEventArgs e)
    {
        await SetLaunchExperienceAsync(LaunchExperience.Offline);
    }

    private async void OnOnlineExperienceClick(object? sender, RoutedEventArgs e)
    {
        await SetLaunchExperienceAsync(LaunchExperience.Online);
    }

    private async Task SetLaunchExperienceAsync(LaunchExperience experience)
    {
        var profile = MainWindow.Settings.CurrentProfile;
        if (profile.Type == InstallationType.D2RMM || profile.LaunchExperience == experience)
        {
            return;
        }

        profile.LaunchExperience = experience;
        await SettingsManager.SaveAsync(MainWindow.Settings);
        RefreshInstallDirectoryState();
    }

    private void OnRefreshLoaderClick(object? sender, RoutedEventArgs e)
    {
        RefreshInstallDirectoryState();
    }

    private void OnOpenLoaderFolderClick(object? sender, RoutedEventArgs e)
    {
        OpenFolder(_loaderInventory?.GlobalRoot);
    }

    private void OnOpenModLoaderFolderClick(object? sender, RoutedEventArgs e)
    {
        var path = _loaderInventory?.ModRoot;
        OpenFolder(Directory.Exists(path) ? path : Path.GetDirectoryName(path));
    }

    private static void OpenFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return;
        }

        try
        {
            var startInfo = OperatingSystem.IsWindows()
                ? new ProcessStartInfo("explorer.exe") { UseShellExecute = false }
                : OperatingSystem.IsMacOS()
                    ? new ProcessStartInfo("open") { UseShellExecute = false }
                    : new ProcessStartInfo("xdg-open") { UseShellExecute = false };

            startInfo.ArgumentList.Add(path);
            Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            Notifications.SendNotification($"Could not open folder: {ex.Message}", "Warning");
        }
    }

    private async void OnInstallationTypeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (InstallationTypeComboBox == null) return;
        
        var selectedIndex = InstallationTypeComboBox.SelectedIndex;
        if (selectedIndex < 0) return;

        var newType = (InstallationType)selectedIndex;
        if (MainWindow.Settings.CurrentProfile.Type == newType) return;

        // Switch profile
        MainWindow.Settings.SelectedProfileIndex = selectedIndex;
        if (MainWindow.Settings.CurrentProfile.Type == InstallationType.D2RMM)
        {
            MainWindow.Settings.CurrentProfile.LaunchExperience = LaunchExperience.Offline;
        }
        BackupService.ApplyDefaultSettings();
        await SettingsManager.SaveAsync(MainWindow.Settings);

        if (TopLevel.GetTopLevel(this) is MainWindow mw)
        {
            mw.RefreshLocalModState();
            await mw.RefreshUpdateStateAsync();
        }
        
        RefreshInstallDirectoryState();
    }

    private async void OnLocateSteamClick(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is Window window)
        {
            var files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = OperatingSystem.IsLinux() ? "Locate Steam or Flatpak executable" : "Locate Steam.exe",
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("Steam Executable")
                    {
                        Patterns = OperatingSystem.IsLinux() ? ["steam", "flatpak"] : ["Steam.exe"]
                    }
                ]
            });

            if (files.Count > 0)
            {
                var selectedPath = files[0].Path.LocalPath;

                MainWindow.Settings.CurrentProfile.SteamDirectory = selectedPath;
                await SettingsManager.SaveAsync(MainWindow.Settings);
                RefreshInstallDirectoryState();
            }
        }
    }


    private void SetLaunchStatus(string status, bool isVisible = true)
    {
        LaunchStatusText.Text = status;
        LaunchStatusPanel.IsVisible = isVisible;
    }

    private async void OnRunClick(object? sender, RoutedEventArgs e)
    {
        await StartGameAsync();
    }

    // Runs the same prepare-backup-launch sequence as the Start Game button.
    // Exposed so callers outside this view (e.g. the navigation Play shortcut)
    // can trigger a launch directly.
    public async Task StartGameAsync()
    {
        LaunchDiagnostics.ResetSession();
        LaunchDiagnostics.Log("Launch/Install button clicked.");

        if (_isLaunching)
        {
            LaunchDiagnostics.Log("Action ignored because an action is already in progress.");
            return;
        }

        var profile = MainWindow.Settings.CurrentProfile;

        if (!profile.IsInstallDirectoryValidated)
        {
            LaunchDiagnostics.Log("Action blocked because install directory is not validated.");
            Notifications.SendNotification(
                "Install directory not validated",
                "Choose the Diablo II: Resurrected folder that contains D2R.exe.");
            return;
        }

        if (!MainWindow.IsLocalModDetected)
        {
            LaunchDiagnostics.Log("Action blocked because the local mod was not detected.");
            Notifications.SendNotification(
                "D2R Reimagined mod not detected",
                "Install the mod in the selected directory before launching/installing.");

            if (MainWindow.Instance is { } mainWindow)
            {
                await mainWindow.PromptInstallForMissingModAsync();
            }

            return;
        }

        if (profile.LaunchExperience == LaunchExperience.Online
            && !D2RLoaderService.CanUseOnlineExperience(profile, out var onlineUnavailableReason))
        {
            LaunchDiagnostics.Log($"Online launch blocked: {onlineUnavailableReason}");
            Notifications.SendNotification(onlineUnavailableReason ?? "D2RLoader is unavailable.", "Warning");
            return;
        }


        _isLaunching = true;
        StartGameButton.IsEnabled = false;
        var actionName = profile.Type == InstallationType.D2RMM ? "Installation" : "Launch";
        SetLaunchStatus($"Preparing {actionName.ToLower()}...");
        var progress = new Progress<string>(status => SetLaunchStatus(status));

        try
        {
            LaunchDiagnostics.Log("Starting mod tweak preparation.");
            var prepared = await Task.Run(() => ModTweaksService.PrepareForLaunchAsync(progress));
            if (!prepared)
            {
                LaunchDiagnostics.Log("Mod tweak preparation returned false.");
                SetLaunchStatus($"{actionName} preparation failed.");
                Notifications.SendNotification($"{actionName} preparation failed. See previous warning for details.", "Warning");
                return;
            }

            if (profile.AutomaticBackupsEnabled)
            {
                LaunchDiagnostics.Log("Starting backup.");
                SetLaunchStatus("Creating backup...");
                var backupCreated = await Task.Run(BackupService.CreateLaunchBackupAsync);
                if (!backupCreated)
                {
                    LaunchDiagnostics.Log("Backup returned false.");
                    Notifications.SendNotification("Backup failed. Continuing.", "Warning");
                }
            }

            try
            {
                if (profile.Type == InstallationType.D2RMM)
                {
                    LaunchDiagnostics.Log("D2RMM: Tweaks applied. Installation complete.");
                    SetLaunchStatus("D2RMM mod tweaks applied.");
                }
                else
                {
                    LaunchDiagnostics.Log("Calling GameLauncherService.LaunchGame.");
                    SetLaunchStatus(profile.LaunchExperience == LaunchExperience.Online
                        ? "Starting D2RLoader..."
                        : "Starting Diablo II: Resurrected...");
                    var gameProcess = LauncherService.LaunchGame();
                    if (gameProcess == null)
                    {
                        LaunchDiagnostics.Log("GameLauncherService.LaunchGame did not start a process.");
                        SetLaunchStatus("Launch failed.");
                        return;
                    }
                    LaunchDiagnostics.Log("GameLauncherService.LaunchGame returned without throwing.");
                    SetLaunchStatus($"{actionName} command sent.");

                    if (MainWindow.Settings.MinimizeToTray && MainWindow.Instance is { } mainWindow)
                    {
                        string? expectedExePath = null;
                        if (profile.Type == InstallationType.Steam
                            || profile.LaunchExperience == LaunchExperience.Online)
                        {
                            expectedExePath = LauncherService.GetExpectedGameExecutablePath();
                        }

                        _ = mainWindow.MinimizeToTrayAndWaitForExitAsync(gameProcess, expectedExePath);
                    }
                }
            }
            catch (Exception ex)
            {
                LaunchDiagnostics.LogException($"{actionName} failed", ex);
                SetLaunchStatus($"{actionName} failed.");
                Notifications.SendNotification($"{actionName} failed: {ex.Message}", "Warning");
                return;
            }

            Notifications.SendNotification(profile.Type == InstallationType.D2RMM ? "Installed Mod to D2RMM" : "Launched Game", "Success");
        }
        finally
        {
            LaunchDiagnostics.Log($"{actionName} flow completed.");
            _isLaunching = false;
            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                await Task.Delay(1500);
                if (!_isLaunching)
                {
                    LaunchStatusPanel.IsVisible = false;
                }
            });
            RefreshInstallDirectoryState();
        }
    }

    private async void OnInstallDirectoryClick(object? sender, RoutedEventArgs e)
    {
        LauncherService.CancelDetection();
        if (TopLevel.GetTopLevel(this) is Window window)
        {
            var profile = MainWindow.Settings.CurrentProfile;
            if (profile.Type == InstallationType.D2RMM)
            {
                var folders = await window.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
                {
                    Title = "Select D2RMM mods folder",
                    AllowMultiple = false
                });

                if (folders.Count > 0)
                {
                    profile.InstallDirectory = folders[0].Path.LocalPath;
                }
                else return;
            }
            else
            {
                var folders = await window.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
                {
                    Title = "Select Install Folder",
                    AllowMultiple = false
                });

                if (folders.Count <= 0) return;

                var path = folders[0].Path.LocalPath;
                profile.InstallDirectory = InstallDirectoryValidator.NormalizeInstallDirectory(path);
            }

            profile.IsInstallDirectoryValidated = profile.Type == InstallationType.D2RMM
                ? InstallDirectoryValidator.IsValidD2RmmModsDirectory(profile.InstallDirectory)
                : profile.Type == InstallationType.Steam
                    ? InstallDirectoryValidator.IsValidSteamInstallDirectory(profile.InstallDirectory)
                    : InstallDirectoryValidator.IsValidInstallDirectory(profile.InstallDirectory);

            // Auto-detect type if it's currently BattleNet (default)
            if (profile.Type == InstallationType.BattleNet && profile.IsInstallDirectoryValidated)
            {
                var detectedType = LauncherService.DetectInstallationType(profile.InstallDirectory!);
                if (detectedType != InstallationType.BattleNet)
                {
                    profile.Type = detectedType;
                }
            }

            // Auto-detect Steam path if it's Steam
            if (profile.Type == InstallationType.Steam)
            {
                var detectedSteam = LauncherService.FindSteamExecutable(profile.InstallDirectory);
                if (!string.IsNullOrEmpty(detectedSteam))
                {
                    profile.SteamDirectory = detectedSteam;
                }
            }

            await SettingsManager.SaveAsync(MainWindow.Settings);
            BackupService.UpdateSchedule();
            if (TopLevel.GetTopLevel(this) is MainWindow mw)
            {
                mw.RefreshLocalModState();
                await mw.RefreshUpdateStateAsync();
            }
            RefreshInstallDirectoryState();

            if (!profile.IsInstallDirectoryValidated)
            {
                if (profile.Type == InstallationType.D2RMM)
                {
                    Notifications.SendNotification(
                        "Invalid D2RMM location",
                        InstallDirectoryValidator.GetD2RmmValidationMessage(profile.InstallDirectory));
                }
                else if (profile.Type == InstallationType.Steam
                         && InstallDirectoryValidator.IsValidInstallDirectory(profile.InstallDirectory))
                {
                    Notifications.SendNotification(
                        "Invalid Steam path",
                        "The selected directory does not contain steam_*.dll. Please select a valid Steam installation or switch to Battle.Net.");
                }
                else
                {
                    Notifications.SendNotification(
                        "D2R install not found",
                        "Select the Diablo II: Resurrected folder that contains D2R.exe.");
                }
                return;
            }

            if (profile.Type != InstallationType.D2RMM && !MainWindow.IsLocalModDetected)
            {
                Notifications.SendNotification(
                    "D2R Reimagined mod not detected",
                    "Install the mod in this directory before launching.");

                if (TopLevel.GetTopLevel(this) is MainWindow mainWindow)
                {
                    await mainWindow.PromptInstallForMissingModAsync();
                }

                return;
            }

            Notifications.SendNotification(profile.Type == InstallationType.D2RMM ? "D2RMM mods folder selected" : "Install directory validated", "Success");
        }
    }
}
