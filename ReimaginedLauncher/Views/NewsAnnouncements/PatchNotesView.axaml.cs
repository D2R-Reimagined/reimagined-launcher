using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Microsoft.Extensions.DependencyInjection;
using ReimaginedLauncher.Utilities;

namespace ReimaginedLauncher.Views.NewsAnnouncements;

public partial class PatchNotesView : UserControl
{
    private readonly PatchNotesService _service;
    private CancellationTokenSource? _request;
    private bool _settingSelection;
    private bool _loaded;
    private bool _articleReady;
    private string _indexStatus = "";

    public PatchNotesView() : this(Program.ServiceProvider.GetRequiredService<PatchNotesService>()) { }
    internal PatchNotesView(PatchNotesService service)
    {
        _service = service;
        InitializeComponent();
        DetachedFromVisualTree += (_, _) => _request?.Cancel();
    }

    public async Task LoadAsync(string? version = null, bool refresh = false)
    {
        if (_loaded && _articleReady && version == null && !refresh) return;
        var token = BeginRequest();
        try
        {
            var result = await _service.GetIndexAsync(refresh, token);
            token.ThrowIfCancellationRequested();
            _indexStatus = Status(result.Status, result.SavedAt);
            var requested = version ?? (VersionPicker.SelectedItem as PatchRelease)?.Version;
            var selection = result.Value.FirstOrDefault(r => PatchNotesParser.MatchesVersion(r.Version, requested)) ?? result.Value[0];
            _settingSelection = true;
            VersionPicker.ItemsSource = result.Value;
            VersionPicker.SelectedItem = selection;
            _settingSelection = false;
            _loaded = true;
            await ShowReleaseAsync(selection, refresh, token);
            if (version != null && !PatchNotesParser.MatchesVersion(selection.Version, version))
                ReaderStatus.Text = $"No wiki entry for {version}; showing {selection.Version}. " + ReaderStatus.Text;
        }
        catch (OperationCanceledException) { }
        catch (IOException ex) { ReaderStatus.Text = ex.Message; }
        finally { EndRequest(token); }
    }

    private CancellationToken BeginRequest()
    {
        _request?.Cancel();
        _request?.Dispose();
        _request = new CancellationTokenSource();
        _articleReady = false;
        ReaderProgress.IsVisible = true;
        RefreshNotesButton.IsEnabled = false;
        ReaderStatus.Text = "Loading patch notes…";
        return _request.Token;
    }

    private void EndRequest(CancellationToken token)
    {
        if (token.IsCancellationRequested) return;
        ReaderProgress.IsVisible = false;
        RefreshNotesButton.IsEnabled = true;
    }

    private async void OnVersionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_settingSelection || VersionPicker.SelectedItem is not PatchRelease release) return;
        var token = BeginRequest();
        try { await ShowReleaseAsync(release, false, token); }
        catch (OperationCanceledException) { }
        catch (IOException ex) { ReaderStatus.Text = ex.Message; }
        finally { EndRequest(token); }
    }

    private async Task ShowReleaseAsync(PatchRelease release, bool refresh, CancellationToken token)
    {
        ReleaseHeader.IsVisible = true;
        ReleaseVersion.Text = "Reimagined " + release.Version;
        ReleaseTitle.Text = release.Title;
        ReleaseDate.Text = release.Date;
        ReleaseHighlights.Text = release.Highlights;
        ReleaseHighlights.IsVisible = !string.IsNullOrWhiteSpace(release.Highlights);
        InstalledBadge.IsVisible = PatchNotesParser.MatchesVersion(release.Version, MainWindow.UpdateCurrentVersion);
        SourceButton.Content = PatchNotesParser.IsWikiPage(release.Url) ? "View on Wiki" : "View on GitHub";
        ArticleBody.Children.Clear();
        ArticleScroller.Offset = new Vector(0, 0);
        if (!PatchNotesParser.IsWikiPage(release.Url))
        {
            ArticleBody.Children.Add(Paragraph("This older release links to GitHub. Use View on GitHub to read the original notes in your browser."));
            ReaderStatus.Text = _indexStatus + " · article available on GitHub";
            _articleReady = true;
            return;
        }
        var article = await _service.GetArticleAsync(release, refresh, token);
        token.ThrowIfCancellationRequested();
        Render(article.Value);
        ReaderStatus.Text = Status(article.Status, article.SavedAt);
        if (_indexStatus.Contains("Could not refresh") && !article.Status.Contains("Could not refresh"))
            ReaderStatus.Text = "Version list could not refresh · " + ReaderStatus.Text;
        _articleReady = true;
    }

    private static string Status(string status, DateTimeOffset date) => $"{status} · {date.ToLocalTime():MMM d, yyyy h:mm tt} Local";
    private static SelectableTextBlock Paragraph(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 14, LineHeight = 22 };

    private void Render(PatchArticle article)
    {
        foreach (var block in article.Blocks)
        {
            var group = new StackPanel { Spacing = 4, Margin = new Thickness(Math.Min(block.Depth, 8) * 16, 0, 0, 0) };
            if (block.Kind == "table" && block.Rows is { Count: > 0 } rows)
            {
                var columns = rows.Max(r => r.Count);
                foreach (var row in rows)
                {
                    var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(string.Join(",", Enumerable.Repeat("*", columns))), ColumnSpacing = 12 };
                    for (var i = 0; i < row.Count; i++)
                    {
                        var cell = RichParagraph(row[i]);
                        Grid.SetColumn(cell, i);
                        grid.Children.Add(cell);
                    }
                    group.Children.Add(new Border { Classes = { "panel" }, Padding = new Thickness(10), Child = grid });
                }
            }
            else
            {
                var text = RichParagraph(block.Inlines, block.Kind == "li" ? "•  " : "");
                if (block.Kind.StartsWith('h'))
                {
                    text.Classes.Add("section-title");
                    text.FontFamily = new FontFamily("Georgia");
                    text.FontSize = block.Kind is "h1" or "h2" ? 21 : 17;
                    text.FontWeight = FontWeight.SemiBold;
                    text.Margin = new Thickness(0, 12, 0, 2);
                }
                if (block.Kind == "pre") text.FontFamily = new FontFamily("monospace");
                group.Children.Add(text);
            }
            ArticleBody.Children.Add(group);
        }
    }

    private TextBlock RichParagraph(List<PatchInline> parts, string prefix = "")
    {
        var text = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, FontSize = 14,
            LineHeight = parts.Any(part => part.Url != null) ? double.NaN : 22
        };
        if (prefix.Length > 0) text.Inlines!.Add(new Run(prefix));
        foreach (var part in parts)
        {
            if (part.Url == null)
            {
                text.Inlines!.Add(new Run(part.Text));
                continue;
            }
            var label = new TextBlock { Text = part.Text, TextWrapping = TextWrapping.Wrap, TextDecorations = TextDecorations.Underline };
            var link = new HyperlinkButton
            {
                Content = label,
                Padding = new Thickness(0), Margin = new Thickness(0), MinHeight = 0, MinWidth = 0,
                FontSize = 14, Cursor = new Cursor(StandardCursorType.Hand),
                Tag = part.Url
            };
            label.SizeChanged += (_, _) => TextBlock.SetBaselineOffset(link, label.TextLayout.Baseline);
            ToolTip.SetTip(link, part.Url);
            link.Click += (_, _) => OpenLink(part.Url);
            text.Inlines!.Add(new InlineUIContainer(link));
        }
        return text;
    }

    private async void OnRefresh(object? sender, RoutedEventArgs e) => await LoadAsync(refresh: true);
    private void OnViewSource(object? sender, RoutedEventArgs e) => OpenLink((VersionPicker.SelectedItem as PatchRelease)?.Url ?? PatchNotesParser.IndexUrl);
    private void OpenLink(string url)
    {
        var safeUrl = PatchNotesParser.SafeLink(url);
        if (safeUrl == null) return;
        try { Process.Start(new ProcessStartInfo(safeUrl) { UseShellExecute = true }); }
        catch (Exception) { ReaderStatus.Text = "Could not open your browser. Source: " + safeUrl; }
    }
}
