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
            ? "Bundled add-ons appear here."
            : $"{catalog.Count} official add-ons available.";
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
            Notifications.SendNotification($"Official add-on '{plugin.Name}' is enabled.", "Success");
        }
        catch (Exception ex)
        {
            Notifications.SendNotification($"Official add-on update failed: {ex.Message}", "Warning");
        }
    }
}
