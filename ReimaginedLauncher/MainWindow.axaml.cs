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
using ReimaginedLauncher.Views.Backups;
using ReimaginedLauncher.Views.Launch;
using ReimaginedLauncher.Views.Settings;
using ReimaginedLauncher.Views.Update;

namespace ReimaginedLauncher;

public partial class MainWindow : Window
{
    private const string NexusGameName = "diablo2resurrected";
    private const int NexusModId = 503;
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
    private NexusModsSSO? _nexusSSO;
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
        Settings.InstallDirectory = InstallDirectoryValidator.NormalizeInstallDirectory(Settings.InstallDirectory);
        Settings.IsInstallDirectoryValidated = InstallDirectoryValidator.IsValidInstallDirectory(Settings.InstallDirectory);
        
        if (!string.IsNullOrWhiteSpace(Settings.NexusModsSSOApiKey))
        {
            User = await _nexusModsHttpClient.ValidateApiKeyAsync();
            UserViewModel.User = User;
        }
        
        var installDir = Settings.InstallDirectory;

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
                VersionTextBlock.Text = string.Equals(_localModVersion, "Unknown", StringComparison.OrdinalIgnoreCase)
                    ? "D2R Reimagined Version Not Detected"
                    : $"D2R Reimagined v{_localModVersion}";
            });
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (ContentArea.Content is LaunchView launchView)
            {
                launchView.RefreshInstallDirectoryState();
            }
            else if (ContentArea.Content is BackupsView backupsView)
            {
                backupsView.RefreshBackupState();
            }
            else if (ContentArea.Content is SettingsView settingsView)
            {
                settingsView.RefreshSettingsState();
            }
        });

        BackupService.UpdateSchedule();
        await SettingsManager.SaveAsync(Settings);

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
        if (!Settings.IsInstallDirectoryValidated || string.IsNullOrWhiteSpace(Settings.InstallDirectory))
            return;

        if (string.IsNullOrWhiteSpace(_localModVersion) ||
            _localModVersion.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
            return;

        var filesResponse = await _nexusModsHttpClient.GetModFilesAsync(NexusGameName, NexusModId);
        if (filesResponse?.Files == null || filesResponse.Files.Count == 0)
            return;

        var latestFile = filesResponse.Files
            .Where(file => file.IsPrimary)
            .OrderByDescending(file => file.UploadedTimestamp)
            .FirstOrDefault()
            ?? filesResponse.Files.OrderByDescending(file => file.UploadedTimestamp).FirstOrDefault();

        if (latestFile == null)
            return;

        var latestVersion = !string.IsNullOrWhiteSpace(latestFile.ModVersion)
            ? latestFile.ModVersion
            : latestFile.Version;

        if (!string.IsNullOrEmpty(_localModVersion) && !string.IsNullOrEmpty(latestVersion) && !_localModVersion.Equals(latestVersion, StringComparison.OrdinalIgnoreCase))
        {
            var downloadUrl = await GetUpdateUrlAsync(latestFile.FileId);

            if (!string.IsNullOrEmpty(downloadUrl))
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    var updateWindow = new UpdateFoundWindow(_localModVersion, latestVersion, downloadUrl);
                    updateWindow.ShowDialog(this);
                });
            }
            else
            {
                Notifications.SendNotification("Failed to retrieve the download link for the latest mod version.");
            }
        }
        else if (!string.IsNullOrEmpty(latestVersion))
        {
            Notifications.SendNotification("You have the latest version of the mod");
        }
    }

    private async Task<string?> GetUpdateUrlAsync(int fileId)
    {
        var downloadLinkResponse = await _nexusModsHttpClient.GenerateDownloadLink(NexusGameName, NexusModId, fileId);
        if (!string.IsNullOrWhiteSpace(downloadLinkResponse?.Uri))
            return downloadLinkResponse.Uri;

        return $"{NexusUrl}?tab=files&file_id={fileId}";
    }
    
    private void OnNavigationSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (NavigationList.SelectedItem is ListBoxItem item)
        {
            switch (item.Content?.ToString())
            {
                case "Launch":
                    var launchView = new LaunchView();
                    launchView.RefreshInstallDirectoryState();
                    ContentArea.Content = launchView;
                    break;
                case "Backups":
                    var backupsView = new BackupsView();
                    backupsView.RefreshBackupState();
                    ContentArea.Content = backupsView;
                    break;
                case "Settings":
                    var settingsView = new SettingsView();
                    settingsView.RefreshSettingsState();
                    ContentArea.Content = settingsView;
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
        catch (Exception)
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
