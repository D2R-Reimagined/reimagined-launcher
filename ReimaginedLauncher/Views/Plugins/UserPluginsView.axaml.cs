using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
    private bool _isRefreshing;
    private IReadOnlyList<UserPluginEntry> _allPlugins = [];

    public UserPluginsView()
    {
        InitializeComponent();
        _ = RefreshUserPluginsAsync();
    }

    public Task RefreshUserPluginsAsync() => RefreshUserPluginsAsync(forceRefresh: false);

    public async Task RefreshUserPluginsAsync(bool forceRefresh)
    {
        // Guard against overlapping refreshes (e.g. rapid button clicks or
        // navigation events firing while a previous fetch is still in flight).
        if (_isRefreshing)
        {
            return;
        }

        _isRefreshing = true;

        LoadingBanner.IsVisible = true;
        EmptyStatePanel.IsVisible = false;
        UserPluginsItemsControl.ItemsSource = null;

        try
        {
            var client = Program.ServiceProvider.GetRequiredService<GitHubDiscussionPluginsHttpClient>();
            var plugins = await client.GetUserPluginsAsync(forceRefresh);

            _allPlugins = plugins;
            ApplySearchFilter();
        }
        catch (Exception ex)
        {
            UserPluginsSummaryTextBlock.Text = $"Failed to load user plugins: {ex.Message}";
            EmptyStatePanel.IsVisible = true;
        }
        finally
        {
            LoadingBanner.IsVisible = false;
            _isRefreshing = false;
        }
    }

    private async void OnRefreshClicked(object? sender, RoutedEventArgs e)
    {
        // Explicit user-initiated refresh bypasses the TTL cache.
        await RefreshUserPluginsAsync(forceRefresh: true);
    }

    private void OnSearchTextChanged(object? sender, TextChangedEventArgs e)
    {
        ApplySearchFilter();
    }

    private void OnSortChanged(object? sender, SelectionChangedEventArgs e)
    {
        ApplySearchFilter();
    }

    private IReadOnlyList<UserPluginEntry> ApplySort(IReadOnlyList<UserPluginEntry> source)
    {
        // Index 0 = Last Updated (default), 1 = Date Created.
        // For unknown timestamps fall back to PublishedAt so plugins without
        // the new fields still show up in a stable, sensible order.
        var index = SortComboBox?.SelectedIndex ?? 0;
        return index switch
        {
            1 => source
                .OrderByDescending(p => p.PublishedAt ?? DateTimeOffset.MinValue)
                .ToList(),
            _ => source
                .OrderByDescending(p => p.UpdatedAt ?? p.PublishedAt ?? DateTimeOffset.MinValue)
                .ToList()
        };
    }

    private void ApplySearchFilter()
    {
        // The ComboBox's SelectionChanged fires during XAML EndInit (because of
        // SelectedIndex="0"), which runs before later named controls in the
        // tree are assigned. Bail out until the view is fully initialized.
        if (UserPluginsItemsControl is null ||
            EmptyStatePanel is null ||
            UserPluginsSummaryTextBlock is null)
        {
            return;
        }

        var query = SearchTextBox?.Text?.Trim() ?? string.Empty;
        IReadOnlyList<UserPluginEntry> filtered = _allPlugins;
        if (query.Length > 0)
        {
            filtered = _allPlugins.Where(plugin =>
                (plugin.Title?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (plugin.Description?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))
                .ToList();
        }

        filtered = ApplySort(filtered);

        UserPluginsItemsControl.ItemsSource = filtered;
        EmptyStatePanel.IsVisible = filtered.Count == 0;

        if (_allPlugins.Count == 0)
        {
            UserPluginsSummaryTextBlock.Text = "No user plugins were found.";
        }
        else if (query.Length == 0)
        {
            UserPluginsSummaryTextBlock.Text = $"{_allPlugins.Count} user plugin(s) available.";
        }
        else
        {
            UserPluginsSummaryTextBlock.Text =
                $"{filtered.Count} of {_allPlugins.Count} user plugin(s) match \"{query}\".";
        }
    }

    private void OnSubmitPluginClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "https://github.com/D2R-Reimagined/reimagined-launcher/discussions/categories/plugins",
                UseShellExecute = true
            };
            process.Start();
        }
        catch (Exception)
        {
            // Keep launcher stable if the shell cannot open the URL.
        }
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
