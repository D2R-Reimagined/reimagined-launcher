using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using ReimaginedLauncher.Utilities;

namespace ReimaginedLauncher.Views.Settings;

public partial class SettingsView : UserControl
{
    private bool _isRefreshingSettings;

    // Set by the tunnelling PointerPressed handler just before a folder button's
    // Click fires, so the handler knows whether Ctrl was held (force picker).
    private bool _forceFolderPicker;

    private enum FolderTarget
    {
        Game,
        Save,
        LauncherSettings,
        LauncherInstall
    }

    public SettingsView()
    {
        InitializeComponent();

        // Capture the Ctrl modifier before the Button consumes the pointer press.
        // Tunnelling handlers run ahead of the Button's own (bubbling) handling.
        foreach (var button in new[] { GameFolderButton, SaveFolderButton, SettingsFolderButton, LauncherFolderButton })
        {
            button.AddHandler(InputElement.PointerPressedEvent, OnFolderButtonPointerPressed, RoutingStrategies.Tunnel);
        }

        RefreshSettingsState();
    }

    public void RefreshSettingsState()
    {
        _isRefreshingSettings = true;
        UiScaleComboBox.SelectedIndex = MainWindow.Settings.UiScale switch
        {
            <= 0.85 => 0,
            <= 0.95 => 1,
            _ => 2
        };

        var profile = MainWindow.Settings.CurrentProfile;
        var isD2Rmm = profile.Type == InstallationType.D2RMM;
        LaunchParametersPanel.IsEnabled = !isD2Rmm;
        D2RmmLaunchParamsNotice.IsVisible = isD2Rmm;

        if (isD2Rmm)
        {
            NoSoundCheckBox.IsChecked = false;
            NoRumbleCheckBox.IsChecked = false;
            ForceDesktopCheckBox.IsChecked = false;
            ResetOfflineMapsCheckBox.IsChecked = false;
            EnableRespecCheckBox.IsChecked = false;
            PlayersComboBox.SelectedIndex = 0;
        }
        else
        {
            NoSoundCheckBox.IsChecked = profile.NoSound;
            NoRumbleCheckBox.IsChecked = profile.NoRumble;
            ForceDesktopCheckBox.IsChecked = profile.ForceDesktop;
            ResetOfflineMapsCheckBox.IsChecked = profile.ResetOfflineMaps;
            EnableRespecCheckBox.IsChecked = profile.EnableRespec;
            PlayersComboBox.SelectedIndex = profile.PlayersCount is >= 2 and <= 8
                ? profile.PlayersCount.Value - 1
                : 0;
        }

        MinimizeToTrayCheckBox.IsChecked = MainWindow.Settings.MinimizeToTray;
        MinimizeToTrayOnCloseCheckBox.IsChecked = MainWindow.Settings.MinimizeToTrayOnClose;

        _isRefreshingSettings = false;
    }

    private async void OnLaunchSettingChanged(object? sender, RoutedEventArgs e)
    {
        if (_isRefreshingSettings)
        {
            return;
        }

        var profile = MainWindow.Settings.CurrentProfile;
        profile.NoSound = NoSoundCheckBox.IsChecked ?? false;
        profile.NoRumble = NoRumbleCheckBox.IsChecked ?? false;
        profile.ForceDesktop = ForceDesktopCheckBox.IsChecked ?? false;
        profile.ResetOfflineMaps = ResetOfflineMapsCheckBox.IsChecked ?? false;
        profile.EnableRespec = EnableRespecCheckBox.IsChecked ?? false;
        await SettingsManager.SaveAsync(MainWindow.Settings);
    }

    private async void OnPlayersSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_isRefreshingSettings)
        {
            return;
        }

        MainWindow.Settings.CurrentProfile.PlayersCount = PlayersComboBox.SelectedIndex switch
        {
            >= 1 and <= 7 => PlayersComboBox.SelectedIndex + 1,
            _ => null
        };

        await SettingsManager.SaveAsync(MainWindow.Settings);
    }

    private async void OnMinimizeToTrayChanged(object? sender, RoutedEventArgs e)
    {
        if (_isRefreshingSettings)
        {
            return;
        }

        MainWindow.Settings.MinimizeToTray = MinimizeToTrayCheckBox.IsChecked ?? false;
        await SettingsManager.SaveAsync(MainWindow.Settings);
    }

    private async void OnMinimizeToTrayOnCloseChanged(object? sender, RoutedEventArgs e)
    {
        if (_isRefreshingSettings)
        {
            return;
        }

        MainWindow.Settings.MinimizeToTrayOnClose = MinimizeToTrayOnCloseCheckBox.IsChecked ?? false;
        await SettingsManager.SaveAsync(MainWindow.Settings);
    }

    private async void OnUiScaleSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_isRefreshingSettings)
        {
            return;
        }

        MainWindow.Settings.UiScale = UiScaleComboBox.SelectedIndex switch
        {
            0 => 0.8,
            1 => 0.9,
            _ => 1.0
        };

        if (TopLevel.GetTopLevel(this) is MainWindow mainWindow)
        {
            mainWindow.ApplyUiScale();
        }

        await SettingsManager.SaveAsync(MainWindow.Settings);
    }

    private void OnFolderButtonPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _forceFolderPicker = e.KeyModifiers.HasFlag(KeyModifiers.Control);
    }

    private async void OnGameFolderClicked(object? sender, RoutedEventArgs e)
        => await OpenOrPickFolderAsync(FolderTarget.Game);

    private async void OnSaveFolderClicked(object? sender, RoutedEventArgs e)
        => await OpenOrPickFolderAsync(FolderTarget.Save);

    private async void OnSettingsFolderClicked(object? sender, RoutedEventArgs e)
        => await OpenOrPickFolderAsync(FolderTarget.LauncherSettings);

    private async void OnLauncherFolderClicked(object? sender, RoutedEventArgs e)
        => await OpenOrPickFolderAsync(FolderTarget.LauncherInstall);

    private async Task OpenOrPickFolderAsync(FolderTarget target)
    {
        // Ctrl+Click always forces the picker; reset the flag so a later
        // keyboard-activated click doesn't inherit a stale value.
        var forcePicker = _forceFolderPicker;
        _forceFolderPicker = false;

        var path = forcePicker ? null : ResolveExistingFolder(target);

        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            var picked = await PromptForFolderAsync(GetPickerTitle(target));
            if (string.IsNullOrWhiteSpace(picked))
            {
                return;
            }

            await SaveFolderOverrideAsync(target, picked);
            path = picked;
        }

        OpenFolder(path);
    }

    private static string? ResolveExistingFolder(FolderTarget target)
    {
        var settings = MainWindow.Settings;
        var profile = settings.CurrentProfile;

        switch (target)
        {
            case FolderTarget.Game:
                if (!string.IsNullOrWhiteSpace(profile.ReimaginedModFolderOverride) &&
                    Directory.Exists(profile.ReimaginedModFolderOverride))
                {
                    return profile.ReimaginedModFolderOverride;
                }

                return ResolveGameModFolder();

            case FolderTarget.Save:
                var savePath = BackupService.GetResolvedSaveDirectory();
                return string.IsNullOrWhiteSpace(savePath) ? null : savePath;

            case FolderTarget.LauncherSettings:
                if (!string.IsNullOrWhiteSpace(settings.LauncherSettingsFolderOverride) &&
                    Directory.Exists(settings.LauncherSettingsFolderOverride))
                {
                    return settings.LauncherSettingsFolderOverride;
                }

                Directory.CreateDirectory(SettingsManager.AppDirectoryPath);
                return SettingsManager.AppDirectoryPath;

            case FolderTarget.LauncherInstall:
                if (!string.IsNullOrWhiteSpace(settings.LauncherInstallFolderOverride) &&
                    Directory.Exists(settings.LauncherInstallFolderOverride))
                {
                    return settings.LauncherInstallFolderOverride;
                }

                return AppContext.BaseDirectory;

            default:
                return null;
        }
    }

    private static string? ResolveGameModFolder()
    {
        var installDirectory = MainWindow.Settings.CurrentProfile.InstallDirectory;
        if (string.IsNullOrWhiteSpace(installDirectory))
        {
            return null;
        }

        var modsPath = SaveFileService.ResolveDirectoryCaseInsensitive(installDirectory, "mods");
        if (modsPath == null)
        {
            return null;
        }

        var reimaginedPath = SaveFileService.ResolveDirectoryCaseInsensitive(modsPath, "Reimagined");
        if (reimaginedPath == null)
        {
            return null;
        }

        return SaveFileService.ResolveDirectoryCaseInsensitive(reimaginedPath, "Reimagined.mpq");
    }

    private static async Task SaveFolderOverrideAsync(FolderTarget target, string path)
    {
        var settings = MainWindow.Settings;

        switch (target)
        {
            case FolderTarget.Game:
                settings.CurrentProfile.ReimaginedModFolderOverride = path;
                break;
            case FolderTarget.Save:
                settings.CurrentProfile.SaveDirectory = path;
                break;
            case FolderTarget.LauncherSettings:
                settings.LauncherSettingsFolderOverride = path;
                break;
            case FolderTarget.LauncherInstall:
                settings.LauncherInstallFolderOverride = path;
                break;
        }

        await SettingsManager.SaveAsync(settings);
    }

    private async Task<string?> PromptForFolderAsync(string title)
    {
        if (TopLevel.GetTopLevel(this) is not Window window)
        {
            return null;
        }

        var folders = await window.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false
        });

        return folders.Count > 0 ? folders[0].Path.LocalPath : null;
    }

    private static string GetPickerTitle(FolderTarget target) => target switch
    {
        FolderTarget.Game => "Locate the Reimagined.mpq game mod folder",
        FolderTarget.Save => "Locate the save folder",
        FolderTarget.LauncherSettings => "Locate the launcher settings folder",
        FolderTarget.LauncherInstall => "Locate the launcher installation folder",
        _ => "Select a folder"
    };

    private static void OpenFolder(string path)
    {
        try
        {
            if (OperatingSystem.IsLinux())
            {
                Process.Start(new ProcessStartInfo { FileName = "xdg-open", Arguments = $"\"{path}\"", UseShellExecute = false });
            }
            else if (OperatingSystem.IsMacOS())
            {
                Process.Start(new ProcessStartInfo { FileName = "open", Arguments = $"\"{path}\"", UseShellExecute = false });
            }
            else
            {
                Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            Notifications.SendNotification($"Could not open folder: {ex.Message}", "Warning");
        }
    }
}
