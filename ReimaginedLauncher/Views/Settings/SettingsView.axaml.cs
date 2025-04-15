using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using ReimaginedLauncher.Utilities;

namespace ReimaginedLauncher.Views.Settings;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
    }
    
    private async void OnBackupDirectoryClick(object? sender, RoutedEventArgs e)
    {
        if (this.GetVisualRoot() is Window window)
        {
            var folders = await window.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Select Backup Folder",
                AllowMultiple = false
            });

            if (folders.Count <= 0) return;
            
            var path = folders[0].Path.LocalPath;
            BackupTextBox.Text = path;
            MainWindow.Settings.BackupSaveDirectory = path;
            await SettingsManager.SaveAsync(MainWindow.Settings);
        }
    }
}