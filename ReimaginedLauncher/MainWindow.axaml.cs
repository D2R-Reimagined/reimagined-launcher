using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
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
using ReimaginedLauncher.Views.ModTweaks;
using ReimaginedLauncher.Views.Plugins;
using ReimaginedLauncher.Views.Settings;
using ReimaginedLauncher.Views.Update;

namespace ReimaginedLauncher;

public partial class MainWindow : Window
{
    private const string NexusGameName = "diablo2resurrected";
    private const int NexusModId = 503;
    private const string LauncherFileMarker = "launcher";
    // Make URLs readonly for safe reuse across the file
    private const string WebsiteUrl = "https://www.d2r-reimagined.com";
    private const string WikiUrl = "https://wiki.d2r-reimagined.com";
    private const string NexusUrl = "https://www.nexusmods.com/diablo2resurrected/mods/503";
    private const string DiscordUrl = "https://discord.gg/5bbjneJCrr";
    private static readonly string LauncherVersion = ResolveLauncherVersion();
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
    public static bool IsUpdateDownloadDirect { get; private set; }
    public static int? UpdateFileId { get; private set; }
    private NexusModsSSO? _nexusSSO;
    private string? _localModVersion;

    public MainWindow()
    {
        _nexusModsHttpClient = Program.ServiceProvider.GetRequiredService<NexusModsHttpClient>();;
        InitializeComponent();
        LogoImage.Source = new Bitmap("Assets/ReimaginedLauncher.ico");
        LauncherVersionTextBlock.Text = $"Launcher v{LauncherVersion}";
        LauncherUpdateService.UpdateDownloaded += (s, e) => RefreshLauncherUpdateUI();
        LauncherUpdateService.UpdateStateChanged += (s, e) => RefreshLauncherUpdateUI();
        RefreshLauncherUpdateUI();

        
        DataContext = UserViewModel;
        _ = LoadSettingsAsync();
        ContentArea.Content = new LaunchView();
        
        // Set the window icon
        Icon = new WindowIcon("Assets/ReimaginedLauncher.ico");
    }

    private static string ResolveLauncherVersion()
    {
        var assembly = typeof(MainWindow).Assembly;
        var informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            var normalizedVersion = informationalVersion.Split('+', 2)[0];
            if (!string.IsNullOrWhiteSpace(normalizedVersion))
            {
                return normalizedVersion;
            }
        }

        return assembly.GetName().Version?.ToString(3) ?? "Unknown";
    }
    
    private async Task LoadSettingsAsync()
    {
        Settings = await SettingsManager.LoadAsync();
        Settings.InstallDirectory = InstallDirectoryValidator.NormalizeInstallDirectory(Settings.InstallDirectory);
        Settings.IsInstallDirectoryValidated = InstallDirectoryValidator.IsValidInstallDirectory(Settings.InstallDirectory);
        BackupService.ApplyDefaultSettings();
        
        if (!string.IsNullOrWhiteSpace(Settings.NexusModsSSOApiKey))
        {
            User = await _nexusModsHttpClient.ValidateApiKeyAsync();
            UserViewModel.User = User;
        }
        
        var installDir = Settings.InstallDirectory;
        RefreshLocalModState(installDir);
        PluginsView? pluginsViewToRefresh = null;

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
            else if (ContentArea.Content is ModTweaksView modTweaksView)
            {
                modTweaksView.RefreshTweaksState();
            }
            else if (ContentArea.Content is PluginsView pluginsView)
            {
                pluginsViewToRefresh = pluginsView;
            }
        });

        if (pluginsViewToRefresh != null)
        {
            await pluginsViewToRefresh.RefreshPluginsStateAsync();
        }

        BackupService.UpdateSchedule();
        await SettingsManager.SaveAsync(Settings);

        await RefreshUpdateStateAsync();
        _ = LauncherUpdateService.CheckForUpdatesAsync();

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
                downloadUrl: NexusUrl,
                isDirectDownload: false,
                fileId: null);
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

                    var installLink = await GetUpdateUrlAsync(installFile.FileId);
                    if (!string.IsNullOrWhiteSpace(installLink.Url))
                    {
                        downloadUrlForInstall = installLink.Url;
                    }

                    SetUpdateState(
                        isUpdateAvailable: true,
                        canInstallOrUpdate: true,
                        title: "Mod not detected",
                        message: "D2R Reimagined is not detected in this install directory. Install the mod to enable Play.",
                        currentVersion: "Not detected",
                        latestVersion: latestVersionForInstall,
                        downloadUrl: downloadUrlForInstall,
                        isDirectDownload: installLink.IsDirect,
                        fileId: installFile.FileId);
                    RefreshCurrentContent();
                    return;
                }
            }

            SetUpdateState(
                isUpdateAvailable: true,
                canInstallOrUpdate: true,
                title: "Mod not detected",
                message: "D2R Reimagined is not detected in this install directory. Install the mod to enable Play.",
                currentVersion: "Not detected",
                latestVersion: latestVersionForInstall,
                downloadUrl: downloadUrlForInstall,
                isDirectDownload: false,
                fileId: null);
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
                downloadUrl: NexusUrl,
                isDirectDownload: false,
                fileId: null);
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
                downloadUrl: NexusUrl,
                isDirectDownload: false,
                fileId: null);
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
                downloadUrl: NexusUrl,
                isDirectDownload: false,
                fileId: null);
            RefreshCurrentContent();
            return;
        }

        var latestVersion = !string.IsNullOrWhiteSpace(latestFile.ModVersion)
            ? latestFile.ModVersion
            : latestFile.Version;

        var updateLink = await GetUpdateUrlAsync(latestFile.FileId);
        var downloadUrl = updateLink.Url;

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
                downloadUrl: downloadUrl,
                isDirectDownload: updateLink.IsDirect,
                fileId: latestFile.FileId);
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
                downloadUrl: downloadUrl,
                isDirectDownload: updateLink.IsDirect,
                fileId: latestFile.FileId);
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

        var modFiles = filesResponse.Files
            .Where(IsModReleaseFile)
            .ToList();

        if (modFiles.Count == 0)
            return null;

        if (filesResponse.FileUpdates.Count > 0)
        {
            var filesById = modFiles.ToDictionary(file => file.FileId);
            var newestUpdate = filesResponse.FileUpdates
                .Where(update => filesById.ContainsKey(update.NewFileId))
                .OrderByDescending(update => update.UploadedTimestamp)
                .ThenByDescending(update => update.NewFileId)
                .FirstOrDefault();

            if (newestUpdate != null && filesById.TryGetValue(newestUpdate.NewFileId, out var updatedFile))
            {
                return updatedFile;
            }
        }

        return modFiles
            .OrderByDescending(file => file.UploadedTimestamp)
            .ThenByDescending(file => file.FileId)
            .FirstOrDefault();
    }

    private static bool IsModReleaseFile(NexusModsFileResponse file)
    {
        var name = file.Name ?? string.Empty;
        var fileName = file.FileName ?? string.Empty;

        return !name.Contains(LauncherFileMarker, StringComparison.OrdinalIgnoreCase) &&
               !fileName.Contains(LauncherFileMarker, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<(string Url, bool IsDirect)> GetUpdateUrlAsync(
        int fileId,
        string? key = null,
        long? expires = null,
        bool allowFallback = true)
    {
        var usesManualNxmKey = !string.IsNullOrWhiteSpace(key) && expires.HasValue;
        if (!usesManualNxmKey && Settings.NexusPremiumDownloadAccess == false)
        {
            if (!allowFallback)
                return (string.Empty, false);

            return ($"{NexusUrl}?tab=files&file_id={fileId}", false);
        }

        var downloadLinkResult = await _nexusModsHttpClient.GenerateDownloadLink(
            NexusGameName,
            NexusModId,
            fileId,
            key,
            expires);

        if (!string.IsNullOrWhiteSpace(downloadLinkResult.Link?.Uri))
        {
            if (!usesManualNxmKey && Settings.NexusPremiumDownloadAccess != true)
            {
                Settings.NexusPremiumDownloadAccess = true;
                await SettingsManager.SaveAsync(Settings);
            }

            return (downloadLinkResult.Link.Uri, true);
        }

        if (!usesManualNxmKey && downloadLinkResult.StatusCode == HttpStatusCode.Forbidden)
        {
            Settings.NexusPremiumDownloadAccess = false;
            await SettingsManager.SaveAsync(Settings);
        }

        if (!allowFallback)
            return (string.Empty, false);

        return ($"{NexusUrl}?tab=files&file_id={fileId}", false);
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
                    _ = NavigateToBackupsViewAsync();
                    break;
                case "Plugins":
                    _ = NavigateToPluginsViewAsync();
                    break;
                case "Settings":
                    var settingsView = new SettingsView();
                    settingsView.RefreshSettingsState();
                    ContentArea.Content = settingsView;
                    break;
                case "Mod Tweaks":
                    var modTweaksView = new ModTweaksView();
                    modTweaksView.RefreshTweaksState();
                    ContentArea.Content = modTweaksView;
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
        string downloadUrl,
        bool isDirectDownload,
        int? fileId)
    {
        IsUpdateAvailable = isUpdateAvailable;
        CanInstallOrUpdate = canInstallOrUpdate;
        UpdateStatusTitle = title;
        UpdateStatusMessage = message;
        UpdateCurrentVersion = currentVersion;
        UpdateLatestVersion = latestVersion;
        UpdateDownloadUrl = downloadUrl;
        IsUpdateDownloadDirect = isDirectDownload;
        UpdateFileId = fileId;
    }

    public async Task NavigateToUpdateViewAsync()
    {
        UpdateView? updateView = null;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            updateView = new UpdateView();
            updateView.SetLoadingState(true);
            updateView.RefreshUpdateState();
            ContentArea.Content = updateView;

            if (UpdateNavItem != null && NavigationList.SelectedItem != UpdateNavItem)
            {
                NavigationList.SelectedItem = UpdateNavItem;
            }
        });

        try
        {
            await RefreshUpdateStateAsync();
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (updateView != null && ReferenceEquals(ContentArea.Content, updateView))
                {
                    updateView.SetLoadingState(false);
                }
            });
        }
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

    public async Task NavigateToBackupsViewAsync()
    {
        BackupsView? backupsView = null;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            backupsView = new BackupsView();
            backupsView.SetLoadingState(true);
            ContentArea.Content = backupsView;

            if (NavigationList.SelectedItem is not ListBoxItem { Content: "Backups" })
            {
                NavigationList.SelectedIndex = 2;
            }
        });

        try
        {
            if (backupsView != null)
            {
                await backupsView.RefreshBackupStateAsync();
            }
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (backupsView != null && ReferenceEquals(ContentArea.Content, backupsView))
                {
                    backupsView.SetLoadingState(false);
                }
            });
        }
    }

    public async Task NavigateToPluginsViewAsync()
    {
        PluginsView? pluginsView = null;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            pluginsView = new PluginsView();
            pluginsView.SetLoadingState(true);
            ContentArea.Content = pluginsView;

            if (NavigationList.SelectedItem is not ListBoxItem { Content: "Plugins" })
            {
                NavigationList.SelectedIndex = 1;
            }
        });

        try
        {
            if (pluginsView != null)
            {
                await pluginsView.RefreshPluginsStateAsync();
            }
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (pluginsView != null && ReferenceEquals(ContentArea.Content, pluginsView))
                {
                    pluginsView.SetLoadingState(false);
                }
            });
        }
    }

    public void NavigateToPluginsView()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(NavigateToPluginsView);
            return;
        }

        _ = NavigateToPluginsViewAsync();
    }

    public void NavigateToPluginAuthoringGuideView()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(NavigateToPluginAuthoringGuideView);
            return;
        }

        ContentArea.Content = new PluginAuthoringGuideView();

        if (NavigationList.SelectedItem is not ListBoxItem { Content: "Plugins" })
        {
            NavigationList.SelectedIndex = 1;
        }
    }

    public void NavigateToOfficialPluginsView()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(NavigateToOfficialPluginsView);
            return;
        }

        ContentArea.Content = new OfficialPluginsView();

        if (NavigationList.SelectedItem is not ListBoxItem { Content: "Plugins" })
        {
            NavigationList.SelectedIndex = 1;
        }
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
            _ = backupsView.RefreshBackupStateAsync();
        }
        else if (ContentArea.Content is SettingsView settingsView)
        {
            settingsView.RefreshSettingsState();
        }
        else if (ContentArea.Content is ModTweaksView modTweaksView)
        {
            modTweaksView.RefreshTweaksState();
        }
        else if (ContentArea.Content is PluginsView pluginsView)
        {
            _ = pluginsView.RefreshPluginsStateAsync();
        }
        else if (ContentArea.Content is OfficialPluginsView officialPluginsView)
        {
            _ = officialPluginsView.RefreshOfficialPluginsStateAsync();
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
                Notifications.SendNotification($"Logged in Via Nexus Mods");
            });
        };

        await _nexusSSO.ConnectAsync();
    }

    private void OnUserMenuClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: not null } button)
        {
            button.ContextMenu.Open(button);
        }
    }

    private void RefreshLauncherUpdateUI()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(RefreshLauncherUpdateUI);
            return;
        }

        bool showBanner = LauncherUpdateService.IsUpdateAvailable || LauncherUpdateService.IsUpdateDownloaded;
        UpdateBanner.IsVisible = showBanner;

        if (showBanner)
        {
            if (LauncherUpdateService.IsUpdateDownloaded)
            {
                UpdateBannerText.Text = $"Launcher update {LauncherUpdateService.LatestVersion} is ready to install.";
                LauncherRestartButton.IsVisible = true;
            }
            else if (LauncherUpdateService.IsDownloading)
            {
                UpdateBannerText.Text = $"Launcher update {LauncherUpdateService.LatestVersion} is downloading...";
                LauncherRestartButton.IsVisible = false;
            }
            else
            {
                UpdateBannerText.Text = $"Launcher update {LauncherUpdateService.LatestVersion} is available.";
                LauncherRestartButton.IsVisible = false;
            }
        }
    }

    private void OnLauncherRestartClicked(object? sender, RoutedEventArgs e)
    {
        LauncherUpdateService.ApplyUpdateAndRestart();
    }


    private async void OnLogoutClicked(object? sender, RoutedEventArgs e)
    {
        Settings.NexusModsSSOApiKey = string.Empty;
        Settings.NexusPremiumDownloadAccess = null;
        User = null;
        UserViewModel.User = null;
        await SettingsManager.SaveAsync(Settings);
        await RefreshUpdateStateAsync();
        RefreshCurrentContent();
        Notifications.SendNotification("Logged out of Nexus Mods.", "Success");
    }
    
    private async Task ValidateKey()
    {
        await SettingsManager.SaveAsync(Settings);
        
        if (!string.IsNullOrWhiteSpace(Settings.NexusModsSSOApiKey))
        {
            User = await _nexusModsHttpClient.ValidateApiKeyAsync(Settings.NexusModsSSOApiKey);
            UserViewModel.User = User;
            if (User != null && (User.IsPremium || User.IsPremiumQ == true))
            {
                Settings.NexusPremiumDownloadAccess = true;
            }
            else
            {
                Settings.NexusPremiumDownloadAccess = null;
            }

            await SettingsManager.SaveAsync(Settings);
            await RefreshUpdateStateAsync();
            RefreshCurrentContent();
        }
    }
}
