using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using ReimaginedLauncher.Utilities;

namespace ReimaginedLauncher.Views.Launch;

public sealed record LadderCopyDialogState(
    bool IsInstallDirectoryValid,
    bool HasLocalLadder,
    LadderToNormalCopyPlan? Plan,
    LadderCharacterMigrationInspection? CharacterMigration,
    bool CanDownloadOrUpdateLadder,
    bool IsLadderUpdateAvailable,
    string? ActiveLadderSummary,
    string LadderStatusMessage);

public sealed record LadderCopyOperationOutcome(
    LadderToNormalCopyResult ModCopyResult,
    LadderCharacterMigrationResult? CharacterMigrationResult);

public sealed class LadderCopyExtensionRowViewModel(
    LadderCopyExtensionItem item,
    bool initialEnabled,
    Action onSelectionChanged) : INotifyPropertyChanged
{
    private bool _isEnabled = initialEnabled;
    private bool _isInteractive = true;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Key { get; } = item.Key;
    public string Name { get; } = item.Name;
    public string FileName { get; } = item.FileName;
    public D2RLoaderExtensionKind Kind { get; } = item.Kind;
    public string KindBadge { get; } = item.Kind == D2RLoaderExtensionKind.Plugin ? "PLUGIN" : "PATCH";
    public string VersionLabel { get; } = string.IsNullOrWhiteSpace(item.Version) ? string.Empty : $"v{item.Version}";
    public bool HasVersion => !string.IsNullOrWhiteSpace(VersionLabel);
    public string AuthorLabel { get; } = string.IsNullOrWhiteSpace(item.Author) ? string.Empty : item.Author!.Trim();
    public bool HasAuthor => !string.IsNullOrWhiteSpace(AuthorLabel);

    public string DetailText { get; } = BuildDetailText(item);
    public bool DefaultEnabled { get; } = item.DefaultEnabled;
    public bool IsDefaultDisabled => !DefaultEnabled;
    public string DefaultDisabledNote => "Disabled by default for Non-Ladder - copied to d2rloader/launcher-disabled.";
    public string WarningNote { get; } = item.WarningNote ?? string.Empty;
    public bool HasWarningNote => !string.IsNullOrWhiteSpace(WarningNote);

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (_isEnabled == value)
            {
                return;
            }

            _isEnabled = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEnabled)));
            onSelectionChanged();
        }
    }

    public bool IsInteractive
    {
        get => _isInteractive;
        set
        {
            if (_isInteractive == value)
            {
                return;
            }

            _isInteractive = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsInteractive)));
        }
    }

    internal bool MatchesSearch(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return true;
        }

        var trimmed = query.Trim();
        return Name.Contains(trimmed, StringComparison.OrdinalIgnoreCase)
               || FileName.Contains(trimmed, StringComparison.OrdinalIgnoreCase)
               || AuthorLabel.Contains(trimmed, StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildDetailText(LadderCopyExtensionItem item)
    {
        var parts = new List<string> { item.FileName };
        if (!string.IsNullOrWhiteSpace(item.Author))
        {
            parts.Add($"by {item.Author.Trim()}");
        }

        if (item.Kind == D2RLoaderExtensionKind.Patch)
        {
            parts.Add($"{item.PatchCount} patch{(item.PatchCount == 1 ? string.Empty : "es")}");
        }

        if (!string.IsNullOrWhiteSpace(item.Description)
            && !item.Description.Equals(item.Name, StringComparison.OrdinalIgnoreCase))
        {
            parts.Add(item.Description.Trim());
        }

        return string.Join(" · ", parts);
    }
}

public sealed class LadderCharacterRowViewModel : INotifyPropertyChanged
{
    private static readonly IBrush SuccessBrush = new SolidColorBrush(Color.Parse("#5CB85C"));
    private static readonly IBrush GoldBrush = new SolidColorBrush(Color.Parse("#F7E4B8"));
    private static readonly IBrush DangerBrush = new SolidColorBrush(Color.Parse("#E0575F"));
    private static readonly IBrush MutedBrush = new SolidColorBrush(Color.Parse("#BCA77D"));

    private readonly Action _onChanged;
    private bool _isInteractive;
    private bool _isSelected;
    private LadderCharacterCopySnapshot? _selectedCopy;
    private bool _renameInstead;
    private string _renameTargetName;
    private bool _overwriteExisting;
    private string _renameValidationError = string.Empty;

    public LadderCharacterRowViewModel(
        LadderCharacterCandidate candidate,
        Action onChanged)
    {
        Candidate = candidate;
        _onChanged = onChanged;
        _selectedCopy = candidate.Copies.FirstOrDefault();
        _renameTargetName = candidate.SuggestedSafeRename ?? string.Empty;
        _renameInstead = candidate.ExistingConflict is not null && CanRenameOnMigrate;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public LadderCharacterCandidate Candidate { get; }
    public string CharacterKey => Candidate.CharacterKey;
    public string CharacterName => Candidate.CharacterName;
    public bool IsMigratable => Candidate.IsMigratable && Candidate.Copies.Count > 0;
    public string BlockedReason => Candidate.BlockedReason ?? string.Empty;
    public bool HasBlockedReason => !string.IsNullOrWhiteSpace(BlockedReason);
    public IReadOnlyList<LadderCharacterCopySnapshot> Copies => Candidate.Copies;
    public ExistingNonLadderCharacterInfo? ExistingConflict => Candidate.ExistingConflict;
    public bool HasConflict => ExistingConflict is not null;

    public string ConflictWarningText => ExistingConflict is null
        ? string.Empty
        : $"Conflict: Non-Ladder already has {ExistingConflict.DescribeExisting()}. Unchecked for safety.";

    public bool CanCheck => _isInteractive && IsMigratable;
    public bool IsCopySelectorEnabled => _isInteractive && IsMigratable;
    public bool IsResolutionControlsEnabled => _isInteractive && IsMigratable;
    public bool ShowCopySelector => IsMigratable && Copies.Count > 1;
    public bool ShowConflictPanel => IsMigratable && HasConflict && _isSelected;
    public bool CanRenameOnMigrate => HasConflict && _selectedCopy is { SaveVersion: >= 104 };
    public bool ShowRenameUnsupportedNote => HasConflict && !CanRenameOnMigrate;
    public bool CanOverwriteExisting =>
        HasConflict
        && ExistingConflict?.IsFilenameMatch == true
        && !string.IsNullOrWhiteSpace(ExistingConflict.Sha256);
    public bool IsOverwriteOptionEnabled => _isInteractive && CanOverwriteExisting;
    public bool ShowFilenameMismatchOverwriteBlock => HasConflict && ExistingConflict?.IsFilenameMatch == false;
    public bool IsHardcore => _selectedCopy?.IsHardcore ?? ExistingConflict?.IsHardcore ?? false;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            var coerced = value && IsMigratable;
            if (_isSelected == coerced)
            {
                return;
            }

            _isSelected = coerced;
            if (_isSelected && HasConflict && CanRenameOnMigrate && !_renameInstead && !_overwriteExisting)
            {
                _renameInstead = true;
                if (string.IsNullOrWhiteSpace(_renameTargetName))
                {
                    _renameTargetName = Candidate.SuggestedSafeRename ?? string.Empty;
                }

                OnPropertyChanged(nameof(RenameInstead));
                OnPropertyChanged(nameof(RenameTargetName));
            }

            OnPropertyChanged(nameof(IsSelected));
            OnPropertyChanged(nameof(ShowConflictPanel));
            OnPropertyChanged(nameof(StatusBadge));
            OnPropertyChanged(nameof(StatusForeground));
            _onChanged();
        }
    }

    public LadderCharacterCopySnapshot? SelectedCopy
    {
        get => _selectedCopy;
        set
        {
            if (value is null || ReferenceEquals(_selectedCopy, value))
            {
                return;
            }

            _selectedCopy = value;
            if (!CanRenameOnMigrate && _renameInstead)
            {
                _renameInstead = false;
                OnPropertyChanged(nameof(RenameInstead));
            }

            OnPropertyChanged(nameof(SelectedCopy));
            OnPropertyChanged(nameof(EffectiveTargetName));
            OnPropertyChanged(nameof(IsHardcore));
            OnPropertyChanged(nameof(DetailText));
            OnPropertyChanged(nameof(CanRenameOnMigrate));
            OnPropertyChanged(nameof(ShowRenameUnsupportedNote));
            OnPropertyChanged(nameof(StatusBadge));
            OnPropertyChanged(nameof(StatusForeground));
            _onChanged();
        }
    }

    public bool RenameInstead
    {
        get => _renameInstead;
        set
        {
            var coerced = value && CanRenameOnMigrate;
            if (_renameInstead == coerced)
            {
                return;
            }

            _renameInstead = coerced;
            if (_renameInstead && _overwriteExisting)
            {
                _overwriteExisting = false;
                OnPropertyChanged(nameof(OverwriteExisting));
            }

            OnPropertyChanged(nameof(RenameInstead));
            OnPropertyChanged(nameof(StatusBadge));
            OnPropertyChanged(nameof(StatusForeground));
            _onChanged();
        }
    }

    public string RenameTargetName
    {
        get => _renameTargetName;
        set
        {
            var normalized = value ?? string.Empty;
            if (string.Equals(_renameTargetName, normalized, StringComparison.Ordinal))
            {
                return;
            }

            _renameTargetName = normalized;
            OnPropertyChanged(nameof(RenameTargetName));
            _onChanged();
        }
    }

    public bool OverwriteExisting
    {
        get => _overwriteExisting;
        set
        {
            var coerced = value && CanOverwriteExisting;
            if (_overwriteExisting == coerced)
            {
                return;
            }

            _overwriteExisting = coerced;
            if (_overwriteExisting && _renameInstead)
            {
                _renameInstead = false;
                OnPropertyChanged(nameof(RenameInstead));
            }

            OnPropertyChanged(nameof(OverwriteExisting));
            OnPropertyChanged(nameof(StatusBadge));
            OnPropertyChanged(nameof(StatusForeground));
            _onChanged();
        }
    }

    public string RenameValidationError
    {
        get => _renameValidationError;
        private set
        {
            if (string.Equals(_renameValidationError, value, StringComparison.Ordinal))
            {
                return;
            }

            _renameValidationError = value;
            OnPropertyChanged(nameof(RenameValidationError));
            OnPropertyChanged(nameof(HasRenameValidationError));
        }
    }

    public bool HasRenameValidationError => !string.IsNullOrWhiteSpace(_renameValidationError);

    public string DetailText
    {
        get
        {
            if (_selectedCopy is null)
            {
                return HasBlockedReason ? "No playable gameplay save found." : string.Empty;
            }

            var parts = new List<string>
            {
                $"Lv {_selectedCopy.Level} {_selectedCopy.ClassName}",
                _selectedCopy.OriginLabel
            };
            if (_selectedCopy.HasDied)
            {
                parts.Add("Has died");
            }

            if (!string.IsNullOrWhiteSpace(_selectedCopy.PairedBindPath))
            {
                parts.Add("+ summon state (.bind)");
            }

            if (_selectedCopy.CompanionPaths.Count > 0)
            {
                parts.Add($"+ {_selectedCopy.CompanionPaths.Count} bind/ctrl file(s)");
            }

            return string.Join(" · ", parts);
        }
    }

    public string StatusBadge
    {
        get
        {
            if (!IsMigratable)
            {
                return "BLOCKED";
            }

            if (!HasConflict)
            {
                return "NEW";
            }

            if (_isSelected && _overwriteExisting)
            {
                return "OVERWRITE";
            }

            if (_isSelected && _renameInstead)
            {
                return "RENAMED";
            }

            return "CONFLICT";
        }
    }

    public IBrush StatusForeground
    {
        get
        {
            if (!IsMigratable)
            {
                return DangerBrush;
            }

            if (!HasConflict)
            {
                return SuccessBrush;
            }

            if (_isSelected && _overwriteExisting)
            {
                return DangerBrush;
            }

            if (_isSelected && _renameInstead)
            {
                return GoldBrush;
            }

            return Copies.Count > 0 ? DangerBrush : MutedBrush;
        }
    }

    public string EffectiveTargetName =>
        HasConflict && _renameInstead
            ? _renameTargetName.Trim()
            : _selectedCopy?.CharacterName ?? CharacterName;

    public bool IsReplacingExisting =>
        _isSelected && HasConflict && !_renameInstead && _overwriteExisting && CanOverwriteExisting;

    internal void SetInteractive(bool interactive)
    {
        if (_isInteractive == interactive)
        {
            return;
        }

        _isInteractive = interactive;
        OnPropertyChanged(nameof(CanCheck));
        OnPropertyChanged(nameof(IsCopySelectorEnabled));
        OnPropertyChanged(nameof(IsResolutionControlsEnabled));
        OnPropertyChanged(nameof(IsOverwriteOptionEnabled));
    }

    internal void SetValidationError(string? error)
    {
        RenameValidationError = error ?? string.Empty;
    }

    internal void RestoreState(
        bool isSelected,
        string? selectedCopySha256,
        bool renameInstead,
        string? renameTargetName,
        bool overwriteExisting)
    {
        if (!string.IsNullOrWhiteSpace(selectedCopySha256))
        {
            var matchingCopy = Copies.FirstOrDefault(copy =>
                string.Equals(copy.SourceSha256, selectedCopySha256, StringComparison.OrdinalIgnoreCase));
            if (matchingCopy is not null)
            {
                _selectedCopy = matchingCopy;
            }
        }

        if (HasConflict)
        {
            _renameInstead = renameInstead && CanRenameOnMigrate;
            _overwriteExisting = !_renameInstead && overwriteExisting && CanOverwriteExisting;
            if (!string.IsNullOrWhiteSpace(renameTargetName))
            {
                _renameTargetName = renameTargetName;
            }
        }
        else
        {
            _renameInstead = false;
            _overwriteExisting = false;
        }

        _isSelected = isSelected && IsMigratable;

        OnPropertyChanged(nameof(SelectedCopy));
        OnPropertyChanged(nameof(IsHardcore));
        OnPropertyChanged(nameof(DetailText));
        OnPropertyChanged(nameof(CanRenameOnMigrate));
        OnPropertyChanged(nameof(ShowRenameUnsupportedNote));
        OnPropertyChanged(nameof(RenameInstead));
        OnPropertyChanged(nameof(RenameTargetName));
        OnPropertyChanged(nameof(OverwriteExisting));
        OnPropertyChanged(nameof(IsSelected));
        OnPropertyChanged(nameof(ShowConflictPanel));
        OnPropertyChanged(nameof(StatusBadge));
        OnPropertyChanged(nameof(StatusForeground));
    }

    private void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class LadderSharedStashRowViewModel(
    LadderSharedStashOption option,
    Action onChanged) : INotifyPropertyChanged
{
    private static readonly IBrush SuccessBrush = new SolidColorBrush(Color.Parse("#5CB85C"));
    private static readonly IBrush DangerBrush = new SolidColorBrush(Color.Parse("#E0575F"));

    private bool _isInteractive;
    private bool _isSelected;

    public event PropertyChangedEventHandler? PropertyChanged;

    public LadderSharedStashOption Option { get; } = option;
    public string FileName => Option.FileName;
    public bool ExistsInDestination => Option.ExistsInDestination;
    public string ModeBadge => Option.IsHardcore ? "HARDCORE" : "SOFTCORE";
    public string DetailText =>
        $"{Option.OriginLabel} · {Option.TabCount} tab{(Option.TabCount == 1 ? string.Empty : "s")} · {FormatBytes(Option.FileSizeBytes)}";

    public bool CanCheck => _isInteractive && !ExistsInDestination;

    public string StatusText => ExistsInDestination
        ? "Blocked - Non-Ladder already has a shared stash for this mode (never overwritten)."
        : "Optional - No Non-Ladder shared stash exists yet for this mode.";

    public IBrush StatusForeground => ExistsInDestination ? DangerBrush : SuccessBrush;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            var coerced = value && !ExistsInDestination;
            if (_isSelected == coerced)
            {
                return;
            }

            _isSelected = coerced;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            onChanged();
        }
    }

    internal void SetInteractive(bool interactive)
    {
        if (_isInteractive == interactive)
        {
            return;
        }

        _isInteractive = interactive;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanCheck)));
    }

    internal void RestoreSelected(bool selected)
    {
        var coerced = selected && !ExistsInDestination;
        if (_isSelected == coerced)
        {
            return;
        }

        _isSelected = coerced;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
    }

    private static string FormatBytes(int bytes) =>
        bytes < 1024 ? $"{bytes} B" : $"{bytes / 1024.0:0.#} KB";
}

public partial class CopyLadderToNonLadderWindow : Window
{
    private readonly Func<CancellationToken, Task<LadderCopyDialogState>>? _inspectStateAsync;
    private readonly Func<IProgress<LadderBundleProgress>, CancellationToken, Task<string?>>? _downloadOrUpdateLadderAsync;
    private readonly Func<
        IReadOnlySet<string>,
        bool,
        LadderCharacterMigrationRequest?,
        IProgress<string>,
        CancellationToken,
        Task<LadderCopyOperationOutcome>>? _copyAsync;
    private readonly Func<bool>? _isGameRunning;
    private readonly Func<bool>? _isStep1Completed;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly DispatcherTimer _gameCheckTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    private readonly List<LadderCopyExtensionRowViewModel> _allRows = [];
    private readonly ObservableCollection<LadderCopyExtensionRowViewModel> _visibleRows = [];
    private readonly ObservableCollection<LadderSaveSourceFolder> _migrationSources = [];
    private readonly ObservableCollection<LadderCharacterRowViewModel> _characterRows = [];
    private readonly ObservableCollection<LadderSharedStashRowViewModel> _stashRows = [];

    private readonly Dictionary<string, (bool IsSelected, string? SelectedCopySha, bool RenameInstead, string? RenameTargetName, bool OverwriteExisting)>
        _savedCharacterSelections = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _savedStashSelections = new(StringComparer.OrdinalIgnoreCase);

    private LadderCopyDialogState? _state;
    private bool _isBusy;
    private bool _isConfirmingCopy;
    private bool _keepConfigsInitialized;
    private bool _suppressMigrationEvents;
    private string _searchQuery = string.Empty;
    private string? _selectedMigrationSourcePath;
    private string _lastOverwriteSignature = string.Empty;
    private int _operationSequence;
    private int _activeOperationId;
    private int _inspectionGeneration;
    private CancellationTokenSource? _inspectionCancellation;

    public CopyLadderToNonLadderWindow()
    {
        InitializeComponent();
        ExtensionsItemsControl.ItemsSource = _visibleRows;
        LadderSourceComboBox.ItemsSource = _migrationSources;
        CharactersItemsControl.ItemsSource = _characterRows;
        SharedStashesItemsControl.ItemsSource = _stashRows;
    }

    internal CopyLadderToNonLadderWindow(
        LadderCopyDialogState initialState,
        Func<CancellationToken, Task<LadderCopyDialogState>> inspectStateAsync,
        Func<IProgress<LadderBundleProgress>, CancellationToken, Task<string?>> downloadOrUpdateLadderAsync,
        Func<
            IReadOnlySet<string>,
            bool,
            LadderCharacterMigrationRequest?,
            IProgress<string>,
            CancellationToken,
            Task<LadderCopyOperationOutcome>> copyAsync,
        Func<bool> isGameRunning,
        Func<bool>? isStep1Completed = null)
        : this()
    {
        _inspectStateAsync = inspectStateAsync;
        _downloadOrUpdateLadderAsync = downloadOrUpdateLadderAsync;
        _copyAsync = copyAsync;
        _isGameRunning = isGameRunning;
        _isStep1Completed = isStep1Completed;

        _gameCheckTimer.Tick += (_, _) => UpdateActionAvailability();
        Opened += async (_, _) =>
        {
            _gameCheckTimer.Start();
            await RefreshDialogStateAsync(allowWhileBusy: false);
        };
        Closing += (_, args) => args.Cancel = _isBusy;
        Closed += (_, _) =>
        {
            _gameCheckTimer.Stop();
            CancelPendingInspection();
            _lifetimeCancellation.Cancel();
            _lifetimeCancellation.Dispose();
        };

        ApplyState(initialState, preservedSelections: null);
    }

    public LadderCopyOperationOutcome? OperationOutcome { get; private set; }

    private void CancelPendingInspection()
    {
        _inspectionGeneration++;
        var previous = Interlocked.Exchange(ref _inspectionCancellation, null);
        if (previous is not null)
        {
            previous.Cancel();
            previous.Dispose();
        }
    }

    private async Task RefreshDialogStateAsync(
        IReadOnlyDictionary<string, bool>? preservedSelections = null,
        bool allowWhileBusy = false)
    {
        if (_inspectStateAsync is null
            || _lifetimeCancellation.IsCancellationRequested
            || (!allowWhileBusy && (_isBusy || _isConfirmingCopy)))
        {
            return;
        }

        CancelPendingInspection();
        var generation = _inspectionGeneration;
        var inspectionCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
        _inspectionCancellation = inspectionCts;

        try
        {
            var nextState = await _inspectStateAsync(inspectionCts.Token);
            if (_lifetimeCancellation.IsCancellationRequested
                || inspectionCts.IsCancellationRequested
                || generation != _inspectionGeneration
                || (!allowWhileBusy && (_isBusy || _isConfirmingCopy)))
            {
                return;
            }

            var effectivePreserved = preservedSelections
                ?? (_allRows.Count > 0
                    ? _allRows.ToDictionary(row => row.Key, row => row.IsEnabled, StringComparer.OrdinalIgnoreCase)
                    : null);
            ApplyState(nextState, effectivePreserved);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (_lifetimeCancellation.IsCancellationRequested
                || inspectionCts.IsCancellationRequested
                || generation != _inspectionGeneration
                || (!allowWhileBusy && (_isBusy || _isConfirmingCopy)))
            {
                return;
            }

            LaunchDiagnostics.LogException("Could not inspect Ladder installation for Copy to Non-Ladder", ex);
            ShowBanner($"Could not check Ladder state: {ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(Interlocked.CompareExchange(ref _inspectionCancellation, null, inspectionCts), inspectionCts))
            {
                inspectionCts.Dispose();
            }
        }
    }

    private void ApplyState(
        LadderCopyDialogState state,
        IReadOnlyDictionary<string, bool>? preservedSelections)
    {
        CaptureMigrationSelections();

        _state = state;
        var hasPlan = state is { IsInstallDirectoryValid: true, HasLocalLadder: true, Plan: not null };

        SetupRequiredPanel.IsVisible = !hasPlan;
        StepsScrollViewer.IsVisible = hasPlan;
        CopyButton.IsVisible = hasPlan;

        if (!hasPlan)
        {
            if (!state.IsInstallDirectoryValid)
            {
                SetupHeadlineText.Text = "Diablo II: Resurrected install path is not validated";
                SetupMessageText.Text =
                    "Install the ladder through the ladder play mode button after setting a valid Diablo II: Resurrected folder on the Launch tab.";
                SetupLadderStatusText.IsVisible = false;
                SetupDownloadButton.IsVisible = false;
            }
            else
            {
                SetupHeadlineText.Text = "ReimaginedLadder is not installed yet";
                SetupMessageText.Text = state.CanDownloadOrUpdateLadder
                    ? "Install the ladder through the ladder play mode button, or download it directly below."
                    : "Install the ladder through the ladder play mode button.";
                var statusLine = string.IsNullOrWhiteSpace(state.ActiveLadderSummary)
                    ? state.LadderStatusMessage
                    : string.IsNullOrWhiteSpace(state.LadderStatusMessage)
                        ? state.ActiveLadderSummary
                        : $"{state.ActiveLadderSummary} - {state.LadderStatusMessage}";
                SetupLadderStatusText.Text = statusLine;
                SetupLadderStatusText.IsVisible = !string.IsNullOrWhiteSpace(statusLine);
                SetupDownloadButton.IsVisible = state.CanDownloadOrUpdateLadder;
            }

            _allRows.Clear();
            _visibleRows.Clear();
            DefaultWarningsBanner.IsVisible = false;
            ClearMigrationUi();
            UpdateActionAvailability();
            return;
        }

        var plan = state.Plan!;
        LadderUpdateBanner.IsVisible = state.IsLadderUpdateAvailable;
        LadderUpdateBannerText.Text = string.IsNullOrWhiteSpace(state.LadderStatusMessage)
            ? $"A Ladder package update is available ({state.ActiveLadderSummary})."
            : state.LadderStatusMessage;

        _allRows.Clear();
        foreach (var item in plan.Extensions)
        {
            var isEnabled = preservedSelections is not null && preservedSelections.TryGetValue(item.Key, out var saved)
                ? saved
                : item.DefaultEnabled;
            _allRows.Add(new LadderCopyExtensionRowViewModel(item, isEnabled, OnRowSelectionChanged));
        }

        UpdateDefaultWarningsBanner();
        ApplySearchFilter();

        if (!_keepConfigsInitialized)
        {
            KeepConfigsCheckBox.IsChecked = true;
            _keepConfigsInitialized = true;
        }

        KeepConfigsHelpText.Text = plan.HasExistingNormalConfigs
            ? "Checked: keep existing Non-Ladder d2rloader/config files. Unchecked: replace with Ladder plugin configs."
            : "No existing Non-Ladder d2rloader/config files were found; Ladder plugin configs will be copied.";

        ApplyMigrationInspection(state.CharacterMigration);
        UpdateActionAvailability();
    }

    private void ClearMigrationUi()
    {
        _suppressMigrationEvents = true;
        try
        {
            _migrationSources.Clear();
            _characterRows.Clear();
            _stashRows.Clear();
            MigrationDetailsPanel.IsVisible = false;
            MigrationUnavailableText.IsVisible = false;
            MigrationHeaderSummaryText.Text = string.Empty;
            OverwriteDangerBanner.IsVisible = false;
        }
        finally
        {
            _suppressMigrationEvents = false;
        }
    }

    private void CaptureMigrationSelections()
    {
        if (LadderSourceComboBox.SelectedItem is LadderSaveSourceFolder currentSource)
        {
            _selectedMigrationSourcePath = currentSource.FullPath;
        }

        if (_characterRows.Count == 0 && _stashRows.Count == 0)
        {
            return;
        }

        _savedCharacterSelections.Clear();
        foreach (var row in _characterRows)
        {
            _savedCharacterSelections[row.CharacterKey] = (
                row.IsSelected,
                row.SelectedCopy?.SourceSha256,
                row.RenameInstead,
                row.RenameTargetName,
                row.OverwriteExisting);
        }

        _savedStashSelections.Clear();
        foreach (var stash in _stashRows.Where(item => item.IsSelected))
        {
            _savedStashSelections.Add(stash.FileName);
        }
    }

    private void ApplyMigrationInspection(LadderCharacterMigrationInspection? inspection)
    {
        _suppressMigrationEvents = true;
        try
        {
            _migrationSources.Clear();
            if (inspection is null || inspection.Sources.Count == 0)
            {
                _characterRows.Clear();
                _stashRows.Clear();
                MigrationDetailsPanel.IsVisible = false;
                MigrationUnavailableText.Text = inspection?.UnavailableReason ?? "No local Ladder save folders were found.";
                MigrationUnavailableText.IsVisible = true;
                EnableMigrationCheckBox.IsChecked = false;
                EnableMigrationCheckBox.IsEnabled = false;
                UpdateMigrationSummaryAndValidation();
                return;
            }

            foreach (var source in inspection.Sources)
            {
                _migrationSources.Add(source);
            }

            var hasAnyItems = inspection.Sources.Any(source => source.Characters.Count > 0 || source.SharedStashes.Count > 0);
            MigrationUnavailableText.Text = inspection.UnavailableReason ?? string.Empty;
            MigrationUnavailableText.IsVisible = !hasAnyItems && !string.IsNullOrWhiteSpace(inspection.UnavailableReason);
            EnableMigrationCheckBox.IsEnabled = !_isBusy && hasAnyItems;
            if (!hasAnyItems)
            {
                EnableMigrationCheckBox.IsChecked = false;
            }

            MigrationDetailsPanel.IsVisible = hasAnyItems;
            var backupPathNote = string.IsNullOrWhiteSpace(inspection.BackupRootDirectory)
                ? string.Empty
                : $" Backups: {inspection.BackupRootDirectory}";
            MigrationTargetText.Text =
                $"Target Non-Ladder save folder: {inspection.TargetSavePath} ({inspection.ExistingTargetCharacterCount} existing character(s)).{backupPathNote}";

            var previousSourcePath = _selectedMigrationSourcePath;
            var selected = _migrationSources.FirstOrDefault(source =>
                               !string.IsNullOrWhiteSpace(previousSourcePath)
                               && string.Equals(source.FullPath, previousSourcePath, StringComparison.OrdinalIgnoreCase))
                           ?? _migrationSources.FirstOrDefault();
            if (!string.Equals(previousSourcePath, selected?.FullPath, StringComparison.OrdinalIgnoreCase))
            {
                _savedCharacterSelections.Clear();
                _savedStashSelections.Clear();
                ConfirmOverwriteCheckBox.IsChecked = false;
                _lastOverwriteSignature = string.Empty;
            }

            LadderSourceComboBox.SelectedItem = selected;
            PopulateMigrationRowsForSource(selected);
        }
        finally
        {
            _suppressMigrationEvents = false;
        }

        UpdateMigrationSummaryAndValidation();
    }

    private void PopulateMigrationRowsForSource(LadderSaveSourceFolder? source)
    {
        _characterRows.Clear();
        _stashRows.Clear();

        if (source is null)
        {
            SharedStashSection.IsVisible = false;
            return;
        }

        _selectedMigrationSourcePath = source.FullPath;

        foreach (var candidate in source.Characters)
        {
            var row = new LadderCharacterRowViewModel(candidate, OnMigrationRowChanged);
            if (_savedCharacterSelections.TryGetValue(candidate.CharacterKey, out var saved))
            {
                row.RestoreState(
                    saved.IsSelected,
                    saved.SelectedCopySha,
                    saved.RenameInstead,
                    saved.RenameTargetName,
                    saved.OverwriteExisting);
            }

            _characterRows.Add(row);
        }

        foreach (var stash in source.SharedStashes)
        {
            var stashRow = new LadderSharedStashRowViewModel(stash, OnMigrationRowChanged);
            if (_savedStashSelections.Contains(stash.FileName))
            {
                stashRow.RestoreSelected(true);
            }

            _stashRows.Add(stashRow);
        }

        SharedStashSection.IsVisible = _stashRows.Count > 0;
    }

    private void OnMigrationRowChanged()
    {
        if (_suppressMigrationEvents)
        {
            return;
        }

        UpdateMigrationSummaryAndValidation();
        UpdateActionAvailability();
    }

    private void UpdateMigrationSummaryAndValidation()
    {
        var inspection = _state?.CharacterMigration;
        var migrationEnabled = EnableMigrationCheckBox.IsChecked == true;
        var validationError = RevalidateCharacterSelections(inspection, migrationEnabled);

        var selectedCharacters = _characterRows.Where(row => row.IsSelected).ToArray();
        var selectedStashes = _stashRows.Where(row => row.IsSelected).ToArray();
        var conflictCount = _characterRows.Count(row => row.HasConflict);
        var migratableCount = _characterRows.Count(row => row.IsMigratable);
        var overwriteRows = selectedCharacters.Where(row => row.IsReplacingExisting).ToArray();

        var currentSignature = string.Join(
            ";",
            overwriteRows.Select(row =>
                    $"{_selectedMigrationSourcePath}|{row.CharacterKey}|{row.SelectedCopy?.SourceSha256}|{row.ExistingConflict?.Sha256}|{row.EffectiveTargetName}")
                .OrderBy(item => item, StringComparer.OrdinalIgnoreCase));
        if (!string.Equals(currentSignature, _lastOverwriteSignature, StringComparison.Ordinal))
        {
            _lastOverwriteSignature = currentSignature;
            if (overwriteRows.Length > 0)
            {
                ConfirmOverwriteCheckBox.IsChecked = false;
            }
        }

        if (overwriteRows.Length > 0 && migrationEnabled)
        {
            OverwriteDangerBanner.IsVisible = true;
            OverwriteDangerListText.Text =
                "WARNING — the following existing Non-Ladder character(s) will be REPLACED:"
                + Environment.NewLine
                + string.Join(
                    Environment.NewLine,
                    overwriteRows.Select(row =>
                        $"• {row.CharacterName}: Non-Ladder {row.ExistingConflict?.DescribeExisting()} <- Ladder Lv {row.SelectedCopy?.Level} {row.SelectedCopy?.ClassName}"));
        }
        else
        {
            OverwriteDangerBanner.IsVisible = false;
            OverwriteDangerListText.Text = string.Empty;
        }

        if (inspection is null || inspection.Sources.Count == 0)
        {
            MigrationHeaderSummaryText.Text = "No Ladder saves found";
        }
        else if (!migrationEnabled)
        {
            MigrationHeaderSummaryText.Text = conflictCount > 0
                ? $"Skipped · {migratableCount} playable ({conflictCount} conflict{(conflictCount == 1 ? string.Empty : "s")})"
                : $"Skipped · {migratableCount} playable";
        }
        else
        {
            MigrationHeaderSummaryText.Text =
                $"{selectedCharacters.Length} char(s){(selectedStashes.Length > 0 ? $" + {selectedStashes.Length} stash" : string.Empty)} selected";
        }

        MigrationSelectionSummaryText.Text =
            $"{selectedCharacters.Length} of {migratableCount} playable character(s) selected · {conflictCount} name conflict(s) · {selectedStashes.Length} shared stash selected";

        if (migrationEnabled && !string.IsNullOrWhiteSpace(validationError))
        {
            MigrationValidationText.Text = validationError;
            MigrationValidationText.IsVisible = true;
        }
        else
        {
            MigrationValidationText.Text = string.Empty;
            MigrationValidationText.IsVisible = false;
        }
    }

    private string? RevalidateCharacterSelections(
        LadderCharacterMigrationInspection? inspection,
        bool migrationEnabled)
    {
        var reservedTargetNames = new HashSet<string>(
            inspection?.ReservedTargetNames ?? [],
            StringComparer.OrdinalIgnoreCase);

        var selectedRows = _characterRows.Where(row => row.IsSelected).ToArray();
        var duplicateTargets = selectedRows
            .GroupBy(row => row.EffectiveTargetName, StringComparer.OrdinalIgnoreCase)
            .Where(group => !string.IsNullOrWhiteSpace(group.Key) && group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        string? firstBlockingMessage = null;

        foreach (var row in _characterRows)
        {
            if (!row.IsSelected || !row.IsMigratable)
            {
                row.SetValidationError(null);
                continue;
            }

            string? rowError = null;
            if (row.HasConflict)
            {
                if (row.RenameInstead)
                {
                    var proposed = row.RenameTargetName.Trim();
                    var sourceName = row.SelectedCopy?.CharacterName ?? row.CharacterName;
                    if (!row.CanRenameOnMigrate)
                    {
                        rowError = "Renaming is only supported for v104+ character saves.";
                    }
                    else if (string.Equals(proposed, sourceName, StringComparison.OrdinalIgnoreCase))
                    {
                        rowError = "Enter a different character name from the existing Non-Ladder character.";
                    }
                    else if (SaveConversionService.ValidateCharacterName(proposed) is { } nameProblem)
                    {
                        rowError = nameProblem;
                    }
                    else if (LadderCharacterMigrationService.IsReservedWindowsName(proposed))
                    {
                        rowError = $"'{proposed}' is a reserved system filename.";
                    }
                    else if (reservedTargetNames.Contains(proposed))
                    {
                        rowError = $"'{proposed}' already exists in your Non-Ladder save folder.";
                    }
                    else if (duplicateTargets.Contains(proposed))
                    {
                        rowError = $"Another selected Ladder character is also targeting '{proposed}'.";
                    }
                }
                else if (row.OverwriteExisting)
                {
                    if (!row.CanOverwriteExisting)
                    {
                        rowError = "Cannot overwrite because a differently named save file uses this name.";
                    }
                }
                else
                {
                    rowError = "Choose a new name or select 'Overwrite existing Non-Ladder character instead'.";
                }
            }
            else if (duplicateTargets.Contains(row.EffectiveTargetName))
            {
                rowError = $"Another selected character is also named '{row.EffectiveTargetName}'.";
            }

            row.SetValidationError(rowError);
            if (firstBlockingMessage is null && rowError is not null)
            {
                firstBlockingMessage = $"{row.CharacterName}: {rowError}";
            }
        }

        if (!migrationEnabled)
        {
            return null;
        }

        if (firstBlockingMessage is not null)
        {
            return firstBlockingMessage;
        }

        var selectedStashes = _stashRows.Count(row => row.IsSelected);
        if (selectedRows.Length == 0 && selectedStashes == 0)
        {
            return "Select at least one character or shared stash to migrate, or uncheck Step 2 above to skip character migration.";
        }

        if (selectedRows.Any(row => row.IsReplacingExisting) && ConfirmOverwriteCheckBox.IsChecked != true)
        {
            return "Confirm the overwrite warning checkbox below before replacing existing Non-Ladder characters.";
        }

        return null;
    }

    private void UpdateDefaultWarningsBanner()
    {
        var warningNames = _allRows
            .Where(row => row.HasWarningNote && row.IsEnabled)
            .Select(row => $"{row.Name} ({row.WarningNote})")
            .ToArray();

        if (warningNames.Length == 0)
        {
            DefaultWarningsBanner.IsVisible = false;
            DefaultWarningsText.Text = string.Empty;
            return;
        }

        DefaultWarningsBanner.IsVisible = true;
        DefaultWarningsText.Text =
            "Warning - active by default in Non-Ladder unless you uncheck them below:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, warningNames.Select(line => $"• {line}"));
    }

    private void ApplySearchFilter()
    {
        _visibleRows.Clear();
        foreach (var row in _allRows.Where(item => item.MatchesSearch(_searchQuery)))
        {
            _visibleRows.Add(row);
        }

        UpdateSelectionSummary();
    }

    private void OnRowSelectionChanged()
    {
        UpdateDefaultWarningsBanner();
        UpdateSelectionSummary();
    }

    private void UpdateSelectionSummary()
    {
        if (_state?.Plan is null)
        {
            SelectionSummaryText.Text = string.Empty;
            PluginsHeaderSummaryText.Text = string.Empty;
            return;
        }

        var enabledCount = _allRows.Count(row => row.IsEnabled);
        var disabledCount = _allRows.Count - enabledCount;
        var visibleNote = _visibleRows.Count == _allRows.Count
            ? string.Empty
            : $" · Showing {_visibleRows.Count} of {_allRows.Count}";
        var appliedSuffix = _isStep1Completed?.Invoke() == true ? " · Applied" : string.Empty;
        SelectionSummaryText.Text =
            $"{enabledCount} enabled · {disabledCount} copied disabled · Save folder: {_state.Plan.TargetSavePath}{visibleNote}{appliedSuffix}";
        PluginsHeaderSummaryText.Text = $"{enabledCount} enabled · {disabledCount} disabled{appliedSuffix}";
    }

    private void UpdateActionAvailability()
    {
        var gameRunning = _isGameRunning?.Invoke() == true;
        var step1Completed = _isStep1Completed?.Invoke() == true;
        var hasPlan = _state is { IsInstallDirectoryValid: true, HasLocalLadder: true, Plan: not null };
        var canDownload = !_isBusy && !step1Completed && !gameRunning && _state?.CanDownloadOrUpdateLadder == true;

        var migrationEnabled = EnableMigrationCheckBox.IsChecked == true;
        var hasMigrationItems = _migrationSources.Any(source => source.Characters.Count > 0 || source.SharedStashes.Count > 0);
        var migrationError = RevalidateCharacterSelections(_state?.CharacterMigration, migrationEnabled);

        CloseButton.IsEnabled = !_isBusy;
        SearchTextBox.IsEnabled = !_isBusy;
        SelectAllButton.IsEnabled = !_isBusy && !step1Completed && _allRows.Count > 0;
        DeselectAllButton.IsEnabled = !_isBusy && !step1Completed && _allRows.Count > 0;
        ResetDefaultsButton.IsEnabled = !_isBusy && !step1Completed && _allRows.Count > 0;
        KeepConfigsCheckBox.IsEnabled = !_isBusy && !step1Completed && _state?.Plan?.HasExistingNormalConfigs == true;
        foreach (var row in _allRows)
        {
            row.IsInteractive = !_isBusy && !step1Completed;
        }

        EnableMigrationCheckBox.IsEnabled = !_isBusy && hasMigrationItems;
        var migrationInteractive = !_isBusy && migrationEnabled && hasMigrationItems;
        MigrationDetailsPanel.IsEnabled = !_isBusy;
        LadderSourceComboBox.IsEnabled = !_isBusy && _migrationSources.Count > 0;
        SelectSafeCharactersButton.IsEnabled = migrationInteractive && _characterRows.Any(row => row.IsMigratable && !row.HasConflict);
        DeselectAllCharactersButton.IsEnabled = migrationInteractive && (_characterRows.Any(row => row.IsSelected) || _stashRows.Any(row => row.IsSelected));
        ConfirmOverwriteCheckBox.IsEnabled = migrationInteractive;
        foreach (var charRow in _characterRows)
        {
            charRow.SetInteractive(migrationInteractive);
        }

        foreach (var stashRow in _stashRows)
        {
            stashRow.SetInteractive(migrationInteractive);
        }

        var selectedCharCount = migrationEnabled ? _characterRows.Count(row => row.IsSelected) : 0;
        var selectedStashCount = migrationEnabled ? _stashRows.Count(row => row.IsSelected) : 0;
        if (step1Completed)
        {
            CopyButton.Content = selectedCharCount > 0
                ? $"Migrate {selectedCharCount} Character{(selectedCharCount == 1 ? string.Empty : "s")}"
                : selectedStashCount > 0
                    ? "Migrate Stash"
                    : "Finish";
        }
        else if (selectedCharCount > 0)
        {
            CopyButton.Content = $"Copy & Migrate {selectedCharCount} Character{(selectedCharCount == 1 ? string.Empty : "s")}";
        }
        else if (selectedStashCount > 0)
        {
            CopyButton.Content = "Copy & Migrate Stash";
        }
        else
        {
            CopyButton.Content = "Copy to Non-Ladder";
        }

        var canExecuteCopy = !_isBusy
                             && !gameRunning
                             && hasPlan
                             && (!migrationEnabled || string.IsNullOrWhiteSpace(migrationError));

        SetupDownloadButton.IsEnabled = canDownload;
        BannerUpdateButton.IsEnabled = canDownload;
        CopyButton.IsEnabled = canExecuteCopy;

        if (_isBusy)
        {
            FooterStatusText.Text = "Please wait for the active operation to complete.";
        }
        else if (gameRunning)
        {
            FooterStatusText.Text = "Close D2R and D2RLoader before copying, migrating saves, or updating Ladder.";
        }
        else if (migrationEnabled && !string.IsNullOrWhiteSpace(migrationError))
        {
            FooterStatusText.Text = migrationError;
        }
        else if (step1Completed)
        {
            FooterStatusText.Text = migrationEnabled
                ? $"mods/Reimagined was updated; retrying will apply Step 2 only and back up saves in {(_state?.Plan?.TargetSavePath ?? LadderToNormalCopyService.DefaultSavePath)} first."
                : "mods/Reimagined has been copied. Select characters in Step 2 or click Finish to close.";
        }
        else if (hasPlan && _state?.Plan is { } plan)
        {
            FooterStatusText.Text = migrationEnabled
                ? $"Non-Ladder mod and saves in {plan.TargetSavePath} will both be backed up first."
                : plan.HasExistingNormalMod
                    ? "Existing mods/Reimagined will be backed up first (keeping the newest 2 backups)."
                    : "mods/Reimagined does not exist yet and will be created from ReimaginedLadder.";
        }
        else
        {
            FooterStatusText.Text = string.Empty;
        }

        UpdateSelectionSummary();
    }

    private void SetBusy(bool busy, string? message = null, double? percentage = null, string? details = null)
    {
        _isBusy = busy;
        BusyPanel.IsVisible = busy;
        if (busy)
        {
            BusyMessageText.Text = message ?? "Working...";
            if (percentage is { } value)
            {
                BusyProgressBar.IsIndeterminate = false;
                BusyProgressBar.Value = Math.Clamp(value, 0, 100);
            }
            else
            {
                BusyProgressBar.IsIndeterminate = true;
            }

            BusyDetailsText.Text = details ?? string.Empty;
            BusyDetailsText.IsVisible = !string.IsNullOrWhiteSpace(details);
        }
        else
        {
            BusyProgressBar.IsIndeterminate = true;
            BusyMessageText.Text = string.Empty;
            BusyDetailsText.Text = string.Empty;
            BusyDetailsText.IsVisible = false;
        }

        UpdateActionAvailability();
    }

    private void ShowBanner(string? message)
    {
        StatusBannerText.Text = message ?? string.Empty;
        StatusBanner.IsVisible = !string.IsNullOrWhiteSpace(message);
    }

    private void OnSearchTextChanged(object? sender, TextChangedEventArgs e)
    {
        _searchQuery = SearchTextBox.Text ?? string.Empty;
        ApplySearchFilter();
    }

    private void OnSelectAllClick(object? sender, RoutedEventArgs e)
    {
        foreach (var row in _visibleRows)
        {
            row.IsEnabled = true;
        }
    }

    private void OnDeselectAllClick(object? sender, RoutedEventArgs e)
    {
        foreach (var row in _visibleRows)
        {
            row.IsEnabled = false;
        }
    }

    private void OnResetDefaultsClick(object? sender, RoutedEventArgs e)
    {
        foreach (var row in _visibleRows)
        {
            row.IsEnabled = row.DefaultEnabled;
        }
    }

    private void OnEnableMigrationChanged(object? sender, RoutedEventArgs e)
    {
        if (_suppressMigrationEvents)
        {
            return;
        }

        if (EnableMigrationCheckBox.IsChecked == true)
        {
            CharacterMigrationStepExpander.IsExpanded = true;
        }

        UpdateMigrationSummaryAndValidation();
        UpdateActionAvailability();
    }

    private void OnLadderSourceSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressMigrationEvents)
        {
            return;
        }

        _suppressMigrationEvents = true;
        try
        {
            _savedCharacterSelections.Clear();
            _savedStashSelections.Clear();
            PopulateMigrationRowsForSource(LadderSourceComboBox.SelectedItem as LadderSaveSourceFolder);
        }
        finally
        {
            _suppressMigrationEvents = false;
        }

        UpdateMigrationSummaryAndValidation();
        UpdateActionAvailability();
    }

    private void OnSelectSafeCharactersClick(object? sender, RoutedEventArgs e)
    {
        if (EnableMigrationCheckBox.IsChecked != true)
        {
            return;
        }

        _suppressMigrationEvents = true;
        try
        {
            foreach (var row in _characterRows)
            {
                if (row.IsMigratable && !row.HasConflict)
                {
                    row.IsSelected = true;
                }
            }
        }
        finally
        {
            _suppressMigrationEvents = false;
        }

        UpdateMigrationSummaryAndValidation();
        UpdateActionAvailability();
    }

    private void OnDeselectAllCharactersClick(object? sender, RoutedEventArgs e)
    {
        _suppressMigrationEvents = true;
        try
        {
            foreach (var row in _characterRows)
            {
                row.IsSelected = false;
            }

            foreach (var stash in _stashRows)
            {
                stash.IsSelected = false;
            }
        }
        finally
        {
            _suppressMigrationEvents = false;
        }

        UpdateMigrationSummaryAndValidation();
        UpdateActionAvailability();
    }

    private void OnConfirmOverwriteChanged(object? sender, RoutedEventArgs e)
    {
        if (_suppressMigrationEvents)
        {
            return;
        }

        UpdateMigrationSummaryAndValidation();
        UpdateActionAvailability();
    }

    private async void OnDownloadOrUpdateLadderClick(object? sender, RoutedEventArgs e)
    {
        if (_isBusy || _isConfirmingCopy || _downloadOrUpdateLadderAsync is null || _isGameRunning?.Invoke() == true)
        {
            return;
        }

        CancelPendingInspection();
        var preservedSelections = _allRows.Count > 0
            ? _allRows.ToDictionary(row => row.Key, row => row.IsEnabled, StringComparer.OrdinalIgnoreCase)
            : null;

        var operationId = ++_operationSequence;
        _activeOperationId = operationId;
        ShowBanner(null);
        SetBusy(true, "Preparing Ladder package download/update...");
        try
        {
            var progress = new Progress<LadderBundleProgress>(update =>
            {
                if (_lifetimeCancellation.IsCancellationRequested || !_isBusy || _activeOperationId != operationId)
                {
                    return;
                }

                SetBusy(true, update.Message, update.Percentage, update.Details);
            });
            var error = await _downloadOrUpdateLadderAsync(progress, _lifetimeCancellation.Token);
            if (_lifetimeCancellation.IsCancellationRequested)
            {
                return;
            }

            await RefreshDialogStateAsync(preservedSelections, allowWhileBusy: true);
            if (!string.IsNullOrWhiteSpace(error))
            {
                ShowBanner(error);
            }
            else if (_state is { HasLocalLadder: false })
            {
                ShowBanner("The Ladder installation could not be verified after downloading.");
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            LaunchDiagnostics.LogException("Failed to download/update Ladder from Copy dialog", ex);
            ShowBanner($"Could not download/update Ladder: {ex.Message}");
        }
        finally
        {
            if (_activeOperationId == operationId)
            {
                _activeOperationId = 0;
            }

            SetBusy(false);
        }
    }

    private LadderCharacterMigrationRequest? TryBuildMigrationRequest(out string? validationError)
    {
        validationError = null;
        if (EnableMigrationCheckBox.IsChecked != true)
        {
            return null;
        }

        var inspection = _state?.CharacterMigration;
        validationError = RevalidateCharacterSelections(inspection, migrationEnabled: true);
        if (!string.IsNullOrWhiteSpace(validationError))
        {
            return null;
        }

        if (inspection is null
            || string.IsNullOrWhiteSpace(inspection.TargetSaveDirectory)
            || string.IsNullOrWhiteSpace(inspection.BackupRootDirectory)
            || LadderSourceComboBox.SelectedItem is not LadderSaveSourceFolder source)
        {
            validationError = "Could not resolve Ladder or Non-Ladder save folders for migration.";
            return null;
        }

        var selectedCharacters = _characterRows
            .Where(row => row.IsSelected && row.SelectedCopy is not null)
            .Select(row =>
            {
                var copy = row.SelectedCopy!;
                return new LadderCharacterMigrationSelection(
                    copy.SourcePath,
                    copy.SourceSha256,
                    copy.CharacterName,
                    row.EffectiveTargetName,
                    row.IsReplacingExisting,
                    row.IsReplacingExisting ? row.ExistingConflict?.Sha256 : null,
                    copy.PairedBindPath,
                    copy.CompanionPaths);
            })
            .ToArray();

        var selectedStashes = _stashRows
            .Where(row => row.IsSelected)
            .Select(row => new LadderSharedStashSelection(
                row.Option.FileName,
                row.Option.SourcePath,
                row.Option.SourceSha256))
            .ToArray();

        if (selectedCharacters.Length == 0 && selectedStashes.Length == 0)
        {
            validationError = "Select at least one character or shared stash to migrate, or uncheck Step 2.";
            return null;
        }

        return new LadderCharacterMigrationRequest(
            source.FullPath,
            inspection.TargetSaveDirectory,
            inspection.BackupRootDirectory,
            MainWindow.Settings?.CurrentProfile.BackupSaveDirectory,
            selectedCharacters,
            selectedStashes);
    }

    private async void OnCopyClick(object? sender, RoutedEventArgs e)
    {
        if (_isBusy
            || _isConfirmingCopy
            || _copyAsync is null
            || _state is not { IsInstallDirectoryValid: true, HasLocalLadder: true, Plan: not null })
        {
            return;
        }

        if (_isGameRunning?.Invoke() == true)
        {
            ShowBanner("Close D2R and D2RLoader before copying.");
            UpdateActionAvailability();
            return;
        }

        CancelPendingInspection();
        _isConfirmingCopy = true;
        LadderCharacterMigrationRequest? migrationRequest;
        try
        {
            migrationRequest = TryBuildMigrationRequest(out var migrationError);
            if (!string.IsNullOrWhiteSpace(migrationError))
            {
                CharacterMigrationStepExpander.IsExpanded = true;
                ShowBanner(migrationError);
                UpdateActionAvailability();
                return;
            }

            var overwriteRows = _characterRows
                .Where(row => row.IsSelected && row.IsReplacingExisting)
                .ToArray();
            if (EnableMigrationCheckBox.IsChecked == true && overwriteRows.Length > 0)
            {
                var confirmed = await ConfirmCharacterOverwriteAsync(overwriteRows);
                if (!confirmed || _lifetimeCancellation.IsCancellationRequested)
                {
                    return;
                }

                if (_isGameRunning?.Invoke() == true)
                {
                    ShowBanner("Close D2R and D2RLoader before copying.");
                    UpdateActionAvailability();
                    return;
                }

                migrationRequest = TryBuildMigrationRequest(out migrationError);
                if (!string.IsNullOrWhiteSpace(migrationError))
                {
                    CharacterMigrationStepExpander.IsExpanded = true;
                    ShowBanner(migrationError);
                    UpdateActionAvailability();
                    return;
                }
            }

            var enabledKeys = _allRows
                .Where(row => row.IsEnabled)
                .Select(row => row.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var keepExistingConfigs = KeepConfigsCheckBox.IsChecked == true;

            var operationId = ++_operationSequence;
            _activeOperationId = operationId;
            ShowBanner(null);
            SetBusy(true, "Copying Ladder to Non-Ladder...");
            try
            {
                var progress = new Progress<string>(message =>
                {
                    if (_lifetimeCancellation.IsCancellationRequested || !_isBusy || _activeOperationId != operationId)
                    {
                        return;
                    }

                    SetBusy(true, message);
                });
                OperationOutcome = await _copyAsync(
                    enabledKeys,
                    keepExistingConfigs,
                    migrationRequest,
                    progress,
                    _lifetimeCancellation.Token);
                _activeOperationId = 0;
                SetBusy(false);
                Close(true);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                LaunchDiagnostics.LogException("Copy Ladder to Non-Ladder failed", ex);
                var preserved = _allRows.Count > 0
                    ? _allRows.ToDictionary(row => row.Key, row => row.IsEnabled, StringComparer.OrdinalIgnoreCase)
                    : null;
                await RefreshDialogStateAsync(preserved, allowWhileBusy: true);
                if (!_lifetimeCancellation.IsCancellationRequested)
                {
                    ShowBanner($"Copy failed: {ex.Message}");
                }
            }
            finally
            {
                if (_activeOperationId == operationId)
                {
                    _activeOperationId = 0;
                }

                if (_isBusy)
                {
                    SetBusy(false);
                }
            }
        }
        finally
        {
            _isConfirmingCopy = false;
        }
    }

    private async Task<bool> ConfirmCharacterOverwriteAsync(IReadOnlyList<LadderCharacterRowViewModel> overwriteRows)
    {
        var listPanel = new StackPanel { Spacing = 8 };
        foreach (var row in overwriteRows)
        {
            var existing = row.ExistingConflict?.DescribeExisting() ?? $"{row.CharacterName}.d2s";
            var ladder = row.SelectedCopy?.DisplayLabel ?? "Ladder character";
            listPanel.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.Parse("#1C1A19")),
                BorderBrush = new SolidColorBrush(Color.Parse("#7B1D18")),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 8),
                Child = new StackPanel
                {
                    Spacing = 2,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = row.CharacterName,
                            FontWeight = FontWeight.SemiBold,
                            Foreground = new SolidColorBrush(Color.Parse("#F7E4B8"))
                        },
                        new TextBlock
                        {
                            Text = $"Current Non-Ladder: {existing}",
                            FontSize = 12,
                            Foreground = new SolidColorBrush(Color.Parse("#E0575F")),
                            TextWrapping = TextWrapping.Wrap
                        },
                        new TextBlock
                        {
                            Text = $"Incoming Ladder: {ladder}",
                            FontSize = 12,
                            Foreground = new SolidColorBrush(Color.Parse("#BCA77D")),
                            TextWrapping = TextWrapping.Wrap
                        }
                    }
                }
            });
        }

        var cancelButton = new Button { Content = "Cancel", MinWidth = 100 };
        var confirmButton = new Button
        {
            Content = "Backup & Replace",
            MinWidth = 150,
            Classes = { "accent" }
        };

        var modal = new Window
        {
            Title = "Confirm Character Overwrite",
            Width = 540,
            SizeToContent = SizeToContent.Height,
            MaxHeight = 560,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Background = Background
        };

        cancelButton.Click += (_, _) => modal.Close(false);
        confirmButton.Click += (_, _) => modal.Close(true);
        modal.Opened += (_, _) => cancelButton.Focus();

        var root = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            RowSpacing = 12,
            Margin = new Thickness(20)
        };

        root.Children.Add(new StackPanel
        {
            Spacing = 6,
            Children =
            {
                new TextBlock
                {
                    Text = "Overwrite existing Non-Ladder character(s)?",
                    Classes = { "section-title" },
                    Foreground = new SolidColorBrush(Color.Parse("#E0575F"))
                },
                new TextBlock
                {
                    Text = "The existing Non-Ladder character save(s) listed below will be replaced after creating a full verified backup of your Non-Ladder save folder.",
                    TextWrapping = TextWrapping.Wrap
                }
            }
        });

        var scroll = new ScrollViewer
        {
            MaxHeight = 280,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = listPanel
        };
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { cancelButton, confirmButton }
        };
        Grid.SetRow(buttons, 2);
        root.Children.Add(buttons);

        modal.Content = root;
        return await modal.ShowDialog<bool>(this);
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        if (!_isBusy)
        {
            Close(false);
        }
    }
}
