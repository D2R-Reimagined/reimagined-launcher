using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using ReimaginedLauncher.SaveMigration;
using ReimaginedLauncher.Utilities;
using ReimaginedLauncher.Utilities.ViewModels;

namespace ReimaginedLauncher.Views.SaveConversion;

public partial class SaveConversionWindow : Window
{
    private readonly ObservableCollection<SaveConversionRowViewModel> _rows = [];
    private readonly Dictionary<(SaveConversionRowViewModel Row, string? Source, bool Force), SaveConversionPlan> _plans = [];
    private readonly string _stagingDirectory =
        Path.Combine(Path.GetTempPath(), "ReimaginedSaveConversion", Guid.NewGuid().ToString("N"));
    private readonly string _saveDirectory;
    private readonly SaveCheckpointRegistry? _registry;
    private IReadOnlyList<SaveCheckpoint> _targets = [];
    private SaveTargetSelection? _defaultTarget;
    private SaveConversionService? _service;
    private bool _isBusy;

    public SaveConversionWindow()
    {
        InitializeComponent();
        FilesItemsControl.ItemsSource = _rows;
        FilesScrollViewer.AddHandler(GotFocusEvent, OnListGotFocus);
        _saveDirectory = MainWindow.Settings is null
            ? string.Empty
            : SaveConversionService.ResolveOfflineSaveDirectory(MainWindow.Settings.CurrentProfile);
        try
        {
            _registry = SaveCheckpointRegistry.Load(SaveConversionService.DefaultDataDirectory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            LaunchDiagnostics.LogException("Could not read the save migration checkpoint registry", exception);
        }

        Opened += (_, _) => SelectInitialTarget();
        Closing += (_, args) => args.Cancel = _isBusy;
        Closed += (_, _) => DeleteStagingDirectory();
    }

    /// <summary>Selects the newest supported target; selecting it starts the analysis.</summary>
    private void SelectInitialTarget()
    {
        BackupDirectoryText.Text = $"Permanent conversion backups: {SaveConversionService.DefaultBackupRoot}";
        if (_registry is null)
        {
            TargetComboBox.IsEnabled = false;
            ForceCheckBox.IsEnabled = false;
            ShowBanner("The bundled save layout registry could not be read, so saves cannot be analyzed. Reinstall the launcher.");
            return;
        }

        _targets = _registry.Targets;
        TargetComboBox.ItemsSource = _targets.Select(checkpoint => checkpoint.DisplayName).ToList();
        var installed = MainWindow.Settings is null ? null : SaveConversionService.ReadInstalledReimaginedVersion(MainWindow.Settings.CurrentProfile);
        _defaultTarget = _registry.SelectTarget(installed);
        if (_defaultTarget.Target is { } target)
        {
            TargetComboBox.SelectedIndex = _targets.ToList().IndexOf(target);
        }
        else
        {
            TargetExplanationText.Text = _defaultTarget.Explanation;
            ForceCheckBox.IsEnabled = false;
        }
    }

    private async void OnTargetSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_registry is null || _isBusy || TargetComboBox.SelectedIndex < 0 || TargetComboBox.SelectedIndex >= _targets.Count)
        {
            return;
        }

        var target = _targets[TargetComboBox.SelectedIndex];
        var isDefault = target == _defaultTarget?.Target;
        _service = SaveConversionService.CreateForTarget(_registry, target.Id);
        TargetExplanationText.Text = isDefault
            ? _defaultTarget!.Explanation
            : $"Chosen manually instead of the latest supported layout, {_defaultTarget?.Target?.DisplayName}."
              + (target.DevelopmentTarget ? " This is a development build layout; use it only when testing that build." : "");
        TargetWarningText.Text = isDefault ? _defaultTarget!.Warning : null;
        TargetWarningText.IsVisible = isDefault && _defaultTarget!.Warning is not null;
        StatusBanner.IsVisible = false;
        await AnalyzeAsync(_service);
    }

    private async Task AnalyzeAsync(SaveConversionService service)
    {
        foreach (var row in _rows)
        {
            row.PropertyChanged -= OnRowPropertyChanged;
        }

        _rows.Clear();
        _plans.Clear();
        if (string.IsNullOrWhiteSpace(_saveDirectory) || !Directory.Exists(_saveDirectory))
        {
            SaveDirectoryText.Text = "Offline save folder: not found";
            ShowBanner("The offline Reimagined save folder could not be found. Install the normal mod on the Install/Update page, "
                       + "or set the save directory on the Backups page.");
            UpdateSummary();
            return;
        }

        SaveDirectoryText.Text = $"Offline save folder: {_saveDirectory}";
        SetBusy(true, "Looking for characters and shared stashes...");
        try
        {
            var files = await Task.Run(() => SaveConversionService.DiscoverSaveFiles(_saveDirectory));
            if (files.Count == 0)
            {
                ShowBanner("No .d2s characters or .d2i shared stashes were found in the offline save folder.");
                return;
            }

            for (var index = 0; index < files.Count; index++)
            {
                var file = files[index];
                BusyText.Text = $"Analyzing {Path.GetFileName(file)} ({index + 1} of {files.Count})...";
                var staging = NewStagingDirectory();
                var plan = await Task.Run(() => service.Plan(file, staging));
                var row = new SaveConversionRowViewModel(plan, service.CheckRename);
                await RefreshPlanAsync(service, row);
                row.PropertyChanged += OnRowPropertyChanged;
                _rows.Add(row);
            }

            if (MainWindow.IsGameRunning())
            {
                ShowBanner("Diablo II: Resurrected is running. Close the game before converting; files cannot be changed while it runs.");
            }
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>Applies the row's source choice and the Force Conversion setting. Only the affected row's plan changes.</summary>
    private async Task RefreshPlanAsync(SaveConversionService service, SaveConversionRowViewModel row, bool? select = null)
    {
        if (row.IsDone)
        {
            return;
        }

        var force = ForceCheckBox.IsChecked == true;
        var plan = row.IsTargetChoiceSelected
            ? row.BasePlan
            : row.IsSourceUncertain
                ? await PlanAsync(service, row, row.SelectedSourceChoice!.Layout, force)
                : force && row.BasePlan is { State: SaveConversionState.Unresolved, ForceOption.Kind: SaveForceKind.AcceptUnresolvedIdentities }
                    ? await PlanAsync(service, row, null, force)
                    : row.BasePlan;
        if (plan != row.Plan || select is not null)
        {
            row.ReplacePlan(plan, select);
        }
    }

    private async Task<SaveConversionPlan> PlanAsync(SaveConversionService service, SaveConversionRowViewModel row, string? source, bool force)
    {
        if (_plans.TryGetValue((row, source, force), out var cached))
        {
            return cached;
        }

        var overrides = new MigrationOverrides(AcceptUnresolvedIdentities: force, SourceLayout: source);
        var path = row.BasePlan.SourcePath;
        var staging = NewStagingDirectory();
        var plan = await Task.Run(() => service.Plan(path, staging, overrides));
        LaunchDiagnostics.Log($"Save conversion analysis for {path} (source {source ?? "identified"}, force {force}): {plan.State} - {plan.Operation}");
        _plans[(row, source, force)] = plan;
        return plan;
    }

    private async void OnForceChanged(object? sender, RoutedEventArgs e)
    {
        if (_isBusy || _service is not { } service)
        {
            return;
        }

        SetBusy(true, ForceCheckBox.IsChecked == true ? "Preparing experimental conversions..." : "Restoring verified conversions...");
        try
        {
            foreach (var row in _rows.ToList())
            {
                BusyText.Text = $"Analyzing {row.FileName}...";
                await RefreshPlanAsync(service, row);
            }
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SaveConversionRowViewModel.IsReadyToApply))
        {
            UpdateSummary();
            return;
        }

        if (e.PropertyName != nameof(SaveConversionRowViewModel.SelectedSourceChoice)
            || sender is not SaveConversionRowViewModel row || _service is not { } service || _isBusy)
        {
            return;
        }

        SetBusy(true, $"Analyzing {row.FileName} as {row.SelectedSourceChoice}...");
        try
        {
            await RefreshPlanAsync(service, row, select: !row.IsTargetChoiceSelected);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnConvertClick(object? sender, RoutedEventArgs e)
    {
        var selected = _rows.Where(row => row.IsReadyToApply).ToList();
        if (_isBusy || selected.Count == 0 || _service is not { } service)
        {
            return;
        }

        if (MainWindow.IsGameRunning())
        {
            ShowBanner("Diablo II: Resurrected is running. Close the game before converting; nothing was changed.");
            return;
        }

        SetBusy(true, "Creating the backup folder...");
        var succeeded = 0;
        try
        {
            var batchDirectory = await Task.Run(service.CreateBackupBatchDirectory);
            foreach (var row in selected)
            {
                BusyText.Text = $"Converting {row.FileName}...";
                var plan = row.Plan;
                var name = row.ApprovedName;
                var outcome = await Task.Run(() => service.Apply(plan, name, batchDirectory));
                row.SetResult(outcome);
                LaunchDiagnostics.Log($"Save conversion {(outcome.Succeeded ? "succeeded" : "failed")} for {plan.SourcePath}: {outcome.Message}");
                succeeded += outcome.Succeeded ? 1 : 0;
            }

            foreach (var row in _rows.Except(selected))
            {
                row.MarkSkipped();
            }

            ShowBanner(succeeded == selected.Count
                ? $"{succeeded} file(s) processed. The originals are kept in {batchDirectory}."
                : $"{succeeded} of {selected.Count} file(s) processed; the others were left unchanged. Backups: {batchDirectory}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            LaunchDiagnostics.LogException("Could not create the save conversion backup folder", exception);
            ShowBanner($"The backup folder could not be created ({exception.Message}). Nothing was changed.");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    /// <summary>Keyboard navigation still scrolls the focused control into view; pointer focus does not move the list.</summary>
    private void OnListGotFocus(object? sender, FocusChangedEventArgs e)
    {
        if (e.NavigationMethod is (NavigationMethod.Tab or NavigationMethod.Directional) && e.Source is Control control && control != FilesScrollViewer)
        {
            control.BringIntoView();
        }
    }

    private void UpdateSummary()
    {
        var ready = _rows.Count(row => row.IsReadyToApply);
        var converted = _rows.Count(row => row.IsDone);
        var compatible = _rows.Count(row => !row.IsDone && (row.IsTargetChoiceSelected || row.Plan.State == SaveConversionState.AlreadyCompatible));
        var blocked = _rows.Count(row => !row.IsDone && !row.CanSelect && row.IsProblem);
        SummaryText.Text = _rows.Count == 0
            ? string.Empty
            : $"{_rows.Count} file(s): {ready} selected"
              + (converted > 0 ? $", {converted} converted" : "")
              + $", {compatible} already compatible, {blocked} blocked.";
        ConvertButton.IsEnabled = !_isBusy && ready > 0;
    }

    private void SetBusy(bool isBusy, string? message = null)
    {
        _isBusy = isBusy;
        BusyPanel.IsVisible = isBusy;
        BusyText.Text = message ?? string.Empty;
        CloseButton.IsEnabled = !isBusy;
        FilesItemsControl.IsEnabled = !isBusy;
        TargetComboBox.IsEnabled = !isBusy && _registry is not null;
        ForceCheckBox.IsEnabled = !isBusy && _service is not null;
        UpdateSummary();
    }

    private void ShowBanner(string message)
    {
        StatusBanner.IsVisible = true;
        StatusBannerText.Text = message;
    }

    private string NewStagingDirectory() => Path.Combine(_stagingDirectory, Guid.NewGuid().ToString("N"));

    private void DeleteStagingDirectory()
    {
        try
        {
            if (Directory.Exists(_stagingDirectory))
            {
                Directory.Delete(_stagingDirectory, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            LaunchDiagnostics.LogException("Could not remove the save conversion staging folder", exception);
        }
    }
}
