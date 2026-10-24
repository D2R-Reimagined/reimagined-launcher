using System;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace ReimaginedLauncher.Views.Launch;

public partial class SteamDeckSetupGuideView : UserControl
{
    private readonly Action? _onBack;

    public SteamDeckSetupGuideView() : this(null)
    {
    }

    public SteamDeckSetupGuideView(Action? onBack)
    {
        _onBack = onBack;
        InitializeComponent();
    }

    private void OnBackClicked(object? sender, RoutedEventArgs e)
    {
        if (_onBack != null)
        {
            _onBack();
            return;
        }

        if (TopLevel.GetTopLevel(this) is MainWindow window)
        {
            _ = window.NavigateToLaunchViewAsync();
        }
    }
}
