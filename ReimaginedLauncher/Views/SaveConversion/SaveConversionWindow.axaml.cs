using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
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

    private readonly Dictionary<(SaveConversionRowViewModel Row, string? Source, bool Force), SaveConversionPlan>
        _plans = [];

    private readonly string _stagingDirectory =
        Path.Combine(Path.GetTempPath(), "ReimaginedSaveConversion", Guid.NewGuid().ToString("N"));

    private readonly string _saveDirectory;
    private readonly SaveCheckpointRegistry? _registry;
    private readonly Func<bool>? _isPromptCurrent;
    private readonly Action<string?, IReadOnlyList<SaveConversionPlan>>? _promptShown;
    private readonly bool _isAutomaticPrompt;
    private SaveConversionService? _service;
    private bool _isBusy;

    public SaveConversionWindow()
    {
        InitializeComponent();
        FilesItemsControl.ItemsSource = _rows;
        FilesScrollViewer.AddHandler(GotFocusEvent, OnListGotFocus);
        BackupDirectoryText.Text = $"Permanent conversion backups: {SaveConversionService.DefaultBackupRoot}";
        _saveDirectory = SaveConversionService.ResolveOfflineSaveDirectory(MainWindow.Settings.CurrentProfile);
        try
        {
            _registry = SaveCheckpointRegistry.Load(SaveConversionService.DefaultDataDirectory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                              or InvalidDataException or System.Text.Json.JsonException)
        {
            LaunchDiagnostics.LogException("Could not read the save migration checkpoint registry", exception);
        }

        Opened += async (_, _) =>
        {
            // Dismiss a stale automatic prompt before initialization makes the window busy and uncloseable.
            if (_isPromptCurrent?.Invoke() == false)
            {
                Close();
                return;
            }

            var analyzedTarget = await InitializeConversionAsync();
            if (_isPromptCurrent?.Invoke() == false || (_isAutomaticPrompt && (analyzedTarget is null ||
                    !_rows.Any(row => SaveConversionService.NeedsConversion(row.BasePlan)))))
            {
                Close();
                return;
            }

            _promptShown?.Invoke(analyzedTarget, _rows.Select(row => row.BasePlan).ToList());
        };
        Closing += (_, args) => args.Cancel = _isBusy;
        Closed += (_, _) => DeleteStagingDirectory();
    }

    /// <param name="promptMessage">Why the launcher opened this window on its own, shown above the description.</param>
    /// <param name="isPromptCurrent">Checked before and after initialization; false closes the window without reporting it as shown.</param>
    /// <param name="promptShown">
    /// Called after initialization with the analyzed installed target fingerprint and the analyzed plans. A failed initialization,
    /// or one that leaves no save needing conversion, closes the window without calling it.
    /// </param>
    public SaveConversionWindow(string promptMessage, Func<bool>? isPromptCurrent = null,
        Action<string?, IReadOnlyList<SaveConversionPlan>>? promptShown = null) : this()
    {
        PromptText.Text = promptMessage;
        PromptBanner.IsVisible = true;
        _isAutomaticPrompt = true;
        _isPromptCurrent = isPromptCurrent;
        _promptShown = promptShown;
    }

    /// <returns>The installed target fingerprint once the saves were analyzed; null when initialization failed.</returns>
    private async Task<string?> InitializeConversionAsync()
    {
        if (_registry is null)
        {
            ForceCheckBox.IsEnabled = false;
            ShowBanner(
                "The bundled save layout registry could not be read, so saves cannot be analyzed. Reinstall the launcher.");
            return null;
        }

        var profile = MainWindow.Settings.CurrentProfile;
        if (profile is null)
        {
            ForceCheckBox.IsEnabled = false;
            ShowBanner(
                "Select a Diablo II: Resurrected installation profile with the normal Reimagined mod before converting saves. No saves were changed.");
            return null;
        }

        SetBusy(true, "Loading the installed Reimagined TXT files...");
        try
        {
            var installedTarget = await Task.Run(() => SaveConversionService.LoadInstalledTarget(
                profile, Path.Combine(_stagingDirectory, "installed-target")));
            TargetText.Text = installedTarget.Label;
            TargetExplanationText.Text =
                "Uses the currently installed normal/offline Reimagined game data as the conversion target.";
            _service = SaveConversionService.CreateForInstalledTarget(_registry, installedTarget);
            StatusBanner.IsVisible = false;
            await AnalyzeAsync(_service);
            return installedTarget.Fingerprint;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                              or InvalidDataException or InvalidOperationException)
        {
            LaunchDiagnostics.LogException("Could not load the installed Reimagined save data", exception);
            ForceCheckBox.IsEnabled = false;
            ShowBanner(exception.Message);
            UpdateSummary();
            return null;
        }
        finally
        {
            SetBusy(false);
        }
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
            ShowBanner(
                "The offline Reimagined save folder could not be found. Install the normal mod on the Install/Update page, "
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
                ShowBanner(
                    "Diablo II: Resurrected is running. Close the game before converting; files cannot be changed while it runs.");
            }
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>Applies the row's source choice and the Force Conversion setting. Only the affected row's plan changes.</summary>
    private async Task RefreshPlanAsync(SaveConversionService service, SaveConversionRowViewModel row,
        bool? select = null)
    {
        if (row.IsDone)
        {
            return;
        }

        var force = ForceCheckBox.IsChecked == true;
        var targetChoiceSelected = row.IsSourceUncertain && row.SelectedSourceChoice is { Layout: null };
        var plan = row.IsSourceUncertain
            ? targetChoiceSelected
                ? force ? await PlanAsync(service, row, null, true) : row.BasePlan
                : await PlanAsync(service, row, row.SelectedSourceChoice!.Layout, force)
            : force && (row.BasePlan.State == SaveConversionState.AlreadyCompatible ||
                        row.BasePlan is
                            { State: SaveConversionState.Unresolved, ForceOption.Kind: SaveForceKind.Force } ||
                        row.BasePlan.CanAttemptRecovery)
                ? await PlanAsync(service, row, null, true)
                : row.BasePlan;
        if (plan != row.Plan || select is not null)
        {
            row.ReplacePlan(plan, select);
        }
    }

    private async Task<SaveConversionPlan> PlanAsync(SaveConversionService service, SaveConversionRowViewModel row,
        string? source, bool force)
    {
        if (_plans.TryGetValue((row, source, force), out var cached))
        {
            return cached;
        }

        var overrides = new MigrationOverrides(Force: force, SourceLayout: source);
        var path = row.BasePlan.SourcePath;
        var staging = NewStagingDirectory();
        var plan = await Task.Run(() => service.Plan(path, staging, overrides));
        LaunchDiagnostics.Log(
            $"Save conversion analysis for {path} (source {source ?? "identified"}, force {force}): {plan.State} - {plan.Operation}");
        _plans[(row, source, force)] = plan;
        return plan;
    }

    private async void OnForceChanged(object? sender, RoutedEventArgs e)
    {
        if (_isBusy || _service is not { } service)
        {
            return;
        }

        SetBusy(true,
            ForceCheckBox.IsChecked == true
                ? "Preparing experimental conversions..."
                : "Restoring verified conversions...");
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
            await RefreshPlanAsync(service, row, select: row.SelectedSourceChoice?.Layout is not null);
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
                LaunchDiagnostics.Log(
                    $"Save conversion {(outcome.Succeeded ? "succeeded" : "failed")} for {plan.SourcePath}: {outcome.Message}");
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

    private void OnBackupDirectoryClick(object? sender, RoutedEventArgs e)
    {
        var path = _service?.BackupRoot ?? SaveConversionService.DefaultBackupRoot;
        try
        {
            Directory.CreateDirectory(path);
            var startInfo = OperatingSystem.IsWindows()
                ? new ProcessStartInfo("explorer.exe") { UseShellExecute = false }
                : OperatingSystem.IsMacOS()
                    ? new ProcessStartInfo("open") { UseShellExecute = false }
                    : new ProcessStartInfo("xdg-open") { UseShellExecute = false };

            startInfo.ArgumentList.Add(path);
            Process.Start(startInfo);
        }
        catch (Exception exception)
        {
            LaunchDiagnostics.LogException("Could not open the save conversion backup folder", exception);
            ShowBanner($"Could not open the backup folder ({exception.Message}).");
        }
    }

    /// <summary>Keyboard navigation still scrolls the focused control into view; pointer focus does not move the list.</summary>
    private void OnListGotFocus(object? sender, FocusChangedEventArgs e)
    {
        if (e.NavigationMethod is (NavigationMethod.Tab or NavigationMethod.Directional) &&
            e.Source is Control control && control != FilesScrollViewer)
        {
            control.BringIntoView();
        }
    }

    private void UpdateSummary()
    {
        var ready = _rows.Count(row => row.IsReadyToApply);
        var converted = _rows.Count(row => row.IsDone);
        var compatible = _rows.Count(row => !row.IsDone && (row.IsTargetChoiceSelected || row.IsAlreadyCompatible));
        var blocked = _rows.Count(row => !row.IsDone && !row.CanSelect && row.IsProblem);
        SummaryText.Text = _rows.Count == 0
            ? string.Empty
            : $"{_rows.Count} file(s): {ready} selected"
              + (converted > 0 ? $", {converted} converted" : "")
              + $", {compatible} parse(s) with target definitions, {blocked} blocked.";
        ConvertButton.IsEnabled = !_isBusy && ready > 0;
    }

    private void SetBusy(bool isBusy, string? message = null)
    {
        _isBusy = isBusy;
        BusyPanel.IsVisible = isBusy;
        BusyText.Text = message ?? string.Empty;
        CloseButton.IsEnabled = !isBusy;
        FilesItemsControl.IsEnabled = !isBusy;
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