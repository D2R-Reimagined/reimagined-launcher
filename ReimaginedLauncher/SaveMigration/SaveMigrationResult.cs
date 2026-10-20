using System.Collections.Generic;
using System.Linq;

namespace ReimaginedLauncher.SaveMigration;

public enum MigrationStatus
{
    /// <summary>Written, reparsed with the target definitions and every check passed without warnings.</summary>
    Verified,
    /// <summary>Written and verified, but some entries were preserved with a warning or need a person to look at them.</summary>
    NeedsManualInspection,
    /// <summary>Written and structurally verified only because the user confirmed a Force Conversion override; identities are not verified.</summary>
    Experimental,
    /// <summary>No approved output: the source was rejected, a blocker was found, or verification failed.</summary>
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
/// Policy overrides for one explicitly confirmed Force Conversion. They never relax parsing, serialization,
/// reparse, checksum, count, stat-encoding or unintended-change checks.
/// </summary>
/// <param name="AcceptUnresolvedIdentities">Write the output although some saved ids would name a different definition.</param>
/// <param name="SourceLayout">Use this source layout instead of identifying one; it must still parse the file completely.</param>
public sealed record MigrationOverrides(bool AcceptUnresolvedIdentities = false, string? SourceLayout = null)
{
    public static readonly MigrationOverrides None = new();

    public bool Any => AcceptUnresolvedIdentities || SourceLayout is not null;
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

public sealed record MigrationCheck(string Name, bool Passed, string Detail);

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
    IReadOnlyList<string> AmbiguousSourceLayouts)
{
    public int ItemsUnchanged => ItemsInspected - ItemsChanged;

    /// <summary>An approved output exists only because a confirmed override was actually needed.</summary>
    public bool IsExperimental => OutputPath is not null
                                  && (Overrides.SourceLayout is not null
                                      || Overrides.AcceptUnresolvedIdentities && References.Any(reference => reference.Blocking));

    public MigrationStatus Status =>
        OutputPath is null || Blockers.Count > 0 || Checks.Any(check => !check.Passed)
        || References.Any(reference => reference.Blocking) && !Overrides.AcceptUnresolvedIdentities
            ? MigrationStatus.Failed
            : IsExperimental
                ? MigrationStatus.Experimental
                : Warnings.Count > 0 || References.Any(reference => reference.Outcome is ReferenceOutcome.PreservedWithWarning or ReferenceOutcome.ManualAttention)
                    ? MigrationStatus.NeedsManualInspection
                    : MigrationStatus.Verified;
}
