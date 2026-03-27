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
        MainWindow.Settings.IsInstallDirectoryValidated = isValidated;
        StartGameButton.IsEnabled = isValidated;
        ValidationBanner.IsVisible = !isValidated;
        ValidationBannerText.Text = string.IsNullOrWhiteSpace(MainWindow.Settings.InstallDirectory)
            ? "Enter your Diablo II: Resurrected install directory before using the launcher."
            : "The selected install directory has not been validated. Choose the folder that contains D2R.exe.";
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
            RefreshInstallDirectoryState();

            if (!MainWindow.Settings.IsInstallDirectoryValidated)
            {
                Notifications.SendNotification(
                    "D2R install not found",
                    "Select the Diablo II: Resurrected folder that contains D2R.exe.");
                return;
            }

            Notifications.SendNotification("Install directory validated", "Success");
        }
    }
}
