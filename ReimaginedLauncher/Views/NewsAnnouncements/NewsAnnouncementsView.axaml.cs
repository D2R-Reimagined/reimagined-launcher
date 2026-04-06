using System;
using System.Diagnostics;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using ReimaginedLauncher.HttpClients.Models;

namespace ReimaginedLauncher.Views.NewsAnnouncements;

public partial class NewsAnnouncementsView : UserControl
{
    private bool _isLoading;

    public NewsAnnouncementsView()
    {
        InitializeComponent();
        RefreshAnnouncementsState();
    }

    public void RefreshAnnouncementsState()
    {
        LoadingBanner.IsVisible = _isLoading;
        EmptyStateBorder.IsVisible = !_isLoading && MainWindow.Announcements.Count == 0;
        AnnouncementsItemsControl.ItemsSource = null;
        AnnouncementsItemsControl.ItemsSource = MainWindow.Announcements;
        RefreshButton.IsEnabled = !_isLoading;
        MarkAllReadButton.IsEnabled = !_isLoading && MainWindow.Announcements.Any(announcement => announcement.IsUnread);
    }

    public void SetLoadingState(bool isLoading)
    {
        _isLoading = isLoading;
        RefreshAnnouncementsState();
    }

    private async void OnRefreshClicked(object? sender, RoutedEventArgs e)
    {
        if (this.GetVisualRoot() is not MainWindow mainWindow)
        {
            return;
        }

        SetLoadingState(true);
        try
        {
            await mainWindow.RefreshAnnouncementsStateAsync();
        }
        finally
        {
            SetLoadingState(false);
        }
    }

    private async void OnMarkAllReadClicked(object? sender, RoutedEventArgs e)
    {
        if (this.GetVisualRoot() is not MainWindow mainWindow || MainWindow.Announcements.Count == 0)
        {
            return;
        }

        await mainWindow.MarkAnnouncementsReadUpToAsync(MainWindow.Announcements.Max(announcement => announcement.Number));
    }

    private async void OnMarkAsReadClicked(object? sender, RoutedEventArgs e)
    {
        if (this.GetVisualRoot() is not MainWindow mainWindow ||
            sender is not Button { Tag: not null } button ||
            !int.TryParse(button.Tag.ToString(), out var discussionNumber))
        {
            return;
        }

        await mainWindow.MarkAnnouncementsReadUpToAsync(discussionNumber);
    }

    private void OnToggleExpandedClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { Tag: not null } button ||
            !int.TryParse(button.Tag.ToString(), out var discussionNumber))
        {
            return;
        }

        if (MainWindow.Announcements.FirstOrDefault(announcement => announcement.Number == discussionNumber) is GitHubAnnouncement announcement)
        {
            announcement.IsExpanded = !announcement.IsExpanded;
            RefreshAnnouncementsState();
        }
    }

    private void OnOpenAnnouncementClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string url } || string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            };
            process.Start();
        }
        catch (Exception)
        {
            // Keep launcher stable if the shell cannot open the URL.
        }
    }
}
