using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Microsoft.Extensions.DependencyInjection;
using ReimaginedLauncher.HttpClients;
using ReimaginedLauncher.HttpClients.Models;
using ReimaginedLauncher.Utilities;

namespace ReimaginedLauncher.Views.Plugins;

public partial class UserPluginsView : UserControl
{
    public UserPluginsView()
    {
        InitializeComponent();
        _ = RefreshUserPluginsAsync();
    }

    public async Task RefreshUserPluginsAsync()
    {
        LoadingBanner.IsVisible = true;
        EmptyStatePanel.IsVisible = false;
        UserPluginsItemsControl.ItemsSource = null;

        try
        {
            var client = Program.ServiceProvider.GetRequiredService<GitHubDiscussionPluginsHttpClient>();
            var plugins = await client.GetUserPluginsAsync();

            UserPluginsItemsControl.ItemsSource = plugins;
            EmptyStatePanel.IsVisible = plugins.Count == 0;
            UserPluginsSummaryTextBlock.Text = plugins.Count == 0
                ? "No user plugins were found."
                : $"{plugins.Count} user plugin(s) available.";
        }
        catch (Exception ex)
        {
            UserPluginsSummaryTextBlock.Text = $"Failed to load user plugins: {ex.Message}";
            EmptyStatePanel.IsVisible = true;
        }
        finally
        {
            LoadingBanner.IsVisible = false;
        }
    }

    private async void OnRefreshClicked(object? sender, RoutedEventArgs e)
    {
        await RefreshUserPluginsAsync();
    }

    private void OnBackClicked(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is MainWindow window)
        {
            window.NavigateToPluginsView();
        }
    }

    private void OnViewOnGitHubClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: UserPluginEntry plugin } ||
            string.IsNullOrWhiteSpace(plugin.DiscussionUrl))
        {
            return;
        }

        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = plugin.DiscussionUrl,
                UseShellExecute = true
            };
            process.Start();
        }
        catch (Exception)
        {
            // Keep launcher stable if the shell cannot open the URL.
        }
    }

    private async void OnInstallClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: UserPluginEntry plugin })
        {
            return;
        }

        string? tempZipPath = null;

        try
        {
            Notifications.SendNotification($"Downloading '{plugin.Title}'...", "Info");

            var client = Program.ServiceProvider.GetRequiredService<GitHubDiscussionPluginsHttpClient>();
            tempZipPath = await client.DownloadZipToTempAsync(plugin.ZipUrl);

            var preview = await PluginsService.LoadPluginImportPreviewAsync(tempZipPath);
            var existingPlugin = await PluginsService.FindInstalledPluginByNameAsync(preview.Name);
            string? replacePluginId = null;

            if (existingPlugin != null)
            {
                var owner = TopLevel.GetTopLevel(this) as Window;
                if (owner != null)
                {
                    var shouldReplace = await PluginsView.ShowReplacePluginConfirmationAsync(
                        owner,
                        existingPlugin.Name,
                        existingPlugin.Version,
                        preview.Version);

                    if (!shouldReplace)
                    {
                        return;
                    }
                }

                replacePluginId = existingPlugin.PluginId;
            }

            await PluginsService.ImportPluginAsync(tempZipPath, replacePluginId);
            Notifications.SendNotification(
                existingPlugin == null
                    ? $"User plugin '{plugin.Title}' installed successfully."
                    : $"User plugin '{plugin.Title}' replaced successfully.",
                "Success");
        }
        catch (Exception ex)
        {
            Notifications.SendNotification($"User plugin install failed: {ex.Message}", "Warning");
        }
        finally
        {
            if (tempZipPath != null && File.Exists(tempZipPath))
            {
                try
                {
                    File.Delete(tempZipPath);
                }
                catch
                {
                    // Ignore cleanup failures.
                }
            }
        }
    }
}
