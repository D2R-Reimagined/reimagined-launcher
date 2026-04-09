using System.Threading.Tasks;
using System;
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

        if (MainWindow.Settings is not null && !MainWindow.Settings.IsInstallDirectoryValidated && !LauncherService.IsDetecting)
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
            RefreshInstallDirectoryState();
        }
    }

    public void RefreshInstallDirectoryState()
    {
        DirectoryTextBox.Text = MainWindow.Settings.InstallDirectory ?? string.Empty;
        DetectionLoadingIndicator.IsVisible = LauncherService.IsDetecting;

        var isValidated = InstallDirectoryValidator.IsValidInstallDirectory(MainWindow.Settings.InstallDirectory);
        var isModDetected = MainWindow.IsLocalModDetected;
        MainWindow.Settings.IsInstallDirectoryValidated = isValidated;
        StartGameButton.IsEnabled = !_isLaunching && isValidated && isModDetected;
        ValidationBanner.IsVisible = !isValidated || !isModDetected;
        ValidationBannerText.Text = !isValidated
            ? string.IsNullOrWhiteSpace(MainWindow.Settings.InstallDirectory)
                ? "Enter your Diablo II: Resurrected install directory before using the launcher."
                : "The selected install directory has not been validated. Choose the folder that contains D2R.exe."
            : "D2R Reimagined mod not detected in this directory. Install the mod before launching.";
        LaunchCommandText.Text = LauncherService.BuildLaunchCommand();
    }

    private void SetLaunchStatus(string status, bool isVisible = true)
    {
        LaunchStatusText.Text = status;
        LaunchStatusPanel.IsVisible = isVisible;
    }

    private async void OnRunClick(object? sender, RoutedEventArgs e)
    {
        LaunchDiagnostics.ResetSession();
        LaunchDiagnostics.Log("Launch button clicked.");

        if (_isLaunching)
        {
            LaunchDiagnostics.Log("Launch ignored because a launch is already in progress.");
            return;
        }

        if (!MainWindow.Settings.IsInstallDirectoryValidated)
        {
            LaunchDiagnostics.Log("Launch blocked because install directory is not validated.");
            Notifications.SendNotification(
                "Install directory not validated",
                "Choose the Diablo II: Resurrected folder that contains D2R.exe.");
            return;
        }

        if (!MainWindow.IsLocalModDetected)
        {
            LaunchDiagnostics.Log("Launch blocked because the local mod was not detected.");
            Notifications.SendNotification(
                "D2R Reimagined mod not detected",
                "Install the mod in the selected directory before launching.");

            if (TopLevel.GetTopLevel(this) is MainWindow mainWindow)
            {
                await mainWindow.PromptInstallForMissingModAsync();
            }

            return;
        }

        _isLaunching = true;
        StartGameButton.IsEnabled = false;
        SetLaunchStatus("Preparing launch...");
        var progress = new Progress<string>(status => SetLaunchStatus(status));

        try
        {
            LaunchDiagnostics.Log("Starting mod tweak preparation.");
            var prepared = await Task.Run(() => ModTweaksService.PrepareForLaunchAsync(progress));
            if (!prepared)
            {
                LaunchDiagnostics.Log("Mod tweak preparation returned false.");
                SetLaunchStatus("Launch preparation failed.");
                Notifications.SendNotification("Launch preparation failed. See previous warning for details.", "Warning");
                return;
            }

            if (MainWindow.Settings.AutomaticBackupsEnabled)
            {
                LaunchDiagnostics.Log("Starting launch backup.");
                SetLaunchStatus("Creating launch backup...");
                var backupCreated = await Task.Run(BackupService.CreateLaunchBackupAsync);
                if (!backupCreated)
                {
                    LaunchDiagnostics.Log("Launch backup returned false.");
                    Notifications.SendNotification("Launch backup failed. Continuing with game launch.", "Warning");
                }
            }

            try
            {
                LaunchDiagnostics.Log("Calling GameLauncherService.LaunchGame.");
                SetLaunchStatus("Starting Diablo II: Resurrected...");
                LauncherService.LaunchGame();
                LaunchDiagnostics.Log("GameLauncherService.LaunchGame returned without throwing.");
            }
            catch (Exception ex)
            {
                LaunchDiagnostics.LogException("GameLauncherService.LaunchGame threw an exception", ex);
                SetLaunchStatus("Game launch failed.");
                Notifications.SendNotification($"Game launch failed: {ex.Message}", "Warning");
                return;
            }

            SetLaunchStatus("Launch command sent.");
            Notifications.SendNotification("Clicked Launch", "Success");
        }
        finally
        {
            LaunchDiagnostics.Log("Launch flow completed.");
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
            var folders = await window.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Select Install Folder",
                AllowMultiple = false
            });

            if (folders.Count <= 0) return;

            var path = folders[0].Path.LocalPath;
            MainWindow.Settings.InstallDirectory = InstallDirectoryValidator.NormalizeInstallDirectory(path);
            MainWindow.Settings.IsInstallDirectoryValidated =
                InstallDirectoryValidator.IsValidInstallDirectory(MainWindow.Settings.InstallDirectory);
            await SettingsManager.SaveAsync(MainWindow.Settings);
            BackupService.UpdateSchedule();
            if (TopLevel.GetTopLevel(this) is MainWindow mw)
            {
                mw.RefreshLocalModState();
                await mw.RefreshUpdateStateAsync();
            }
            RefreshInstallDirectoryState();

            if (!MainWindow.Settings.IsInstallDirectoryValidated)
            {
                Notifications.SendNotification(
                    "D2R install not found",
                    "Select the Diablo II: Resurrected folder that contains D2R.exe.");
                return;
            }

            if (!MainWindow.IsLocalModDetected)
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

            Notifications.SendNotification("Install directory validated", "Success");
        }
    }
}
