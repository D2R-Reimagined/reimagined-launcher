using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Microsoft.Extensions.DependencyInjection;
using ReimaginedLauncher.HttpClients;
using ReimaginedLauncher.HttpClients.Models;
using AvaloniaEdit.TextMate;
using AvaloniaEdit;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using ReimaginedLauncher.Utilities;
using TextMateSharp.Grammars;
using ReimaginedLauncher;

namespace ReimaginedLauncher.Views.Plugins;

public partial class PluginsView : UserControl
{
    private readonly RegistryOptions _registryOptions = new(ThemeName.DarkPlus);
    private TextMate.Installation? _textMateInstallation;
    private string? _selectedPluginId;
    private string? _selectedRelativePath;
    private string _originalEditorContent = string.Empty;
    private bool _isUpdatingEditorState;
    private bool _isLoading;
    private bool _isDryRunning;

    public PluginsView()
    {
        InitializeComponent();
        ConfigureEditor();
        SupportedTargetsTextBlock.Text = PluginsService.GetSupportedTargetsSummary();
        SetEditorState(fileSelected: false, isDirty: false);
        SetLoadingState(false);
    }

    public async Task RefreshPluginsStateAsync()
    {
        SetLoadingState(true);

        try
        {
            await Task.Yield();
            var catalog = await PluginsService.GetCatalogAsync();
            await ApplyPluginUpdatesAsync(catalog);
            PluginsItemsControl.ItemsSource = catalog;
            EmptyStatePanel.IsVisible = catalog.Count == 0;
            if (!string.IsNullOrWhiteSpace(_selectedPluginId) &&
                !catalog.Any(plugin => plugin.Id.Equals(_selectedPluginId, StringComparison.Ordinal)))
            {
                ClearEditorSelection();
            }

            PluginsSummaryTextBlock.Text = catalog.Count == 0
                ? "Plugins you install appear here. Enable them to apply their JSON changes before launch."
                : $"{catalog.Count} installed plugin(s). Enabled plugins run in the order shown here.";
        }
        finally
        {
            SetLoadingState(false);
        }
    }

    // Marks user-sourced plugins whose discussion advertises a different plug version than the
    // installed one, so the Installed view can surface a "latest" badge. Reads from the cached
    // user-plugins list (non-forced); failures (e.g. offline) leave all items without an update.
    private static async Task ApplyPluginUpdatesAsync(IEnumerable<PluginCatalogItem> catalog)
    {
        var userPlugins = catalog.Where(p => p.IsUserPlugin && p.HasUserPluginVersion).ToList();
        if (userPlugins.Count == 0)
        {
            return;
        }

        try
        {
            var client = Program.ServiceProvider.GetRequiredService<GitHubDiscussionPluginsHttpClient>();
            var entries = await client.GetUserPluginsAsync();

            foreach (var plugin in userPlugins)
            {
                var source = entries.FirstOrDefault(entry =>
                    string.Equals(entry.DiscussionUrl, plugin.DiscussionUrl, StringComparison.OrdinalIgnoreCase));

                var latest = source?.PluginVersion?.Trim();
                plugin.LatestPluginVersion =
                    !string.IsNullOrWhiteSpace(latest) &&
                    !string.Equals(latest, plugin.UserPluginVersion?.Trim(), StringComparison.OrdinalIgnoreCase)
                        ? latest
                        : null;
            }
        }
        catch (Exception)
        {
            // Leave items without an update marker when the discussions list is unavailable.
        }
    }

    public void SetLoadingState(bool isLoading)
    {
        _isLoading = isLoading;
        LoadingBanner.IsVisible = isLoading;
        ContentPanel.IsVisible = !isLoading;
    }

    private async void OnRefreshClicked(object? sender, RoutedEventArgs e)
    {
        await RefreshPluginsStateAsync();
    }

    private async void OnDryRunClicked(object? sender, RoutedEventArgs e)
    {
        if (_isDryRunning || _isLoading)
        {
            return;
        }

        var profile = MainWindow.Settings.CurrentProfile;
        if (!profile.IsInstallDirectoryValidated)
        {
            Notifications.SendNotification(
                "Install directory not validated",
                "Choose the Diablo II: Resurrected folder that contains D2R.exe before running a dry run.");
            return;
        }

        if (profile.Type != InstallationType.D2RMM && !MainWindow.IsLocalModDetected)
        {
            Notifications.SendNotification(
                "D2R Reimagined mod not detected",
                "Install the mod in the selected directory before running a dry run.");
            return;
        }

        var mainWindow = TopLevel.GetTopLevel(this) as MainWindow;

        _isDryRunning = true;
        mainWindow?.SetNavigationEnabled(false);
        ContentPanel.IsEnabled = false;
        DryRunStatusText.Text = "Applying all tweaks and enabled plugins without launching the game.";
        DryRunBanner.IsVisible = true;

        // Progress is created on the UI thread, so its callback marshals back to it.
        var progress = new Progress<string>(status => DryRunStatusText.Text = status);

        try
        {
            var prepared = await Task.Run(() => ModTweaksService.PrepareForLaunchAsync(progress));
            if (prepared)
            {
                Notifications.SendNotification(
                    "Dry run complete. All tweaks and enabled plugins were applied without launching the game.",
                    "Success");
            }
            else
            {
                Notifications.SendNotification(
                    "Dry run failed. See previous warning for details.",
                    "Warning");
            }
        }
        catch (Exception ex)
        {
            Notifications.SendNotification($"Dry run failed: {ex.Message}", "Warning");
        }
        finally
        {
            _isDryRunning = false;
            DryRunBanner.IsVisible = false;
            ContentPanel.IsEnabled = true;
            mainWindow?.SetNavigationEnabled(true);
        }
    }

    private async void OnImportPluginClicked(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not Window window)
        {
            return;
        }

        var files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select Plugin Zip",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Zip Archives")
                {
                    Patterns = ["*.zip"]
                }
            ]
        });

        if (files.Count == 0)
        {
            return;
        }

        var zipPath = files[0].Path.LocalPath;
        if (string.IsNullOrWhiteSpace(zipPath))
        {
            Notifications.SendNotification("Selected plugin archive could not be accessed locally.", "Warning");
            return;
        }

        try
        {
            var importPreview = await PluginsService.LoadPluginImportPreviewAsync(zipPath);
            var existingPlugin = await PluginsService.FindInstalledPluginByNameAsync(importPreview.Name);
            string? replacePluginId = null;

            if (existingPlugin != null)
            {
                var shouldReplace = await ShowReplacePluginConfirmationAsync(
                    window,
                    importPreview.Name,
                    existingPlugin.Version,
                    importPreview.Version);
                if (!shouldReplace)
                {
                    return;
                }

                replacePluginId = existingPlugin.PluginId;
            }

            await PluginsService.ImportPluginAsync(zipPath, replacePluginId);
            if (!string.IsNullOrWhiteSpace(replacePluginId) &&
                string.Equals(_selectedPluginId, replacePluginId, StringComparison.Ordinal))
            {
                ClearEditorSelection();
            }

            await RefreshPluginsStateAsync();
            Notifications.SendNotification(
                existingPlugin == null ? "Plugin imported successfully." : "Plugin replaced successfully.",
                "Success");
        }
        catch (Exception ex)
        {
            Notifications.SendNotification($"Plugin import failed: {ex.Message}", "Warning");
        }
    }

    private void OnViewUserPluginOnGitHubClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: PluginCatalogItem plugin } ||
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

    private async void OnUpdateUserPluginClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: PluginCatalogItem plugin } ||
            string.IsNullOrWhiteSpace(plugin.DiscussionUrl))
        {
            return;
        }

        string? tempZipPath = null;

        try
        {
            var client = Program.ServiceProvider.GetRequiredService<GitHubDiscussionPluginsHttpClient>();
            var userPlugins = await client.GetUserPluginsAsync();
            var source = userPlugins.FirstOrDefault(entry =>
                string.Equals(entry.DiscussionUrl, plugin.DiscussionUrl, StringComparison.OrdinalIgnoreCase));

            if (source == null)
            {
                Notifications.SendNotification(
                    $"'{plugin.Name}' could not be found on the discussions board to update.",
                    "Warning");
                return;
            }

            Notifications.SendNotification($"Updating '{plugin.Name}'...", "Info");

            tempZipPath = await client.DownloadZipToTempAsync(source.ZipUrl);
            await PluginsService.ImportPluginAsync(tempZipPath, plugin.Id, source.DiscussionUrl, source.PluginVersion);

            if (string.Equals(_selectedPluginId, plugin.Id, StringComparison.Ordinal))
            {
                ClearEditorSelection();
            }

            await RefreshPluginsStateAsync();
            Notifications.SendNotification($"User plugin '{plugin.Name}' updated successfully.", "Success");
        }
        catch (Exception ex)
        {
            Notifications.SendNotification($"User plugin update failed: {ex.Message}", "Warning");
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

    private void OnOpenAuthoringGuideClicked(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is MainWindow window)
        {
            window.NavigateToPluginAuthoringGuideView();
        }
    }

    private void OnOpenOfficialPluginsClicked(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is MainWindow window)
        {
            window.NavigateToOfficialPluginsView();
        }
    }

    private void OnOpenUserPluginsClicked(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is MainWindow window)
        {
            window.NavigateToUserPluginsView();
        }
    }

    private async void OnPluginEnabledClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { DataContext: PluginCatalogItem plugin } checkBox)
        {
            return;
        }

        var isEnabled = checkBox.IsChecked == true;
        var wasEnabled = plugin.IsEnabled;

        try
        {
            await PluginsService.SetPluginEnabledAsync(plugin.Id, isEnabled);
            plugin.IsEnabled = isEnabled;
        }
        catch (Exception ex)
        {
            plugin.IsEnabled = wasEnabled;
            checkBox.IsChecked = wasEnabled;
            Notifications.SendNotification($"Plugin update failed: {ex.Message}", "Warning");
        }
    }

    private void OnBackgroundPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is TextBox)
        {
            return;
        }

        RootScrollViewer.Focus();
    }

    // Shift+Click on Up/Down jumps to the top/bottom of the installed plugin list; a plain click
    // moves the plugin one slot. The Shift state is captured via PointerPressed (Click events do
    // not expose modifiers) and consumed by the matching Click handler.
    private bool _shiftPressedDuringMoveClick;

    private void OnMovePluginButtonPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _shiftPressedDuringMoveClick = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
    }

    private async void OnMovePluginUpClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: PluginCatalogItem plugin })
        {
            return;
        }

        var moveToEdge = _shiftPressedDuringMoveClick;
        _shiftPressedDuringMoveClick = false;

        if (moveToEdge)
        {
            await PluginsService.MovePluginToEdgeAsync(plugin.Id, toTop: true);
        }
        else
        {
            await PluginsService.MovePluginAsync(plugin.Id, -1);
        }

        await RefreshPluginsStateAsync();
    }

    private async void OnMovePluginDownClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: PluginCatalogItem plugin })
        {
            return;
        }

        var moveToEdge = _shiftPressedDuringMoveClick;
        _shiftPressedDuringMoveClick = false;

        if (moveToEdge)
        {
            await PluginsService.MovePluginToEdgeAsync(plugin.Id, toTop: false);
        }
        else
        {
            await PluginsService.MovePluginAsync(plugin.Id, 1);
        }

        await RefreshPluginsStateAsync();
    }

    private async void OnDeletePluginClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: PluginCatalogItem plugin })
        {
            return;
        }

        try
        {
            await PluginsService.DeletePluginAsync(plugin.Id);

            if (string.Equals(_selectedPluginId, plugin.Id, StringComparison.Ordinal))
            {
                ClearEditorSelection();
            }

            await RefreshPluginsStateAsync();
            Notifications.SendNotification($"Plugin '{plugin.Name}' deleted.", "Success");
        }
        catch (Exception ex)
        {
            Notifications.SendNotification($"Plugin delete failed: {ex.Message}", "Warning");
        }
    }

    private async void OnEditPluginFileClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: PluginCatalogFileItem pluginFile })
        {
            return;
        }

        try
        {
            var document = await PluginsService.LoadEditorDocumentAsync(pluginFile.PluginId, pluginFile.RelativePath);
            _selectedPluginId = document.PluginId;
            _selectedRelativePath = document.RelativePath;
            EditorTitleTextBlock.Text = $"{document.PluginName} - {document.RelativePath}";
            _originalEditorContent = document.Content;
            _isUpdatingEditorState = true;
            EditorTextBox.Text = document.Content;
            _isUpdatingEditorState = false;
            SetEditorState(fileSelected: true, isDirty: false);
        }
        catch (Exception ex)
        {
            Notifications.SendNotification($"Could not open plugin JSON: {ex.Message}", "Warning");
        }
    }

    private async void OnParameterValueChanged(object? sender, RoutedEventArgs e)
    {
        if (sender is not TextBox { DataContext: PluginParameterItem parameter } textBox)
        {
            return;
        }

        var value = (textBox.Text ?? string.Empty).Trim();

        try
        {
            var changed = await PluginsService.SaveParameterValueAsync(parameter.PluginId, parameter.Key, value);
            if (!changed)
            {
                return;
            }

            parameter.UpdateValue(value);
            Notifications.SendNotification("Plugin parameter saved.", "Success");
        }
        catch (Exception ex)
        {
            Notifications.SendNotification($"Could not save plugin parameter: {ex.Message}", "Warning");
        }
    }

    // Persists checkbox-typed plugin parameters as the canonical "true"/"false" string. Uses the
    // same SaveParameterValueAsync entry point as the text editor so plugin authors only need to
    // round-trip through one serialization path.
    private async void OnParameterCheckedChanged(object? sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { DataContext: PluginParameterItem parameter } checkBox)
        {
            return;
        }

        var serialized = checkBox.IsChecked == true ? "true" : "false";

        // Update the in-memory value first so any parameter whose visibleWhen condition depends on
        // this checkbox shows/hides immediately, then persist. Intentionally avoid
        // RefreshPluginsStateAsync() and a success toast here: checkbox-heavy plugins would otherwise
        // spam notifications and rebuild the catalog on every click, causing flicker, focus loss, and
        // scroll jumps. Save quietly; only surface failures.
        parameter.UpdateValue(serialized);
        try
        {
            await PluginsService.SaveParameterValueAsync(parameter.PluginId, parameter.Key, serialized);
        }
        catch (Exception ex)
        {
            Notifications.SendNotification($"Could not save plugin parameter: {ex.Message}", "Warning");
        }
    }

    // Persists dropdown-typed plugin parameters. Mirrors OnParameterCheckedChanged: the in-memory
    // value is updated first so dependent visibleWhen parameters react live, then the selection is
    // saved quietly without rebuilding the catalog.
    private async void OnParameterSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox { DataContext: PluginParameterItem parameter } comboBox)
        {
            return;
        }

        if (comboBox.SelectedItem is not string selected)
        {
            return;
        }

        if (string.Equals(parameter.Value, selected, StringComparison.Ordinal))
        {
            return;
        }

        parameter.UpdateValue(selected);
        try
        {
            await PluginsService.SaveParameterValueAsync(parameter.PluginId, parameter.Key, selected);
        }
        catch (Exception ex)
        {
            Notifications.SendNotification($"Could not save plugin parameter: {ex.Message}", "Warning");
        }
    }

    private async void OnSaveJsonClicked(object? sender, RoutedEventArgs e)
    {
        await SaveEditorAsync();
    }

    private async void OnEditorKeyDown(object? sender, KeyEventArgs e)
    {
        var hasSaveModifier = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        if (!hasSaveModifier || e.Key != Key.S)
        {
            return;
        }

        e.Handled = true;
        await SaveEditorAsync();
        RootScrollViewer.Focus();
    }

    private void OnEditorTextChanged(object? sender, EventArgs e)
    {
        if (_isUpdatingEditorState)
        {
            return;
        }

        SetEditorState(fileSelected: !string.IsNullOrWhiteSpace(_selectedPluginId), isDirty: IsEditorDirty());
    }

    private async Task SaveEditorAsync()
    {
        if (string.IsNullOrWhiteSpace(_selectedPluginId) || string.IsNullOrWhiteSpace(_selectedRelativePath))
        {
            Notifications.SendNotification("Select a plugin JSON file before saving.", "Warning");
            return;
        }

        if (!IsEditorDirty())
        {
            return;
        }

        try
        {
            await PluginsService.SaveEditorDocumentAsync(_selectedPluginId, _selectedRelativePath, EditorTextBox.Text ?? string.Empty);
            _originalEditorContent = EditorTextBox.Text ?? string.Empty;
            SetEditorState(fileSelected: true, isDirty: false);
            await RefreshPluginsStateAsync();
            Notifications.SendNotification("Plugin JSON saved.", "Success");
        }
        catch (Exception ex)
        {
            Notifications.SendNotification($"Could not save plugin JSON: {ex.Message}", "Warning");
        }
    }

    private bool IsEditorDirty()
    {
        return !string.Equals(_originalEditorContent, EditorTextBox.Text ?? string.Empty, StringComparison.Ordinal);
    }

    private void ClearEditorSelection()
    {
        _selectedPluginId = null;
        _selectedRelativePath = null;
        _originalEditorContent = string.Empty;
        _isUpdatingEditorState = true;
        EditorTextBox.Text = string.Empty;
        _isUpdatingEditorState = false;
        EditorTitleTextBlock.Text = "Select a plugin JSON file to edit it here.";
        SetEditorState(fileSelected: false, isDirty: false);
    }

    private void SetEditorState(bool fileSelected, bool isDirty)
    {
        SaveJsonButton.IsEnabled = !_isLoading && fileSelected && isDirty;
        EditorStatusTextBlock.Text = !fileSelected
            ? "No file selected."
            : isDirty
                ? "Unsaved changes"
                : "Saved";
    }

    public static async Task<bool> ShowReplacePluginConfirmationAsync(
        Window owner,
        string pluginName,
        string existingVersion,
        string incomingVersion)
    {
        var dialog = new Window
        {
            Width = 420,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Title = "Replace Plugin?"
        };

        var replaceButton = new Button
        {
            Content = "Replace",
            Classes = { "accent" },
            MinWidth = 96
        };
        var cancelButton = new Button
        {
            Content = "Cancel",
            MinWidth = 96
        };

        replaceButton.Click += (_, _) => dialog.Close(true);
        cancelButton.Click += (_, _) => dialog.Close(false);

        dialog.Content = new Border
        {
            Padding = new Thickness(20),
            Child = new StackPanel
            {
                Spacing = 14,
                Children =
                {
                    new TextBlock
                    {
                        Text = $"A plugin named '{pluginName}' is already installed.",
                        FontWeight = FontWeight.SemiBold,
                        TextWrapping = TextWrapping.Wrap
                    },
                    new TextBlock
                    {
                        Text = $"Installed version: {existingVersion}{Environment.NewLine}Incoming version: {incomingVersion}{Environment.NewLine}{Environment.NewLine}Do you want to replace the existing install?",
                        TextWrapping = TextWrapping.Wrap
                    },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 10,
                        Children =
                        {
                            cancelButton,
                            replaceButton
                        }
                    }
                }
            }
        };

        return await dialog.ShowDialog<bool>(owner);
    }

    private void ConfigureEditor()
    {
        EditorTextBox.IsReadOnly = false;
        EditorTextBox.HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto;
        EditorTextBox.VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto;
        EditorTextBox.Options.ConvertTabsToSpaces = true;
        EditorTextBox.Options.IndentationSize = 2;
        EditorTextBox.Options.EnableHyperlinks = false;
        EditorTextBox.Options.HighlightCurrentLine = true;

        _textMateInstallation = EditorTextBox.InstallTextMate(_registryOptions);
        var jsonScope = _registryOptions.GetLanguageByExtension(".json").Id;
        _textMateInstallation.SetGrammar(jsonScope);
    }
}
