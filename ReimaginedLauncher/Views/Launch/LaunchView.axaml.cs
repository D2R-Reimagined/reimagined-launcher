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
        
        if (!string.IsNullOrWhiteSpace(MainWindow.Settings.InstallDirectory))
            DirectoryTextBox.Text = MainWindow.Settings.InstallDirectory;
    }
    
    private async void OnRunClick(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(MainWindow.Settings.InstallDirectory))
        {
            Notifications.SendNotification("Install Directory Not Set", "Please set the install directory in settings.");
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
            DirectoryTextBox.Text = path;
            MainWindow.Settings.InstallDirectory = path;
            await SettingsManager.SaveAsync(MainWindow.Settings);
        }
    }
}