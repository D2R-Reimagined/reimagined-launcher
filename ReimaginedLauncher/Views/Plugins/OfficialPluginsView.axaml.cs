using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using ReimaginedLauncher.Utilities;

namespace ReimaginedLauncher.Views.Plugins;

public partial class OfficialPluginsView : UserControl
{
    public OfficialPluginsView()
    {
        InitializeComponent();
        _ = RefreshOfficialPluginsStateAsync();
    }

    public async Task RefreshOfficialPluginsStateAsync()
    {
        var catalog = await PluginsService.GetOfficialCatalogAsync();
        OfficialPluginsItemsControl.ItemsSource = catalog;
        EmptyStatePanel.IsVisible = catalog.Count == 0;
        OfficialPluginsSummaryTextBlock.Text = catalog.Count == 0
            ? "Bundled plugins appear here."
            : $"{catalog.Count} official plugin(s) available.";
    }

    private async void OnRefreshClicked(object? sender, RoutedEventArgs e)
    {
        await RefreshOfficialPluginsStateAsync();
    }

    private void OnBackClicked(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is MainWindow window)
        {
            window.NavigateToPluginsView();
        }
    }

    private async void OnInstallOrEnableClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: OfficialPluginCatalogItem plugin })
        {
            return;
        }

        try
        {
            await PluginsService.InstallOfficialPluginAsync(plugin.FolderName);
            await RefreshOfficialPluginsStateAsync();
            Notifications.SendNotification($"Official plugin '{plugin.Name}' is enabled.", "Success");
        }
        catch (Exception ex)
        {
            Notifications.SendNotification($"Official plugin update failed: {ex.Message}", "Warning");
        }
    }
}
