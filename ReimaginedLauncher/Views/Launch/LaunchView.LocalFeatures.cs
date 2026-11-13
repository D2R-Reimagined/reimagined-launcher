using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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

public partial class LaunchView
{
    private static readonly SolidColorBrush IdleFilterBrush = new(Color.Parse("#BCA77D"));
    private static readonly SolidColorBrush SyncingFilterBrush = new(Color.Parse("#E0BD72"));
    private static readonly SolidColorBrush SyncedFilterBrush = new(Color.Parse("#5CB85C"));

    private readonly AccountLootFilterService _accountFilters = Program.ServiceProvider.GetRequiredService<AccountLootFilterService>();
    private readonly DispatcherTimer _filterRefreshTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private CancellationTokenSource _filterContextCancellation = new();
    private string? _filterContext;
    private string? _cachedActiveFilterRoot;
    private (string ContextKey, bool ActiveRootExists, bool FullySynced, int SyncedCount, string? SyncedNamesTooltip)? _cachedFilterDiskState;
    private int _filterTickCounter;
    private long _filterEpoch;
    private bool _filterBusy;
    private string? _filterInstallContext;
    private bool _pluginBusy;
    private bool _extensionToggleInProgress;
    private bool _filterListLoading;
    private bool _filterListRequested;

    private void InitializeLocalFeatures()
    {
        _filterRefreshTimer.Tick += (_, _) =>
        {
            var pollDisk = (++_filterTickCounter % 5) == 0;
            if (pollDisk)
            {
                RefreshLocalFeatures();
            }
            else
            {
                UpdateLocalFeatureButtons(pollDisk: false);
            }
            ApplyHeroPluginFilter();
        };
        AttachedToVisualTree += (_, _) =>
        {
            _filterTickCounter = 0;
            _filterRefreshTimer.Start();
            RefreshLocalFeatures();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _filterRefreshTimer.Stop();
            _filterContextCancellation.Cancel();
            _filterContext = null;
            _cachedFilterDiskState = null;
            _filterEpoch++;
        };
    }

    private string ContextKey(bool refreshActiveRoot = false)
    {
        var profile = MainWindow.Settings.CurrentProfile;
        if (refreshActiveRoot)
        {
            AccountLootFilterService.TryResolveRoot(profile, out _cachedActiveFilterRoot);
        }
        return $"{_launcherAuthenticationService.CurrentUser?.Id}|{MainWindow.Settings.SelectedProfileIndex}|{profile.Type}|{profile.InstallDirectory}|{profile.LaunchExperience}|{profile.SelectedLadderId}|{_cachedActiveFilterRoot}";
    }

    private void RefreshLocalFeatures()
    {
        UpdateFilterSectionLayout();
        var profile = MainWindow.Settings.CurrentProfile;
        LootFilterPanel.IsVisible = profile.Type != InstallationType.D2RMM && profile.LaunchExperience != LaunchExperience.Offline;
        _cachedFilterDiskState = null;
        var key = ContextKey(refreshActiveRoot: true);
        if (_filterContext != key)
        {
            _accountFilters.UseAccount(_launcherAuthenticationService.CurrentUser?.Id);
            _filterContextCancellation.Cancel();
            _filterContextCancellation.Dispose();
            _filterContextCancellation = new CancellationTokenSource();
            _filterContext = key;
            _filterEpoch++;
            _filterListLoading = false;
            _filterBusy = false;
            _filterInstallContext = null;
            _filterListRequested = false;
            SetFilterMessage(null);
        }
        if (!_filterListRequested && _launcherAuthenticationService.IsSignedIn
            && LootFilterPanel.IsVisible && IsAttachedToVisualTree())
        {
            _filterListRequested = true;
            _ = SyncAccountFiltersAsync(false);
        }
        LootFilterScopeText.Text = "Select your active filter in-game under Options → Gameplay → Loot Filter Profile.";
        UpdateLocalFeatureButtons(pollDisk: true);
    }

    private void SetFilterMessage(string? message)
    {
        LootFilterStatusText.Text = message;
        LootFilterStatusText.IsVisible = !string.IsNullOrWhiteSpace(message);
    }

    private void UpdateFilterIndicator(bool pollDisk)
    {
        var user = _launcherAuthenticationService.CurrentUser;
        var contextKey = ContextKey();
        var syncing = (_filterBusy && _filterInstallContext == contextKey) || _filterListLoading;

        bool activeRootExists;
        bool fullySynced;
        int syncedCount;
        string? syncedNamesTooltip;

        if (!pollDisk && _cachedFilterDiskState is { } cached && cached.ContextKey == contextKey)
        {
            activeRootExists = cached.ActiveRootExists;
            fullySynced = cached.FullySynced;
            syncedCount = cached.SyncedCount;
            syncedNamesTooltip = cached.SyncedNamesTooltip;
        }
        else
        {
            activeRootExists = false;
            fullySynced = false;
            syncedCount = 0;
            syncedNamesTooltip = null;

            try
            {
                var profile = MainWindow.Settings.CurrentProfile;
                if (AccountLootFilterService.TryResolveRoot(profile, out var activeRoot) && activeRoot is not null)
                {
                    activeRootExists = true;
                    MainWindow.Settings.SyncedLootFilters.TryGetValue(activeRoot, out var activeSaved);
                    if (activeSaved is { Count: > 0 })
                    {
                        syncedCount = activeSaved.Count;
                        syncedNamesTooltip = "Synced: " + string.Join(", ", activeSaved.Select(entry => entry.Name));
                    }

                    if (user is not null && _accountFilters.TryGetCachedList(user.Id, out var remote))
                    {
                        var roots = AccountLootFilterService.ResolveInstalledSyncRoots(profile);
                        fullySynced = roots.Count > 0 && roots.All(root =>
                        {
                            MainWindow.Settings.SyncedLootFilters.TryGetValue(root, out var saved);
                            var hadLegacy = MainWindow.Settings.InstalledLootFilters.ContainsKey(root);
                            return AccountLootFilterService.IsFullySynced(root, remote, saved, hadLegacy);
                        });
                        if (fullySynced && activeSaved is null)
                        {
                            syncedCount = remote.Count;
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException or ArgumentException) { }

            _cachedFilterDiskState = (contextKey, activeRootExists, fullySynced, syncedCount, syncedNamesTooltip);
        }

        string indicator;
        SolidColorBrush brush;
        string summary;
        string tooltip;

        if (user is null)
        {
            indicator = "○";
            brush = IdleFilterBrush;
            summary = "Sign in to sync your saved filters.";
            tooltip = summary;
        }
        else if (syncing)
        {
            indicator = "↻";
            brush = SyncingFilterBrush;
            summary = "Syncing saved filters...";
            tooltip = summary;
        }
        else if (!activeRootExists)
        {
            indicator = "○";
            brush = IdleFilterBrush;
            summary = "Install this mode's mod package to sync filters.";
            tooltip = summary;
        }
        else if (fullySynced)
        {
            indicator = "✓";
            brush = SyncedFilterBrush;
            summary = syncedCount == 0
                ? "No saved filters on your account."
                : $"Filters are synced ({syncedCount} {(syncedCount == 1 ? "filter" : "filters")}).";
            tooltip = syncedCount > 0 && syncedNamesTooltip is not null
                ? syncedNamesTooltip
                : summary;
        }
        else
        {
            indicator = "○";
            brush = IdleFilterBrush;
            summary = "Filters not synced · Click ↻ to sync.";
            tooltip = summary;
        }

        FilterInstallIndicator.Text = indicator;
        FilterInstallIndicator.Foreground = brush;
        FilterSyncSummaryText.Text = summary;
        ToolTip.SetTip(FilterInstallIndicator, tooltip);
        ToolTip.SetTip(FilterSyncSummaryText, tooltip);
        Avalonia.Automation.AutomationProperties.SetName(FilterInstallIndicator, summary);
    }

    private bool IsAttachedToVisualTree() => TopLevel.GetTopLevel(this) is not null;

    private void UpdateLocalFeatureButtons(bool pollDisk = true)
    {
        UpdateFilterIndicator(pollDisk);
        var profile = MainWindow.Settings.CurrentProfile;
        var user = _launcherAuthenticationService.CurrentUser;
        var wait = user is null ? TimeSpan.Zero : _accountFilters.Wait(user.Id);
        RefreshAccountFiltersButton.Content = wait > TimeSpan.Zero ? $"{Math.Ceiling(wait.TotalSeconds)}s" : "↻";
        RefreshAccountFiltersButton.IsEnabled = user is not null && wait <= TimeSpan.Zero && !_filterListLoading && !_filterBusy;
        FilterSignInButton.IsVisible = user is null;
        var busy = _filterBusy || _pluginBusy || MainWindow.IsInstallInProgress || _isLaunching || _isRunningLadderAction;
        var extensionControlsBusy = busy && !_extensionToggleInProgress;
        var canEdit = !extensionControlsBusy && profile.Type != InstallationType.D2RMM && profile.LaunchExperience == LaunchExperience.Online;
        var canToggle = canEdit && !SteamGameHandoff.IsLaunchPendingOrRunning && !MainWindow.IsGameRunning();
        LadderExtensionsGrid.IsEnabled = !busy;
        SetExtensionControlAvailability(canEdit, canToggle);
        LoaderPluginsItemsControl.IsHitTestVisible = !_extensionToggleInProgress;
        LoaderPatchesItemsControl.IsHitTestVisible = !_extensionToggleInProgress;
        InstallLocalPluginButton.IsHitTestVisible = !_extensionToggleInProgress;
        ExtensionToggleHelpText.Text = profile.LaunchExperience == LaunchExperience.Ladder
            ? "Required Ladder entries are package-managed. Select approved optional entries below. Manual installation is unavailable, and Ladder rule configs are locked."
            : "Checked means this file is enabled for the next D2RLoader launch; loader scope settings still apply. Close D2R and D2RLoader to change it. Global changes affect other mods, and another copy may remain enabled.";
        InstallLocalPluginButton.IsEnabled = !extensionControlsBusy && profile.Type != InstallationType.D2RMM
            && profile.LaunchExperience != LaunchExperience.Ladder && !string.IsNullOrWhiteSpace(profile.InstallDirectory)
            && profile.IsInstallDirectoryValidated && MainWindow.IsLocalModDetected;
        LocalPluginPolicyText.Text = profile.LaunchExperience == LaunchExperience.Ladder
            ? "Manual plugin installation is unavailable for Ladder. Changing package contents can require a fresh package download."
            : "Manual installations target the normal Reimagined mod only (Offline / D2RLoader); global plugins are unchanged.";
        ToolTip.SetTip(InstallLocalPluginButton, profile.LaunchExperience == LaunchExperience.Ladder
            ? "Manual installation is unavailable for Ladder. Changing package contents can require a fresh package download."
            : "Install a DLL or ZIP into this mod only. Existing configs are preserved by default.");
        var available = AccountLootFilterService.IsFilterPluginAvailable(_loaderInventory, profile.LaunchExperience);
        var legacyUnhoarderOnly = AccountLootFilterService.HasLegacySingleFileFilterPluginOnly(_loaderInventory, profile.LaunchExperience);
        LootFilterAvailabilityText.IsVisible = !available && user is not null;
        LootFilterAvailabilityText.Text = legacyUnhoarderOnly
            ? "Synced filter profiles require Pyre-LootFilter (UnHoarder only reads a single config/filter.json file)."
            : "Enable Pyre-LootFilter in Installed Plugins to use synced loot filters in-game.";
    }

    private async Task SyncAccountFiltersAsync(bool force)
    {
        if (_launcherAuthenticationService.CurrentUser is not { } user || _filterListLoading || _filterBusy) return;
        var context = ContextKey(refreshActiveRoot: true);
        _filterContext = context;
        var epoch = _filterEpoch;
        var token = _filterContextCancellation.Token;
        _filterInstallContext = context;
        _filterListLoading = true;
        _filterBusy = true;
        var entered = false;
        var ownsInstall = false;
        var anyCommitted = false;
        SetFilterMessage(null);
        UpdateLocalFeatureButtons(pollDisk: false);
        try
        {
            var filters = await _accountFilters.ListAsync(user.Id, force, token);
            if (epoch != _filterEpoch || ContextKey() != context) return;

            var profile = MainWindow.Settings.CurrentProfile;
            AccountLootFilterService.TryResolveRoot(profile, out var activeRoot);
            var roots = AccountLootFilterService.ResolveInstalledSyncRoots(profile);
            if (roots.Count == 0)
            {
                if (force)
                {
                    SetFilterMessage("Install the mod package first to sync filters on disk.");
                }
                return;
            }

            var pathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            var primaryRoot = activeRoot ?? roots[0];
            var previousByRoot = new Dictionary<string, IReadOnlyList<SyncedLootFilterEntry>>(pathComparer);
            var allSynced = true;
            foreach (var root in roots)
            {
                if (MainWindow.Settings.SyncedLootFilters.TryGetValue(root, out var prev))
                    previousByRoot[root] = prev;
                var hadLegacy = MainWindow.Settings.InstalledLootFilters.ContainsKey(root);
                if (!AccountLootFilterService.IsFullySynced(root, filters, prev, hadLegacy))
                    allSynced = false;
            }

            if (allSynced)
            {
                SetFilterMessage(null);
                if (force)
                {
                    var message = activeRoot is null
                        ? "Installed mod filters are up to date."
                        : $"Filters are up to date ({filters.Count} {(filters.Count == 1 ? "filter" : "filters")}).";
                    Notifications.SendNotification(message, "Success");
                }
                return;
            }

            void Check()
            {
                token.ThrowIfCancellationRequested();
                if (epoch != _filterEpoch || context != ContextKey())
                    throw new OperationCanceledException("The selected account or play mode changed.");
                if (_isLaunching || _isRunningLadderAction)
                    throw new InvalidOperationException("Wait for the launch or package operation to finish.");
            }

            Check();
            var prepared = await _accountFilters.PrepareMultiRootSyncAsync(roots, filters, previousByRoot, Check, token);
            Check();

            entered = await LocalPluginInstallService.InstallGate.WaitAsync(TimeSpan.FromSeconds(10), token);
            if (!entered || MainWindow.IsInstallInProgress)
                throw new InvalidOperationException("Another installation is in progress.");
            MainWindow.IsInstallInProgress = true;
            ownsInstall = true;

            LootFilterSyncResult? primaryResult = null;
            var warnings = new List<string>();
            if (!string.IsNullOrWhiteSpace(prepared.Warning))
                warnings.Add(prepared.Warning);

            foreach (var root in roots)
            {
                previousByRoot.TryGetValue(root, out var prev);
                var hadLegacy = MainWindow.Settings.InstalledLootFilters.ContainsKey(root);
                var result = await Task.Run(() => AccountLootFilterService.CommitSync(root, prepared, Check, prev, hadLegacy), token);
                MainWindow.Settings.SyncedLootFilters[root] = [.. result.Synced];
                MainWindow.Settings.InstalledLootFilters.Remove(root);
                anyCommitted = true;
                if (!string.IsNullOrWhiteSpace(result.BackupWarning) && !warnings.Contains(result.BackupWarning, StringComparer.Ordinal))
                    warnings.Add(result.BackupWarning);
                if (pathComparer.Equals(root, primaryRoot))
                    primaryResult = result;
            }

            primaryResult ??= new LootFilterSyncResult(prepared.TargetEntries, 0, 0, null);
            var warning = warnings.Count > 0 ? string.Join(" ", warnings) : null;
            try
            {
                await SettingsManager.SaveAsync(MainWindow.Settings);
                anyCommitted = false;
            }
            catch (Exception ex)
            {
                warning = "Filters synced, but sync state could not be saved: " + ex.Message;
            }

            if (context == ContextKey())
            {
                SetFilterMessage(warning);
            }

            if (force || primaryResult.UpdatedCount > 0 || primaryResult.DeletedCount > 0 || warning is not null)
            {
                var summary = warning
                    ?? (primaryResult.Synced.Count == 0
                        ? "Removed deleted account filters."
                        : $"Synced {primaryResult.Synced.Count} account {(primaryResult.Synced.Count == 1 ? "filter" : "filters")}.");
                Notifications.SendNotification(summary, warning is null ? "Success" : "Warning");
            }
        }
        catch (LootFilterRateLimitException ex)
        {
            if (epoch == _filterEpoch && context == ContextKey())
            {
                _accountFilters.Block(user.Id, ex.RetryAt);
                SetFilterMessage(ex.Message);
                if (force) Notifications.SendNotification(ex.Message, "Warning");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            LaunchDiagnostics.LogException("Account loot filter sync", ex);
            if (epoch == _filterEpoch && context == ContextKey())
            {
                SetFilterMessage("Could not sync filters. " + ex.Message + " Click ↻ to retry.");
                if (force) Notifications.SendNotification("Filters were not synced: " + ex.Message, "Error");
            }
        }
        finally
        {
            if (anyCommitted)
            {
                try { await SettingsManager.SaveAsync(MainWindow.Settings); }
                catch (Exception saveEx) { LaunchDiagnostics.LogException("Saving partial loot filter sync state", saveEx); }
            }
            if (ownsInstall) MainWindow.IsInstallInProgress = false;
            if (entered) LocalPluginInstallService.InstallGate.Release();
            if (epoch == _filterEpoch)
            {
                _filterListLoading = false;
                _filterBusy = false;
                _filterInstallContext = null;
                _cachedFilterDiskState = null;
                UpdateLocalFeatureButtons(pollDisk: true);
            }
        }
    }

    private async void OnRefreshAccountFiltersClick(object? sender, RoutedEventArgs e) => await SyncAccountFiltersAsync(true);
    private async void OnFilterSignInClick(object? sender, RoutedEventArgs e)
    {
        if (MainWindow.Instance is { } window) await window.SignInToReimaginedAsync();
        RefreshLocalFeatures();
    }
    private void OnFilterWebsiteClick(object? sender, RoutedEventArgs e) => OpenFilterWebsite("loot-filter");
    private void OnFilterWebsiteV2Click(object? sender, RoutedEventArgs e) => OpenFilterWebsite("loot-filterv2");

    private static void OpenFilterWebsite(string relativePath)
    {
        try { Process.Start(new ProcessStartInfo(new Uri(LauncherAuthenticationService.ResolveWebsiteBaseAddress(), relativePath).AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex) { Notifications.SendNotification("Could not open the filter website: " + ex.Message, "Warning"); }
    }

    private static void RequireGameClosed()
    {
        if (MainWindow.IsGameRunning() || SteamGameHandoff.IsLaunchPendingOrRunning)
            throw new InvalidOperationException("Close D2R and D2RLoader before installing plugins.");
    }

    private async void OnInstallLocalPluginClick(object? sender, RoutedEventArgs e)
    {
        if (_pluginBusy || !InstallLocalPluginButton.IsEnabled || TopLevel.GetTopLevel(this) is not Window owner) return;
        _pluginBusy = true;
        var context = ContextKey();
        var experience = MainWindow.Settings.CurrentProfile.LaunchExperience;
        try
        {
            LocalPluginInstallService.RefuseLadder(experience);
            var picked = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Install Plugin/Zip", AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("D2RLoader plugin or ZIP") { Patterns = ["*.dll", "*.zip"] }]
            });
            if (picked.Count == 0 || context != ContextKey()) return;
            var path = picked[0].TryGetLocalPath() ?? throw new IOException("Choose a local DLL or ZIP file.");
            var plan = await Task.Run(() => LocalPluginInstallService.Preview(path, MainWindow.Settings.CurrentProfile));
            if (context != ContextKey()) return;
            var replaceConfigs = await ShowLocalPluginPreviewAsync(owner, plan, Path.GetFileName(path));
            if (replaceConfigs is null) return;
            void Check()
            {
                LocalPluginInstallService.RefuseLadder(MainWindow.Settings.CurrentProfile.LaunchExperience);
                if (context != ContextKey()) throw new InvalidOperationException("The selected installation or play mode changed. Open the package again.");
                if (_isLaunching || _isRunningLadderAction) throw new InvalidOperationException("Wait for the launch or package operation to finish.");
                RequireGameClosed();
            }
            Check();
            if (!await LocalPluginInstallService.InstallGate.WaitAsync(0)) throw new InvalidOperationException("Another installation is in progress.");
            var ownsInstall = false;
            try
            {
                if (MainWindow.IsInstallInProgress) throw new InvalidOperationException("Another installation is in progress.");
                MainWindow.IsInstallInProgress = true;
                ownsInstall = true;
                RefreshInstallDirectoryState();
                var backup = await Task.Run(() => LocalPluginInstallService.Install(plan, replaceConfigs.Value, Check));
                Notifications.SendNotification("Plugin installed for the normal Reimagined mod. Replaced files are backed up in " + backup, "Success");
            }
            finally { if (ownsInstall) MainWindow.IsInstallInProgress = false; LocalPluginInstallService.InstallGate.Release(); }
        }
        catch (Exception ex) { Notifications.SendNotification("Plugin installation stopped: " + ex.Message, "Error"); }
        finally { _pluginBusy = false; RefreshInstallDirectoryState(); }
    }

    private async void OnEditExtensionConfigClick(object? sender, RoutedEventArgs e)
    {
        var profile = MainWindow.Settings.CurrentProfile;
        var entry = (sender as Button)?.DataContext switch
        {
            D2RLoaderExtensionInfo extension => extension,
            LadderExtensionChoice choice => D2RLoaderConfigService.CreateLadderExtensionInfo(
                profile, choice.Name, choice.FileName, choice.Kind, choice.IsLadderDisabled),
            _ => null
        };
        if (entry is null || TopLevel.GetTopLevel(this) is not { } top) return;
        var activeBundle = SelectedLadder?.ActiveBundle;
        var context = ContextKey();
        try
        {
            void Check()
            {
                if (context != ContextKey() || MainWindow.IsInstallInProgress || _pluginBusy || _isLaunching || _isRunningLadderAction)
                    throw new InvalidOperationException("The play mode or installation state changed. Refresh and retry.");
                if (profile.LaunchExperience is not (LaunchExperience.Online or LaunchExperience.Ladder))
                    throw new InvalidOperationException("Config editing is available in Mod + D2RLoader and Ladder modes.");
            }
            Check();
            var target = D2RLoaderConfigService.Resolve(profile, entry, activeBundle);
            var path = target.File ?? throw new FileNotFoundException("No unique matching config exists. Refresh after the plugin creates its config.");
            Check();
            path = D2RLoaderConfigService.ValidateSelection(profile, target, path, activeBundle);
            if (top is not Window owner) return;
            void ValidateSave()
            {
                Check();
                var current = D2RLoaderConfigService.Resolve(profile, entry, SelectedLadder?.ActiveBundle);
                if (current.File != path) throw new IOException("The matching config changed. Cancel and reopen the editor.");
                D2RLoaderConfigService.ValidateSelection(profile, current, path, SelectedLadder?.ActiveBundle);
            }
            var editor = new ConfigEditorWindow(entry.Name, new ConfigEditSession(path), ValidateSave);
            await editor.ShowDialog(owner);
        }
        catch (Exception ex) { Notifications.SendNotification("Could not open config: " + ex.Message, "Warning"); }
    }

    private void SetExtensionControlAvailability(bool canEdit, bool canToggle)
    {
        LoaderPluginsItemsControl.IsEnabled = canEdit;
        LoaderPatchesItemsControl.IsEnabled = canEdit;
        if (_loaderInventory is { } inventory)
            foreach (var entry in inventory.Extensions)
                entry.ToggleControlsEnabled = canToggle;
    }

    private async void OnExtensionToggleClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { DataContext: D2RLoaderExtensionInfo extension } checkBox) return;
        if (_pluginBusy)
        {
            checkBox.IsChecked = extension.IsEnabled;
            return;
        }
        var enabled = checkBox.IsChecked == true;
        if (enabled == extension.IsEnabled) return;
        var context = ContextKey();
        var profile = MainWindow.Settings.CurrentProfile;
        var entered = false; var ownsInstall = false;
        var inventory = _loaderInventory;
        _pluginBusy = true;
        _extensionToggleInProgress = true;
        try
        {
            void Check()
            {
                LocalPluginInstallService.RefuseLadder(profile.LaunchExperience);
                if (context != ContextKey() || _isLaunching || _isRunningLadderAction)
                    throw new InvalidOperationException("The play mode or launch state changed. Refresh and retry.");
                if (profile.LaunchExperience != LaunchExperience.Online)
                    throw new InvalidOperationException("Extension toggling is available in Mod + D2RLoader mode.");
                RequireGameClosed();
            }
            Check();
            entered = await LocalPluginInstallService.InstallGate.WaitAsync(0);
            if (!entered || MainWindow.IsInstallInProgress) throw new InvalidOperationException("Another installation is in progress.");
            MainWindow.IsInstallInProgress = true; ownsInstall = true;
            UpdateLocalFeatureButtons();
            var destination = await Task.Run(() => D2RLoaderExtensionToggleService.SetEnabled(profile, extension, enabled, Check));
            extension.ApplyToggleState(destination, enabled);
            if (!enabled)
            {
                var pluginId = "d2rloader:" + Path.GetFileNameWithoutExtension(extension.FileName).ToLowerInvariant();
                await PluginAssetBackupService.RestoreForPluginAsync(pluginId);
            }
            extension.CanEditConfig = D2RLoaderConfigService.HasExistingConfig(profile, extension);
            if (context == ContextKey() && ReferenceEquals(inventory, _loaderInventory) && inventory is not null)
                RefreshLoaderExtensionCounts(inventory);
            Notifications.SendNotification($"{(enabled ? "Enabled" : "Disabled")} {extension.FileName} ({extension.ScopeLabel}).", "Success");
        }
        catch (Exception ex) { Notifications.SendNotification("Extension was not changed: " + ex.Message, "Warning"); }
        finally
        {
            if (ownsInstall) MainWindow.IsInstallInProgress = false;
            if (entered) LocalPluginInstallService.InstallGate.Release();
            _pluginBusy = false;
            _extensionToggleInProgress = false;
            checkBox.IsChecked = extension.IsEnabled;
            UpdateLocalFeatureButtons();
        }
    }

    private static Task<bool?> ShowLocalPluginPreviewAsync(Window owner, LocalPluginPlan plan, string package)
    {
        var dialog = new Window { Title = "Install Plugin/Zip", Width = 780, Height = 670, MinWidth = 480, MinHeight = 400,
            WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new StackPanel { Spacing = 14, Margin = new Thickness(22) };
        TextBlock Text(string value, string? cls = null)
        {
            var text = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap };
            if (cls is not null) text.Classes.Add(cls);
            return text;
        }
        panel.Children.Add(Text("Install Plugin/Zip", "section-title"));
        panel.Children.Add(Text("Applies to the normal Reimagined mod (Offline / D2RLoader). Ladder is unchanged.", "muted"));
        panel.Children.Add(Text("Package: " + package));
        panel.Children.Add(Text("Destination: " + plan.Root, "muted"));
        var rows = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions
            {
                new ColumnDefinition(GridLength.Auto) { MinWidth = 140 },
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto) { MinWidth = 120 },
            },
            ColumnSpacing = 28,
            RowSpacing = 14,
        };
        var configurations = new CheckBox { Content = "Replace existing configuration files", IsChecked = false };
        void DrawRows()
        {
            rows.Children.Clear();
            rows.RowDefinitions.Clear();
            rows.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var headingFile = Text("File"); var headingDestination = Text("Destination"); var headingAction = Text("Action");
            headingFile.FontWeight = headingDestination.FontWeight = headingAction.FontWeight = FontWeight.SemiBold;
            Grid.SetColumn(headingDestination, 1); Grid.SetColumn(headingAction, 2);
            rows.Children.Add(headingFile); rows.Children.Add(headingDestination); rows.Children.Add(headingAction);
            for (var i = 0; i < plan.Files.Count; i++)
            {
                var file = plan.Files[i];
                rows.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                var name = Text(Path.GetFileName(file.RelativePath));
                var target = Text(Path.GetDirectoryName(file.RelativePath) ?? "", "muted");
                var action = Text(file.Exists ? file.IsConfig && configurations.IsChecked != true ? "Keep existing" : "Replace + backup" : "Add");
                name.TextWrapping = action.TextWrapping = TextWrapping.NoWrap;
                Grid.SetRow(name, i + 1); Grid.SetRow(target, i + 1); Grid.SetRow(action, i + 1);
                Grid.SetColumn(target, 1); Grid.SetColumn(action, 2);
                rows.Children.Add(name); rows.Children.Add(target); rows.Children.Add(action);
            }
        }
        DrawRows(); configurations.IsCheckedChanged += (_, _) => DrawRows();
        panel.Children.Add(new Border { Classes = { "panel" }, Padding = new Thickness(12), Child = rows });
        panel.Children.Add(configurations);
        panel.Children.Add(Text("Existing configs are preserved by default. Every replaced file is backed up.", "muted"));
        if (plan.Skipped.Count != 0) panel.Children.Add(Text("Not installed: " + string.Join(", ", plan.Skipped.Take(40)) + (plan.Skipped.Count > 40 ? " ..." : ""), "muted"));
        var status = Text("Close D2R and D2RLoader before installing.", "muted"); panel.Children.Add(status);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Cancel" }; cancel.Click += (_, _) => dialog.Close((bool?)null);
        var install = new Button { Content = "Install", Classes = { "accent" } }; install.Click += (_, _) => dialog.Close((bool?)(configurations.IsChecked == true));
        buttons.Children.Add(cancel); buttons.Children.Add(install);
        var layout = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        layout.Children.Add(new ScrollViewer { Content = panel, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
        Grid.SetRow(buttons, 1); buttons.Margin = new Thickness(22, 10); layout.Children.Add(buttons); dialog.Content = layout;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        void Check() { try { RequireGameClosed(); install.IsEnabled = true; status.Text = "Ready. Replaced files will be backed up before installation."; } catch (Exception ex) { install.IsEnabled = false; status.Text = ex.Message; } }
        timer.Tick += (_, _) => Check(); dialog.Opened += (_, _) => { Check(); cancel.Focus(); timer.Start(); }; dialog.Closed += (_, _) => timer.Stop();
        return dialog.ShowDialog<bool?>(owner);
    }
}
