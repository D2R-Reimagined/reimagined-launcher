using Avalonia.Controls;
using Avalonia.Interactivity;
using ReimaginedLauncher.Utilities;

namespace ReimaginedLauncher.Views.ModTweaks;

public partial class ModTweaksView : UserControl
{
    private const int DefaultSkillPointsPerLevel = 1;
    private const int DefaultAttributesPerLevel = 5;
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

        MainWindow.Settings.SkillPointsPerLevel = skillPointsPerLevel;
        MainWindow.Settings.AttributesPerLevel = attributesPerLevel;

        SkillPointsComboBox.SelectedIndex = skillPointsPerLevel - 1;
        AttributesComboBox.SelectedIndex = attributesPerLevel - 1;
        WarningBorder.IsVisible = skillPointsPerLevel != DefaultSkillPointsPerLevel
                                  || attributesPerLevel != DefaultAttributesPerLevel;

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
        WarningBorder.IsVisible = MainWindow.Settings.SkillPointsPerLevel != DefaultSkillPointsPerLevel
                                  || MainWindow.Settings.AttributesPerLevel != DefaultAttributesPerLevel;

        await SettingsManager.SaveAsync(MainWindow.Settings);
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
