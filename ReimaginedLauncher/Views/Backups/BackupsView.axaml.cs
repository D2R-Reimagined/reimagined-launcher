using System;
using System.Globalization;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using ReimaginedLauncher.Utilities;

namespace ReimaginedLauncher.Views.Backups;

public partial class BackupsView : UserControl
{
    private bool _isRefreshing;
    private bool _isLoading;

    public BackupsView()
    {
        InitializeComponent();
        SetLoadingState(false);
    }

    public void RefreshBackupState()
    {
        _isRefreshing = true;
        BackupDirectoryTextBox.Text = MainWindow.Settings.BackupSaveDirectory ?? string.Empty;
        AutomaticBackupsCheckBox.IsChecked = MainWindow.Settings.AutomaticBackupsEnabled;
        BackupIntervalTextBox.Text = MainWindow.Settings.BackupIntervalMinutes.ToString(CultureInfo.InvariantCulture);
        BackupAmountTextBox.Text = MainWindow.Settings.BackupAmount.ToString(CultureInfo.InvariantCulture);
        SaveDirectoryTextBlock.Text = BuildSaveDirectoryText();
        BackupsListBox.ItemsSource = BackupService.GetBackups();
        RestoreSelectionTextBlock.Text = "Select a backup to restore.";
        _isRefreshing = false;
    }

    public async Task RefreshBackupStateAsync()
    {
        SetLoadingState(true);

        try
        {
            await Task.Yield();
            RefreshBackupState();
        }
        finally
        {
            SetLoadingState(false);
        }
    }

    public void SetLoadingState(bool isLoading)
    {
        _isLoading = isLoading;
        LoadingBanner.IsVisible = isLoading;
        ContentGrid.IsVisible = !isLoading;
    }

    private async void OnBackupDirectoryClick(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not Window window)
        {
            return;
        }

        var folders = await window.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select Backup Folder",
            AllowMultiple = false
        });

        if (folders.Count <= 0)
        {
            return;
        }

        MainWindow.Settings.BackupSaveDirectory = folders[0].Path.LocalPath;
        await PersistBackupSettingsAsync();
        RefreshBackupState();
    }

    private async void OnTakeBackupNowClick(object? sender, RoutedEventArgs e)
    {
        if (!TryApplyNumericSettings())
        {
            RefreshBackupState();
            return;
        }

        await PersistBackupSettingsAsync();
        if (await BackupService.CreateBackupAsync())
        {
            RefreshBackupState();
        }
    }

    private async void OnRestoreSelectedClick(object? sender, RoutedEventArgs e)
    {
        if (await BackupService.RestoreBackupAsync(BackupsListBox.SelectedItem as BackupEntry))
        {
            RefreshBackupState();
        }
    }

    private void OnRefreshClick(object? sender, RoutedEventArgs e)
    {
        RefreshBackupState();
    }

    private async void OnBackupConfigurationChanged(object? sender, RoutedEventArgs e)
    {
        if (_isRefreshing)
        {
            return;
        }

        if (!TryApplyNumericSettings())
        {
            RefreshBackupState();
            return;
        }

        await PersistBackupSettingsAsync();
    }

    private async void OnAutomaticBackupsChanged(object? sender, RoutedEventArgs e)
    {
        if (_isRefreshing)
        {
            return;
        }

        MainWindow.Settings.AutomaticBackupsEnabled = AutomaticBackupsCheckBox.IsChecked == true;
        await PersistBackupSettingsAsync();
    }

    private void OnBackupSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (BackupsListBox.SelectedItem is BackupEntry backupEntry)
        {
            RestoreSelectionTextBlock.Text = $"Restore ready: {backupEntry.Name}";
            return;
        }

        RestoreSelectionTextBlock.Text = "Select a backup to restore.";
    }

    private bool TryApplyNumericSettings()
    {
        if (!int.TryParse(BackupIntervalTextBox.Text, CultureInfo.InvariantCulture, out var intervalMinutes) || intervalMinutes <= 0)
        {
            Notifications.SendNotification("Interval must be a whole number greater than 0.", "Warning");
            return false;
        }

        if (!int.TryParse(BackupAmountTextBox.Text, CultureInfo.InvariantCulture, out var backupAmount) || backupAmount <= 0)
        {
            Notifications.SendNotification("Backup Amount must be a whole number greater than 0.", "Warning");
            return false;
        }

        MainWindow.Settings.BackupIntervalMinutes = intervalMinutes;
        MainWindow.Settings.BackupAmount = backupAmount;
        return true;
    }

    private async Task PersistBackupSettingsAsync()
    {
        await SettingsManager.SaveAsync(MainWindow.Settings);
        BackupService.UpdateSchedule();
        BackupService.EnforceBackupLimit();
        SaveDirectoryTextBlock.Text = BuildSaveDirectoryText();
        BackupsListBox.ItemsSource = BackupService.GetBackups();
    }

    private static string BuildSaveDirectoryText()
    {
        var saveDirectory = BackupService.GetResolvedSaveDirectory();
        return string.IsNullOrWhiteSpace(saveDirectory)
            ? "Save directory: install directory or modinfo.json not resolved yet."
            : $"Save directory: {saveDirectory}";
    }
}
