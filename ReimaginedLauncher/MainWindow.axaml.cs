using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using ReimaginedLauncher.Generators;
using ReimaginedLauncher.HttpClients;
using ReimaginedLauncher.HttpClients.Models;
using ReimaginedLauncher.Utilities;
using ReimaginedLauncher.Utilities.Casc;
using ReimaginedLauncher.Utilities.Json;
using ReimaginedLauncher.Utilities.ViewModels;
using Avalonia;
using ReimaginedLauncher.Views.Backups;
using ReimaginedLauncher.Views.CascFastload;
using ReimaginedLauncher.Views.Launch;
using ReimaginedLauncher.Views.ModTweaks;
using ReimaginedLauncher.Views.NewsAnnouncements;
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
    private readonly GitHubAnnouncementsHttpClient _gitHubAnnouncementsHttpClient;
    private readonly INexusModsHttpClient _nexusModsHttpClient;
    public NexusModsValidateResponse? User { get; set; }
    public static NexusUserViewModel UserViewModel { get; } = new();
    
    public static INotificationManager? NotificationManager { get; private set; }
    public static AppSettings Settings = new();
    public static IReadOnlyList<GitHubAnnouncement> Announcements { get; private set; } = [];
    public static bool HasUnreadAnnouncements { get; private set; }
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
    public static bool IsInstallInProgress { get; set; }
    public static string? InstallProgressTitle { get; set; }
    public static string? InstallProgressMessage { get; set; }
    public static MainWindow? Instance { get; private set; }
    private NexusModsSSO? _nexusSSO;
    private string? _localModVersion;
    private double _currentScale = 1.0;
    private TrayIcon? _trayIcon;
    private bool _isExiting;
    private readonly DispatcherTimer _saveWindowStateTimer;
    private DispatcherTimer? _launcherUpdateCheckTimer;
    private bool _isRestoringWindowState;
    // Hourly auto-poll cadence for launcher update checks. The same check is also runnable on
    // demand by clicking the "Launcher v#.#.#" label in the navigation panel.
    private static readonly TimeSpan LauncherUpdateCheckInterval = TimeSpan.FromHours(1);

    public MainWindow()
    {
        Instance = this;
        _gitHubAnnouncementsHttpClient = Program.ServiceProvider.GetRequiredService<GitHubAnnouncementsHttpClient>();
        _nexusModsHttpClient = Program.ServiceProvider.GetRequiredService<NexusModsHttpClient>();;
        Opacity = 0;
        InitializeComponent();
        NotificationManager = new WindowNotificationManager(this)
        {
            Position = NotificationPosition.BottomRight,
            MaxItems = 3
        };
        LogoImage.Source = new Bitmap("Assets/ReimaginedLauncher.ico");
        LauncherVersionTextBlock.Text = $"Launcher v{LauncherVersion}";
        LauncherUpdateService.UpdateDownloaded += (s, e) => RefreshLauncherUpdateUI();
        LauncherUpdateService.UpdateStateChanged += (s, e) => RefreshLauncherUpdateUI();
        RefreshLauncherUpdateUI();

        
        DataContext = UserViewModel;
        RootScaleControl.SizeChanged += (_, _) => UpdateRootGridSize();
        ApplyUiScale();
        _ = LoadSettingsAsync();
        _ = NavigateToLaunchViewAsync();
        
        // Set the window icon
        Icon = new WindowIcon("Assets/ReimaginedLauncher.ico");
        InitializeTrayIcon();
        _saveWindowStateTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _saveWindowStateTimer.Tick += async (_, _) =>
        {
            _saveWindowStateTimer.Stop();
            await SettingsManager.SaveAsync(Settings);
        };
        PositionChanged += OnWindowPositionOrSizeChanged;
        SizeChanged += OnWindowPositionOrSizeChanged;
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
        RestoreWindowState();
        Opacity = 1;
        Settings.UiScale = ClampUiScale(Settings.UiScale);
        var profile = Settings.CurrentProfile;
        profile.InstallDirectory = InstallDirectoryValidator.NormalizeInstallDirectory(profile.InstallDirectory);
        profile.IsInstallDirectoryValidated = InstallDirectoryValidator.IsValidInstallDirectory(profile.InstallDirectory);
        BackupService.ApplyDefaultSettings();
        var openedUnreadAnnouncements = await RefreshAnnouncementsStateAsync(openUnreadAnnouncements: true);
        ApplyUiScale();
        
        if (!string.IsNullOrWhiteSpace(Settings.NexusModsSSOApiKey))
        {
            User = await _nexusModsHttpClient.ValidateApiKeyAsync();
            UserViewModel.User = User;
        }
        
        var installDir = Settings.CurrentProfile.InstallDirectory;
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
            else if (ContentArea.Content is NewsAnnouncementsView newsAnnouncementsView)
            {
                newsAnnouncementsView.RefreshAnnouncementsState();
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
        StartLauncherUpdateCheckTimer();

        // Subscribe to UserViewModel.User property changed to trigger check when user logs in
        UserViewModel.PropertyChanged -= UserViewModelOnPropertyChanged;
        UserViewModel.PropertyChanged += UserViewModelOnPropertyChanged;

        if (!IsLocalModDetected)
        {
            if (!openedUnreadAnnouncements)
            {
                await PromptInstallForMissingModAsync();
            }
        }

        // If a fastload manifest exists, check for a build mismatch and offer a delta update.
        _ = PromptCascFastloadUpdateIfMismatchedAsync();
    }

    /// <summary>
    /// Compares the live CASC build against the persisted fastload manifest and offers a one-click delta update on mismatch; silent no-op when fastload is disabled or unavailable.
    /// </summary>
    public async Task PromptCascFastloadUpdateIfMismatchedAsync()
    {
        try
        {
            var installDir = Settings.CurrentProfile?.InstallDirectory;
            if (string.IsNullOrWhiteSpace(installDir) || !Directory.Exists(installDir))
            {
                return;
            }

            var manifestService = new CascFastloadManifestService(installDir);
            if (!manifestService.Exists)
            {
                // No canonical manifest = fresh slate; sweep stale legacy-location manifests.
                TryDeleteLegacyFastloadManifest(installDir);
                return;
            }

            var manifest = await manifestService.LoadAsync().ConfigureAwait(false);
            if (manifest.Files.Count == 0)
            {
                // Empty manifest file = fresh slate; remove it so the on-disk state matches.
                TryDeleteEmptyFastloadManifest(manifestService);
                return;
            }

            var native = new NativeCascLib();
            if (!native.IsAvailable)
            {
                return;
            }

            var extraction = new CascExtractionService(native);
            using var storage = extraction.OpenLocal(installDir);
            if (storage is null || storage.IsInvalid)
            {
                return;
            }

            var product = extraction.GetProduct(storage);
            if (product is null)
            {
                return;
            }

            if (CascFastloadManifestService.BuildMatches(manifest, product))
            {
                return;
            }

            // Mismatch — prompt the user.
            var oldDescriptor = string.IsNullOrWhiteSpace(manifest.BuildName)
                ? $"#{manifest.BuildNumber}"
                : $"{manifest.BuildName} (#{manifest.BuildNumber})";
            var newDescriptor = string.IsNullOrWhiteSpace(product.CodeName)
                ? $"#{product.BuildNumber}"
                : $"{product.CodeName} (#{product.BuildNumber})";

            var accepted = await Dispatcher.UIThread.InvokeAsync(
                () => ShowFastloadUpdatePromptAsync(oldDescriptor, newDescriptor));

            if (!accepted)
            {
                return;
            }

            await NavigateToCascFastloadViewAsync().ConfigureAwait(false);

            // Delegate to the view's public entry point so the op is owned by CascFastloadOperationState.
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (ContentArea.Content is CascFastloadView fastloadView)
                {
                    _ = fastloadView.StartExtractAsync(installDir);
                }
            });
        }
        catch
        {
            // Never let the prompt block startup. Silent failure is correct
            // here — the user can always run Extract manually from the view.
        }
    }

    /// <summary>Best-effort delete of the legacy <c>&lt;install&gt;\data\.reimagined-fastload.json</c>; called only when the canonical manifest is absent.</summary>
    private static void TryDeleteLegacyFastloadManifest(string installDirectory)
    {
        try
        {
            var legacyPath = Path.Combine(installDirectory, "data", ".reimagined-fastload.json");
            if (File.Exists(legacyPath))
            {
                File.Delete(legacyPath);
            }
        }
        catch
        {
            // Cleanup-only path; ignore I/O / permission failures.
        }
    }

    /// <summary>Removes a zero-entry manifest so observable state matches "never extracted".</summary>
    private static void TryDeleteEmptyFastloadManifest(CascFastloadManifestService manifestService)
    {
        try
        {
            if (File.Exists(manifestService.ManifestPath))
            {
                File.Delete(manifestService.ManifestPath);
            }
        }
        catch
        {
            // Cleanup-only path; ignore I/O / permission failures.
        }
    }

    private async Task<bool> ShowFastloadUpdatePromptAsync(string oldBuild, string newBuild)
    {
        var dialog = new Window
        {
            Width = 460,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Title = "D2R patched — update CASC fastload?"
        };

        var updateButton = new Button { Content = "Update Now", Classes = { "accent" }, MinWidth = 110 };
        var laterButton = new Button { Content = "Later", MinWidth = 96 };

        updateButton.Click += (_, _) => dialog.Close(true);
        laterButton.Click += (_, _) => dialog.Close(false);

        dialog.Content = new Border
        {
            Padding = new Avalonia.Thickness(20),
            Child = new StackPanel
            {
                Spacing = 14,
                Children =
                {
                    new TextBlock
                    {
                        Text = "D2R appears to have patched since your last CASC fastload extraction.",
                        FontWeight = FontWeight.SemiBold,
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap
                    },
                    new TextBlock
                    {
                        Text = $"Last extracted build: {oldBuild}{Environment.NewLine}Current CASC build: {newBuild}{Environment.NewLine}{Environment.NewLine}Run a delta update now to refresh only the changed files?",
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap
                    },
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Spacing = 10,
                        Children = { laterButton, updateButton }
                    }
                }
            }
        };

        return await dialog.ShowDialog<bool>(this);
    }

    public void ApplyUiScale()
    {
        _currentScale = ClampUiScale(Settings.UiScale);
        RootScaleControl.LayoutTransform = new ScaleTransform(_currentScale, _currentScale);
        UpdateRootGridSize();
    }

    private void UpdateRootGridSize()
    {
        var bounds = RootScaleControl.Bounds;
        if (bounds.Width > 0 && bounds.Height > 0 && _currentScale > 0)
        {
            var horizontalMargin = RootGrid.Margin.Left + RootGrid.Margin.Right;
            var verticalMargin = RootGrid.Margin.Top + RootGrid.Margin.Bottom;
            RootGrid.Width = (bounds.Width / _currentScale) - horizontalMargin;
            RootGrid.Height = (bounds.Height / _currentScale) - verticalMargin;
        }
    }

    private static double ClampUiScale(double uiScale)
    {
        if (uiScale <= 0)
        {
            return 1.0;
        }

        return Math.Clamp(uiScale, 0.8, 1.0);
    }

    private async void UserViewModelOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(UserViewModel.User) && UserViewModel.User != null)
        {
            await RefreshUpdateStateAsync();
            RefreshCurrentContent();
        }
    }

    public async Task<bool> RefreshAnnouncementsStateAsync(bool openUnreadAnnouncements = false)
    {
        var announcements = await _gitHubAnnouncementsHttpClient.GetAnnouncementsAsync();
        foreach (var announcement in announcements)
        {
            announcement.IsUnread = IsAnnouncementUnread(announcement.Number);
        }

        Announcements = announcements;
        HasUnreadAnnouncements = announcements.Any(announcement => announcement.IsUnread);

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            RefreshNewsNavigationLabel();

            if (ContentArea.Content is NewsAnnouncementsView newsAnnouncementsView)
            {
                newsAnnouncementsView.RefreshAnnouncementsState();
            }
        });

        if (openUnreadAnnouncements && HasUnreadAnnouncements)
        {
            await NavigateToNewsAnnouncementsViewAsync();
            return true;
        }

        return false;
    }

    public async Task MarkAnnouncementsReadUpToAsync(int discussionNumber)
    {
        if (discussionNumber <= Settings.LastReadAnnouncementNumber)
        {
            return;
        }

        Settings.LastReadAnnouncementNumber = discussionNumber;
        foreach (var announcement in Announcements)
        {
            announcement.IsUnread = IsAnnouncementUnread(announcement.Number);
        }

        HasUnreadAnnouncements = Announcements.Any(announcement => announcement.IsUnread);
        await SettingsManager.SaveAsync(Settings);

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            RefreshNewsNavigationLabel();
            RefreshCurrentContent();
        });
    }

    public async Task RefreshUpdateStateAsync()
    {
        if (!Settings.CurrentProfile.IsInstallDirectoryValidated || string.IsNullOrWhiteSpace(Settings.CurrentProfile.InstallDirectory))
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
            var isNewerAvailable = true;

            if (Version.TryParse(_localModVersion, out var localVer) &&
                Version.TryParse(latestVersion, out var latestVer))
            {
                isNewerAvailable = latestVer > localVer;
            }

            if (isNewerAvailable)
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
                    message: "Custom or developer build detected — your version is ahead of the latest release.",
                    currentVersion: _localModVersion,
                    latestVersion: latestVersion,
                    downloadUrl: downloadUrl,
                    isDirectDownload: updateLink.IsDirect,
                    fileId: latestFile.FileId);
            }
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
        if (!Settings.CurrentProfile.IsInstallDirectoryValidated || string.IsNullOrWhiteSpace(Settings.CurrentProfile.InstallDirectory) || IsLocalModDetected)
            return;

        await NavigateToUpdateViewAsync();
    }

    public void RefreshLocalModState(string? installDirectory = null)
    {
        _localModVersion = "Unknown";
        IsLocalModDetected = false;

        var profile = Settings.CurrentProfile;
        if (profile.Type == InstallationType.D2RMM)
        {
            if (!string.IsNullOrWhiteSpace(profile.InstallDirectory))
            {
                var modPath = InstallDirectoryValidator.ResolveD2RmmModFolder(profile.InstallDirectory);
                IsLocalModDetected = modPath != null;

                // modinfo.json is the canonical version source (see CharacterSelectPanelService).
                var modInfoVersion = CharacterSelectPanelService.GetModVersion(modPath);
                _localModVersion = !string.IsNullOrWhiteSpace(modInfoVersion)
                    ? modInfoVersion
                    : "Unknown";
            }
        }
        else
        {
            var installDir = installDirectory ?? Settings.CurrentProfile.InstallDirectory;
            if (!string.IsNullOrWhiteSpace(installDir))
            {
                var modRootDirectory = Path.Combine(installDir, "mods", "Reimagined");
                var modInfoPath = Path.Combine(modRootDirectory, "modinfo.json");
                var modInfoPathInMpq = Path.Combine(modRootDirectory, "Reimagined.mpq", "modinfo.json");

                // modinfo.json is the single source of truth for the installed mod version;
                // also stamped onto mod-source manifest entries for stale-overlay detection.
                var modInfoVersion =
                    CharacterSelectPanelService.GetModVersionFromFile(modInfoPath)
                    ?? CharacterSelectPanelService.GetModVersionFromFile(modInfoPathInMpq);
                _localModVersion = !string.IsNullOrWhiteSpace(modInfoVersion)
                    ? modInfoVersion
                    : "Unknown";

                // Detection requires modinfo.json; directory existence alone is the bootstrapped
                // "mod uninstalled" state and must not enable the launch button.
                IsLocalModDetected = File.Exists(modInfoPath) || File.Exists(modInfoPathInMpq);
            }
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
            switch (item.Tag?.ToString())
            {
                case "NewsAnnouncements":
                    _ = NavigateToNewsAnnouncementsViewAsync();
                    break;
                case "Launch":
                    var launchView = new LaunchView();
                    launchView.RefreshInstallDirectoryState();
                    ContentArea.Content = launchView;
                    break;
                case "Backups":
                    _ = NavigateToBackupsViewAsync();
                    break;
                case "CascFastload":
                    _ = NavigateToCascFastloadViewAsync();
                    break;
                case "Plugins":
                    _ = NavigateToPluginsViewAsync();
                    break;
                case "Settings":
                    var settingsView = new SettingsView();
                    settingsView.RefreshSettingsState();
                    ContentArea.Content = settingsView;
                    break;
                case "ModTweaks":
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
        RefreshUpdateAvailableBadge();
    }

    private void RefreshUpdateAvailableBadge()
    {
        void UpdateBadge()
        {
            var show = IsUpdateAvailable &&
                       !string.IsNullOrWhiteSpace(UpdateLatestVersion) &&
                       !UpdateLatestVersion.Equals("Unknown", StringComparison.OrdinalIgnoreCase) &&
                       !UpdateLatestVersion.Equals("Latest available", StringComparison.OrdinalIgnoreCase);

            UpdateAvailableTextBlock.IsVisible = show;
            UpdateAvailableTextBlock.Text = show ? $"(Update v{UpdateLatestVersion} Available)" : string.Empty;
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            UpdateBadge();
        }
        else
        {
            Dispatcher.UIThread.Post(UpdateBadge);
        }
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

    public async Task NavigateToNewsAnnouncementsViewAsync()
    {
        NewsAnnouncementsView? newsAnnouncementsView = null;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            newsAnnouncementsView = new NewsAnnouncementsView();
            newsAnnouncementsView.SetLoadingState(true);
            ContentArea.Content = newsAnnouncementsView;

            if (NewsAnnouncementsNavItem != null && NavigationList.SelectedItem != NewsAnnouncementsNavItem)
            {
                NavigationList.SelectedItem = NewsAnnouncementsNavItem;
            }
        });

        try
        {
            await RefreshAnnouncementsStateAsync();
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (newsAnnouncementsView != null && ReferenceEquals(ContentArea.Content, newsAnnouncementsView))
                {
                    newsAnnouncementsView.SetLoadingState(false);
                }
            });
        }
    }

    public async Task NavigateToBackupsViewAsync()
    {
        BackupsView? backupsView = null;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            backupsView = new BackupsView();
            backupsView.SetLoadingState(true);
            ContentArea.Content = backupsView;

            if (BackupsNavItem != null && NavigationList.SelectedItem != BackupsNavItem)
            {
                NavigationList.SelectedItem = BackupsNavItem;
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

    public async Task NavigateToCascFastloadViewAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var view = new CascFastloadView();
            ContentArea.Content = view;

            if (CascFastloadNavItem != null && NavigationList.SelectedItem != CascFastloadNavItem)
            {
                NavigationList.SelectedItem = CascFastloadNavItem;
            }
        });
    }

    public async Task NavigateToPluginsViewAsync()
    {
        PluginsView? pluginsView = null;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            pluginsView = new PluginsView();
            pluginsView.SetLoadingState(true);
            ContentArea.Content = pluginsView;

            if (PluginsNavItem != null && NavigationList.SelectedItem != PluginsNavItem)
            {
                NavigationList.SelectedItem = PluginsNavItem;
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

        if (PluginsNavItem != null && NavigationList.SelectedItem != PluginsNavItem)
        {
            NavigationList.SelectedItem = PluginsNavItem;
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

        if (PluginsNavItem != null && NavigationList.SelectedItem != PluginsNavItem)
        {
            NavigationList.SelectedItem = PluginsNavItem;
        }
    }

    public void NavigateToUserPluginsView()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(NavigateToUserPluginsView);
            return;
        }

        ContentArea.Content = new UserPluginsView();

        if (PluginsNavItem != null && NavigationList.SelectedItem != PluginsNavItem)
        {
            NavigationList.SelectedItem = PluginsNavItem;
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
        else if (ContentArea.Content is NewsAnnouncementsView newsAnnouncementsView)
        {
            newsAnnouncementsView.RefreshAnnouncementsState();
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
        else if (ContentArea.Content is UserPluginsView userPluginsView)
        {
            _ = userPluginsView.RefreshUserPluginsAsync();
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

    private static bool IsAnnouncementUnread(int discussionNumber)
    {
        return discussionNumber > Settings.LastReadAnnouncementNumber;
    }

    private void RefreshNewsNavigationLabel()
    {
        if (NewsAnnouncementsNavItem == null)
        {
            return;
        }

        NewsAnnouncementsNavItem.Content = HasUnreadAnnouncements
            ? "News & Announcements (New)"
            : "News & Announcements";
    }

    private void OnLauncherRestartClicked(object? sender, RoutedEventArgs e)
    {
        LauncherUpdateService.ApplyUpdateAndRestart();
    }

    // Starts/restarts the hourly background launcher-update check; idempotent.
    private void StartLauncherUpdateCheckTimer()
    {
        _launcherUpdateCheckTimer?.Stop();
        _launcherUpdateCheckTimer = new DispatcherTimer { Interval = LauncherUpdateCheckInterval };
        _launcherUpdateCheckTimer.Tick += (_, _) => _ = LauncherUpdateService.CheckForUpdatesAsync();
        _launcherUpdateCheckTimer.Start();
    }

    // "Launcher v#.#.#" click handler: triggers an immediate update check with a brief notification.
    private async void OnLauncherVersionClicked(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        try
        {
            Notifications.SendNotification("Checking for launcher updates...", "Launcher");
            await LauncherUpdateService.CheckForUpdatesAsync();
            if (!LauncherUpdateService.IsUpdateAvailable && !LauncherUpdateService.IsUpdateDownloaded)
            {
                Notifications.SendNotification($"Launcher v{LauncherVersion} is up to date.", "Launcher");
            }
        }
        catch (Exception ex)
        {
            Notifications.SendNotification($"Update check failed: {ex.Message}", "Error");
        }
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
    
    private void InitializeTrayIcon()
    {
        var showItem = new NativeMenuItem("Show Launcher");
        showItem.Click += (_, _) => RestoreFromTray();

        var exitItem = new NativeMenuItem("Exit Launcher");
        exitItem.Click += (_, _) => ExitFromTray();

        _trayIcon = new TrayIcon
        {
            Icon = Icon,
            ToolTipText = "D2R Reimagined Launcher",
            IsVisible = true,
            Menu = new NativeMenu { showItem, exitItem }
        };
        _trayIcon.Clicked += (_, _) => RestoreFromTray();
    }

    public void MinimizeToTray()
    {
        Hide();
    }

    public void RestoreFromTray()
    {
        Dispatcher.UIThread.Post(() =>
        {
            Show();
            WindowState = Settings.IsMaximized ? WindowState.Maximized : WindowState.Normal;
            Activate();
        });
    }

    private void RestoreWindowState()
    {
        _isRestoringWindowState = true;
        try
        {
            if (Settings.WindowWidth.HasValue && Settings.WindowHeight.HasValue)
            {
                Width = Settings.WindowWidth.Value;
                Height = Settings.WindowHeight.Value;
            }

            if (Settings.WindowX.HasValue && Settings.WindowY.HasValue)
            {
                var x = (int)Settings.WindowX.Value;
                var y = (int)Settings.WindowY.Value;
                var w = (int)(Settings.WindowWidth ?? Width);
                var h = (int)(Settings.WindowHeight ?? Height);

                if (IsPositionWithinScreenBounds(x, y, w, h))
                {
                    Position = new PixelPoint(x, y);
                    WindowStartupLocation = WindowStartupLocation.Manual;
                }
            }

            if (Settings.IsMaximized)
            {
                WindowState = WindowState.Maximized;
            }
        }
        finally
        {
            _isRestoringWindowState = false;
        }
    }

    private bool IsPositionWithinScreenBounds(int x, int y, int width, int height)
    {
        var screens = Screens;
        if (screens.ScreenCount == 0)
            return false;

        // Check that at least a meaningful portion of the window is visible on some screen
        const int minVisiblePixels = 50;
        foreach (var screen in screens.All)
        {
            var workArea = screen.WorkingArea;
            var overlapLeft = Math.Max(x, workArea.X);
            var overlapTop = Math.Max(y, workArea.Y);
            var overlapRight = Math.Min(x + width, workArea.X + workArea.Width);
            var overlapBottom = Math.Min(y + height, workArea.Y + workArea.Height);

            if (overlapRight - overlapLeft >= minVisiblePixels && overlapBottom - overlapTop >= minVisiblePixels)
                return true;
        }

        return false;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty)
        {
            OnWindowPositionOrSizeChanged(null, EventArgs.Empty);
        }
    }

    private void OnWindowPositionOrSizeChanged(object? sender, EventArgs e)
    {
        if (_isRestoringWindowState)
            return;

        Settings.IsMaximized = WindowState == WindowState.Maximized;

        if (WindowState == WindowState.Normal)
        {
            Settings.WindowWidth = Bounds.Width;
            Settings.WindowHeight = Bounds.Height;
            Settings.WindowX = Position.X;
            Settings.WindowY = Position.Y;
        }

        _saveWindowStateTimer?.Stop();
        _saveWindowStateTimer?.Start();
    }

    private void ExitFromTray()
    {
        Dispatcher.UIThread.Post(() =>
        {
            _isExiting = true;
            _trayIcon?.Dispose();
            _trayIcon = null;
            Close();
        });
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!_isExiting && Settings.MinimizeToTrayOnClose)
        {
            e.Cancel = true;
            MinimizeToTray();
            return;
        }

        _trayIcon?.Dispose();
        _trayIcon = null;
        base.OnClosing(e);
    }

    public async Task MinimizeToTrayAndWaitForExitAsync(Process gameProcess, string? expectedExePath = null)
    {
        MinimizeToTray();

        await Task.Run(() =>
        {
            Process? processToWatch = null;
            try
            {
                // When launched via Steam, the returned process is Steam.exe, not the game.
                // Poll briefly to find the actual D2R.exe process by its executable path.
                processToWatch = gameProcess;
                if (!string.IsNullOrEmpty(expectedExePath))
                {
                    var found = WaitForProcessByPath(expectedExePath, timeout: TimeSpan.FromSeconds(60));
                    if (found != null)
                    {
                        gameProcess.Dispose();
                        processToWatch = found;
                    }
                }

                processToWatch.WaitForExit();
            }
            catch (InvalidOperationException)
            {
                // Process already exited or handle is invalid
            }
            finally
            {
                processToWatch?.Dispose();
            }
        });

        RestoreFromTray();
    }

    private static Process? WaitForProcessByPath(string exePath, TimeSpan timeout)
    {
        var processName = Path.GetFileNameWithoutExtension(exePath);
        var normalizedTarget = Path.GetFullPath(exePath);
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                foreach (var proc in Process.GetProcessesByName(processName))
                {
                    try
                    {
                        var mainModule = proc.MainModule;
                        if (mainModule != null &&
                            string.Equals(Path.GetFullPath(mainModule.FileName), normalizedTarget, StringComparison.OrdinalIgnoreCase))
                        {
                            return proc;
                        }
                    }
                    catch
                    {
                        // Access denied or process exited — skip
                    }

                    proc.Dispose();
                }
            }
            catch
            {
                // Enumeration failed — retry
            }

            Thread.Sleep(2000);
        }

        return null;
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
