using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace ReimaginedLauncher.SaveMigration;

public enum SaveCheckpointType
{
    Release,
    /// <summary>A layout that never shipped as a release, such as a pre-swap candidate or a development build.</summary>
    Intermediate,
}

/// <summary>One registered save-sensitive game data layout, read from <c>checkpoints.json</c>.</summary>
/// <param name="Order">Explicit chronological order. Folder names are never sorted.</param>
/// <param name="AppendedStatsDirectory">Overlay whose stats past the end of <paramref name="Directory"/>'s stat table are appended.</param>
/// <param name="InstalledVersions">modinfo.json <c>version</c> values that identify an installation using this layout.</param>
/// <param name="DevelopmentTarget">May be chosen as a target explicitly, but is never selected automatically.</param>
/// <param name="SupportedTarget">An unreleased layout, such as an upcoming release snapshot, that is a supported conversion target.</param>
/// <param name="ItemTablesDirectory">
/// Item tables and base items to read the save with instead of <paramref name="Directory"/>'s; the stat encoding stays
/// <paramref name="Directory"/>'s. Only valid together with <paramref name="AppendedStatsDirectory"/>.
/// </param>
public sealed record SaveCheckpoint(
    string Id,
    string Label,
    string Build,
    SaveCheckpointType Type,
    int Order,
    string Directory,
    string? AppendedStatsDirectory,
    IReadOnlyList<string> InstalledVersions,
    bool DevelopmentTarget,
    string? Description,
    bool SupportedTarget = false,
    string? ItemTablesDirectory = null)
{
    public bool CanBeTarget => AppendedStatsDirectory is null && ItemTablesDirectory is null
                               && (Type == SaveCheckpointType.Release || DevelopmentTarget || SupportedTarget);

    /// <summary>May become the default target; development-only layouts never do.</summary>
    public bool IsSupportedTarget => CanBeTarget && !DevelopmentTarget && (Type == SaveCheckpointType.Release || SupportedTarget);

    public string DisplayName => DevelopmentTarget ? $"{Build} (development testing)" : Build;
}

/// <summary>
/// An explicit identity for a row that was renamed between checkpoints. A unique or set mapping applies only when the saved id
/// still names <see cref="SourceKey"/> on <see cref="Code"/> in the source checkpoint and the target has exactly one row
/// named <see cref="TargetKey"/> on the same base. An affix mapping covers saved ids <see cref="SourceId"/> to
/// <see cref="LastSourceId"/>; every id is kept and must still name <see cref="TargetKey"/> at the same position in the target.
/// </summary>
public sealed record SaveIdentityMapping(
    TableKind Kind,
    string SourceCheckpoint,
    int SourceId,
    string SourceKey,
    string? Code,
    string TargetCheckpoint,
    string TargetKey,
    string? Description,
    int? LastSourceId = null)
{
    public bool IsAffix => Kind is TableKind.MagicPrefix or TableKind.MagicSuffix;

    public bool Covers(int saveId) => saveId >= SourceId && saveId <= (LastSourceId ?? SourceId);
}

/// <summary>A registered mapping as it applies to one migration target, with later registered renames already followed.</summary>
public sealed record ResolvedIdentityMapping(SaveIdentityMapping Mapping, string SourceDirectory, string TargetKey);

/// <param name="Warning">Set when the installed game version is known to use a different layout than <paramref name="Target"/>.</param>
public sealed record SaveTargetSelection(SaveCheckpoint? Target, string Explanation, string? Warning = null);

public sealed record SaveCheckpointRegistry(
    string DataDirectory,
    IReadOnlyList<SaveCheckpoint> Checkpoints,
    IReadOnlyList<SaveIdentityMapping> IdentityMappings)
{
    public const string FileName = "checkpoints.json";
    public const string PullRequest1181Id = "pr-1181";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static SaveCheckpointRegistry Load(string dataDirectory) =>
        Parse(File.ReadAllText(Path.Combine(dataDirectory, FileName)), dataDirectory);

    public static SaveCheckpointRegistry Parse(string json, string dataDirectory)
    {
        var file = JsonSerializer.Deserialize<RegistryFile>(json, JsonOptions)
                   ?? throw new InvalidDataException($"{FileName} is empty.");
        var checkpoints = (file.Checkpoints ?? []).Select(entry => new SaveCheckpoint(
                Required(entry.Id, "id"),
                Required(entry.Label, "label"),
                entry.Build ?? entry.Label!,
                Enum.TryParse<SaveCheckpointType>(entry.Type, ignoreCase: true, out var type)
                    ? type
                    : throw new InvalidDataException($"Checkpoint '{entry.Id}' has unknown type '{entry.Type}' (release or intermediate)."),
                entry.Order ?? throw new InvalidDataException($"Checkpoint '{entry.Id}' has no order."),
                Required(entry.Directory, "directory"),
                string.IsNullOrWhiteSpace(entry.AppendedStatsDirectory) ? null : entry.AppendedStatsDirectory,
                [.. (entry.InstalledVersions ?? []).Select(version => version.Trim()).Where(version => version.Length > 0)],
                entry.DevelopmentTarget,
                entry.Description,
                entry.SupportedTarget,
                string.IsNullOrWhiteSpace(entry.ItemTablesDirectory) ? null : entry.ItemTablesDirectory))
            .OrderBy(checkpoint => checkpoint.Order)
            .ToList();

        if (checkpoints.FirstOrDefault(checkpoint => checkpoint is { ItemTablesDirectory: not null, AppendedStatsDirectory: null }) is { } itemTablesOnly)
        {
            throw new InvalidDataException($"Checkpoint '{itemTablesOnly.Id}' sets itemTablesDirectory without appendedStatsDirectory.");
        }

        if (checkpoints.GroupBy(checkpoint => checkpoint.Id, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1) is { } duplicateId)
        {
            throw new InvalidDataException($"{FileName} registers checkpoint '{duplicateId.Key}' more than once.");
        }

        if (checkpoints.GroupBy(checkpoint => checkpoint.Order).FirstOrDefault(group => group.Count() > 1) is { } duplicateOrder)
        {
            throw new InvalidDataException($"{FileName} gives order {duplicateOrder.Key} to more than one checkpoint.");
        }

        var ids = checkpoints.Select(checkpoint => checkpoint.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var mappings = (file.IdentityMappings ?? []).Select(entry =>
            {
                var kind = Enum.TryParse<TableKind>(entry.Table, ignoreCase: true, out var parsed) && parsed is not TableKind.ItemStatCost
                    ? parsed
                    : throw new InvalidDataException($"Identity mapping table '{entry.Table}' must be UniqueItems, SetItems, MagicPrefix or MagicSuffix.");
                var sourceId = entry.SourceId ?? throw new InvalidDataException("An identity mapping has no sourceId.");
                var affix = kind is TableKind.MagicPrefix or TableKind.MagicSuffix;
                if (affix ? entry.Code is not null || entry.LastSourceId < sourceId : entry.LastSourceId is not null)
                {
                    throw new InvalidDataException($"Identity mapping '{entry.SourceKey}': affix mappings take a sourceId..lastSourceId range and no code; "
                                                   + "unique and set mappings take one sourceId and a code.");
                }

                return new SaveIdentityMapping(
                    kind,
                    Known(entry.SourceCheckpoint),
                    sourceId,
                    Required(entry.SourceKey, "sourceKey"),
                    affix ? null : Required(entry.Code, "code"),
                    Known(entry.TargetCheckpoint),
                    Required(entry.TargetKey, "targetKey"),
                    entry.Description,
                    entry.LastSourceId);
            })
            .ToList();
        return new SaveCheckpointRegistry(Path.GetFullPath(dataDirectory), checkpoints, mappings);

        string Known(string? id) => id is not null && ids.Contains(id)
            ? id
            : throw new InvalidDataException($"Identity mapping refers to unregistered checkpoint '{id}'.");
    }

    public SaveCheckpoint Get(string id) =>
        Checkpoints.FirstOrDefault(checkpoint => checkpoint.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
        ?? throw new KeyNotFoundException($"No save checkpoint '{id}' is registered in {FileName}.");

    public IReadOnlyList<SaveCheckpoint> Targets => [.. Checkpoints.Where(checkpoint => checkpoint.CanBeTarget)];

    /// <summary>Every earlier checkpoint is a direct source candidate; saves never step through intermediate versions.</summary>
    public IReadOnlyList<SaveCheckpoint> SourcesFor(SaveCheckpoint target) => [.. Checkpoints.Where(checkpoint => checkpoint.Order < target.Order)];

    public string PathOf(string directory) => Path.GetFullPath(Path.Combine(DataDirectory, directory));

    /// <summary>The label a table directory is loaded under: its earliest checkpoint that uses it without an overlay.</summary>
    public string SnapshotLabel(string directory) =>
        Checkpoints.FirstOrDefault(checkpoint => checkpoint.AppendedStatsDirectory is null
                                                 && string.Equals(PathOf(checkpoint.Directory), PathOf(directory), StringComparison.OrdinalIgnoreCase))?.Label
        ?? Path.GetFileName(directory);

    /// <summary>The newest supported target by explicit order.</summary>
    public SaveCheckpoint? DefaultTarget => Targets.Where(checkpoint => checkpoint.IsSupportedTarget).MaxBy(checkpoint => checkpoint.Order);

    /// <summary>The release immediately before <paramref name="target"/>; intermediate layouts are skipped.</summary>
    public SaveCheckpoint? PreviousRelease(SaveCheckpoint target) =>
        Checkpoints.Where(checkpoint => checkpoint.Type == SaveCheckpointType.Release && checkpoint.Order < target.Order).MaxBy(checkpoint => checkpoint.Order);

    /// <summary>Always selects <see cref="DefaultTarget"/>; the installed version only decides whether a compatibility warning is shown.</summary>
    public SaveTargetSelection SelectTarget(string? installedVersion)
    {
        if (DefaultTarget is not { } target)
        {
            return new SaveTargetSelection(null, "No supported conversion target is registered.");
        }

        var version = installedVersion?.Trim();
        if (string.IsNullOrEmpty(version))
        {
            return new SaveTargetSelection(target, "Latest supported layout. The installed Reimagined version could not be read.");
        }

        if (target.InstalledVersions.Contains(version, StringComparer.OrdinalIgnoreCase))
        {
            return new SaveTargetSelection(target, $"Matches the installed Reimagined version {version}.");
        }

        var installed = Checkpoints.Where(checkpoint => checkpoint.InstalledVersions.Contains(version, StringComparer.OrdinalIgnoreCase)).ToList();
        return installed.Count == 1
            ? new SaveTargetSelection(target, "Latest supported layout.",
                $"Your installed Reimagined {version} uses the {installed[0].Label} layout. Converted saves need {target.DisplayName} game data.")
            : new SaveTargetSelection(target, $"Latest supported layout. The installed Reimagined version {version} is not registered in this launcher.");
    }

    /// <summary>The registered mappings that reach <paramref name="target"/>, following later registered renames of the same row.</summary>
    public IReadOnlyList<ResolvedIdentityMapping> IdentityMappingsFor(SaveCheckpoint target)
    {
        var resolved = new List<ResolvedIdentityMapping>();
        foreach (var mapping in IdentityMappings)
        {
            if (Get(mapping.SourceCheckpoint).Order >= target.Order || Get(mapping.TargetCheckpoint).Order > target.Order)
            {
                continue;
            }

            var (key, order) = (mapping.TargetKey, Get(mapping.TargetCheckpoint).Order);
            for (var step = 0; step < IdentityMappings.Count; step++)
            {
                var next = IdentityMappings
                    .Where(later => later.Kind == mapping.Kind
                                    && string.Equals(later.Code, mapping.Code, StringComparison.OrdinalIgnoreCase)
                                    && (!mapping.IsAffix || (later.SourceId, later.LastSourceId) == (mapping.SourceId, mapping.LastSourceId))
                                    && later.SourceKey.Equals(key, StringComparison.OrdinalIgnoreCase)
                                    && Get(later.SourceCheckpoint).Order >= order
                                    && Get(later.TargetCheckpoint).Order > order
                                    && Get(later.TargetCheckpoint).Order <= target.Order)
                    .OrderBy(later => Get(later.TargetCheckpoint).Order)
                    .FirstOrDefault();
                if (next is null)
                {
                    break;
                }

                (key, order) = (next.TargetKey, Get(next.TargetCheckpoint).Order);
            }

            resolved.Add(new ResolvedIdentityMapping(mapping, PathOf(Get(mapping.SourceCheckpoint).Directory), key));
        }

        return resolved;
    }

    /// <summary>Checks every registered mapping against the bundled snapshots; an empty list means all are consistent.</summary>
    public IReadOnlyList<string> ValidateIdentityMappings()
    {
        var problems = new List<string>();
        foreach (var mapping in IdentityMappings)
        {
            if (mapping.IsAffix)
            {
                ValidateAffixRename(mapping);
                continue;
            }

            var codeColumn = TableSchema.CodeColumn(mapping.Kind)!;
            var source = Definitions(mapping.SourceCheckpoint, mapping.Kind).FirstOrDefault(definition => definition.SaveId == mapping.SourceId);
            if (source is null || !source.Key.Equals(mapping.SourceKey, StringComparison.OrdinalIgnoreCase)
                               || !source.Field(codeColumn).Equals(mapping.Code, StringComparison.OrdinalIgnoreCase))
            {
                problems.Add($"{mapping.SourceCheckpoint} {mapping.Kind} {mapping.SourceId} is {source?.ToString() ?? "missing"}, not '{mapping.SourceKey}' on '{mapping.Code}'.");
            }

            var targets = Definitions(mapping.TargetCheckpoint, mapping.Kind)
                .Where(definition => !definition.IsPlaceholder && definition.Key.Equals(mapping.TargetKey, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (targets.Count != 1 || !targets[0].Field(codeColumn).Equals(mapping.Code, StringComparison.OrdinalIgnoreCase))
            {
                problems.Add($"{mapping.TargetCheckpoint} has {targets.Count} '{mapping.TargetKey}' row(s); exactly one on '{mapping.Code}' is required.");
            }
        }

        return problems;

        // Each covered row must keep its position and every definition field; only the name may differ.
        void ValidateAffixRename(SaveIdentityMapping mapping)
        {
            var sources = Definitions(mapping.SourceCheckpoint, mapping.Kind);
            var targets = Definitions(mapping.TargetCheckpoint, mapping.Kind);
            var last = mapping.LastSourceId ?? mapping.SourceId;
            var named = sources.Count(definition => !definition.IsPlaceholder && definition.Key.Equals(mapping.SourceKey, StringComparison.OrdinalIgnoreCase));
            if (named != last - mapping.SourceId + 1)
            {
                problems.Add($"{mapping.SourceCheckpoint} has {named} '{mapping.SourceKey}' row(s), but the mapping covers ids {mapping.SourceId}-{last}.");
            }

            for (var id = mapping.SourceId; id <= last; id++)
            {
                var source = sources.FirstOrDefault(definition => definition.SaveId == id);
                var target = targets.FirstOrDefault(definition => definition.SaveId == id);
                if (source is null || !source.Key.Equals(mapping.SourceKey, StringComparison.OrdinalIgnoreCase))
                {
                    problems.Add($"{mapping.SourceCheckpoint} {mapping.Kind} {id} is {source?.ToString() ?? "missing"}, not '{mapping.SourceKey}'.");
                }
                else if (target is null || !target.Key.Equals(mapping.TargetKey, StringComparison.OrdinalIgnoreCase)
                                        || TableSchema.Fingerprint(source, TableSchema.AffixDefinition) != TableSchema.Fingerprint(target, TableSchema.AffixDefinition))
                {
                    problems.Add($"{mapping.TargetCheckpoint} {mapping.Kind} {id} is {target?.ToString() ?? "missing"}, not '{mapping.TargetKey}' with the definition of {source}.");
                }
            }
        }

        IReadOnlyList<TableDefinition> Definitions(string checkpoint, TableKind kind) => TableSchema.Extract(
            kind, TableSnapshot.Load(Path.Combine(PathOf(Get(checkpoint).Directory), TableSchema.FileName(kind))));
    }

    private static string Required(string? value, string name) => string.IsNullOrWhiteSpace(value)
        ? throw new InvalidDataException($"A {FileName} entry has no '{name}'.")
        : value;

    private sealed class RegistryFile
    {
        public List<CheckpointEntry>? Checkpoints { get; set; }
        public List<IdentityMappingEntry>? IdentityMappings { get; set; }
    }

    private sealed class CheckpointEntry
    {
        public string? Id { get; set; }
        public string? Label { get; set; }
        public string? Build { get; set; }
        public string? Type { get; set; }
        public int? Order { get; set; }
        public string? Directory { get; set; }
        public string? AppendedStatsDirectory { get; set; }
        public string? ItemTablesDirectory { get; set; }
        public List<string>? InstalledVersions { get; set; }
        public bool DevelopmentTarget { get; set; }
        public bool SupportedTarget { get; set; }
        public string? Description { get; set; }
    }

    private sealed class IdentityMappingEntry
    {
        public string? Table { get; set; }
        public string? SourceCheckpoint { get; set; }
        public int? SourceId { get; set; }
        public int? LastSourceId { get; set; }
        public string? SourceKey { get; set; }
        public string? Code { get; set; }
        public string? TargetCheckpoint { get; set; }
        public string? TargetKey { get; set; }
        public string? Description { get; set; }
    }
}
