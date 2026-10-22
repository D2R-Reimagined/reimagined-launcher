using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using ReimaginedLauncher.HttpClients.Models;
using ReimaginedLauncher.Utilities;

namespace ReimaginedLauncher.Views.Launch;

public partial class LaunchView
{
    private string _heroPluginFilter = "All";
    private (InstallationProfile Profile, string? Path, bool Verified)? _installAttentionState;
    private bool _restoringCardExpansion;
    private AppSettings? _cardSettings;
    internal Func<AppSettings, Task> SaveCardSettingsAsync { get; set; } = SettingsManager.SaveAsync;

    private void UpdateStartButtonSheen()
    {
        var ready = StartGameButton.IsEffectivelyEnabled && StartGameButton.Content is "Start Game";
        StartGameSheen.IsVisible = ready;
        StartGameSheen.Classes.Set("ready", ready);
    }

    private IEnumerable<(string Key, Expander Card)> LaunchCards => _installationOnly
        ? [("UpdateInstallDirectory", HeroInstallExpander)] :
    [
        ("LootFilter", LootFilterExpander),
        ("ServerRegions", RegionsExpander), ("LadderSafeguards", SafeguardsExpander),
        ("InstallDirectory", HeroInstallExpander), ("Plugins", PluginsExpander),
        ("LaunchStatus", LaunchStatusExpander)
    ];

    private void RestoreCardExpansion()
    {
        if (_installationOnly && !_collapsibleInstallation) return;
        _cardSettings = MainWindow.Settings;
        _restoringCardExpansion = true;
        try
        {
            MainWindow.Settings.LaunchCardExpansion ??= [];
            foreach (var (key, card) in LaunchCards)
                card.IsExpanded = MainWindow.Settings.LaunchCardExpansion.GetValueOrDefault(key, key is not ("InstallDirectory" or "UpdateInstallDirectory"));
        }
        finally { _restoringCardExpansion = false; }
        _installAttentionState = null;
    }

    private void TrackCardExpansion()
    {
        if (_installationOnly && !_collapsibleInstallation) return;
        foreach (var (key, card) in LaunchCards)
            card.PropertyChanged += async (_, change) =>
            {
                if (change.Property != Expander.IsExpandedProperty || _restoringCardExpansion) return;
                MainWindow.Settings.LaunchCardExpansion[key] = card.IsExpanded;
                try { await SaveCardSettingsAsync(MainWindow.Settings); }
                catch (Exception exception) { LaunchDiagnostics.LogException("Saving card layout", exception); }
            };
    }

    private void UpdateInstallDirectoryAttention(InstallationProfile profile, bool isVerified)
    {
        if (_installationOnly && !_collapsibleInstallation) return;
        var previous = _installAttentionState;
        var changedPath = previous is null || !ReferenceEquals(previous.Value.Profile, profile)
            || !string.Equals(previous.Value.Path, profile.InstallDirectory, StringComparison.Ordinal);
        _restoringCardExpansion = true;
        try
        {
            if (changedPath || previous!.Value.Verified != isVerified)
                HeroInstallExpander.IsExpanded = !isVerified || MainWindow.Settings.LaunchCardExpansion.GetValueOrDefault(_installationOnly ? "UpdateInstallDirectory" : "InstallDirectory", false);
        }
        finally { _restoringCardExpansion = false; }

        InstallDirectoryPulse.IsVisible = !isVerified;
        InstallDirectoryPulse.Classes.Set("install-attention-pulse", !isVerified);
        InstallDirectoryAttentionBadge.IsVisible = !isVerified;
        _installAttentionState = (profile, profile.InstallDirectory, isVerified);
    }


    private void OnHeroChangePathClick(object? sender, RoutedEventArgs e)
    {
        HeroInstallExpander.IsExpanded = true;
        HeroInstallExpander.BringIntoView();
    }

    private void OnPluginsExpanded(object? sender, RoutedEventArgs e)
        => Avalonia.Threading.Dispatcher.UIThread.Post(ApplyHeroPluginFilter, Avalonia.Threading.DispatcherPriority.Loaded);

    private void OnHeroSearchChanged(object? sender, TextChangedEventArgs e) => ApplyHeroPluginFilter();

    private void OnHeroFilterClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string filter }) _heroPluginFilter = filter;
        ApplyHeroPluginFilter();
    }

    private void ApplyHeroPluginFilter()
    {
        if (HeroPluginSearch is null || HeroPluginCount is null) return;
        var verified = _ladderBundleReadiness?.IsReady == true && _ladderPolicyVerified;
        HeroPolicySummary.Text = verified ? "✓ Package and plugins verified"
            : _isRefreshingLadders ? "Checking Ladder…"
            : _ladderBundleReadiness?.IsReady == true ? LadderExtensionPolicyStatusText.Text
            : LadderBundleStatusText.Text;
        HeroPolicySummary.Foreground = verified ? Avalonia.Media.Brushes.LightGreen : LadderBundleStatusText.Foreground;
        LootFilterStatusText.IsVisible = !string.IsNullOrWhiteSpace(LootFilterStatusText.Text);
        if (!PluginsExpander.IsExpanded) return;
        var query = HeroPluginSearch.Text?.Trim() ?? string.Empty;
        var ladder = MainWindow.Settings.CurrentProfile.LaunchExperience == LaunchExperience.Ladder;
        var shown = 0;
        var total = 0;
        foreach (var border in this.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("extension-card")))
        {
            string name, file, scope;
            bool enabled;
            if (!ladder && border.DataContext is D2RLoaderExtensionInfo entry)
            {
                name = entry.Name; file = entry.FileName;
                scope = entry.Scope == D2RLoaderExtensionScope.Global ? "Global" : "Mod";
                enabled = entry.IsEnabled;
            }
            else if (ladder && border.DataContext is LadderExtensionChoice choice)
            {
                name = choice.Name; file = choice.FileName; scope = "Mod"; enabled = choice.IsSelected;
            }
            else continue;
            total++;
            var visible = (name.Contains(query, StringComparison.OrdinalIgnoreCase) || file.Contains(query, StringComparison.OrdinalIgnoreCase))
                && (_heroPluginFilter == "All" || _heroPluginFilter == "Enabled" && enabled || _heroPluginFilter == scope);
            if (border.IsVisible != visible) border.IsVisible = visible;
            if (visible) shown++;
        }
        foreach (var button in this.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("hero-filter")))
        {
            button.Classes.Set("selected", button.Tag?.ToString() == _heroPluginFilter);
            if (button.Tag?.ToString() == "All") button.Content = $"All ({total})";
        }
        var label = $"{shown} of {total} plugins / patches";
        if (HeroPluginCount.Text != label) HeroPluginCount.Text = label;
    }
}
