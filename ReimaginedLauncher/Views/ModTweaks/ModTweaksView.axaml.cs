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

        var skillPointsPerLevel = Clamp(MainWindow.Settings.SkillPointsPerLevel, 1, 5);
        var attributesPerLevel = Clamp(MainWindow.Settings.AttributesPerLevel, 1, 20);
        var maxSkillLevel = Clamp(MainWindow.Settings.MaxSkillLevel, 5, 25);
        var normalResistPenalty = MainWindow.Settings.NormalResistPenalty;
        var nightmareResistPenalty = MainWindow.Settings.NightmareResistPenalty;
        var hellResistPenalty = MainWindow.Settings.HellResistPenalty;
        var removePaladinAuraSound = MainWindow.Settings.RemovePaladinAuraSound;
        var removeSplashVfx = MainWindow.Settings.RemoveSplashVfx;
        var makeTooltipBackgroundOpaque = MainWindow.Settings.MakeTooltipBackgroundOpaque;
        var removeHelmetVisual = MainWindow.Settings.RemoveHelmetVisual;
        var terrorizeAllZones = MainWindow.Settings.TerrorizeAllZones;

        MainWindow.Settings.SkillPointsPerLevel = skillPointsPerLevel;
        MainWindow.Settings.AttributesPerLevel = attributesPerLevel;
        MainWindow.Settings.MaxSkillLevel = maxSkillLevel;
        MainWindow.Settings.NormalResistPenalty = normalResistPenalty;
        MainWindow.Settings.NightmareResistPenalty = nightmareResistPenalty;
        MainWindow.Settings.HellResistPenalty = hellResistPenalty;
        MainWindow.Settings.RemovePaladinAuraSound = removePaladinAuraSound;
        MainWindow.Settings.RemoveSplashVfx = removeSplashVfx;
        MainWindow.Settings.MakeTooltipBackgroundOpaque = makeTooltipBackgroundOpaque;
        MainWindow.Settings.RemoveHelmetVisual = removeHelmetVisual;
        MainWindow.Settings.TerrorizeAllZones = terrorizeAllZones;

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

        MainWindow.Settings.SkillPointsPerLevel = SkillPointsComboBox.SelectedIndex + 1;
        MainWindow.Settings.AttributesPerLevel = AttributesComboBox.SelectedIndex + 1;
        MainWindow.Settings.MaxSkillLevel = MaxSkillLevelComboBox.SelectedIndex + 5;
        WarningBorder.IsVisible = HasNonDefaultTweaks(
            MainWindow.Settings.SkillPointsPerLevel,
            MainWindow.Settings.AttributesPerLevel,
            MainWindow.Settings.MaxSkillLevel,
            MainWindow.Settings.NormalResistPenalty,
            MainWindow.Settings.NightmareResistPenalty,
            MainWindow.Settings.HellResistPenalty);

        await SettingsManager.SaveAsync(MainWindow.Settings);
    }

    private async void OnSoundTweaksChanged(object? sender, RoutedEventArgs e)
    {
        if (_isRefreshing)
        {
            return;
        }

        MainWindow.Settings.RemovePaladinAuraSound = RemovePaladinAuraSoundCheckBox.IsChecked ?? false;
        WarningBorder.IsVisible = HasNonDefaultTweaks(
            MainWindow.Settings.SkillPointsPerLevel,
            MainWindow.Settings.AttributesPerLevel,
            MainWindow.Settings.MaxSkillLevel,
            MainWindow.Settings.NormalResistPenalty,
            MainWindow.Settings.NightmareResistPenalty,
            MainWindow.Settings.HellResistPenalty);
        await SettingsManager.SaveAsync(MainWindow.Settings);
    }

    private async void OnZoneTweaksChanged(object? sender, RoutedEventArgs e)
    {
        if (_isRefreshing)
        {
            return;
        }

        MainWindow.Settings.TerrorizeAllZones = TerrorizeAllZonesCheckBox.IsChecked ?? false;
        await SettingsManager.SaveAsync(MainWindow.Settings);
    }

    private async void OnVisualTweaksChanged(object? sender, RoutedEventArgs e)
    {
        if (_isRefreshing)
        {
            return;
        }

        MainWindow.Settings.RemoveSplashVfx = RemoveSplashVfxCheckBox.IsChecked ?? false;
        MainWindow.Settings.MakeTooltipBackgroundOpaque = MakeTooltipBackgroundOpaqueCheckBox.IsChecked ?? false;
        MainWindow.Settings.RemoveHelmetVisual = RemoveHelmetVisualCheckBox.IsChecked ?? false;
        WarningBorder.IsVisible = HasNonDefaultTweaks(
            MainWindow.Settings.SkillPointsPerLevel,
            MainWindow.Settings.AttributesPerLevel,
            MainWindow.Settings.MaxSkillLevel,
            MainWindow.Settings.NormalResistPenalty,
            MainWindow.Settings.NightmareResistPenalty,
            MainWindow.Settings.HellResistPenalty);
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

        WarningBorder.IsVisible = HasNonDefaultTweaks(
            MainWindow.Settings.SkillPointsPerLevel,
            MainWindow.Settings.AttributesPerLevel,
            MainWindow.Settings.MaxSkillLevel,
            MainWindow.Settings.NormalResistPenalty,
            MainWindow.Settings.NightmareResistPenalty,
            MainWindow.Settings.HellResistPenalty);
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

        MainWindow.Settings.NormalResistPenalty = normalResistPenalty;
        MainWindow.Settings.NightmareResistPenalty = nightmareResistPenalty;
        MainWindow.Settings.HellResistPenalty = hellResistPenalty;
        return true;
    }

    private void StepResistPenaltyValue(TextBox textBox, int step)
    {
        var fallbackValue = textBox.Name switch
        {
            nameof(NormalResistPenaltyTextBox) => MainWindow.Settings.NormalResistPenalty,
            nameof(NightmareResistPenaltyTextBox) => MainWindow.Settings.NightmareResistPenalty,
            nameof(HellResistPenaltyTextBox) => MainWindow.Settings.HellResistPenalty,
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
