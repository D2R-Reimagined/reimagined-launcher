using Avalonia.Controls;
using Avalonia.Interactivity;
using ReimaginedLauncher.Utilities;

namespace ReimaginedLauncher.Views.Settings;

public partial class SettingsView : UserControl
{
    private bool _isRefreshingSettings;

    public SettingsView()
    {
        InitializeComponent();
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
}
