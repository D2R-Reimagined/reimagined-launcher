using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
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
    public static bool IsLocalModDetected { get; private set; }
    public static bool IsUpdateAvailable { get; private set; }
    public static bool CanInstallOrUpdate { get; private set; }
    public static string UpdateStatusTitle { get; private set; } = "Update status unavailable";
    public static string UpdateStatusMessage { get; private set; } = "Open this tab to check mod install and update status.";
    public static string UpdateCurrentVersion { get; private set; } = "Unknown";
    public static string UpdateLatestVersion { get; private set; } = "Unknown";
    public static string UpdateDownloadUrl { get; private set; } = NexusUrl;
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
        RefreshLocalModState(installDir);

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

        await RefreshUpdateStateAsync();

        // Subscribe to UserViewModel.User property changed to trigger check when user logs in
        UserViewModel.PropertyChanged -= UserViewModelOnPropertyChanged;
        UserViewModel.PropertyChanged += UserViewModelOnPropertyChanged;

        if (!IsLocalModDetected)
        {
            await PromptInstallForMissingModAsync();
        }
    }

    private async void UserViewModelOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(UserViewModel.User) && UserViewModel.User != null)
        {
            await RefreshUpdateStateAsync();
            RefreshCurrentContent();
        }
    }

    public async Task RefreshUpdateStateAsync()
    {
        if (!Settings.IsInstallDirectoryValidated || string.IsNullOrWhiteSpace(Settings.InstallDirectory))
        {
            SetUpdateState(
                isUpdateAvailable: false,
                canInstallOrUpdate: false,
                title: "Install directory required",
                message: "Select a valid Diablo II: Resurrected install directory to check mod updates.",
                currentVersion: "Unknown",
                latestVersion: "Unknown",
                downloadUrl: NexusUrl);
            RefreshCurrentContent();
            return;
        }

        if (!IsLocalModDetected)
        {
            var latestVersionForInstall = "Latest available";
            var downloadUrlForInstall = NexusUrl;

            if (UserViewModel.User != null)
            {
                var installFile = await GetLatestModFileAsync();
                if (installFile != null)
                {
                    latestVersionForInstall = !string.IsNullOrWhiteSpace(installFile.ModVersion)
                        ? installFile.ModVersion
                        : installFile.Version;

                    var resolvedInstallUrl = await GetUpdateUrlAsync(installFile.FileId);
                    if (!string.IsNullOrWhiteSpace(resolvedInstallUrl))
                    {
                        downloadUrlForInstall = resolvedInstallUrl;
                    }
                }
            }

            SetUpdateState(
                isUpdateAvailable: true,
                canInstallOrUpdate: true,
                title: "Mod not detected",
                message: "D2R Reimagined is not detected in this install directory. Install the mod to enable Play.",
                currentVersion: "Not detected",
                latestVersion: latestVersionForInstall,
                downloadUrl: downloadUrlForInstall);
            RefreshCurrentContent();
            return;
        }

        if (string.IsNullOrWhiteSpace(_localModVersion) ||
            _localModVersion.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
        {
            SetUpdateState(
                isUpdateAvailable: false,
                canInstallOrUpdate: true,
                title: "Version not detected",
                message: "Mod files are installed, but the local version could not be detected automatically.",
                currentVersion: "Unknown",
                latestVersion: "Unknown",
                downloadUrl: NexusUrl);
            RefreshCurrentContent();
            return;
        }

        if (UserViewModel.User == null)
        {
            SetUpdateState(
                isUpdateAvailable: false,
                canInstallOrUpdate: true,
                title: "Nexus login required",
                message: "Log in with Nexus Mods to compare your local mod version with the latest release.",
                currentVersion: _localModVersion,
                latestVersion: "Unknown",
                downloadUrl: NexusUrl);
            RefreshCurrentContent();
            return;
        }

        var latestFile = await GetLatestModFileAsync();

        if (latestFile == null)
        {
            SetUpdateState(
                isUpdateAvailable: false,
                canInstallOrUpdate: true,
                title: "Unable to check updates",
                message: "Could not retrieve latest mod file information from Nexus Mods.",
                currentVersion: _localModVersion,
                latestVersion: "Unknown",
                downloadUrl: NexusUrl);
            RefreshCurrentContent();
            return;
        }

        var latestVersion = !string.IsNullOrWhiteSpace(latestFile.ModVersion)
            ? latestFile.ModVersion
            : latestFile.Version;

        var downloadUrl = await GetUpdateUrlAsync(latestFile.FileId) ?? NexusUrl;

        if (!string.IsNullOrEmpty(_localModVersion) &&
            !string.IsNullOrEmpty(latestVersion) &&
            !_localModVersion.Equals(latestVersion, StringComparison.OrdinalIgnoreCase))
        {
            SetUpdateState(
                isUpdateAvailable: true,
                canInstallOrUpdate: true,
                title: "Update available",
                message: "A newer version of D2R Reimagined is available.",
                currentVersion: _localModVersion,
                latestVersion: latestVersion,
                downloadUrl: downloadUrl);
        }
        else
        {
            SetUpdateState(
                isUpdateAvailable: false,
                canInstallOrUpdate: true,
                title: "No updates detected",
                message: "Your local D2R Reimagined version is up to date.",
                currentVersion: _localModVersion,
                latestVersion: latestVersion,
                downloadUrl: downloadUrl);
        }

        RefreshCurrentContent();
    }

    public async Task PromptInstallForMissingModAsync()
    {
        if (!Settings.IsInstallDirectoryValidated || string.IsNullOrWhiteSpace(Settings.InstallDirectory) || IsLocalModDetected)
            return;

        await NavigateToUpdateViewAsync();
    }

    public void RefreshLocalModState(string? installDirectory = null)
    {
        _localModVersion = "Unknown";
        IsLocalModDetected = false;

        var installDir = installDirectory ?? Settings.InstallDirectory;
        if (!string.IsNullOrWhiteSpace(installDir))
        {
            var modRootDirectory = Path.Combine(installDir, "mods", "Reimagined");
            var modInfoPath = Path.Combine(modRootDirectory, "modinfo.json");
            var modInfoPathInMpq = Path.Combine(modRootDirectory, "Reimagined.mpq", "modinfo.json");
            var layoutsDir = Path.Combine(
                modRootDirectory,
                "Reimagined.mpq", "data", "global", "ui", "layouts"
            );

            var panel = CharacterSelectPanelService.FromJson(layoutsDir);
            var panelVersion = panel?.GetModVersion();
            var modInfoVersion = TryGetVersionFromModInfo(modInfoPath) ?? TryGetVersionFromModInfo(modInfoPathInMpq);
            _localModVersion = !string.IsNullOrWhiteSpace(panelVersion) && !panelVersion.Equals("Unknown", StringComparison.OrdinalIgnoreCase)
                ? panelVersion
                : !string.IsNullOrWhiteSpace(modInfoVersion)
                    ? modInfoVersion
                    : "Unknown";

            IsLocalModDetected = Directory.Exists(modRootDirectory) || File.Exists(modInfoPath) || File.Exists(modInfoPathInMpq);
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            VersionTextBlock.Text = IsLocalModDetected
                ? string.Equals(_localModVersion, "Unknown", StringComparison.OrdinalIgnoreCase)
                    ? "D2R Reimagined Installed (version unknown)"
                    : $"D2R Reimagined v{_localModVersion}"
                : "D2R Reimagined Version Not Detected";
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            VersionTextBlock.Text = IsLocalModDetected
                ? string.Equals(_localModVersion, "Unknown", StringComparison.OrdinalIgnoreCase)
                    ? "D2R Reimagined Installed (version unknown)"
                    : $"D2R Reimagined v{_localModVersion}"
                : "D2R Reimagined Version Not Detected";
        });
    }

    private static string? TryGetVersionFromModInfo(string modInfoPath)
    {
        if (!File.Exists(modInfoPath))
            return null;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(modInfoPath));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return null;

            if (document.RootElement.TryGetProperty("version", out var versionElement) &&
                versionElement.ValueKind == JsonValueKind.String)
            {
                return versionElement.GetString();
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private async Task<NexusModsFileResponse?> GetLatestModFileAsync()
    {
        var filesResponse = await _nexusModsHttpClient.GetModFilesAsync(NexusGameName, NexusModId);
        if (filesResponse?.Files == null || filesResponse.Files.Count == 0)
            return null;

        if (filesResponse.FileUpdates.Count > 0)
        {
            var filesById = filesResponse.Files.ToDictionary(file => file.FileId);
            var newestUpdate = filesResponse.FileUpdates
                .OrderByDescending(update => update.UploadedTimestamp)
                .ThenByDescending(update => update.NewFileId)
                .FirstOrDefault();

            if (newestUpdate != null && filesById.TryGetValue(newestUpdate.NewFileId, out var updatedFile))
            {
                return updatedFile;
            }
        }

        return filesResponse.Files
            .OrderByDescending(file => file.UploadedTimestamp)
            .ThenByDescending(file => file.FileId)
            .FirstOrDefault();
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
                case "Update":
                    _ = NavigateToUpdateViewAsync();
                    break;
            }
        }
    }

    private void SetUpdateState(
        bool isUpdateAvailable,
        bool canInstallOrUpdate,
        string title,
        string message,
        string currentVersion,
        string latestVersion,
        string downloadUrl)
    {
        IsUpdateAvailable = isUpdateAvailable;
        CanInstallOrUpdate = canInstallOrUpdate;
        UpdateStatusTitle = title;
        UpdateStatusMessage = message;
        UpdateCurrentVersion = currentVersion;
        UpdateLatestVersion = latestVersion;
        UpdateDownloadUrl = downloadUrl;
    }

    public async Task NavigateToUpdateViewAsync()
    {
        await RefreshUpdateStateAsync();

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var updateView = new UpdateView();
            updateView.RefreshUpdateState();
            ContentArea.Content = updateView;

            if (UpdateNavItem != null && NavigationList.SelectedItem != UpdateNavItem)
            {
                NavigationList.SelectedItem = UpdateNavItem;
            }
        });
    }

    public async Task NavigateToLaunchViewAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var launchView = new LaunchView();
            launchView.RefreshInstallDirectoryState();
            ContentArea.Content = launchView;

            if (LaunchNavItem != null && NavigationList.SelectedItem != LaunchNavItem)
            {
                NavigationList.SelectedItem = LaunchNavItem;
            }
        });
    }

    private void RefreshCurrentContent()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(RefreshCurrentContent);
            return;
        }

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
        else if (ContentArea.Content is UpdateView updateView)
        {
            updateView.RefreshUpdateState();
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
            await RefreshUpdateStateAsync();
            RefreshCurrentContent();
        }
    }
}
