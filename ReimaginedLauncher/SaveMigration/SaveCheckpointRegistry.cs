using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace ReimaginedLauncher.SaveMigration;

public enum SaveCheckpointType
{
    Release,
}

/// <summary>One registered save-sensitive game data layout, read from <c>checkpoints.json</c>.</summary>
/// <param name="Order">Explicit chronological order. Folder names are never sorted.</param>
/// <param name="InstalledVersions">modinfo.json <c>version</c> values that identify an installation using this layout.</param>
public sealed record SaveCheckpoint(
    string Id,
    string Label,
    string Build,
    SaveCheckpointType Type,
    int Order,
    string Directory,
    IReadOnlyList<string> InstalledVersions,
    string? Description)
{
    public string DisplayName => Build;
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

public sealed record SaveCheckpointRegistry(
    string DataDirectory,
    IReadOnlyList<SaveCheckpoint> Checkpoints,
    IReadOnlyList<SaveIdentityMapping> IdentityMappings)
{
    public const string FileName = "checkpoints.json";

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
                Enum.TryParse<SaveCheckpointType>(entry.Type, ignoreCase: true, out var type) &&
                type == SaveCheckpointType.Release
                    ? type
                    : throw new InvalidDataException(
                        $"Checkpoint '{entry.Id}' has unknown type '{entry.Type}' (release only)."),
                entry.Order ?? throw new InvalidDataException($"Checkpoint '{entry.Id}' has no order."),
                Required(entry.Directory, "directory"),
                [
                    .. (entry.InstalledVersions ?? []).Select(version => version.Trim())
                    .Where(version => version.Length > 0)
                ],
                entry.Description))
            .OrderBy(checkpoint => checkpoint.Order)
            .ToList();

        if (checkpoints.GroupBy(checkpoint => checkpoint.Id, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(group => group.Count() > 1) is { } duplicateId)
        {
            throw new InvalidDataException($"{FileName} registers checkpoint '{duplicateId.Key}' more than once.");
        }

        if (checkpoints.GroupBy(checkpoint => checkpoint.Order).FirstOrDefault(group => group.Count() > 1) is
            { } duplicateOrder)
        {
            throw new InvalidDataException($"{FileName} gives order {duplicateOrder.Key} to more than one checkpoint.");
        }

        var ids = checkpoints.Select(checkpoint => checkpoint.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var mappings = (file.IdentityMappings ?? []).Select(entry =>
            {
                var kind = Enum.TryParse<TableKind>(entry.Table, ignoreCase: true, out var parsed) &&
                           parsed is not TableKind.ItemStatCost
                    ? parsed
                    : throw new InvalidDataException(
                        $"Identity mapping table '{entry.Table}' must be UniqueItems, SetItems, MagicPrefix or MagicSuffix.");
                var sourceId = entry.SourceId ?? throw new InvalidDataException("An identity mapping has no sourceId.");
                var affix = kind is TableKind.MagicPrefix or TableKind.MagicSuffix;
                if (affix ? entry.Code is not null || entry.LastSourceId < sourceId : entry.LastSourceId is not null)
                {
                    throw new InvalidDataException(
                        $"Identity mapping '{entry.SourceKey}': affix mappings take a sourceId..lastSourceId range and no code; "
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

    /// <summary>Every earlier frozen release is a direct source candidate; saves never step through intermediate versions.</summary>
    public IReadOnlyList<SaveCheckpoint> SourcesBefore(int targetOrder) =>
        [.. Checkpoints.Where(checkpoint => checkpoint.Order < targetOrder)];

    public string PathOf(string directory) => Path.GetFullPath(Path.Combine(DataDirectory, directory));

    public SaveCheckpoint? LatestCheckpoint => Checkpoints.MaxBy(checkpoint => checkpoint.Order);

    /// <summary>Mappings between earlier releases, including any registered changes through <paramref name="targetOrder"/>.</summary>
    public IReadOnlyList<ResolvedIdentityMapping> IdentityMappingsUpTo(int targetOrder)
    {
        var resolved = new List<ResolvedIdentityMapping>();
        foreach (var mapping in IdentityMappings)
        {
            if (Get(mapping.SourceCheckpoint).Order >= targetOrder || Get(mapping.TargetCheckpoint).Order > targetOrder)
            {
                continue;
            }

            var (key, order) = (mapping.TargetKey, Get(mapping.TargetCheckpoint).Order);
            for (var step = 0; step < IdentityMappings.Count; step++)
            {
                var next = IdentityMappings
                    .Where(later => later.Kind == mapping.Kind
                                    && string.Equals(later.Code, mapping.Code, StringComparison.OrdinalIgnoreCase)
                                    && (!mapping.IsAffix || (later.SourceId, later.LastSourceId) ==
                                        (mapping.SourceId, mapping.LastSourceId))
                                    && later.SourceKey.Equals(key, StringComparison.OrdinalIgnoreCase)
                                    && Get(later.SourceCheckpoint).Order >= order
                                    && Get(later.TargetCheckpoint).Order > order
                                    && Get(later.TargetCheckpoint).Order <= targetOrder)
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
            var source = Definitions(mapping.SourceCheckpoint, mapping.Kind)
                .FirstOrDefault(definition => definition.SaveId == mapping.SourceId);
            if (source is null || !source.Key.Equals(mapping.SourceKey, StringComparison.OrdinalIgnoreCase)
                               || !source.Field(codeColumn).Equals(mapping.Code, StringComparison.OrdinalIgnoreCase))
            {
                problems.Add(
                    $"{mapping.SourceCheckpoint} {mapping.Kind} {mapping.SourceId} is {source?.ToString() ?? "missing"}, not '{mapping.SourceKey}' on '{mapping.Code}'.");
            }

            var targets = Definitions(mapping.TargetCheckpoint, mapping.Kind)
                .Where(definition => !definition.IsPlaceholder &&
                                     definition.Key.Equals(mapping.TargetKey, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (targets.Count != 1 ||
                !targets[0].Field(codeColumn).Equals(mapping.Code, StringComparison.OrdinalIgnoreCase))
            {
                problems.Add(
                    $"{mapping.TargetCheckpoint} has {targets.Count} '{mapping.TargetKey}' row(s); exactly one on '{mapping.Code}' is required.");
            }
        }

        return problems;

        // Each covered row must keep its position and every definition field; only the name may differ.
        void ValidateAffixRename(SaveIdentityMapping mapping)
        {
            var sources = Definitions(mapping.SourceCheckpoint, mapping.Kind);
            var targets = Definitions(mapping.TargetCheckpoint, mapping.Kind);
            var last = mapping.LastSourceId ?? mapping.SourceId;
            var named = sources.Count(definition =>
                !definition.IsPlaceholder &&
                definition.Key.Equals(mapping.SourceKey, StringComparison.OrdinalIgnoreCase));
            if (named != last - mapping.SourceId + 1)
            {
                problems.Add(
                    $"{mapping.SourceCheckpoint} has {named} '{mapping.SourceKey}' row(s), but the mapping covers ids {mapping.SourceId}-{last}.");
            }

            for (var id = mapping.SourceId; id <= last; id++)
            {
                var source = sources.FirstOrDefault(definition => definition.SaveId == id);
                var target = targets.FirstOrDefault(definition => definition.SaveId == id);
                if (source is null || !source.Key.Equals(mapping.SourceKey, StringComparison.OrdinalIgnoreCase))
                {
                    problems.Add(
                        $"{mapping.SourceCheckpoint} {mapping.Kind} {id} is {source?.ToString() ?? "missing"}, not '{mapping.SourceKey}'.");
                }
                else if (target is null || !target.Key.Equals(mapping.TargetKey, StringComparison.OrdinalIgnoreCase)
                                        || TableSchema.Fingerprint(source, TableSchema.AffixDefinition) !=
                                        TableSchema.Fingerprint(target, TableSchema.AffixDefinition))
                {
                    problems.Add(
                        $"{mapping.TargetCheckpoint} {mapping.Kind} {id} is {target?.ToString() ?? "missing"}, not '{mapping.TargetKey}' with the definition of {source}.");
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
        public List<string>? InstalledVersions { get; set; }
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