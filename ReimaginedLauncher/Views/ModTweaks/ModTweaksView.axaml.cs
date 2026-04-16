using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using ReimaginedLauncher.Utilities;

namespace ReimaginedLauncher.Views.ModTweaks;

public partial class ModTweaksView : UserControl
{
    private const int DefaultSkillPointsPerLevel = 1;
    private const int DefaultAttributesPerLevel = 5;
    private const int DefaultMaxSkillLevel = 25;
    private const int DefaultNormalResistPenalty = 0;
    private const int DefaultNightmareResistPenalty = -60;
    private const int DefaultHellResistPenalty = -120;
    private bool _isRefreshing;

    public ModTweaksView()
    {
        InitializeComponent();
        RefreshTweaksState();
    }

    public void RefreshTweaksState()
    {
        _isRefreshing = true;

        var profile = MainWindow.Settings.CurrentProfile;
        var skillPointsPerLevel = Clamp(profile.SkillPointsPerLevel, 1, 5);
        var attributesPerLevel = Clamp(profile.AttributesPerLevel, 1, 20);
        var maxSkillLevel = Clamp(profile.MaxSkillLevel, 5, 25);
        var normalResistPenalty = profile.NormalResistPenalty;
        var nightmareResistPenalty = profile.NightmareResistPenalty;
        var hellResistPenalty = profile.HellResistPenalty;
        var removePaladinAuraSound = profile.RemovePaladinAuraSound;
        var removeSplashVfx = profile.RemoveSplashVfx;
        var makeTooltipBackgroundOpaque = profile.MakeTooltipBackgroundOpaque;
        var removeHelmetVisual = profile.RemoveHelmetVisual;
        var terrorizeAllZones = profile.TerrorizeAllZones;
        var terrorZonePurpleOverlay = profile.TerrorZonePurpleOverlay;
        var restoreTerrorZoneFanfare = profile.RestoreTerrorZoneFanfare;

        profile.SkillPointsPerLevel = skillPointsPerLevel;
        profile.AttributesPerLevel = attributesPerLevel;
        profile.MaxSkillLevel = maxSkillLevel;
        profile.NormalResistPenalty = normalResistPenalty;
        profile.NightmareResistPenalty = nightmareResistPenalty;
        profile.HellResistPenalty = hellResistPenalty;
        profile.RemovePaladinAuraSound = removePaladinAuraSound;
        profile.RemoveSplashVfx = removeSplashVfx;
        profile.MakeTooltipBackgroundOpaque = makeTooltipBackgroundOpaque;
        profile.RemoveHelmetVisual = removeHelmetVisual;
        profile.TerrorizeAllZones = terrorizeAllZones;
        profile.TerrorZonePurpleOverlay = terrorZonePurpleOverlay;
        profile.RestoreTerrorZoneFanfare = restoreTerrorZoneFanfare;

        SkillPointsComboBox.SelectedIndex = skillPointsPerLevel - 1;
        AttributesComboBox.SelectedIndex = attributesPerLevel - 1;
        MaxSkillLevelComboBox.SelectedIndex = maxSkillLevel - 5;
        NormalResistPenaltyTextBox.Text = normalResistPenalty.ToString();
        NightmareResistPenaltyTextBox.Text = nightmareResistPenalty.ToString();
        HellResistPenaltyTextBox.Text = hellResistPenalty.ToString();
        RemovePaladinAuraSoundCheckBox.IsChecked = removePaladinAuraSound;
        RemoveSplashVfxCheckBox.IsChecked = removeSplashVfx;
        MakeTooltipBackgroundOpaqueCheckBox.IsChecked = makeTooltipBackgroundOpaque;
        RemoveHelmetVisualCheckBox.IsChecked = removeHelmetVisual;
        TerrorizeAllZonesCheckBox.IsChecked = terrorizeAllZones;
        TerrorZonePurpleOverlayCheckBox.IsChecked = terrorZonePurpleOverlay;
        RestoreTerrorZoneFanfareCheckBox.IsChecked = restoreTerrorZoneFanfare;
        WarningBorder.IsVisible = HasNonDefaultTweaks(
            skillPointsPerLevel,
            attributesPerLevel,
            maxSkillLevel,
            normalResistPenalty,
            nightmareResistPenalty,
            hellResistPenalty);

        _isRefreshing = false;
    }

    private async void OnTweaksSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_isRefreshing)
        {
            return;
        }

        var profile = MainWindow.Settings.CurrentProfile;
        profile.SkillPointsPerLevel = SkillPointsComboBox.SelectedIndex + 1;
        profile.AttributesPerLevel = AttributesComboBox.SelectedIndex + 1;
        profile.MaxSkillLevel = MaxSkillLevelComboBox.SelectedIndex + 5;
        WarningBorder.IsVisible = HasNonDefaultTweaks(
            profile.SkillPointsPerLevel,
            profile.AttributesPerLevel,
            profile.MaxSkillLevel,
            profile.NormalResistPenalty,
            profile.NightmareResistPenalty,
            profile.HellResistPenalty);

        await SettingsManager.SaveAsync(MainWindow.Settings);
    }

    private async void OnSoundTweaksChanged(object? sender, RoutedEventArgs e)
    {
        if (_isRefreshing)
        {
            return;
        }

        var profile = MainWindow.Settings.CurrentProfile;
        profile.RemovePaladinAuraSound = RemovePaladinAuraSoundCheckBox.IsChecked ?? false;
        profile.RestoreTerrorZoneFanfare = RestoreTerrorZoneFanfareCheckBox.IsChecked ?? false;
        WarningBorder.IsVisible = HasNonDefaultTweaks(
            profile.SkillPointsPerLevel,
            profile.AttributesPerLevel,
            profile.MaxSkillLevel,
            profile.NormalResistPenalty,
            profile.NightmareResistPenalty,
            profile.HellResistPenalty);
        await SettingsManager.SaveAsync(MainWindow.Settings);
    }

    private async void OnZoneTweaksChanged(object? sender, RoutedEventArgs e)
    {
        if (_isRefreshing)
        {
            return;
        }

        MainWindow.Settings.CurrentProfile.TerrorizeAllZones = TerrorizeAllZonesCheckBox.IsChecked ?? false;
        await SettingsManager.SaveAsync(MainWindow.Settings);
    }

    private async void OnVisualTweaksChanged(object? sender, RoutedEventArgs e)
    {
        if (_isRefreshing)
        {
            return;
        }

        var profile = MainWindow.Settings.CurrentProfile;
        profile.RemoveSplashVfx = RemoveSplashVfxCheckBox.IsChecked ?? false;
        profile.MakeTooltipBackgroundOpaque = MakeTooltipBackgroundOpaqueCheckBox.IsChecked ?? false;
        profile.RemoveHelmetVisual = RemoveHelmetVisualCheckBox.IsChecked ?? false;
        profile.TerrorZonePurpleOverlay = TerrorZonePurpleOverlayCheckBox.IsChecked ?? false;
        WarningBorder.IsVisible = HasNonDefaultTweaks(
            profile.SkillPointsPerLevel,
            profile.AttributesPerLevel,
            profile.MaxSkillLevel,
            profile.NormalResistPenalty,
            profile.NightmareResistPenalty,
            profile.HellResistPenalty);
        await SettingsManager.SaveAsync(MainWindow.Settings);
    }

    private async void OnResistPenaltyChanged(object? sender, RoutedEventArgs e)
    {
        await SaveResistPenaltyValuesAsync();
    }

    private async void OnResistPenaltyKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox textBox)
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await SaveResistPenaltyValuesAsync();
            RootScrollViewer.Focus();
            return;
        }

        if (e.Key != Key.Up && e.Key != Key.Down)
        {
            return;
        }

        e.Handled = true;
        StepResistPenaltyValue(textBox, e.Key == Key.Up ? 5 : -5);
        await SaveResistPenaltyValuesAsync();
    }

    private async void OnResetTweaksClick(object? sender, RoutedEventArgs e)
    {
        var profile = MainWindow.Settings.CurrentProfile;
        profile.SkillPointsPerLevel = DefaultSkillPointsPerLevel;
        profile.AttributesPerLevel = DefaultAttributesPerLevel;
        profile.MaxSkillLevel = DefaultMaxSkillLevel;
        profile.NormalResistPenalty = DefaultNormalResistPenalty;
        profile.NightmareResistPenalty = DefaultNightmareResistPenalty;
        profile.HellResistPenalty = DefaultHellResistPenalty;
        profile.RemovePaladinAuraSound = false;
        profile.RemoveSplashVfx = false;
        profile.MakeTooltipBackgroundOpaque = false;
        profile.RemoveHelmetVisual = false;
        profile.TerrorizeAllZones = false;
        profile.TerrorZonePurpleOverlay = false;
        profile.RestoreTerrorZoneFanfare = false;

        await SettingsManager.SaveAsync(MainWindow.Settings);
        RefreshTweaksState();
    }

    private void OnBackgroundPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is TextBox)
        {
            return;
        }

        RootScrollViewer.Focus();
    }

    private async Task SaveResistPenaltyValuesAsync()
    {
        if (_isRefreshing)
        {
            return;
        }

        if (!TryApplyResistPenaltyValues())
        {
            RefreshTweaksState();
            Notifications.SendNotification(
                "Resist penalties must be whole numbers.",
                "Use numeric values for Normal, Nightmare, and Hell resist penalties.");
            return;
        }

        var profile = MainWindow.Settings.CurrentProfile;
        WarningBorder.IsVisible = HasNonDefaultTweaks(
            profile.SkillPointsPerLevel,
            profile.AttributesPerLevel,
            profile.MaxSkillLevel,
            profile.NormalResistPenalty,
            profile.NightmareResistPenalty,
            profile.HellResistPenalty);
        await SettingsManager.SaveAsync(MainWindow.Settings);
    }
    private bool TryApplyResistPenaltyValues()
    {
        if (!int.TryParse(NormalResistPenaltyTextBox.Text, out var normalResistPenalty) ||
            !int.TryParse(NightmareResistPenaltyTextBox.Text, out var nightmareResistPenalty) ||
            !int.TryParse(HellResistPenaltyTextBox.Text, out var hellResistPenalty))
        {
            return false;
        }

        var profile = MainWindow.Settings.CurrentProfile;
        profile.NormalResistPenalty = normalResistPenalty;
        profile.NightmareResistPenalty = nightmareResistPenalty;
        profile.HellResistPenalty = hellResistPenalty;
        return true;
    }

    private void StepResistPenaltyValue(TextBox textBox, int step)
    {
        var profile = MainWindow.Settings.CurrentProfile;
        var fallbackValue = textBox.Name switch
        {
            nameof(NormalResistPenaltyTextBox) => profile.NormalResistPenalty,
            nameof(NightmareResistPenaltyTextBox) => profile.NightmareResistPenalty,
            nameof(HellResistPenaltyTextBox) => profile.HellResistPenalty,
            _ => 0
        };

        var currentValue = int.TryParse(textBox.Text, out var parsedValue)
            ? parsedValue
            : fallbackValue;
        textBox.Text = (currentValue + step).ToString();
        textBox.CaretIndex = textBox.Text.Length;
    }

    private static bool HasNonDefaultTweaks(
        int skillPointsPerLevel,
        int attributesPerLevel,
        int maxSkillLevel,
        int normalResistPenalty,
        int nightmareResistPenalty,
        int hellResistPenalty)
    {
        return skillPointsPerLevel != DefaultSkillPointsPerLevel
               || attributesPerLevel != DefaultAttributesPerLevel
               || maxSkillLevel != DefaultMaxSkillLevel
               || normalResistPenalty != DefaultNormalResistPenalty
               || nightmareResistPenalty != DefaultNightmareResistPenalty
               || hellResistPenalty != DefaultHellResistPenalty;
    }

    private static int Clamp(int value, int min, int max)
    {
        if (value < min)
        {
            return min;
        }

        return value > max ? max : value;
    }
}
