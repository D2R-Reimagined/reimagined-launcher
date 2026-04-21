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
        var profile = MainWindow.Settings.CurrentProfile;
        SaveDirectoryTextBox.Text = BackupService.GetResolvedSaveDirectory();
        BackupDirectoryTextBox.Text = profile.BackupSaveDirectory ?? string.Empty;
        AutomaticBackupsCheckBox.IsChecked = profile.AutomaticBackupsEnabled;
        BackupIntervalTextBox.Text = profile.BackupIntervalMinutes.ToString(CultureInfo.InvariantCulture);
        BackupAmountTextBox.Text = profile.BackupAmount.ToString(CultureInfo.InvariantCulture);
        SaveDirectoryTextBlock.Text = BuildSaveDirectoryText();
        BackupsListBox.ItemsSource = BackupService.GetBackups();
        RestoreSelectionTextBlock.Text = "Select a backup to restore.";
        UpdateSaveDirectoryBrowseState();
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

    private async void OnSaveDirectoryClick(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not Window window)
        {
            return;
        }

        var folders = await window.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select Save Folder",
            AllowMultiple = false
        });

        if (folders.Count <= 0)
        {
            return;
        }

        MainWindow.Settings.CurrentProfile.SaveDirectory = folders[0].Path.LocalPath;
        await PersistBackupSettingsAsync();
        RefreshBackupState();
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

        MainWindow.Settings.CurrentProfile.BackupSaveDirectory = folders[0].Path.LocalPath;
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

        MainWindow.Settings.CurrentProfile.AutomaticBackupsEnabled = AutomaticBackupsCheckBox.IsChecked == true;
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

        var profile = MainWindow.Settings.CurrentProfile;
        profile.BackupIntervalMinutes = intervalMinutes;
        profile.BackupAmount = backupAmount;
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

    private void UpdateSaveDirectoryBrowseState()
    {
        var profile = MainWindow.Settings.CurrentProfile;

        if (profile.Type == InstallationType.D2RMM)
        {
            // D2RMM: save directory must always be selected manually.
            SaveDirectoryBrowseButton.IsEnabled = true;
            return;
        }

        // Steam / B.net: disable browse when auto-resolution succeeds.
        var autoResolved = BackupService.GetAutoResolvedSaveDirectory();
        SaveDirectoryBrowseButton.IsEnabled = string.IsNullOrWhiteSpace(autoResolved);
    }

    private static string BuildSaveDirectoryText()
    {
        var saveDirectory = BackupService.GetResolvedSaveDirectory();
        return string.IsNullOrWhiteSpace(saveDirectory)
            ? "Save directory: not resolved — use Browse to select it."
            : $"Save directory: {saveDirectory}";
    }
}
