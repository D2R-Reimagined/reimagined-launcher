using System;
using System.Threading.Tasks;
using AvaloniaEdit.TextMate;
using AvaloniaEdit;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using ReimaginedLauncher.Utilities;
using TextMateSharp.Grammars;

namespace ReimaginedLauncher.Views.Plugins;

public partial class PluginsView : UserControl
{
    private readonly RegistryOptions _registryOptions = new(ThemeName.DarkPlus);
    private TextMate.Installation? _textMateInstallation;
    private string? _selectedPluginId;
    private string? _selectedRelativePath;
    private string _originalEditorContent = string.Empty;
    private bool _isUpdatingEditorState;

    public PluginsView()
    {
        InitializeComponent();
        ConfigureEditor();
        SupportedTargetsTextBlock.Text = PluginsService.GetSupportedTargetsSummary();
        SetEditorState(fileSelected: false, isDirty: false);
        _ = RefreshPluginsStateAsync();
    }

    public async Task RefreshPluginsStateAsync()
    {
        var catalog = await PluginsService.GetCatalogAsync();
        PluginsItemsControl.ItemsSource = catalog;
        EmptyStatePanel.IsVisible = catalog.Count == 0;
        PluginsSummaryTextBlock.Text = catalog.Count == 0
            ? "Imported plugin zips appear here. Enable them to apply their JSON changes before launch."
            : $"{catalog.Count} plugin(s) imported. Enabled plugins run in the order shown here.";
    }

    private async void OnRefreshClicked(object? sender, RoutedEventArgs e)
    {
        await RefreshPluginsStateAsync();
    }

    private async void OnImportPluginClicked(object? sender, RoutedEventArgs e)
    {
        if (this.GetVisualRoot() is not Window window)
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
            await PluginsService.ImportPluginAsync(zipPath);
            await RefreshPluginsStateAsync();
            Notifications.SendNotification("Plugin imported successfully.", "Success");
        }
        catch (Exception ex)
        {
            Notifications.SendNotification($"Plugin import failed: {ex.Message}", "Warning");
        }
    }

    private void OnOpenAuthoringGuideClicked(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is MainWindow window)
        {
            window.NavigateToPluginAuthoringGuideView();
        }
    }

    private async void OnPluginEnabledClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { DataContext: PluginCatalogItem plugin } checkBox)
        {
            return;
        }

        try
        {
            await PluginsService.SetPluginEnabledAsync(plugin.Id, checkBox.IsChecked == true);
            await RefreshPluginsStateAsync();
        }
        catch (Exception ex)
        {
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

    private async void OnMovePluginUpClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: PluginCatalogItem plugin })
        {
            return;
        }

        await PluginsService.MovePluginAsync(plugin.Id, -1);
        await RefreshPluginsStateAsync();
    }

    private async void OnMovePluginDownClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: PluginCatalogItem plugin })
        {
            return;
        }

        await PluginsService.MovePluginAsync(plugin.Id, 1);
        await RefreshPluginsStateAsync();
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

        try
        {
            var changed = await PluginsService.SaveParameterValueAsync(parameter.PluginId, parameter.Key, textBox.Text ?? string.Empty);
            if (!changed)
            {
                return;
            }

            await RefreshPluginsStateAsync();
            Notifications.SendNotification("Plugin parameter saved.", "Success");
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

    private void SetEditorState(bool fileSelected, bool isDirty)
    {
        SaveJsonButton.IsEnabled = fileSelected && isDirty;
        EditorStatusTextBlock.Text = !fileSelected
            ? "No file selected."
            : isDirty
                ? "Unsaved changes"
                : "Saved";
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
