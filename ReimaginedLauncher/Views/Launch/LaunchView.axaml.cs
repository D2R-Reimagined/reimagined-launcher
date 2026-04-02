using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using ReimaginedLauncher.Utilities;

namespace ReimaginedLauncher.Views.Launch;

public partial class LaunchView : UserControl
{
    public GameLauncherService LauncherService = new();
    
    public LaunchView()
    {
        InitializeComponent();

        RefreshInstallDirectoryState();
    }

    public void RefreshInstallDirectoryState()
    {
        DirectoryTextBox.Text = MainWindow.Settings.InstallDirectory ?? string.Empty;

        var isValidated = InstallDirectoryValidator.IsValidInstallDirectory(MainWindow.Settings.InstallDirectory);
        var isModDetected = MainWindow.IsLocalModDetected;
        MainWindow.Settings.IsInstallDirectoryValidated = isValidated;
        StartGameButton.IsEnabled = isValidated && isModDetected;
        ValidationBanner.IsVisible = !isValidated || !isModDetected;
        ValidationBannerText.Text = !isValidated
            ? string.IsNullOrWhiteSpace(MainWindow.Settings.InstallDirectory)
                ? "Enter your Diablo II: Resurrected install directory before using the launcher."
                : "The selected install directory has not been validated. Choose the folder that contains D2R.exe."
            : "D2R Reimagined mod not detected in this directory. Install the mod before launching.";
        LaunchCommandText.Text = LauncherService.BuildLaunchCommand();
    }
    
    private async void OnRunClick(object? sender, RoutedEventArgs e)
    {
        if (!MainWindow.Settings.IsInstallDirectoryValidated)
        {
            Notifications.SendNotification(
                "Install directory not validated",
                "Choose the Diablo II: Resurrected folder that contains D2R.exe.");
            return;
        }

        if (!MainWindow.IsLocalModDetected)
        {
            Notifications.SendNotification(
                "D2R Reimagined mod not detected",
                "Install the mod in the selected directory before launching.");

            if (this.GetVisualRoot() is MainWindow mainWindow)
            {
                await mainWindow.PromptInstallForMissingModAsync();
            }

            return;
        }

        if (!await ModTweaksService.PrepareForLaunchAsync())
        {
            return;
        }

        if (MainWindow.Settings.AutomaticBackupsEnabled)
        {
            await BackupService.CreateLaunchBackupAsync();
        }

        LauncherService.LaunchGame();
        Notifications.SendNotification("Clicked Launch", "Success");
    }
    
    private async void OnInstallDirectoryClick(object? sender, RoutedEventArgs e)
    {
        if (this.GetVisualRoot() is Window window)
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
            if (this.GetVisualRoot() is MainWindow mw)
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

                if (this.GetVisualRoot() is MainWindow mainWindow)
                {
                    await mainWindow.PromptInstallForMissingModAsync();
                }

                return;
            }

            Notifications.SendNotification("Install directory validated", "Success");
        }
    }
}
