using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Interactivity;
using Avalonia.Notification;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using ReimaginedLauncher.Generators;
using ReimaginedLauncher.HttpClients;
using ReimaginedLauncher.HttpClients.Models;
using ReimaginedLauncher.Utilities;
using ReimaginedLauncher.Utilities.Json;
using ReimaginedLauncher.Utilities.ViewModels;
using ReimaginedLauncher.Views.Launch;
using ReimaginedLauncher.Views.Settings;

namespace ReimaginedLauncher;

public partial class MainWindow : Window
{
    // Make URLs readonly for safe reuse across the file
    private const string WebsiteUrl = "https://www.d2r-reimagined.com";
    private const string WikiUrl = "https://wiki.d2r-reimagined.com";
    private const string NexusUrl = "https://www.nexusmods.com/diablo2resurrected/mods/503";
    private const string DiscordUrl = "https://discord.gg/5bbjneJCrr";
    private readonly INexusModsHttpClient _nexusModsHttpClient;
    public NexusModsValidateResponse? User { get; set; }
    public static NexusUserViewModel UserViewModel { get; } = new();
    
    public static INotificationMessageManager ManagerInstance { get; } = new NotificationMessageManager();
    public static AppSettings Settings = new();
    private NexusModsSSO _nexusSSO;
    private string? _localModVersion;

    public MainWindow()
    {
        _nexusModsHttpClient = Program.ServiceProvider.GetRequiredService<NexusModsHttpClient>();;
        InitializeComponent();
        
        DataContext = UserViewModel;
        _ = LoadSettingsAsync();
        ContentArea.Content = new LaunchView();
        
        // Set the window icon
        Icon = new WindowIcon("Assets/ReimaginedLauncher.ico");
    }
    
    private async Task LoadSettingsAsync()
    {
        Settings = await SettingsManager.LoadAsync();
        
        if (!string.IsNullOrWhiteSpace(Settings.NexusModsSSOApiKey))
        {
            User = await _nexusModsHttpClient.ValidateApiKeyAsync();
            UserViewModel.User = User;
        }
        
        var installDir = Settings.InstallDirectory;
        if (installDir != null && installDir.EndsWith("D2R.exe", StringComparison.OrdinalIgnoreCase))
        {
            installDir = Path.GetDirectoryName(installDir);
        }

        if (!string.IsNullOrEmpty(installDir))
        {
            var layoutsDir = Path.Combine(
                installDir,
                "mods", "Reimagined", "Reimagined.mpq", "data", "global", "ui", "layouts"
            );

            var panel = CharacterSelectPanelService.FromJson(layoutsDir);
            _localModVersion = panel?.GetModVersion() ?? "Unknown";

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                VersionTextBlock.Text = $"D2R Reimagined v{_localModVersion}";
            });
        }

        // Only check for latest mod version if user is logged in
        if (UserViewModel.User != null)
        {
            await CheckLatestModVersionAsync();
        }
        else
        {
            // Subscribe to UserViewModel.User property changed to trigger check when user logs in
            UserViewModel.PropertyChanged += UserViewModelOnPropertyChanged;
        }
    }

    private async void UserViewModelOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(UserViewModel.User) && UserViewModel.User != null)
        {
            UserViewModel.PropertyChanged -= UserViewModelOnPropertyChanged;
            await CheckLatestModVersionAsync();
        }
    }

    private async Task CheckLatestModVersionAsync()
    {
        var filesResponse = await _nexusModsHttpClient.GetModFilesAsync("diablo2resurrected", 503);
        if (filesResponse?.Files == null || filesResponse.Files.Count == 0)
            return;
        var latestFile = filesResponse.Files.OrderByDescending(f => f.UploadedTimestamp).FirstOrDefault();
        if (latestFile == null)
            return;
        var latestVersion = latestFile.Version;
        if (!string.IsNullOrEmpty(_localModVersion) && !string.IsNullOrEmpty(latestVersion) && !_localModVersion.Equals(latestVersion, StringComparison.OrdinalIgnoreCase))
        {
            // Get the download link for the latest file
            var downloadLinkResponse = await _nexusModsHttpClient.GenerateDownloadLink("diablo2resurrected", 503, latestFile.FileId);
            string downloadUrl = downloadLinkResponse.ToString();

            // Show update popup if downloadUrl is not null
            if (!string.IsNullOrEmpty(downloadUrl))
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    var updateWindow = new ReimaginedLauncher.Views.UpdateFoundWindow(_localModVersion, latestVersion, downloadUrl);
                    updateWindow.ShowDialog(this);
                });
            }
            else
            {
                Notifications.SendNotification("Failed to retrieve the download link for the latest mod version.");
            }
        }
        else
        {
            Notifications.SendNotification("You have the latest version of the mod");
        }
    }
    
    private void OnNavigationSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (NavigationList.SelectedItem is ListBoxItem item)
        {
            switch (item.Content?.ToString())
            {
                case "Launch":
                    ContentArea.Content = new LaunchView();
                    break;
                case "Settings":
                    ContentArea.Content = new SettingsView();
                    break;
            }
        }
    }
    
    private void OnVisitWebsiteClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            string? urlToOpen = null;

            // Use the sender's context to determine which URL to use
            if (sender is Button button)
            {
                urlToOpen = button.Name switch
                {
                    "WebsiteButton" => WebsiteUrl,
                    "WikiButton" => WikiUrl,
                    "NexusButton" => NexusUrl,
                    "DiscordButton" => DiscordUrl,
                    _ => urlToOpen
                };
            }

            if (!string.IsNullOrEmpty(urlToOpen))
            {
                using var process = new System.Diagnostics.Process();
                process.StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = urlToOpen,
                    UseShellExecute = true
                };
                process.Start();
            }
        }
        catch (Exception ex)
        {
            // Handle exception (log, display error, etc.)
        }
    }

    private async void OnNexusLoginClicked(object sender, RoutedEventArgs e)
    {
        _nexusSSO = new NexusModsSSO();
        _nexusSSO.OnApiKeyReceived += apiKey =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                Settings.NexusModsSSOApiKey = apiKey;
                _ = ValidateKey();
                Notifications.SendNotification($"Nexus Login API Key: {apiKey}");
            });
        };

        await _nexusSSO.ConnectAsync();
    }
    
    private async Task ValidateKey()
    {
        await SettingsManager.SaveAsync(Settings);
        
        if (!string.IsNullOrWhiteSpace(Settings.NexusModsSSOApiKey))
        {
            User = await _nexusModsHttpClient.ValidateApiKeyAsync(Settings.NexusModsSSOApiKey);
            UserViewModel.User = User;
        }
    }
}

