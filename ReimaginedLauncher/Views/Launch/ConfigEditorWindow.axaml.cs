using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia;
using Avalonia.Media;
using AvaloniaEdit.Editing;
using ReimaginedLauncher.Utilities;

namespace ReimaginedLauncher.Views.Launch;

public partial class ConfigEditorWindow : Window
{
    private readonly ConfigEditSession _session = null!;
    private readonly Action _validate = null!;
    private bool _discard;
    private bool _saving;

    public ConfigEditorWindow()
    {
        InitializeComponent();
        Editor.Options.HighlightCurrentLine = true;
        Editor.Options.LineHeightFactor = 1.35;
        Editor.TextArea.TextView.CurrentLineBackground = new SolidColorBrush(Color.Parse("#302A20"));
        Editor.TextArea.TextView.CurrentLineBorder = new Pen(new SolidColorBrush(Color.Parse("#655437")), 1);
        Editor.TextArea.CaretBrush = new SolidColorBrush(Color.Parse("#FFF0C8"));
        foreach (var margin in Editor.TextArea.LeftMargins)
        {
            margin.Margin = new Thickness(0, 0, 12, 0);
            if (margin is LineNumberMargin numbers) numbers.MinWidth = 36;
        }
        Editor.TextArea.Caret.PositionChanged += (_, _) =>
            CaretStatus.Text = $"Line {Editor.TextArea.Caret.Line} · Column {Editor.TextArea.Caret.Column}";
    }

    internal ConfigEditorWindow(string name, ConfigEditSession session, Action validate) : this()
    {
        _session = session;
        _validate = validate;
        ConfigTitle.Text = name;
        ConfigPath.Text = session.Path;
        Editor.Text = session.Text;
        Opened += (_, _) =>
        {
            if (Owner is { } owner)
            {
                Width = Math.Min(900, Math.Max(540, owner.Bounds.Width - 40));
                Height = Math.Min(600, Math.Max(400, owner.Bounds.Height - 60));
            }
            Editor.Focus();
        };
        Closing += (_, e) =>
        {
            if (_saving) { e.Cancel = true; return; }
            if (!_discard && Editor.Text != _session.Text)
            {
                e.Cancel = true;
                StatusText.Text = "Unsaved changes. Save them, or choose Discard changes to close.";
                DiscardButton.IsVisible = true;
            }
        };
    }

    private async void OnSave(object? sender, RoutedEventArgs e)
    {
        if (_saving) return;
        _saving = true;
        SaveButton.IsEnabled = false;
        var entered = false;
        try
        {
            entered = await LocalPluginInstallService.InstallGate.WaitAsync(0);
            if (!entered) throw new InvalidOperationException("Another installation is in progress. Try again when it finishes.");
            _session.Save(Editor.Text, _validate);
            _saving = false;
            Close();
        }
        catch (Exception exception) { StatusText.Text = "Could not save: " + exception.Message; }
        finally
        {
            if (entered) LocalPluginInstallService.InstallGate.Release();
            _saving = false;
            SaveButton.IsEnabled = true;
        }
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();
    private void OnDiscard(object? sender, RoutedEventArgs e) { _discard = true; Close(); }
}
