using System.Collections.Generic;
using System.Linq;

namespace ReimaginedLauncher.SaveMigration;

public enum MigrationStatus
{
    /// <summary>Written, reparsed with the target definitions and every check passed without warnings.</summary>
    Verified,
    /// <summary>Written and verified, but some entries were preserved with a warning or need a person to look at them.</summary>
    NeedsManualInspection,
    /// <summary>Written and structurally verified, but only because Force Conversion or a manual source choice accepted the listed uncertainties.</summary>
    Experimental,
    /// <summary>No approved output: a hard blocker or critical check failed, or the listed uncertainties were not accepted.</summary>
    Failed,
}

public enum ReferenceOutcome
{
    Mapped,
    AlreadyCompatible,
    PreservedWithWarning,
    ManualAttention,
}

/// <summary>
/// Overrides for one Force Conversion analysis. They never relax decoding, serialization, the output header or the target reparse.
/// </summary>
/// <param name="Force">Accept every reported uncertainty: unresolved identities, unverifiable stats, repaired headers and reported value or field losses.</param>
/// <param name="SourceLayout">Use this source layout instead of identifying one; it must still parse the file.</param>
/// <param name="RebuildWithTarget">Decode and rebuild with the installed target definitions instead of historical layouts.</param>
public sealed record MigrationOverrides(bool Force = false, string? SourceLayout = null, bool RebuildWithTarget = false)
{
    public static readonly MigrationOverrides None = new();

    public bool Any => Force || SourceLayout is not null || RebuildWithTarget;
}

/// <param name="Blocking">The reference cannot be carried into the target without silently changing what the item is.</param>
/// <param name="SourceKey">The source row the saved id names, when there is one.</param>
/// <param name="SameIdTargetKey">The target row the unchanged saved id would name, when there is one.</param>
public sealed record ReferenceResult(
    string ItemPath,
    string ItemCode,
    string Role,
    TableKind Kind,
    int SourceId,
    int TargetId,
    ReferenceOutcome Outcome,
    bool Blocking,
    string Reason,
    string? SourceKey = null,
    string? SameIdTargetKey = null)
{
    public override string ToString() =>
        $"[{Outcome}{(Blocking ? ", BLOCKING" : "")}] {ItemPath}: {Role} {SourceId}"
        + (TargetId == SourceId ? "" : $" -> {TargetId}") + $"; {Reason}";
}

/// <param name="Scope">"item" for item-property encoding, "character" or "demon" for player-stat (CSv) encoding.</param>
public sealed record StatResult(
    string Scope,
    string SourceName,
    string TargetName,
    int SourceId,
    int TargetId,
    int Uses,
    long MinValue,
    long MaxValue,
    string SourceEncoding,
    string TargetEncoding,
    IReadOnlyList<string> Problems)
{
    public bool IdChanged => SourceId != TargetId;
    public bool EncodingChanged => SourceEncoding != TargetEncoding;
}

/// <param name="Page">D2SSharp's <c>StorePage</c> value; the raw 3-bit save field is <c>Page + 1</c>.</param>
public sealed record CustomPageItem(string Path, string Code, int Page, int X, int Y, int StatCount)
{
    public override string ToString() => $"{Path} '{Code}' page {Page} (raw {Page + 1}) at {X},{Y}, {StatCount} stat(s)";
}

/// <param name="Critical">A failure means the output is structurally unusable; otherwise it is a reported difference Force Conversion may accept.</param>
public sealed record MigrationCheck(string Name, bool Passed, string Detail, bool Critical = false);

public sealed record SaveMigrationResult(
    string InputPath,
    string? OutputPath,
    string? RejectedOutputPath,
    SaveFileKind Kind,
    uint SourceVersion,
    uint? OutputVersion,
    string? SourceLayout,
    string TargetLayout,
    IReadOnlyList<string> Identification,
    int ItemsInspected,
    int ItemsChanged,
    IReadOnlyList<ReferenceResult> References,
    IReadOnlyList<StatResult> Stats,
    IReadOnlyList<CustomPageItem> CustomPageItems,
    IReadOnlyList<string> Notes,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Blockers,
    IReadOnlyList<MigrationCheck> Checks,
    MigrationOverrides Overrides,
    IReadOnlyList<string> AmbiguousSourceLayouts,
    IReadOnlyList<string> Uncertainties)
{
    public int ItemsUnchanged => ItemsInspected - ItemsChanged;

    /// <summary>Nothing makes an output impossible: no hard blocker and no failed critical check.</summary>
    public bool IsRecoverable => Blockers.Count == 0 && SourceLayout is not null && !Checks.Any(check => check is { Critical: true, Passed: false });

    /// <summary>Something could not be verified: an <see cref="Uncertainties"/> entry or an unresolved (blocking) item identity.</summary>
    public bool HasUncertainty => Uncertainties.Count > 0 || References.Any(reference => reference.Blocking);

    /// <summary>Recoverable, but only by accepting the uncertainties with Force Conversion.</summary>
    public bool NeedsForce => IsRecoverable && OutputPath is null && HasUncertainty;

    /// <summary>An approved output exists only because an override accepted an uncertainty or a manual source choice.</summary>
    public bool IsExperimental => OutputPath is not null &&
                                  (Overrides.RebuildWithTarget || Overrides.SourceLayout is not null || HasUncertainty);

    public MigrationStatus Status =>
        OutputPath is null
            ? MigrationStatus.Failed
            : IsExperimental
                ? MigrationStatus.Experimental
                : Warnings.Count > 0 || References.Any(reference => reference.Outcome is ReferenceOutcome.PreservedWithWarning or ReferenceOutcome.ManualAttention)
                    ? MigrationStatus.NeedsManualInspection
                    : MigrationStatus.Verified;
}