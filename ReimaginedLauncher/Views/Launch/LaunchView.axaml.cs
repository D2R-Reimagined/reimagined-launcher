using System.Threading.Tasks;
using System;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ReimaginedLauncher.Utilities;

namespace ReimaginedLauncher.Views.Launch;

public partial class LaunchView : UserControl
{
    public GameLauncherService LauncherService = new();
    private bool _isLaunching;

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
                needsDetection = !MainWindow.Settings.IsInstallDirectoryValidated;
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

        InstallationTypeComboBox.SelectedIndex = (int)profile.Type;
        DirectoryTextBox.Text = profile.InstallDirectory ?? string.Empty;
        DetectionLoadingIndicator.IsVisible = LauncherService.IsDetecting;

        SteamExtraPanel.IsVisible = profile.Type == InstallationType.Steam;
        SteamPathTextBox.Text = profile.SteamDirectory ?? string.Empty;

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
            InstallDirectoryDescription.Text = "Select your D2RMM mods folder where Reimagined.mpq will be installed.";
            
            isValidated = !string.IsNullOrWhiteSpace(profile.InstallDirectory) && Directory.Exists(profile.InstallDirectory);
            
            // For D2RMM, check if Reimagined.mpq exists in the mods folder
            isModDetected = isValidated && Directory.Exists(Path.Combine(profile.InstallDirectory!, "Reimagined.mpq"));
        }
        else
        {
            InstallDirectoryTitle.Text = "Install Directory";
            InstallDirectoryDescription.Text = "Select the Diablo II: Resurrected folder that contains your local mod installation (Folder with .exe in it)";
            isValidated = InstallDirectoryValidator.IsValidInstallDirectory(profile.InstallDirectory);
            isModDetected = MainWindow.IsLocalModDetected;
        }

        profile.IsInstallDirectoryValidated = isValidated;

        if (profile.Type == InstallationType.D2RMM)
        {
            StartGameButton.Content = "Install Mod";
            StartGameDescription.Text = "Clicking install mod will apply tweaks and adjustments to the files in your D2RMM/mods/Reimagined.mpq/data directory.";
            StartGameButton.IsEnabled = !_isLaunching && isValidated && isModDetected;
        }
        else
        {
            StartGameButton.Content = "Start Game";
            StartGameDescription.Text = "The button stays available here and will enable once the install directory is valid and the mod is detected.";
            StartGameButton.IsEnabled = !_isLaunching && isValidated && isModDetected;

            if (profile.Type == InstallationType.Steam && string.IsNullOrWhiteSpace(profile.SteamDirectory))
            {
                StartGameButton.IsEnabled = false;
            }
        }

        ValidationBanner.IsVisible = !isValidated || !isModDetected;
        
        if (profile.Type == InstallationType.D2RMM)
        {
            ValidationBannerText.Text = string.IsNullOrWhiteSpace(profile.InstallDirectory)
                ? "Select your D2RMM mods folder."
                : !isModDetected && isValidated
                    ? "Reimagined.mpq not yet installed in this mods folder."
                    : "The selected folder could not be found.";
        }
        else
        {
            ValidationBannerText.Text = !isValidated
                ? string.IsNullOrWhiteSpace(profile.InstallDirectory)
                    ? "Enter your Diablo II: Resurrected install directory before using the launcher."
                    : "The selected install directory has not been validated. Choose the folder that contains D2R.exe."
                : "D2R Reimagined mod not detected in this directory. Install the mod before launching.";
        }
        
        LaunchCommandText.Text = LauncherService.BuildLaunchCommand();
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
                Title = "Locate Steam.exe",
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("Steam Executable") { Patterns = ["Steam.exe"] }]
            });

            if (files.Count > 0)
            {
                MainWindow.Settings.CurrentProfile.SteamDirectory = files[0].Path.LocalPath;
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

            if (TopLevel.GetTopLevel(this) is MainWindow mainWindow)
            {
                await mainWindow.PromptInstallForMissingModAsync();
            }

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
                    SetLaunchStatus("Starting Diablo II: Resurrected...");
                    LauncherService.LaunchGame();
                    LaunchDiagnostics.Log("GameLauncherService.LaunchGame returned without throwing.");
                    SetLaunchStatus($"{actionName} command sent.");
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
                ? !string.IsNullOrWhiteSpace(profile.InstallDirectory)
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
                        "D2RMM mods folder not found",
                        "Select your D2RMM mods folder.");
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
