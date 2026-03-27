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
        DirectLaunchCheckBox.IsChecked = MainWindow.Settings.UseDirectLaunch;
        NoSoundCheckBox.IsChecked = MainWindow.Settings.NoSound;
        SkipLogoVideoCheckBox.IsChecked = MainWindow.Settings.SkipLogoVideo;
        NoRumbleCheckBox.IsChecked = MainWindow.Settings.NoRumble;
        ResetOfflineMapsCheckBox.IsChecked = MainWindow.Settings.ResetOfflineMaps;
        EnableRespecCheckBox.IsChecked = MainWindow.Settings.EnableRespec;
        PlayersComboBox.SelectedIndex = MainWindow.Settings.PlayersCount is >= 2 and <= 8
            ? MainWindow.Settings.PlayersCount.Value - 1
            : 0;
        _isRefreshingSettings = false;
    }

    private async void OnLaunchSettingChanged(object? sender, RoutedEventArgs e)
    {
        if (_isRefreshingSettings)
        {
            return;
        }

        MainWindow.Settings.UseDirectLaunch = DirectLaunchCheckBox.IsChecked ?? false;
        MainWindow.Settings.NoSound = NoSoundCheckBox.IsChecked ?? false;
        MainWindow.Settings.SkipLogoVideo = SkipLogoVideoCheckBox.IsChecked ?? false;
        MainWindow.Settings.NoRumble = NoRumbleCheckBox.IsChecked ?? false;
        MainWindow.Settings.ResetOfflineMaps = ResetOfflineMapsCheckBox.IsChecked ?? false;
        MainWindow.Settings.EnableRespec = EnableRespecCheckBox.IsChecked ?? false;
        await SettingsManager.SaveAsync(MainWindow.Settings);
    }

    private async void OnPlayersSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_isRefreshingSettings)
        {
            return;
        }

        MainWindow.Settings.PlayersCount = PlayersComboBox.SelectedIndex switch
        {
            >= 1 and <= 7 => PlayersComboBox.SelectedIndex + 1,
            _ => null
        };

        await SettingsManager.SaveAsync(MainWindow.Settings);
    }
}
