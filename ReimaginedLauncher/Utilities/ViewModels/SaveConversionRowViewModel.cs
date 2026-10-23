using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;

namespace ReimaginedLauncher.Utilities.ViewModels;

/// <param name="Layout">The source layout to convert from; null means the file is already in the target layout.</param>
public sealed record SaveSourceChoice(string? Layout, string Display)
{
    public override string ToString() => Display;
}

public class SaveConversionRowViewModel : INotifyPropertyChanged
{
    private const int MaxListedRisks = 5;

    private static readonly string[] PlanProperties =
    [
        nameof(Plan), nameof(StatusLabel), nameof(Explanation), nameof(IsProblem), nameof(FullReport), nameof(HasNameMismatch),
        nameof(NameMismatchText), nameof(CanSelect), nameof(CanChangeSource), nameof(IsAlreadyCompatible), nameof(IsSelected)
    ];

    private readonly Func<SaveConversionPlan, string, string?> _checkRename;
    private bool _isSelected;
    private bool _renameApproved;
    private string _proposedName;
    private string? _result;
    private bool _resultIsProblem;
    private bool _isDone;
    private SaveSourceChoice? _selectedSourceChoice;

    public SaveConversionRowViewModel(SaveConversionPlan plan, Func<SaveConversionPlan, string, string?> checkRename)
    {
        BasePlan = plan;
        Plan = plan;
        _checkRename = checkRename;
        // The file name is what the player sees and what D2RLoader matches, so it is the default proposal.
        _proposedName = plan.FileStem;
        SourceChoices = plan switch
        {
            { State: SaveConversionState.Unresolved, ForceOption: { Kind: SaveForceKind.ChooseSourceLayout } option } =>
            [
                .. option.SourceLayouts.Select(layout => new SaveSourceChoice(layout, layout)),
                .. option.TargetAlsoFits
                    ? [new SaveSourceChoice(null, $"{plan.TargetLabel} (target parse)")]
                    : Array.Empty<SaveSourceChoice>()
            ],
            {
                    State: SaveConversionState.AlreadyCompatible,
                    ForceOption: { Kind: SaveForceKind.ChooseSourceLayout, TargetAlsoFits: true } option
                } =>
                [
                    new SaveSourceChoice(null, $"{plan.TargetLabel} (target parse)"),
                    .. option.SourceLayouts.Select(layout => new SaveSourceChoice(layout, layout))
                ],
            _ => []
        };
        _selectedSourceChoice = SourceChoices.FirstOrDefault();
        _isSelected = IsSelectedByDefault;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The first analysis, without any choice or Force Conversion applied.</summary>
    public SaveConversionPlan BasePlan { get; }

    /// <summary>The plan that Convert Selected applies: <see cref="BasePlan"/> with the chosen source and Force Conversion applied.</summary>
    public SaveConversionPlan Plan { get; private set; }

    public string FileName => BasePlan.FileName;

    public bool IsDone => _isDone;

    public IReadOnlyList<SaveSourceChoice> SourceChoices { get; }

    /// <summary>The source layout could not be established, so the user confirms or changes the preselected one.</summary>
    public bool IsSourceUncertain => SourceChoices.Count > 0;

    public bool CanChangeSource => IsSourceUncertain && !_isDone;

    public SaveSourceChoice? SelectedSourceChoice
    {
        get => _selectedSourceChoice;
        set => Set(ref _selectedSourceChoice, value);
    }

    public bool IsTargetChoiceSelected =>
        IsSourceUncertain && SelectedSourceChoice is { Layout: null } && Plan == BasePlan;

    public bool IsAlreadyCompatible => Plan.State == SaveConversionState.AlreadyCompatible && !Plan.IsTargetRebuild;

    private int ForcedIssues => Plan.ForceOption?.Risks.Count ?? 0;

    private bool IsForced =>
        Plan.State == SaveConversionState.Experimental && (ForcedIssues > 0 || Plan.IsTargetRebuild);

    /// <summary>The uncertain source is still waiting for the user, rather than blocked under the chosen version.</summary>
    private bool AwaitsSourceConfirmation => IsSourceUncertain && (Plan == BasePlan || !Plan.IsBlocked) && !IsForced;

    public string StatusLabel => _result is not null
        ? _resultIsProblem ? "Failed" : _isDone ? DoneLabel : "Skipped"
        : IsTargetChoiceSelected
            ? "Parses with target"
            : Plan.State switch
            {
                SaveConversionState.AlreadyCompatible when Plan.IsTargetRebuild && Plan.ConvertedBytes is null =>
                    "No repair",
                SaveConversionState.AlreadyCompatible => "Parses with target",
                SaveConversionState.Verified => "Ready",
                SaveConversionState.VerifiedWithWarnings => "Warnings",
                SaveConversionState.Experimental when IsForced => "Experimental",
                _ when AwaitsSourceConfirmation => "Source uncertain",
                SaveConversionState.Unresolved when Plan.ForceOption?.Kind == SaveForceKind.Force => "Needs Force",
                _ => "Blocked"
            };

    private string DoneLabel => Plan.ConvertedBytes is null
        ? "Renamed"
        : Plan.State switch
        {
            SaveConversionState.Experimental => "Converted (experimental)",
            SaveConversionState.VerifiedWithWarnings => "Converted with warnings",
            _ => "Converted"
        };

    public bool IsProblem => _result is not null ? _resultIsProblem : Plan.IsBlocked && !IsTargetChoiceSelected && !AwaitsSourceConfirmation;

    public string Explanation => _result ?? (IsTargetChoiceSelected
        ? string.IsNullOrWhiteSpace(Plan.Summary)
            ? "No conversion selected; a target parse does not guarantee game compatibility."
            : Plan.Summary
        : Plan.State switch
        {
            SaveConversionState.AlreadyCompatible => Plan.Summary,
            SaveConversionState.Verified => $"Ready to convert from {Plan.SourceLabel} to {Plan.TargetLabel}.",
            SaveConversionState.VerifiedWithWarnings => WarningsText(),
            SaveConversionState.Experimental when IsForced => $"Experimental: {Issues(ForcedIssues)} accepted without verification.",
            _ when AwaitsSourceConfirmation && Plan == BasePlan => "Source version uncertain. Choose the version this save was made with.",
            _ when AwaitsSourceConfirmation => $"Source version uncertain. Select this file to convert it from {Plan.SourceLabel} to {Plan.TargetLabel}.",
            SaveConversionState.Unresolved when Plan.ForceOption?.Kind == SaveForceKind.Force =>
                $"{Issues(ForcedIssues)} cannot be verified. Check Force conversion to attempt it.",
            _ => BlockedText()
        });

    private string WarningsText()
    {
        var warnings = Plan.IssueGroups.Where(group => !group.Blocking).ToList();
        return warnings.Count == 1 && warnings[0].Category == SaveIssueCategory.HistoricalModifiers
            ? $"{warnings[0].Count} historical item modifier(s) will be preserved."
            : $"Ready to convert from {Plan.SourceLabel} to {Plan.TargetLabel}; {warnings.Sum(group => group.Count)} warning(s) in the full report.";
    }

    private string BlockedText()
    {
        var reason = Plan.BlockingReason ?? Plan.Summary;
        return reason.StartsWith("Source rejected:", StringComparison.Ordinal) || reason.StartsWith("The file could not be read", StringComparison.Ordinal)
            ? $"Save file is invalid or cannot be read ({reason[(reason.IndexOf(':') + 1)..].Trim().TrimEnd('.')})."
            : reason;
    }

    private static string Issues(int count) => count == 1 ? "1 issue" : $"{count} issues";

    public string FullReport
    {
        get
        {
            var parts = new List<string> { Plan.Operation };
            if (Plan.IssueGroups.Count > 0)
            {
                parts.Add(string.Join(Environment.NewLine, Plan.IssueGroups.Select(group => "• " + group)));
            }

            if (Plan.ForceOption is { Risks.Count: > 0 } option)
            {
                parts.Add(FormatRisks(option.Risks));
            }

            parts.Add(Plan.Report);
            return string.Join(Environment.NewLine + Environment.NewLine, parts);
        }
    }

    public static string FormatRisks(IReadOnlyList<string> risks) =>
        string.Join(Environment.NewLine, risks.Take(MaxListedRisks).Select(risk => "• " + risk))
        + (risks.Count > MaxListedRisks ? $"{Environment.NewLine}… {risks.Count - MaxListedRisks} more below." : "");

    public bool HasNameMismatch => Plan.HasNameMismatch && !_isDone;

    public string NameMismatchText => $"The character inside is named '{Plan.EmbeddedName}'. D2RLoader rejects characters whose file and stored names differ.";

    public bool CanSelect => !_isDone && !IsTargetChoiceSelected && !Plan.IsBlocked && (Plan.ConvertedBytes is not null || Plan.HasNameMismatch);

    /// <summary>Every convertible file, except one whose uncertain source still needs the user's confirmation.</summary>
    private bool IsSelectedByDefault => CanSelect && Plan.ConvertedBytes is not null && !IsSourceUncertain
                                        && !(BasePlan.State == SaveConversionState.AlreadyCompatible &&
                                             Plan.IsTargetRebuild);

    public bool IsSelected
    {
        get => _isSelected && CanSelect;
        set => Set(ref _isSelected, value);
    }

    public bool RenameApproved
    {
        get => _renameApproved;
        set
        {
            Set(ref _renameApproved, value);
            if (value)
            {
                IsSelected = true;
            }
        }
    }

    public string ProposedName
    {
        get => _proposedName;
        set => Set(ref _proposedName, value ?? string.Empty);
    }

    public string? NameProblem => HasNameMismatch && RenameApproved ? _checkRename(Plan, ProposedName.Trim()) : null;
    public bool HasNameProblem => NameProblem is not null;

    /// <summary>Selected and, when a rename was requested, with a valid name.</summary>
    public bool IsReadyToApply => IsSelected
                                  && (!RenameApproved || !HasNameProblem)
                                  && (Plan.ConvertedBytes is not null || RenameApproved);

    public string? ApprovedName => HasNameMismatch && RenameApproved ? ProposedName.Trim() : null;

    public void SetResult(SaveConversionOutcome outcome)
    {
        _isDone = outcome.Succeeded;
        _resultIsProblem = !outcome.Succeeded;
        var message = outcome.BackupDirectory is { } backup ? outcome.Message.Replace($" Backup: {backup}", "") : outcome.Message;
        _result = outcome.Succeeded ? message : $"Not changed: {message}";
        RaisePlanProperties();
    }

    public void MarkSkipped()
    {
        if (_result is null && !_isDone && (CanSelect || IsTargetChoiceSelected || Plan.State == SaveConversionState.AlreadyCompatible))
        {
            _result = Plan.IsTargetRebuild && Plan.ConvertedBytes is null
                ? "The identical target round-trip did not repair the save."
                : IsTargetChoiceSelected || Plan.State == SaveConversionState.AlreadyCompatible
                    ? "No conversion needed."
                    : "Not selected; left unchanged.";
            RaisePlanProperties();
        }
    }

    /// <summary>Swaps in the plan for the current source choice or Force Conversion setting; the row itself is kept.</summary>
    /// <param name="select">Select the row, as when the user just chose its source; null keeps a confirmed choice or applies the default.</param>
    public void ReplacePlan(SaveConversionPlan plan, bool? select = null)
    {
        Plan = plan;
        _result = null;
        _resultIsProblem = false;
        _isSelected = select ?? (IsSourceUncertain ? _isSelected : IsSelectedByDefault);
        RaisePlanProperties();
    }

    private void RaisePlanProperties()
    {
        foreach (var property in PlanProperties)
        {
            OnPropertyChanged(property);
        }

        RaiseDependents();
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (Equals(field, value))
        {
            return;
        }

        field = value;
        OnPropertyChanged(propertyName);
        RaiseDependents();
    }

    private void RaiseDependents()
    {
        foreach (var dependent in new[] { nameof(NameProblem), nameof(HasNameProblem), nameof(IsReadyToApply) })
        {
            OnPropertyChanged(dependent);
        }
    }

    private void OnPropertyChanged(string? propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}