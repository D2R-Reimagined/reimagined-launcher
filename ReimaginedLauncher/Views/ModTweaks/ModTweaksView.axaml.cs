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
        var maxSkillLevel = Clamp(MainWindow.Settings.MaxSkillLevel, 1, 99);
        var normalResistPenalty = MainWindow.Settings.NormalResistPenalty;
        var nightmareResistPenalty = MainWindow.Settings.NightmareResistPenalty;
        var hellResistPenalty = MainWindow.Settings.HellResistPenalty;
        var removeSplashVfx = MainWindow.Settings.RemoveSplashVfx;

        MainWindow.Settings.SkillPointsPerLevel = skillPointsPerLevel;
        MainWindow.Settings.AttributesPerLevel = attributesPerLevel;
        MainWindow.Settings.MaxSkillLevel = maxSkillLevel;
        MainWindow.Settings.NormalResistPenalty = normalResistPenalty;
        MainWindow.Settings.NightmareResistPenalty = nightmareResistPenalty;
        MainWindow.Settings.HellResistPenalty = hellResistPenalty;
        MainWindow.Settings.RemoveSplashVfx = removeSplashVfx;

        SkillPointsComboBox.SelectedIndex = skillPointsPerLevel - 1;
        AttributesComboBox.SelectedIndex = attributesPerLevel - 1;
        MaxSkillLevelTextBox.Text = maxSkillLevel.ToString();
        NormalResistPenaltyTextBox.Text = normalResistPenalty.ToString();
        NightmareResistPenaltyTextBox.Text = nightmareResistPenalty.ToString();
        HellResistPenaltyTextBox.Text = hellResistPenalty.ToString();
        RemoveSplashVfxCheckBox.IsChecked = removeSplashVfx;
        WarningBorder.IsVisible = HasNonDefaultTweaks(
            skillPointsPerLevel,
            attributesPerLevel,
            maxSkillLevel,
            normalResistPenalty,
            nightmareResistPenalty,
            hellResistPenalty,
            removeSplashVfx);

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
        WarningBorder.IsVisible = HasNonDefaultTweaks(
            MainWindow.Settings.SkillPointsPerLevel,
            MainWindow.Settings.AttributesPerLevel,
            MainWindow.Settings.MaxSkillLevel,
            MainWindow.Settings.NormalResistPenalty,
            MainWindow.Settings.NightmareResistPenalty,
            MainWindow.Settings.HellResistPenalty,
            MainWindow.Settings.RemoveSplashVfx);

        await SettingsManager.SaveAsync(MainWindow.Settings);
    }

    private async void OnVisualTweaksChanged(object? sender, RoutedEventArgs e)
    {
        if (_isRefreshing)
        {
            return;
        }

        MainWindow.Settings.RemoveSplashVfx = RemoveSplashVfxCheckBox.IsChecked ?? false;
        WarningBorder.IsVisible = HasNonDefaultTweaks(
            MainWindow.Settings.SkillPointsPerLevel,
            MainWindow.Settings.AttributesPerLevel,
            MainWindow.Settings.MaxSkillLevel,
            MainWindow.Settings.NormalResistPenalty,
            MainWindow.Settings.NightmareResistPenalty,
            MainWindow.Settings.HellResistPenalty,
            MainWindow.Settings.RemoveSplashVfx);
        await SettingsManager.SaveAsync(MainWindow.Settings);
    }

    private async void OnMaxSkillLevelChanged(object? sender, RoutedEventArgs e)
    {
        await SaveMaxSkillLevelAsync();
    }

    private async void OnMaxSkillLevelKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox textBox)
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await SaveMaxSkillLevelAsync();
            RootScrollViewer.Focus();
            return;
        }

        if (e.Key != Key.Up && e.Key != Key.Down)
        {
            return;
        }

        e.Handled = true;
        StepNumericValue(textBox, e.Key == Key.Up ? 1 : -1, MainWindow.Settings.MaxSkillLevel);
        await SaveMaxSkillLevelAsync();
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
            MainWindow.Settings.HellResistPenalty,
            MainWindow.Settings.RemoveSplashVfx);
        await SettingsManager.SaveAsync(MainWindow.Settings);
    }

    private async Task SaveMaxSkillLevelAsync()
    {
        if (_isRefreshing)
        {
            return;
        }

        if (!TryApplyMaxSkillLevel())
        {
            RefreshTweaksState();
            Notifications.SendNotification(
                "Max Skill Level must be a whole number.",
                "Use a numeric value for the global max skill level.");
            return;
        }

        WarningBorder.IsVisible = HasNonDefaultTweaks(
            MainWindow.Settings.SkillPointsPerLevel,
            MainWindow.Settings.AttributesPerLevel,
            MainWindow.Settings.MaxSkillLevel,
            MainWindow.Settings.NormalResistPenalty,
            MainWindow.Settings.NightmareResistPenalty,
            MainWindow.Settings.HellResistPenalty,
            MainWindow.Settings.RemoveSplashVfx);
        await SettingsManager.SaveAsync(MainWindow.Settings);
    }

    private bool TryApplyMaxSkillLevel()
    {
        if (!int.TryParse(MaxSkillLevelTextBox.Text, out var maxSkillLevel))
        {
            return false;
        }

        MainWindow.Settings.MaxSkillLevel = Clamp(maxSkillLevel, 1, 99);
        MaxSkillLevelTextBox.Text = MainWindow.Settings.MaxSkillLevel.ToString();
        return true;
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

    private static void StepNumericValue(TextBox textBox, int step, int fallbackValue)
    {
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
        int hellResistPenalty,
        bool removeSplashVfx)
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
