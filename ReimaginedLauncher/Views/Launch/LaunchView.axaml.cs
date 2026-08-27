using System.Threading.Tasks;
using System;
using System.Diagnostics;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using ReimaginedLauncher.HttpClients;
using ReimaginedLauncher.HttpClients.Models;
using ReimaginedLauncher.Utilities;

namespace ReimaginedLauncher.Views.Launch;

public partial class LaunchView : UserControl
{
    public GameLauncherService LauncherService = new();
    private readonly ReimaginedApiHttpClient _apiHttpClient;
    private readonly LauncherAuthenticationService _launcherAuthenticationService;
    private readonly D2RLoaderInstallerService _d2rLoaderInstallerService;
    private bool _isLaunching;
    private bool _isLoaderInstallPromptOpen;
    private bool _isRefreshingLadders;
    private bool _isRefreshingLadderControls;
    private bool _ladderStatusLoaded;
    private bool _ladderPolicyVerified;
    private string? _ladderLoadError;
    private IReadOnlyList<string> _missingRequiredLadderExtensions = [];
    private IReadOnlyList<LadderResponse> _activeLadders = [];
    private IReadOnlyList<LadderExtensionChoice> _ladderExtensionChoices = [];
    private D2RLoaderInventory? _loaderInventory;
    private bool? _isCompactLayout;

    public LaunchView()
    {
        InitializeComponent();
        _apiHttpClient = Program.ServiceProvider.GetRequiredService<ReimaginedApiHttpClient>();
        _launcherAuthenticationService = Program.ServiceProvider.GetRequiredService<LauncherAuthenticationService>();
        _d2rLoaderInstallerService = Program.ServiceProvider.GetRequiredService<D2RLoaderInstallerService>();
        SizeChanged += (_, _) => UpdateResponsiveLayout();

        RefreshInstallDirectoryState();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        UpdateResponsiveLayout();

        if (MainWindow.Settings is not null && !LauncherService.IsDetecting)
        {
            // Skip D2R.exe detection entirely when the active profile is D2RMM —
            // it doesn't need a game executable.
            var currentType = MainWindow.Settings.CurrentProfile.Type;
            var needsDetection = false;

            if (currentType != InstallationType.D2RMM)
            {
                // Run detection if the current profile isn't validated, or if any
                // non-D2RMM profile is still missing its install directory (dual-install check).
                needsDetection = !MainWindow.Settings.CurrentProfile.IsInstallDirectoryValidated;
                if (!needsDetection)
                {
                    foreach (var p in MainWindow.Settings.Profiles)
                    {
                        if (p.Type != InstallationType.D2RMM && !p.IsInstallDirectoryValidated)
                        {
                            needsDetection = true;
                            break;
                        }
                    }
                }
            }

            if (needsDetection)
            {
                _ = LauncherService.CheckForD2RExecutableAsync(async () =>
                {
                    await Dispatcher.UIThread.InvokeAsync(async () =>
                    {
                        RefreshInstallDirectoryState();
                        if (TopLevel.GetTopLevel(this) is MainWindow mw)
                        {
                            mw.RefreshLocalModState();
                            await mw.RefreshUpdateStateAsync();
                        }
                    });
                });
            }

            RefreshInstallDirectoryState();
        }

        _ = RefreshLadderStateAsync();
    }

    private void UpdateResponsiveLayout()
    {
        if (Bounds.Width <= 0)
        {
            return;
        }

        var isCompact = Bounds.Width < 960;
        if (_isCompactLayout == isCompact)
        {
            return;
        }

        _isCompactLayout = isCompact;

        ConfigureGrid(ExperienceGrid, isCompact ? 1 : 3, isCompact ? 3 : 1);
        ExperienceGrid.ColumnSpacing = isCompact ? 0 : 12;
        ExperienceGrid.RowSpacing = isCompact ? 12 : 0;
        PositionGridChild(OfflineExperienceButton, 0, 0);
        PositionGridChild(OnlineExperienceButton, isCompact ? 0 : 1, isCompact ? 1 : 0);
        PositionGridChild(LadderExperienceButton, isCompact ? 0 : 2, isCompact ? 2 : 0);

        ConfigureTwoPanelGrid(
            LaunchSetupGrid,
            LaunchPanel,
            InstallDirectoryPanel,
            isCompact);

        ConfigureTwoPanelGrid(
            LadderExtensionsGrid,
            AllowedLadderPluginsPanel,
            AllowedLadderPatchesPanel,
            isCompact);
        ConfigureTwoPanelGrid(
            LoaderExtensionsGrid,
            LoaderPluginsPanel,
            LoaderPatchesPanel,
            isCompact);

        ConfigureGrid(LoaderHeaderGrid, isCompact ? 1 : 2, isCompact ? 2 : 1, secondColumnAuto: true);
        LoaderHeaderGrid.ColumnSpacing = isCompact ? 0 : 16;
        LoaderHeaderGrid.RowSpacing = isCompact ? 12 : 0;
        PositionGridChild(LoaderActionsPanel, isCompact ? 0 : 1, isCompact ? 1 : 0);

        LaunchActionsPanel.Orientation = isCompact ? Orientation.Vertical : Orientation.Horizontal;
    }

    private static void ConfigureTwoPanelGrid(Grid grid, Control first, Control second, bool isCompact)
    {
        ConfigureGrid(grid, isCompact ? 1 : 2, isCompact ? 2 : 1);
        grid.ColumnSpacing = isCompact ? 0 : 14;
        grid.RowSpacing = isCompact ? 14 : 0;
        PositionGridChild(first, 0, 0);
        PositionGridChild(second, isCompact ? 0 : 1, isCompact ? 1 : 0);
    }

    private static void ConfigureGrid(Grid grid, int columnCount, int rowCount, bool secondColumnAuto = false)
    {
        grid.ColumnDefinitions.Clear();
        for (var index = 0; index < columnCount; index++)
        {
            var width = secondColumnAuto && index == 1 ? GridLength.Auto : GridLength.Star;
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
        }

        grid.RowDefinitions.Clear();
        for (var index = 0; index < rowCount; index++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }
    }

    private static void PositionGridChild(Control control, int column, int row)
    {
        Grid.SetColumn(control, column);
        Grid.SetRow(control, row);
    }

    public void RefreshInstallDirectoryState()
    {
        var settings = MainWindow.Settings;
        var profile = settings.CurrentProfile;
        var isOnlineExperience = profile.LaunchExperience == LaunchExperience.Online;
        var isLadderExperience = profile.LaunchExperience == LaunchExperience.Ladder;
        var ladderAvailable = HasActiveLadder;
        var isReimaginedSignedIn = _launcherAuthenticationService.IsSignedIn;

        InstallationTypeComboBox.SelectedIndex = (int)profile.Type;
        DirectoryTextBox.Text = profile.InstallDirectory ?? string.Empty;
        DetectionLoadingIndicator.IsVisible = LauncherService.IsDetecting;

        SteamExtraPanel.IsVisible = profile.Type == InstallationType.Steam;
        SteamPathTextBox.Text = profile.SteamDirectory ?? string.Empty;
        SteamPathTextBox.PlaceholderText = OperatingSystem.IsLinux() ? "Steam or Flatpak executable" : "Steam.exe Path";
        LocateSteamButton.Content = OperatingSystem.IsLinux() ? "Locate Steam" : "Locate Steam.exe";

        // Auto-detect Steam path if not set or if it's currently Steam type
        if (profile.Type == InstallationType.Steam)
        {
            var detectedSteam = LauncherService.FindSteamExecutable(profile.InstallDirectory);
            if (!string.IsNullOrEmpty(detectedSteam) && File.Exists(detectedSteam))
            {
                if (profile.SteamDirectory != detectedSteam)
                {
                    profile.SteamDirectory = detectedSteam;
                    SteamPathTextBox.Text = detectedSteam;
                }
                LocateSteamButton.IsEnabled = false;
            }
            else
            {
                LocateSteamButton.IsEnabled = true;
            }
        }


        bool isValidated;
        bool isModDetected;

        if (profile.Type == InstallationType.D2RMM)
        {
            InstallDirectoryTitle.Text = "D2RMM Mods Folder";
            InstallDirectoryDescription.Text = "Select your D2RMM mods folder where Reimagined will be installed.";
            
            isValidated = InstallDirectoryValidator.IsValidD2RmmModsDirectory(profile.InstallDirectory) && Directory.Exists(profile.InstallDirectory);
            
            // For D2RMM, check if Reimagined or Reimagined.mpq exists in the mods folder
            isModDetected = isValidated && InstallDirectoryValidator.ResolveD2RmmModFolder(profile.InstallDirectory) != null;
        }
        else
        {
            InstallDirectoryTitle.Text = "Install Directory";
            InstallDirectoryDescription.Text = "Select the Diablo II: Resurrected folder that contains your local mod installation (Folder with .exe in it)";
            isValidated = profile.Type == InstallationType.Steam
                ? InstallDirectoryValidator.IsValidSteamInstallDirectory(profile.InstallDirectory)
                : InstallDirectoryValidator.IsValidInstallDirectory(profile.InstallDirectory);
            isModDetected = MainWindow.IsLocalModDetected;
        }

        profile.IsInstallDirectoryValidated = isValidated;

        _loaderInventory = D2RLoaderService.Discover(profile.InstallDirectory);
        RefreshD2RLoaderState(profile, _loaderInventory);
        var loaderAvailable = D2RLoaderService.CanUseOnlineExperience(profile, out var loaderUnavailableReason);

        OfflineExperienceButton.Classes.Set("selected", profile.LaunchExperience == LaunchExperience.Offline);
        OnlineExperienceButton.Classes.Set("selected", isOnlineExperience);
        LadderExperienceButton.Classes.Set("selected", isLadderExperience);
        OnlineExperienceButton.IsEnabled = profile.Type != InstallationType.D2RMM;
        LadderExperienceButton.IsEnabled = profile.Type != InstallationType.D2RMM && ladderAvailable;
        OnlineExperiencePanel.IsVisible = isOnlineExperience && profile.Type != InstallationType.D2RMM;
        LadderPolicyPanel.IsVisible = isLadderExperience && profile.Type != InstallationType.D2RMM;
        LadderAuthenticationWarningBanner.IsVisible = isLadderExperience && !isReimaginedSignedIn;
        RefreshLadderAuthenticationState();

        if (profile.Type == InstallationType.D2RMM)
        {
            StartGameButton.Content = "Install Tweaks";
            StartGameDescription.Text = "Clicking 'Install Tweaks' will apply tweaks and adjustments to the files in your D2RMM/mods/Reimagined/data directory.";
            StartGameButton.IsEnabled = !_isLaunching && isValidated && isModDetected;
        }
        else
        {
            StartGameButton.Content = isOnlineExperience
                ? "Start Online"
                : isLadderExperience
                    ? "Start Ladder"
                    : "Start Offline";
            StartGameDescription.Text = isOnlineExperience
                ? "Starts D2RLoader with Reimagined selected. Choose TCP/IP in-game to host or join; this does not connect to Battle.net."
                : isLadderExperience
                    ? isReimaginedSignedIn
                        ? "Restores clean base files, enforces the ladder extension allowlist, and starts Reimagined through D2RLoader."
                        : "Sign in with your D2R Reimagined website account before starting a ladder character."
                    : "Starts the standard Reimagined offline experience with your saved launch options.";
            StartGameButton.IsEnabled = !_isLaunching
                                        && isValidated
                                        && isModDetected
                                        && (!isOnlineExperience || loaderAvailable)
                                        && (!isLadderExperience
                                            || isReimaginedSignedIn
                                            && ladderAvailable
                                            && loaderAvailable
                                            && _ladderPolicyVerified);

            if (!isOnlineExperience
                && !isLadderExperience
                && profile.Type == InstallationType.Steam
                && string.IsNullOrWhiteSpace(profile.SteamDirectory))
            {
                StartGameButton.IsEnabled = false;
            }
        }

        ValidationBanner.IsVisible = !isValidated
                                     || !isModDetected
                                     || isOnlineExperience && !loaderAvailable
                                     || isLadderExperience
                                     && (!ladderAvailable
                                         || !loaderAvailable
                                         || !_ladderPolicyVerified);
        
        if (profile.Type == InstallationType.D2RMM)
        {
            ValidationBannerText.Text = string.IsNullOrWhiteSpace(profile.InstallDirectory)
                ? "Select your D2RMM mods folder."
                : !InstallDirectoryValidator.IsValidD2RmmModsDirectory(profile.InstallDirectory)
                    ? InstallDirectoryValidator.GetD2RmmValidationMessage(profile.InstallDirectory)
                    : !isModDetected && isValidated
                        ? "Reimagined not yet installed in this mods folder."
                        : "The selected folder could not be found.";
        }
        else
        {
            ValidationBannerText.Text = isLadderExperience
                                        && isValidated
                                        && isModDetected
                                        && (!ladderAvailable
                                            || !loaderAvailable
                                            || !_ladderPolicyVerified)
                ? !_ladderPolicyVerified && ladderAvailable && loaderAvailable
                    ? GetLadderPolicyUnavailableMessage()
                    : GetLadderUnavailableMessage(loaderAvailable ? null : loaderUnavailableReason)
                : isOnlineExperience && isValidated && isModDetected && !loaderAvailable
                ? loaderUnavailableReason ?? "D2RLoader is unavailable for this profile."
                : !isValidated
                ? string.IsNullOrWhiteSpace(profile.InstallDirectory)
                    ? "Enter your Diablo II: Resurrected install directory before using the launcher."
                    : profile.Type == InstallationType.Steam
                        && InstallDirectoryValidator.IsValidInstallDirectory(profile.InstallDirectory)
                        ? "The selected directory does not contain steam_*.dll. Please select a valid Steam installation or switch to Battle.Net."
                        : "The selected install directory has not been validated. Choose the folder that contains D2R.exe."
                : "D2R Reimagined mod not detected in this directory. Install the mod before launching.";
        }
        
        LaunchCommandText.Text = LauncherService.BuildLaunchCommand();

        BackupOnLaunchSummary.Text = $"Backup on Launch: {(profile.AutomaticBackupsEnabled ? "Yes" : "No")}";
        BackupIntervalSummary.Text = profile.AutomaticBackupsEnabled
            ? $"Auto-Backup Interval: {profile.BackupIntervalMinutes} min"
            : "Auto-Backup Interval: N/A";
    }

    public void RefreshAuthenticationState()
    {
        RefreshInstallDirectoryState();
    }

    private void RefreshLadderAuthenticationState()
    {
        var user = _launcherAuthenticationService.CurrentUser;
        LadderSignInButton.IsVisible = user is null;
        LadderSignInButton.IsEnabled = user is null;
    }

    private async void OnLadderSignInClick(object? sender, RoutedEventArgs e)
    {
        LadderSignInButton.IsEnabled = false;
        try
        {
            if (MainWindow.Instance is { } mainWindow)
            {
                await mainWindow.SignInToReimaginedAsync();
            }
        }
        finally
        {
            RefreshInstallDirectoryState();
        }
    }

    private void RefreshD2RLoaderState(InstallationProfile profile, D2RLoaderInventory inventory)
    {
        LoaderPluginsItemsControl.ItemsSource = inventory.Plugins;
        LoaderPatchesItemsControl.ItemsSource = inventory.Patches;
        LoaderPluginCountText.Text = inventory.Plugins.Count.ToString();
        LoaderPatchCountText.Text = inventory.Patches.Count.ToString();
        NoLoaderPluginsText.IsVisible = inventory.Plugins.Count == 0;
        NoLoaderPatchesText.IsVisible = inventory.Patches.Count == 0;

        LoaderStatusBadge.Background = new SolidColorBrush(Color.Parse(inventory.IsInstalled ? "#17351D" : "#3A1818"));
        LoaderStatusBadgeText.Foreground = new SolidColorBrush(Color.Parse(inventory.IsInstalled ? "#86D88F" : "#E98B91"));
        LoaderStatusBadgeText.Text = inventory.IsInstalled ? "READY" : "NOT FOUND";
        LoaderStatusText.Text = inventory.IsInstalled
            ? $"D2RLoader {inventory.Version ?? "unknown version"} detected beside D2R.exe. "
              + $"Found {inventory.Plugins.Count} plugin{(inventory.Plugins.Count == 1 ? string.Empty : "s")} and "
              + $"{inventory.Patches.Count} patch manifest{(inventory.Patches.Count == 1 ? string.Empty : "s")}."
            : "Place D2RLoader.exe in the same folder as D2R.exe to enable this experience.";

        var disabledScopes = new[]
            {
                inventory.AllowGlobalExtensions ? null : "global extensions",
                inventory.AllowModExtensions ? null : "Reimagined extensions"
            }
            .Where(value => value is not null)
            .ToArray();
        LoaderExtensionPolicyText.Text = disabledScopes.Length == 0
            ? "Global and Reimagined extension loading are enabled in d2rloader.toml."
            : $"Disabled by d2rloader.toml: {string.Join(", ", disabledScopes!)}.";

        var canUseOnline = D2RLoaderService.CanUseOnlineExperience(profile, out var reason);
        LoaderWarningBanner.IsVisible = !canUseOnline;
        LoaderWarningText.Text = reason ?? string.Empty;
        OpenLoaderFolderButton.IsEnabled = Directory.Exists(inventory.GlobalRoot);
        OpenModLoaderFolderButton.IsEnabled = Directory.Exists(inventory.ModRoot)
                                                || Directory.Exists(Path.GetDirectoryName(inventory.ModRoot));
    }

    private async void OnOfflineExperienceClick(object? sender, RoutedEventArgs e)
    {
        await SetLaunchExperienceAsync(LaunchExperience.Offline);
    }

    private async void OnOnlineExperienceClick(object? sender, RoutedEventArgs e)
    {
        await SetLaunchExperienceAsync(LaunchExperience.Online);
    }

    private async void OnLadderExperienceClick(object? sender, RoutedEventArgs e)
    {
        if (HasActiveLadder)
        {
            await SetLaunchExperienceAsync(LaunchExperience.Ladder);
        }
    }

    private async Task SetLaunchExperienceAsync(LaunchExperience experience)
    {
        var profile = MainWindow.Settings.CurrentProfile;
        if (profile.Type == InstallationType.D2RMM)
        {
            return;
        }

        if (profile.LaunchExperience != experience)
        {
            profile.LaunchExperience = experience;
            await SettingsManager.SaveAsync(MainWindow.Settings);
            RefreshInstallDirectoryState();
        }

        if (experience is LaunchExperience.Online or LaunchExperience.Ladder)
        {
            await PromptInstallD2RLoaderAsync(profile);
        }
    }

    private async Task PromptInstallD2RLoaderAsync(InstallationProfile profile)
    {
        if (_isLoaderInstallPromptOpen
            || D2RLoaderService.IsInstalled(profile.InstallDirectory)
            || !OperatingSystem.IsWindows()
            || !InstallDirectoryValidator.IsValidInstallDirectory(profile.InstallDirectory)
            || TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }

        _isLoaderInstallPromptOpen = true;
        try
        {
            var installed = await ShowD2RLoaderInstallDialogAsync(owner, profile.InstallDirectory!);
            if (!installed)
            {
                return;
            }

            LaunchDiagnostics.Log($"D2RLoader installed to {profile.InstallDirectory}.");
            Notifications.SendNotification(
                "D2RLoader installed. Online and Ladder modes are now ready to use.",
                "Success");
            RefreshInstallDirectoryState();
            await RefreshLadderStateAsync();
        }
        finally
        {
            _isLoaderInstallPromptOpen = false;
        }
    }

    private async Task<bool> ShowD2RLoaderInstallDialogAsync(Window owner, string installDirectory)
    {
        var dialog = new Window
        {
            Width = 520,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Title = "Install D2RLoader?"
        };

        var statusText = new TextBlock
        {
            IsVisible = false,
            TextWrapping = TextWrapping.Wrap
        };
        var progressBar = new ProgressBar
        {
            IsVisible = false,
            IsIndeterminate = true,
            Minimum = 0,
            Maximum = 100
        };
        var installButton = new Button
        {
            Content = "Download and Install",
            Classes = { "accent" },
            MinWidth = 156
        };
        var cancelButton = new Button
        {
            Content = "Not Now",
            MinWidth = 96
        };
        var isInstalling = false;
        var isInstalled = false;

        installButton.Click += async (_, _) =>
        {
            if (isInstalled)
            {
                dialog.Close(true);
                return;
            }

            isInstalling = true;
            installButton.IsEnabled = false;
            cancelButton.IsEnabled = false;
            statusText.IsVisible = true;
            statusText.Foreground = new SolidColorBrush(Color.Parse("#F7F1E3"));
            progressBar.IsVisible = true;

            var progress = new Progress<D2RLoaderInstallProgress>(update =>
            {
                statusText.Text = update.Message;
                progressBar.IsIndeterminate = !update.Percentage.HasValue;
                if (update.Percentage.HasValue)
                {
                    progressBar.Value = update.Percentage.Value;
                }
            });

            try
            {
                await _d2rLoaderInstallerService.InstallAsync(installDirectory, progress);
                isInstalled = true;
                statusText.Text = "D2RLoader installed successfully.";
                statusText.Foreground = new SolidColorBrush(Color.Parse("#86D88F"));
                progressBar.IsIndeterminate = false;
                progressBar.Value = 100;
                installButton.Content = "Close";
                installButton.IsEnabled = true;
                cancelButton.IsVisible = false;
            }
            catch (Exception exception)
            {
                LaunchDiagnostics.LogException("D2RLoader installation failed", exception);
                statusText.Text = $"Installation failed: {exception.Message}";
                statusText.Foreground = new SolidColorBrush(Color.Parse("#E98B91"));
                progressBar.IsVisible = false;
                installButton.IsEnabled = true;
                cancelButton.IsEnabled = true;
                cancelButton.Content = "Close";
            }
            finally
            {
                isInstalling = false;
            }
        };

        cancelButton.Click += (_, _) => dialog.Close(false);
        dialog.Closing += (_, args) =>
        {
            if (isInstalling)
            {
                args.Cancel = true;
            }
        };

        dialog.Content = new Border
        {
            Padding = new Thickness(22),
            Child = new StackPanel
            {
                Spacing = 14,
                Children =
                {
                    new TextBlock
                    {
                        Text = "D2RLoader is required for Online and Ladder modes, but it was not found beside D2R.exe.",
                        FontWeight = FontWeight.SemiBold,
                        TextWrapping = TextWrapping.Wrap
                    },
                    new TextBlock
                    {
                        Text = "Would you like the launcher to download D2RLoader and extract it into your Diablo II: Resurrected installation?",
                        TextWrapping = TextWrapping.Wrap
                    },
                    new TextBlock
                    {
                        Text = installDirectory,
                        Classes = { "muted" },
                        TextWrapping = TextWrapping.Wrap
                    },
                    statusText,
                    progressBar,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 10,
                        Children =
                        {
                            cancelButton,
                            installButton
                        }
                    }
                }
            }
        };

        return await dialog.ShowDialog<bool>(owner);
    }

    private void OnRefreshLoaderClick(object? sender, RoutedEventArgs e)
    {
        RefreshInstallDirectoryState();
    }

    private bool HasActiveLadder => _activeLadders.Any(ladder =>
        ladder.StartDateUtc <= DateTimeOffset.UtcNow && ladder.EndDateUtc >= DateTimeOffset.UtcNow);

    private LadderResponse? SelectedLadder
    {
        get
        {
            var selectedId = MainWindow.Settings.CurrentProfile.SelectedLadderId;
            return _activeLadders.FirstOrDefault(ladder => ladder.Id == selectedId)
                   ?? _activeLadders.FirstOrDefault(ladder =>
                       ladder.StartDateUtc <= DateTimeOffset.UtcNow
                       && ladder.EndDateUtc >= DateTimeOffset.UtcNow);
        }
    }

    private async Task RefreshLadderStateAsync()
    {
        if (_isRefreshingLadders)
        {
            return;
        }

        _isRefreshingLadders = true;
        _ladderStatusLoaded = false;
        _ladderLoadError = null;
        LadderStatusText.Text = "Checking for active ladders...";
        LadderExtensionPolicyStatusText.Text = "Checking installed D2RLoader extensions...";
        _ladderPolicyVerified = false;
        ActiveLaddersItemsControl.ItemsSource = null;
        RefreshInstallDirectoryState();

        try
        {
            _activeLadders = await _apiHttpClient.GetActiveLaddersAsync();
            ActiveLaddersItemsControl.ItemsSource = _activeLadders;
            EnsureSelectedLadder();
            _isRefreshingLadderControls = true;
            ActiveLadderComboBox.ItemsSource = _activeLadders;
            ActiveLadderComboBox.SelectedItem = SelectedLadder;
            _isRefreshingLadderControls = false;
            LadderStatusText.Text = _activeLadders.Count == 0
                ? "No active ladders right now."
                : _activeLadders.Count == 1
                    ? "Active ladder:"
                    : "Active ladders:";
        }
        catch (Exception ex)
        {
            _activeLadders = [];
            _ladderExtensionChoices = [];
            _ladderLoadError = "Ladder status is temporarily unavailable.";
            LadderStatusText.Text = _ladderLoadError;
            LadderExtensionPolicyStatusText.Text = _ladderLoadError;
            ActiveLadderComboBox.ItemsSource = null;
            AllowedLadderPluginsItemsControl.ItemsSource = null;
            AllowedLadderPatchesItemsControl.ItemsSource = null;
            UnapprovedLadderExtensionsBanner.IsVisible = false;
            UnapprovedLadderExtensionsSummaryBanner.IsVisible = false;
            LaunchDiagnostics.LogException("Failed to fetch active ladders", ex);
        }
        finally
        {
            _ladderStatusLoaded = true;
            _isRefreshingLadders = false;
            RefreshInstallDirectoryState();
        }

        if (_ladderLoadError is null)
        {
            await RefreshLadderExtensionPolicyAsync();
        }
    }

    private string GetLadderUnavailableMessage(string? loaderUnavailableReason = null)
    {
        if (!_ladderStatusLoaded)
        {
            return "Checking the Reimagined API for an active ladder.";
        }

        if (!string.IsNullOrWhiteSpace(loaderUnavailableReason))
        {
            return loaderUnavailableReason;
        }

        return _ladderLoadError ?? "No active Reimagined ladder is available right now.";
    }

    private string GetLadderPolicyUnavailableMessage()
    {
        return _missingRequiredLadderExtensions.Count > 0
            ? $"Required ladder extension(s) are missing or do not match the approved hash: {string.Join(", ", _missingRequiredLadderExtensions)}."
            : "Installed D2RLoader extensions have not been verified against the ladder policy.";
    }

    private void EnsureSelectedLadder()
    {
        var selectedLadder = SelectedLadder;
        MainWindow.Settings.CurrentProfile.SelectedLadderId = selectedLadder?.Id;
    }

    private async Task RefreshLadderExtensionPolicyAsync()
    {
        _ladderPolicyVerified = false;
        _missingRequiredLadderExtensions = [];
        var ladder = SelectedLadder;
        if (ladder is null)
        {
            _ladderExtensionChoices = [];
            _missingRequiredLadderExtensions = [];
            AllowedLadderPluginsItemsControl.ItemsSource = null;
            AllowedLadderPatchesItemsControl.ItemsSource = null;
            LadderExtensionPolicyStatusText.Text = "No active ladder extension policy is available.";
            UnapprovedLadderExtensionsBanner.IsVisible = false;
            UnapprovedLadderExtensionsSummaryBanner.IsVisible = false;
            return;
        }

        LadderExtensionPolicyStatusText.Text = "Checking installed D2RLoader extensions...";
        try
        {
            var approvals = MapApprovals(ladder);
            var preview = await D2RLoaderService.PreviewLadderPolicyAsync(
                MainWindow.Settings.CurrentProfile.InstallDirectory,
                approvals);
            var selectedIds = GetEffectiveSelectedLadderExtensionIds(ladder);
            _ladderExtensionChoices = preview.ApprovedExtensions
                .Select(state =>
                {
                    var isProvidedByLauncher = !state.IsInstalled
                                               && state.Approval.IsRequired
                                               && state.Approval.Kind == D2RLoaderExtensionKind.Plugin
                                               && (ServerSavesConfigService.CanSupplyApprovedPlugin(
                                                       state.Approval.FileName,
                                                       state.Approval.Sha256)
                                                   || ChatRelayConfigService.CanSupplyApprovedPlugin(
                                                       state.Approval.FileName,
                                                       state.Approval.Sha256));
                    return new LadderExtensionChoice
                    {
                        ApprovalId = state.Approval.Id,
                        Name = state.Approval.Name,
                        FileName = state.Approval.FileName,
                        Kind = state.Approval.Kind,
                        IsRequired = state.Approval.IsRequired,
                        IsInstalled = state.IsInstalled,
                        IsProvidedByLauncher = isProvidedByLauncher,
                        IsLadderDisabled = state.IsLadderDisabled,
                        IsSelected = (state.IsInstalled || isProvidedByLauncher)
                                     && selectedIds.Contains(state.Approval.Id)
                    };
                })
                .ToArray();
            AllowedLadderPluginsItemsControl.ItemsSource = _ladderExtensionChoices
                .Where(choice => choice.Kind == D2RLoaderExtensionKind.Plugin)
                .ToArray();
            AllowedLadderPatchesItemsControl.ItemsSource = _ladderExtensionChoices
                .Where(choice => choice.Kind == D2RLoaderExtensionKind.Patch)
                .ToArray();

            _missingRequiredLadderExtensions = _ladderExtensionChoices
                .Where(choice => choice.IsRequired && !choice.IsAvailable)
                .Select(choice => choice.Name)
                .ToArray();
            var requiredCount = approvals.Count(approval => approval.IsRequired);
            var optionalCount = approvals.Count - requiredCount;
            LadderExtensionPolicyStatusText.Text = approvals.Count == 0
                ? "No D2RLoader plugins or patches are approved for this ladder. All installed extensions will be disabled."
                : _missingRequiredLadderExtensions.Count > 0
                    ? $"Ladder launch is blocked. Required extension(s) missing or hash-mismatched: {string.Join(", ", _missingRequiredLadderExtensions)}."
                    : $"{requiredCount} required and {optionalCount} optional extension(s). Required extensions are enabled automatically; select any optional extensions you want to use.";
            var hasUnapprovedExtensions = preview.UnapprovedExtensions.Count > 0;
            UnapprovedLadderExtensionsBanner.IsVisible = hasUnapprovedExtensions;
            var policyWarnings = new List<string>();
            if (_missingRequiredLadderExtensions.Count > 0)
            {
                policyWarnings.Add(
                    $"Install the exact required extension file(s) before launching: {string.Join(", ", _missingRequiredLadderExtensions)}.");
            }

            if (hasUnapprovedExtensions)
            {
                var extensionLabel = preview.UnapprovedExtensions.Count == 1 ? "extension is" : "extensions are";
                policyWarnings.Add(
                    $"{preview.UnapprovedExtensions.Count} installed {extensionLabel} not approved for this ladder and will be disabled for launch. Expand the policy details to review them.");
            }
            UnapprovedLadderExtensionsSummaryBanner.IsVisible = policyWarnings.Count > 0;
            UnapprovedLadderExtensionsSummaryText.Text = string.Join(" ", policyWarnings);
            var pendingUnapproved = preview.UnapprovedExtensions
                .Where(extension => !extension.IsLadderDisabled)
                .Select(extension => extension.FileName)
                .ToArray();
            var alreadyDisabled = preview.UnapprovedExtensions
                .Where(extension => extension.IsLadderDisabled)
                .Select(extension => extension.FileName)
                .ToArray();
            UnapprovedLadderExtensionsText.Text = string.Join(
                " ",
                new[]
                {
                    pendingUnapproved.Length == 0
                        ? null
                        : "Not approved and will be moved before launch: " + string.Join(", ", pendingUnapproved) + ".",
                    alreadyDisabled.Length == 0
                        ? null
                        : "Already ladder-disabled: " + string.Join(", ", alreadyDisabled) + "."
                }.OfType<string>());
            _ladderPolicyVerified = _missingRequiredLadderExtensions.Count == 0;
        }
        catch (Exception ex)
        {
            _ladderExtensionChoices = [];
            _missingRequiredLadderExtensions = [];
            AllowedLadderPluginsItemsControl.ItemsSource = null;
            AllowedLadderPatchesItemsControl.ItemsSource = null;
            LadderExtensionPolicyStatusText.Text = "Could not verify installed D2RLoader extensions.";
            UnapprovedLadderExtensionsBanner.IsVisible = true;
            UnapprovedLadderExtensionsSummaryBanner.IsVisible = true;
            UnapprovedLadderExtensionsSummaryText.Text =
                "Extension verification failed. Ladder launch remains blocked until the installed extensions can be checked.";
            UnapprovedLadderExtensionsText.Text =
                "Ladder launch will remain blocked until installed extensions can be verified.";
            LaunchDiagnostics.LogException("Failed to preview ladder extension policy", ex);
        }

        RefreshInstallDirectoryState();
    }

    private async void OnActiveLadderSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_isRefreshingLadderControls || ActiveLadderComboBox.SelectedItem is not LadderResponse ladder)
        {
            return;
        }

        MainWindow.Settings.CurrentProfile.SelectedLadderId = ladder.Id;
        await SettingsManager.SaveAsync(MainWindow.Settings);
        await RefreshLadderExtensionPolicyAsync();
        RefreshInstallDirectoryState();
    }

    private async void OnLadderExtensionSelectionChanged(object? sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { DataContext: LadderExtensionChoice choice } checkBox
            || !choice.IsInstalled
            || SelectedLadder is not { } ladder)
        {
            return;
        }

        if (choice.IsRequired)
        {
            checkBox.IsChecked = true;
            return;
        }

        choice.IsSelected = checkBox.IsChecked ?? false;
        var selectedIds = GetSelectedLadderExtensionIds(ladder.Id);
        if (choice.IsSelected)
        {
            selectedIds.Add(choice.ApprovalId);
        }
        else
        {
            selectedIds.Remove(choice.ApprovalId);
        }

        MainWindow.Settings.CurrentProfile.SelectedLadderExtensions ??= [];
        MainWindow.Settings.CurrentProfile.SelectedLadderExtensions[ladder.Id.ToString("N")] = selectedIds.ToList();
        await SettingsManager.SaveAsync(MainWindow.Settings);
    }

    private static IReadOnlyList<LadderExtensionApproval> MapApprovals(LadderResponse ladder)
    {
        return (ladder.AllowedExtensions ?? [])
            .Select(extension => new LadderExtensionApproval(
                extension.Id,
                extension.Name,
                extension.FileName,
                extension.Sha256,
                extension.Kind,
                extension.IsRequired))
            .ToArray();
    }

    private static HashSet<Guid> GetEffectiveSelectedLadderExtensionIds(LadderResponse ladder)
    {
        var selectedIds = GetSelectedLadderExtensionIds(ladder.Id);
        selectedIds.UnionWith((ladder.AllowedExtensions ?? [])
            .Where(extension => extension.IsRequired)
            .Select(extension => extension.Id));
        return selectedIds;
    }

    private static HashSet<Guid> GetSelectedLadderExtensionIds(Guid ladderId)
    {
        var selections = MainWindow.Settings.CurrentProfile.SelectedLadderExtensions ??= [];
        return selections.TryGetValue(ladderId.ToString("N"), out var selected)
            ? selected.ToHashSet()
            : [];
    }

    private void OnOpenLoaderFolderClick(object? sender, RoutedEventArgs e)
    {
        OpenFolder(_loaderInventory?.GlobalRoot);
    }

    private void OnOpenModLoaderFolderClick(object? sender, RoutedEventArgs e)
    {
        var path = _loaderInventory?.ModRoot;
        OpenFolder(Directory.Exists(path) ? path : Path.GetDirectoryName(path));
    }

    private static void OpenFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return;
        }

        try
        {
            var startInfo = OperatingSystem.IsWindows()
                ? new ProcessStartInfo("explorer.exe") { UseShellExecute = false }
                : OperatingSystem.IsMacOS()
                    ? new ProcessStartInfo("open") { UseShellExecute = false }
                    : new ProcessStartInfo("xdg-open") { UseShellExecute = false };

            startInfo.ArgumentList.Add(path);
            Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            Notifications.SendNotification($"Could not open folder: {ex.Message}", "Warning");
        }
    }

    private async void OnInstallationTypeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (InstallationTypeComboBox == null) return;
        
        var selectedIndex = InstallationTypeComboBox.SelectedIndex;
        if (selectedIndex < 0) return;

        var newType = (InstallationType)selectedIndex;
        if (MainWindow.Settings.CurrentProfile.Type == newType) return;

        // Switch profile
        MainWindow.Settings.SelectedProfileIndex = selectedIndex;
        if (MainWindow.Settings.CurrentProfile.Type == InstallationType.D2RMM)
        {
            MainWindow.Settings.CurrentProfile.LaunchExperience = LaunchExperience.Offline;
        }
        BackupService.ApplyDefaultSettings();
        await SettingsManager.SaveAsync(MainWindow.Settings);

        if (TopLevel.GetTopLevel(this) is MainWindow mw)
        {
            mw.RefreshLocalModState();
            await mw.RefreshUpdateStateAsync();
        }
        
        RefreshInstallDirectoryState();
    }

    private async void OnLocateSteamClick(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is Window window)
        {
            var files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = OperatingSystem.IsLinux() ? "Locate Steam or Flatpak executable" : "Locate Steam.exe",
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("Steam Executable")
                    {
                        Patterns = OperatingSystem.IsLinux() ? ["steam", "flatpak"] : ["Steam.exe"]
                    }
                ]
            });

            if (files.Count > 0)
            {
                var selectedPath = files[0].Path.LocalPath;

                MainWindow.Settings.CurrentProfile.SteamDirectory = selectedPath;
                await SettingsManager.SaveAsync(MainWindow.Settings);
                RefreshInstallDirectoryState();
            }
        }
    }


    private void SetLaunchStatus(string status, bool isVisible = true)
    {
        LaunchStatusText.Text = status;
        LaunchStatusPanel.IsVisible = isVisible;
    }

    private async void OnRunClick(object? sender, RoutedEventArgs e)
    {
        await StartGameAsync();
    }

    // Runs the same prepare-backup-launch sequence as the Start Game button.
    // Exposed so callers outside this view (e.g. the navigation Play shortcut)
    // can trigger a launch directly.
    public async Task StartGameAsync()
    {
        LaunchDiagnostics.ResetSession();
        LaunchDiagnostics.Log("Launch/Install button clicked.");

        if (_isLaunching)
        {
            LaunchDiagnostics.Log("Action ignored because an action is already in progress.");
            return;
        }

        var profile = MainWindow.Settings.CurrentProfile;

        if (profile.LaunchExperience == LaunchExperience.Ladder
            && !await EnsureLadderAuthenticationAsync())
        {
            return;
        }

        if (profile.LaunchExperience == LaunchExperience.Ladder
            && (!_ladderStatusLoaded || !_ladderPolicyVerified))
        {
            await RefreshLadderStateAsync();
        }

        if (!profile.IsInstallDirectoryValidated)
        {
            LaunchDiagnostics.Log("Action blocked because install directory is not validated.");
            Notifications.SendNotification(
                "Install directory not validated",
                "Choose the Diablo II: Resurrected folder that contains D2R.exe.");
            return;
        }

        if (!MainWindow.IsLocalModDetected)
        {
            LaunchDiagnostics.Log("Action blocked because the local mod was not detected.");
            Notifications.SendNotification(
                "D2R Reimagined mod not detected",
                "Install the mod in the selected directory before launching/installing.");

            if (MainWindow.Instance is { } mainWindow)
            {
                await mainWindow.PromptInstallForMissingModAsync();
            }

            return;
        }

        if (profile.LaunchExperience is LaunchExperience.Online or LaunchExperience.Ladder
            && !D2RLoaderService.IsInstalled(profile.InstallDirectory))
        {
            await PromptInstallD2RLoaderAsync(profile);
        }

        if (profile.LaunchExperience is LaunchExperience.Online or LaunchExperience.Ladder
            && !D2RLoaderService.CanUseOnlineExperience(profile, out var loaderUnavailableReason))
        {
            LaunchDiagnostics.Log($"D2RLoader launch blocked: {loaderUnavailableReason}");
            Notifications.SendNotification(loaderUnavailableReason ?? "D2RLoader is unavailable.", "Warning");
            return;
        }

        if (profile.LaunchExperience == LaunchExperience.Ladder && !HasActiveLadder)
        {
            var unavailableMessage = GetLadderUnavailableMessage();
            LaunchDiagnostics.Log($"Ladder launch blocked: {unavailableMessage}");
            Notifications.SendNotification(unavailableMessage, "Warning");
            return;
        }

        if (profile.LaunchExperience == LaunchExperience.Ladder && !_ladderPolicyVerified)
        {
            var message = GetLadderPolicyUnavailableMessage();
            LaunchDiagnostics.Log($"Ladder launch blocked: {message}");
            Notifications.SendNotification(message, "Warning");
            return;
        }


        _isLaunching = true;
        StartGameButton.IsEnabled = false;
        var actionName = profile.Type == InstallationType.D2RMM ? "Installation" : "Launch";
        SetLaunchStatus($"Preparing {actionName.ToLower()}...");
        var progress = new Progress<string>(status => SetLaunchStatus(status));

        try
        {
            if (!await PrepareD2RLoaderExtensionsAsync(profile, progress))
            {
                SetLaunchStatus($"{actionName} preparation failed.");
                return;
            }

            LaunchDiagnostics.Log("Starting mod tweak preparation.");
            var ladderDisplay = profile.LaunchExperience == LaunchExperience.Ladder && SelectedLadder is { } activeLadder
                ? new LadderDisplayInfo(activeLadder.Name, activeLadder.StartDateUtc, activeLadder.EndDateUtc)
                : null;
            var prepared = await Task.Run(() => ModTweaksService.PrepareForLaunchAsync(progress, ladderDisplay));
            if (!prepared)
            {
                LaunchDiagnostics.Log("Mod tweak preparation returned false.");
                SetLaunchStatus($"{actionName} preparation failed.");
                Notifications.SendNotification($"{actionName} preparation failed. See previous warning for details.", "Warning");
                return;
            }

            if (!await PrepareServerSavesAsync(profile))
            {
                SetLaunchStatus($"{actionName} preparation failed.");
                return;
            }

            if (profile.AutomaticBackupsEnabled)
            {
                LaunchDiagnostics.Log("Starting backup.");
                SetLaunchStatus("Creating backup...");
                var backupCreated = await Task.Run(BackupService.CreateLaunchBackupAsync);
                if (!backupCreated)
                {
                    LaunchDiagnostics.Log("Backup returned false.");
                    Notifications.SendNotification("Backup failed. Continuing.", "Warning");
                }
            }

            try
            {
                if (profile.Type == InstallationType.D2RMM)
                {
                    LaunchDiagnostics.Log("D2RMM: Tweaks applied. Installation complete.");
                    SetLaunchStatus("D2RMM mod tweaks applied.");
                }
                else
                {
                    LaunchDiagnostics.Log("Calling GameLauncherService.LaunchGame.");
                    SetLaunchStatus(profile.LaunchExperience is LaunchExperience.Online or LaunchExperience.Ladder
                        ? "Starting D2RLoader..."
                        : "Starting Diablo II: Resurrected...");
                    var gameProcess = LauncherService.LaunchGame();
                    if (gameProcess == null)
                    {
                        LaunchDiagnostics.Log("GameLauncherService.LaunchGame did not start a process.");
                        SetLaunchStatus("Launch failed.");
                        return;
                    }
                    LaunchDiagnostics.Log("GameLauncherService.LaunchGame returned without throwing.");
                    SetLaunchStatus($"{actionName} command sent.");

                    if (MainWindow.Settings.MinimizeToTray && MainWindow.Instance is { } mainWindow)
                    {
                        string? expectedExePath = null;
                        if (profile.Type == InstallationType.Steam
                            || profile.LaunchExperience is LaunchExperience.Online or LaunchExperience.Ladder)
                        {
                            expectedExePath = LauncherService.GetExpectedGameExecutablePath();
                        }

                        _ = mainWindow.MinimizeToTrayAndWaitForExitAsync(gameProcess, expectedExePath);
                    }
                }
            }
            catch (Exception ex)
            {
                LaunchDiagnostics.LogException($"{actionName} failed", ex);
                SetLaunchStatus($"{actionName} failed.");
                Notifications.SendNotification($"{actionName} failed: {ex.Message}", "Warning");
                return;
            }

            Notifications.SendNotification(profile.Type == InstallationType.D2RMM ? "Installed Mod to D2RMM" : "Launched Game", "Success");
        }
        finally
        {
            LaunchDiagnostics.Log($"{actionName} flow completed.");
            _isLaunching = false;
            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                await Task.Delay(1500);
                if (!_isLaunching)
                {
                    LaunchStatusPanel.IsVisible = false;
                }
            });
            RefreshInstallDirectoryState();
        }
    }

    private async Task<bool> EnsureLadderAuthenticationAsync()
    {
        try
        {
            if (await _launcherAuthenticationService.GetAccessTokenAsync() is not null)
            {
                return true;
            }
        }
        catch (Exception exception)
        {
            LaunchDiagnostics.LogException("Could not validate the Reimagined API session", exception);
            Notifications.SendNotification(
                "The launcher could not validate your D2R Reimagined login. Check your connection and try again.",
                "Ladder login required");
            return false;
        }

        LaunchDiagnostics.Log("Ladder launch blocked because no D2R Reimagined account is signed in.");
        Notifications.SendNotification(
            "Sign in with your D2R Reimagined website account before playing on the ladder.",
            "Ladder login required");

        if (MainWindow.Instance is not { } mainWindow
            || !await mainWindow.SignInToReimaginedAsync())
        {
            return false;
        }

        return await _launcherAuthenticationService.GetAccessTokenAsync() is not null;
    }

    /// <summary>
    /// Points the chat-relay plugin at the API for a ladder launch.
    /// </summary>
    /// <remarks>
    /// Never blocks the launch. Chat not reaching Discord costs a convenience;
    /// refusing to start the game over it would cost the session. Every failure
    /// here leaves the plugin disabled and says so in the log.
    ///
    /// The token decides whose name Discord shows against the messages, because
    /// the API reads the sender from the token rather than the payload - so this
    /// writes the same signed-in account's token that server-saves just used.
    /// </remarks>
    private async Task ConfigureChatRelayAsync(InstallationProfile profile, string accessToken)
    {
        try
        {
            // Not approved for this ladder, or the player left it unchecked.
            // A legitimate outcome: the launch proceeds without the bridge.
            if (!ChatRelayConfigService.IsPluginInstalled(profile.InstallDirectory))
            {
                await ChatRelayConfigService.DisableAsync(profile.InstallDirectory);
                LaunchDiagnostics.Log("chat-relay is not approved for this ladder; this launch has no Discord chat bridge.");
                return;
            }

            var settings = new ChatRelayLaunchSettings(
                _apiHttpClient.BaseAddress.GetLeftPart(UriPartial.Authority),
                accessToken);
            if (!await ChatRelayConfigService.EnableAsync(profile.InstallDirectory, settings))
            {
                LaunchDiagnostics.Log("chat-relay: the plugin configuration could not be written; the Discord chat bridge is off for this launch.");
                return;
            }

            LaunchDiagnostics.Log("chat-relay configured for the Discord chat bridge.");
        }
        catch (Exception exception)
        {
            LaunchDiagnostics.Log($"chat-relay: configuration failed ({exception.Message}); the Discord chat bridge is off for this launch.");
        }
    }

    /// <summary>
    /// Points the server-saves plugin at the API for a ladder launch, and turns
    /// it off for every other launch. A stale token left enabled would keep
    /// hiding the player's own characters outside the ladder.
    /// </summary>
    private async Task<bool> PrepareServerSavesAsync(InstallationProfile profile)
    {
        var isLadderLaunch = profile.Type != InstallationType.D2RMM
                             && profile.LaunchExperience == LaunchExperience.Ladder;

        try
        {
            if (!isLadderLaunch)
            {
                await ServerSavesConfigService.DisableAsync(profile.InstallDirectory);
                await ChatRelayConfigService.DisableAsync(profile.InstallDirectory);
                await LadderSaveDirectoryService.RestoreAsync(profile.InstallDirectory);
                return true;
            }

            // PrepareD2RLoaderExtensionsAsync already installed the plugin and ran
            // the ladder's extension policy against it. If it is not here now,
            // that ladder has not approved it (or the player left it unchecked)
            // - a legitimate outcome, not a failure, so the launch proceeds on
            // local characters exactly as it would for a ladder with no server
            // saves at all.
            //
            // The save folder is only redirected once server saves are certain to
            // run. Redirecting without the plugin would drop the player into an
            // empty folder where any character they made would never sync.
            if (!ServerSavesConfigService.IsPluginInstalled(profile.InstallDirectory))
            {
                LaunchDiagnostics.Log("server-saves plugin is not approved for this ladder; this launch uses local characters.");
                await LadderSaveDirectoryService.RestoreAsync(profile.InstallDirectory);
                return true;
            }

            var ladder = SelectedLadder;
            if (ladder is null)
            {
                const string message = "The selected active ladder could not be resolved.";
                LaunchDiagnostics.Log($"Ladder launch blocked: {message}");
                Notifications.SendNotification(message, "Warning");
                return false;
            }

            var accessToken = await _launcherAuthenticationService.GetAccessTokenAsync();
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                const string message = "Sign in to your Reimagined account to play on the ladder - your characters are stored on the server.";
                LaunchDiagnostics.Log($"Ladder launch blocked: {message}");
                Notifications.SendNotification(message, "Warning");
                await LadderSaveDirectoryService.RestoreAsync(profile.InstallDirectory);
                return false;
            }

            var settings = new ServerSavesLaunchSettings(
                _apiHttpClient.BaseAddress.GetLeftPart(UriPartial.Authority),
                accessToken,
                ladder.Id);
            if (!await ServerSavesConfigService.EnableAsync(profile.InstallDirectory, settings))
            {
                const string message = "The server-saves plugin configuration could not be written.";
                LaunchDiagnostics.Log($"Ladder launch blocked: {message}");
                Notifications.SendNotification(message, "Warning");
                await LadderSaveDirectoryService.RestoreAsync(profile.InstallDirectory);
                return false;
            }

            // Only now, with the plugin present and configured, is it safe to send
            // D2R at this ladder's own save folder.
            var ladderDirectory = await LadderSaveDirectoryService.PrepareAsync(
                profile.InstallDirectory,
                ladder.Id,
                ladder.Name);
            if (ladderDirectory is null)
            {
                const string message = "The ladder save folder could not be prepared.";
                LaunchDiagnostics.Log($"Ladder launch blocked: {message}");
                Notifications.SendNotification(message, "Warning");
                await LadderSaveDirectoryService.RestoreAsync(profile.InstallDirectory);
                return false;
            }

            LaunchDiagnostics.Log($"server-saves configured for ladder {ladder.Id} at \"{ladderDirectory}\".");

            await ConfigureChatRelayAsync(profile, accessToken);
            return true;
        }
        catch (Exception exception)
        {
            LaunchDiagnostics.LogException("server-saves preparation failed", exception);
            Notifications.SendNotification($"Server save preparation failed: {exception.Message}", "Warning");

            // Never leave the mod pointed at a ladder folder after a failure -
            // the player would find their own characters missing.
            try
            {
                await LadderSaveDirectoryService.RestoreAsync(profile.InstallDirectory);
            }
            catch (Exception restoreException)
            {
                LaunchDiagnostics.LogException("ladder save folder restore failed", restoreException);
            }

            return !isLadderLaunch;
        }
    }

    private async Task<bool> PrepareD2RLoaderExtensionsAsync(
        InstallationProfile profile,
        IProgress<string> progress)
    {
        try
        {
            if (profile.Type == InstallationType.D2RMM)
            {
                return true;
            }

            if (profile.LaunchExperience != LaunchExperience.Ladder)
            {
                var restoredCount = await Task.Run(() =>
                    D2RLoaderService.RestoreLadderDisabledExtensions(profile.InstallDirectory));
                if (restoredCount > 0)
                {
                    LaunchDiagnostics.Log($"Restored {restoredCount} extension(s) disabled by the previous ladder launch.");
                }

                return true;
            }

            var ladder = SelectedLadder;
            if (ladder is null)
            {
                Notifications.SendNotification("The selected active ladder could not be resolved.", "Warning");
                return false;
            }

            // Install our own bundled plugin before the ladder policy below
            // decides what stays and what moves to ladder-disabled - a player
            // should never have to source this DLL themselves. This can only
            // fail on a real I/O problem; whether the ladder actually approves
            // the plugin is decided separately, by the policy step that follows.
            if (!await ServerSavesConfigService.EnsureInstalledAsync(profile.InstallDirectory))
            {
                const string message = "The Server Saves plugin could not be installed.";
                LaunchDiagnostics.Log($"Ladder launch blocked: {message}");
                Notifications.SendNotification(message, "Warning");
                return false;
            }

            // Chat relay is a convenience, not a requirement: if it cannot be
            // installed the ladder still plays correctly, only without the
            // Discord bridge. Blocking the launch over it would be the wrong
            // trade, so this only records why.
            if (!await ChatRelayConfigService.EnsureInstalledAsync(profile.InstallDirectory))
            {
                LaunchDiagnostics.Log("chat-relay: the plugin could not be installed; this launch has no Discord chat bridge.");
            }

            progress.Report("Enforcing ladder D2RLoader extension policy...");
            var approvals = MapApprovals(ladder);
            var selectedIds = GetEffectiveSelectedLadderExtensionIds(ladder);
            var result = await Task.Run(() => D2RLoaderService.ApplyLadderPolicyAsync(
                profile.InstallDirectory,
                approvals,
                selectedIds));

            LaunchDiagnostics.Log(
                $"Ladder extension policy: {result.UnapprovedMoved.Count} unapproved and "
                + $"{result.UnselectedMoved.Count} unselected extension(s) moved; "
                + $"{result.RestoredCount} previously disabled extension(s) restored for re-evaluation.");
            if (result.UnapprovedMoved.Count > 0)
            {
                Notifications.SendNotification(
                    $"Moved {result.UnapprovedMoved.Count} unapproved D2RLoader extension(s) to their ladder-disabled folders.",
                    "Warning");
            }

            return true;
        }
        catch (Exception ex)
        {
            LaunchDiagnostics.LogException("Failed to enforce D2RLoader ladder policy", ex);
            Notifications.SendNotification($"Could not enforce the ladder extension policy: {ex.Message}", "Warning");
            return false;
        }
    }

    private async void OnInstallDirectoryClick(object? sender, RoutedEventArgs e)
    {
        LauncherService.CancelDetection();
        if (TopLevel.GetTopLevel(this) is Window window)
        {
            var profile = MainWindow.Settings.CurrentProfile;
            if (profile.Type == InstallationType.D2RMM)
            {
                var folders = await window.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
                {
                    Title = "Select D2RMM mods folder",
                    AllowMultiple = false
                });

                if (folders.Count > 0)
                {
                    profile.InstallDirectory = folders[0].Path.LocalPath;
                }
                else return;
            }
            else
            {
                var folders = await window.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
                {
                    Title = "Select Install Folder",
                    AllowMultiple = false
                });

                if (folders.Count <= 0) return;

                var path = folders[0].Path.LocalPath;
                profile.InstallDirectory = InstallDirectoryValidator.NormalizeInstallDirectory(path);
            }

            profile.IsInstallDirectoryValidated = profile.Type == InstallationType.D2RMM
                ? InstallDirectoryValidator.IsValidD2RmmModsDirectory(profile.InstallDirectory)
                : profile.Type == InstallationType.Steam
                    ? InstallDirectoryValidator.IsValidSteamInstallDirectory(profile.InstallDirectory)
                    : InstallDirectoryValidator.IsValidInstallDirectory(profile.InstallDirectory);

            // Auto-detect type if it's currently BattleNet (default)
            if (profile.Type == InstallationType.BattleNet && profile.IsInstallDirectoryValidated)
            {
                var detectedType = LauncherService.DetectInstallationType(profile.InstallDirectory!);
                if (detectedType != InstallationType.BattleNet)
                {
                    profile.Type = detectedType;
                }
            }

            // Auto-detect Steam path if it's Steam
            if (profile.Type == InstallationType.Steam)
            {
                var detectedSteam = LauncherService.FindSteamExecutable(profile.InstallDirectory);
                if (!string.IsNullOrEmpty(detectedSteam))
                {
                    profile.SteamDirectory = detectedSteam;
                }
            }

            await SettingsManager.SaveAsync(MainWindow.Settings);
            BackupService.UpdateSchedule();
            if (TopLevel.GetTopLevel(this) is MainWindow mw)
            {
                mw.RefreshLocalModState();
                await mw.RefreshUpdateStateAsync();
            }
            RefreshInstallDirectoryState();

            if (!profile.IsInstallDirectoryValidated)
            {
                if (profile.Type == InstallationType.D2RMM)
                {
                    Notifications.SendNotification(
                        "Invalid D2RMM location",
                        InstallDirectoryValidator.GetD2RmmValidationMessage(profile.InstallDirectory));
                }
                else if (profile.Type == InstallationType.Steam
                         && InstallDirectoryValidator.IsValidInstallDirectory(profile.InstallDirectory))
                {
                    Notifications.SendNotification(
                        "Invalid Steam path",
                        "The selected directory does not contain steam_*.dll. Please select a valid Steam installation or switch to Battle.Net.");
                }
                else
                {
                    Notifications.SendNotification(
                        "D2R install not found",
                        "Select the Diablo II: Resurrected folder that contains D2R.exe.");
                }
                return;
            }

            if (profile.Type != InstallationType.D2RMM && !MainWindow.IsLocalModDetected)
            {
                Notifications.SendNotification(
                    "D2R Reimagined mod not detected",
                    "Install the mod in this directory before launching.");

                if (TopLevel.GetTopLevel(this) is MainWindow mainWindow)
                {
                    await mainWindow.PromptInstallForMissingModAsync();
                }

                return;
            }

            Notifications.SendNotification(profile.Type == InstallationType.D2RMM ? "D2RMM mods folder selected" : "Install directory validated", "Success");
        }
    }
}
