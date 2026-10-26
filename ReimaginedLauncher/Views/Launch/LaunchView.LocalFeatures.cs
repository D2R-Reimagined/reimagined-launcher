using System;
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
using ReimaginedLauncher.Utilities;

namespace ReimaginedLauncher.Views.Launch;

public partial class LaunchView
{
    private readonly AccountLootFilterService _accountFilters = Program.ServiceProvider.GetRequiredService<AccountLootFilterService>();
    private readonly DispatcherTimer _filterRefreshTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private CancellationTokenSource _filterContextCancellation = new();
    private string? _filterContext;
    private long _filterEpoch;
    private bool _filterBusy;
    private string? _filterInstallContext;
    private bool _pluginBusy;
    private bool _extensionToggleInProgress;
    private bool _filterListLoading;
    private bool _filterListRequested;

    private void InitializeLocalFeatures()
    {
        _filterRefreshTimer.Tick += (_, _) => { UpdateLocalFeatureButtons(); ApplyHeroPluginFilter(); };
        AttachedToVisualTree += (_, _) => { _filterRefreshTimer.Start(); RefreshLocalFeatures(); };
        DetachedFromVisualTree += (_, _) =>
        {
            _filterRefreshTimer.Stop();
            _filterContextCancellation.Cancel();
            _filterContext = null;
            _filterEpoch++;
        };
    }

    private string ContextKey()
    {
        var profile = MainWindow.Settings.CurrentProfile;
        return $"{_launcherAuthenticationService.CurrentUser?.Id}|{MainWindow.Settings.SelectedProfileIndex}|{profile.Type}|{profile.InstallDirectory}|{profile.LaunchExperience}|{profile.SelectedLadderId}";
    }

    private void RefreshLocalFeatures()
    {
        UpdateFilterSectionLayout();
        var profile = MainWindow.Settings.CurrentProfile;
        LootFilterPanel.IsVisible = profile.Type != InstallationType.D2RMM && profile.LaunchExperience != LaunchExperience.Offline;
        var key = ContextKey();
        if (_filterContext != key)
        {
            var account = _launcherAuthenticationService.CurrentUser?.Id.ToString();
            _accountFilters.UseAccount(_launcherAuthenticationService.CurrentUser?.Id);
            _filterContextCancellation.Cancel();
            _filterContextCancellation.Dispose();
            _filterContextCancellation = new CancellationTokenSource();
            _filterContext = key;
            _filterEpoch++;
            _filterListLoading = false;
            _filterListRequested = false;
            AccountFiltersComboBox.ItemsSource = null;
            AccountFiltersComboBox.SelectedItem = null;
            SetFilterMessage(account is null ? "Sign in to access your account-saved filters." : null);

        }
        if (!_filterListRequested && _launcherAuthenticationService.IsSignedIn
            && LootFilterPanel.IsVisible && IsAttachedToVisualTree())
        {
            _filterListRequested = true;
            _ = LoadAccountFiltersAsync(false);
        }
        LootFilterRequirementText.IsVisible = profile.LaunchExperience != LaunchExperience.Ladder;
        LootFilterScopeText.Text = profile.LaunchExperience == LaunchExperience.Ladder
            ? "Applies to Ladder only."
            : "Applies only to Mod+D2RLoader.";
        UpdateLocalFeatureButtons();
    }

    private void SetFilterMessage(string? message)
    {
        LootFilterStatusText.Text = message;
        LootFilterStatusText.IsVisible = !string.IsNullOrWhiteSpace(message);
    }

    private void UpdateFilterIndicator()
    {
        InstalledLootFilter? installed = null;
        var exists = false;
        try
        {
            var root = LocalPluginInstallService.ResolveRoot(MainWindow.Settings.CurrentProfile);
            MainWindow.Settings.InstalledLootFilters.TryGetValue(root, out installed);
            exists = File.Exists(LocalPluginInstallService.SafePath(root, "config/filter.json"));
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException) { }
        AccountFiltersComboBox.PlaceholderText = installed?.Name ?? "Select a filter";
        var selected = AccountFiltersComboBox.SelectedItem as AccountLootFilter;
        var pending = selected is not null && selected.Id != installed?.Id;
        var applying = _filterBusy && _filterInstallContext == ContextKey();
        FilterInstallIndicator.Text = applying ? "↻" : exists && !pending ? "✓" : "○";
        FilterInstallIndicator.Foreground = new SolidColorBrush(applying ? Color.Parse("#E0BD72")
            : exists && !pending ? Color.Parse("#5CB85C") : Color.Parse("#BCA77D"));
        var status = applying ? "Applying filter..." : pending ? "Selected filter has not been applied."
            : exists ? "Filter installed." : "No filter installed for this mode.";
        ToolTip.SetTip(FilterInstallIndicator, status);
        Avalonia.Automation.AutomationProperties.SetName(FilterInstallIndicator, status);
    }

    private bool IsAttachedToVisualTree() => TopLevel.GetTopLevel(this) is not null;

    private void UpdateLocalFeatureButtons()
    {
        UpdateFilterIndicator();
        var profile = MainWindow.Settings.CurrentProfile;
        var user = _launcherAuthenticationService.CurrentUser;
        var wait = user is null ? TimeSpan.Zero : _accountFilters.Wait(user.Id);
        RefreshAccountFiltersButton.Content = wait > TimeSpan.Zero ? $"{Math.Ceiling(wait.TotalSeconds)}s" : "↻";
        RefreshAccountFiltersButton.IsEnabled = user is not null && wait <= TimeSpan.Zero && !_filterListLoading && !_filterBusy;
        FilterSignInButton.IsVisible = user is null;
        AccountFiltersComboBox.IsEnabled = user is not null && !_filterBusy;
        var busy = _filterBusy || _pluginBusy || MainWindow.IsInstallInProgress || _isLaunching || _isRunningLadderAction;
        var extensionControlsBusy = busy && !_extensionToggleInProgress;
        var canEdit = !extensionControlsBusy && profile.Type != InstallationType.D2RMM && profile.LaunchExperience == LaunchExperience.Online;
        var canToggle = canEdit;
        try { RequireGameClosed(); } catch (Exception) { canToggle = false; }
        LadderExtensionsGrid.IsEnabled = !busy;
        SetExtensionControlAvailability(canEdit, canToggle);
        LoaderPluginsItemsControl.IsHitTestVisible = !_extensionToggleInProgress;
        LoaderPatchesItemsControl.IsHitTestVisible = !_extensionToggleInProgress;
        InstallLocalPluginButton.IsHitTestVisible = !_extensionToggleInProgress;
        ExtensionToggleHelpText.Text = profile.LaunchExperience == LaunchExperience.Ladder
            ? "Required Ladder entries are package-managed. Select approved optional entries below. Manual installation and config editing are unavailable."
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
        var available = _loaderInventory is { IsInstalled: true, AllowModExtensions: true } inventory
            && inventory.Plugins.Any(plugin => plugin.IsEnabled && plugin.FileName.Equals("unhoarder.dll", StringComparison.OrdinalIgnoreCase)
                && (plugin.Scope == D2RLoaderExtensionScope.Reimagined
                    || profile.LaunchExperience != LaunchExperience.Ladder && inventory.AllowGlobalExtensions));
        ApplyAccountFilterButton.IsEnabled = !busy && user is not null && _accountFilters.ServerWait(user.Id) <= TimeSpan.Zero
            && AccountFiltersComboBox.SelectedItem is AccountLootFilter && available;
        ToolTip.SetTip(ApplyAccountFilterButton, available ? "Download, back up, and install the selected filter."
            : "UnHoarder and mod extensions must be installed and enabled for this mode. This action does not install or enable plugins.");
        LootFilterAvailabilityText.IsVisible = !available && user is not null;
        LootFilterAvailabilityText.Text = "UnHoarder must be installed and enabled for this mode before applying a filter. You can still browse your account filters.";
    }

    private async Task LoadAccountFiltersAsync(bool force)
    {
        if (_launcherAuthenticationService.CurrentUser is not { } user || _filterListLoading) return;
        var epoch = _filterEpoch;
        var token = _filterContextCancellation.Token;
        var selected = (AccountFiltersComboBox.SelectedItem as AccountLootFilter)?.Id;
        _filterListLoading = true;
        UpdateLocalFeatureButtons();
        try
        {
            var filters = await _accountFilters.ListAsync(user.Id, force, token);
            if (epoch != _filterEpoch || ContextKey() != _filterContext) return;
            AccountFiltersComboBox.ItemsSource = filters;
            if (selected is null)
            {
                try
                {
                    MainWindow.Settings.InstalledLootFilters.TryGetValue(LocalPluginInstallService.ResolveRoot(MainWindow.Settings.CurrentProfile), out var installed);
                    selected = installed?.Id;
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException) { }
            }
            AccountFiltersComboBox.SelectedItem = filters.FirstOrDefault(filter => filter.Id == selected);
            SetFilterMessage(filters.Count == 0
                ? "No saved filters. Create or save one on the website."
                : null);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (epoch == _filterEpoch) SetFilterMessage("Could not load filters. " + ex.Message + " Refresh to retry.");
        }
        finally
        {
            if (epoch == _filterEpoch) { _filterListLoading = false; UpdateLocalFeatureButtons(); }
        }
    }

    private async void OnRefreshAccountFiltersClick(object? sender, RoutedEventArgs e) => await LoadAccountFiltersAsync(true);
    private void OnAccountFilterSelectionChanged(object? sender, SelectionChangedEventArgs e) => UpdateLocalFeatureButtons();
    private async void OnFilterSignInClick(object? sender, RoutedEventArgs e)
    {
        if (MainWindow.Instance is { } window) await window.SignInToReimaginedAsync();
        RefreshLocalFeatures();
    }
    private void OnFilterWebsiteClick(object? sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(new Uri(LauncherAuthenticationService.ResolveWebsiteBaseAddress(), "loot-filter").AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex) { Notifications.SendNotification("Could not open the filter website: " + ex.Message, "Warning"); }
    }

    private async void OnApplyAccountFilterClick(object? sender, RoutedEventArgs e)
    {
        if (_filterBusy || !ApplyAccountFilterButton.IsEnabled || AccountFiltersComboBox.SelectedItem is not AccountLootFilter selected
            || _launcherAuthenticationService.CurrentUser is not { } user) return;
        var context = ContextKey();
        var epoch = _filterEpoch;
        var token = _filterContextCancellation.Token;
        _filterInstallContext = context;
        _filterBusy = true;
        var entered = false;
        var ownsInstall = false;
        try
        {
            var root = LocalPluginInstallService.ResolveRoot(MainWindow.Settings.CurrentProfile);
            SetFilterMessage(null);
            UpdateLocalFeatureButtons();
            var details = await _apiHttpClient.GetLootFilterAsync(selected.Id, token);
            void Check()
            {
                token.ThrowIfCancellationRequested();
                if (epoch != _filterEpoch || context != ContextKey()) throw new OperationCanceledException("The selected account or play mode changed.");
                if (_isLaunching || _isRunningLadderAction) throw new InvalidOperationException("Wait for the launch or package operation to finish.");
                }
            Check();
            entered = await LocalPluginInstallService.InstallGate.WaitAsync(0, token);
            if (!entered || MainWindow.IsInstallInProgress) throw new InvalidOperationException("Another installation is in progress.");
            MainWindow.IsInstallInProgress = true;
            ownsInstall = true;
            await Task.Run(() => AccountLootFilterService.Install(root, details.Document, Check), token);
            MainWindow.Settings.InstalledLootFilters[root] = new InstalledLootFilter(details.Id, details.Name);
            string? warning = AccountLootFilterService.TrimBackups(root);
            try { await SettingsManager.SaveAsync(MainWindow.Settings); }
            catch (Exception ex) { warning = "Filter installed, but the last-installed name could not be saved: " + ex.Message; }
            Notifications.SendNotification(warning ?? "Filter installed: " + details.Name, warning is null ? "Success" : "Warning");
            if (context == ContextKey()) SetFilterMessage(warning);
        }
        catch (LootFilterRateLimitException ex)
        {
            if (epoch == _filterEpoch && context == ContextKey())
            {
                _accountFilters.Block(user.Id, ex.RetryAt);
                Notifications.SendNotification(ex.Message, "Warning");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (epoch == _filterEpoch && context == ContextKey())
                Notifications.SendNotification("Filter was not installed: " + ex.Message, "Error");
        }
        finally
        {
            if (ownsInstall) MainWindow.IsInstallInProgress = false;
            if (entered) LocalPluginInstallService.InstallGate.Release();
            _filterBusy = false;
            _filterInstallContext = null;
            RefreshLocalFeatures();
        }
    }

    private static void RequireGameClosed()
    {
        if (MainWindow.IsGameRunning() || SteamGameHandoff.IsLaunchPendingOrRunning)
            throw new InvalidOperationException("Close D2R and D2RLoader before installing plugins.");
        foreach (var name in OperatingSystem.IsWindows() ? new[] { "D2RLoader" } : new[] { "D2RLoader", "D2RLoader.exe" })
        {
            var processes = Process.GetProcessesByName(name);
            try { if (processes.Length != 0) throw new InvalidOperationException("Close D2RLoader before installing plugins."); }
            finally { foreach (var process in processes) process.Dispose(); }
        }
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
        if (sender is not Button { DataContext: D2RLoaderExtensionInfo entry } || TopLevel.GetTopLevel(this) is not { } top) return;
        var profile = MainWindow.Settings.CurrentProfile;
        var context = ContextKey();
        try
        {
            void Check()
            {
                LocalPluginInstallService.RefuseLadder(profile.LaunchExperience);
                if (context != ContextKey() || MainWindow.IsInstallInProgress || _pluginBusy || _isLaunching || _isRunningLadderAction)
                    throw new InvalidOperationException("The play mode or installation state changed. Refresh and retry.");
                if (profile.LaunchExperience != LaunchExperience.Online)
                    throw new InvalidOperationException("Config editing is available in Mod + D2RLoader mode.");
            }
            Check();
            var target = D2RLoaderConfigService.Resolve(profile, entry);
            var path = target.File ?? throw new FileNotFoundException("No unique matching config exists. Refresh after the plugin creates its config.");
            Check();
            path = D2RLoaderConfigService.ValidateSelection(profile, target, path);
            if (top is not Window owner) return;
            void ValidateSave()
            {
                Check();
                var current = D2RLoaderConfigService.Resolve(profile, entry);
                if (current.File != path) throw new IOException("The matching config changed. Cancel and reopen the editor.");
                D2RLoaderConfigService.ValidateSelection(profile, current, path);
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
                if (context != ContextKey() || _isLaunching || _isRunningLadderAction)
                    throw new InvalidOperationException("The play mode or launch state changed. Refresh and retry.");
                LocalPluginInstallService.RefuseLadder(profile.LaunchExperience);
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
