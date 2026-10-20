using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ReimaginedLauncher.SaveMigration;

public enum MappingStatus
{
    Matched,
    Added,
    Removed,
    Ambiguous,
}

public enum MatchMethod
{
    None,
    /// <summary>The semantic key alone is unique on both sides (stat name, unique/set index).</summary>
    Key,
    /// <summary>Same key and base item code, after the key alone was duplicated.</summary>
    KeyAndCode,
    /// <summary>Same key and identical compared definition fields.</summary>
    KeyAndDefinition,
    /// <summary>Same affix name and mod codes/params/group/item types; ranges or levels may differ.</summary>
    KeyAndModStructure,
    /// <summary>Different key but identical code and properties, unique on both sides (a rename).</summary>
    DefinitionFingerprint,
}

public sealed record FieldChange(string Column, string Source, string Target);

public sealed record DefinitionMapping(
    MappingStatus Status,
    MatchMethod Method,
    TableDefinition? Source,
    TableDefinition? Target,
    IReadOnlyList<TableDefinition> Candidates,
    IReadOnlyList<FieldChange> FieldChanges,
    string? Reason = null)
{
    public bool SaveIdChanged => Status == MappingStatus.Matched && Source!.SaveId != Target!.SaveId;
}

public sealed record DuplicateKey(string Key, IReadOnlyList<TableDefinition> Definitions);

/// <summary>A save id that names one definition in the source and a different one in the target.</summary>
public sealed record SaveIdReassignment(int SaveId, TableDefinition Source, TableDefinition Target);

public sealed class TableComparison(
    TableKind kind,
    string sourceLabel,
    string targetLabel,
    IReadOnlyList<TableDefinition> sourceDefinitions,
    IReadOnlyList<TableDefinition> targetDefinitions,
    IReadOnlyList<DefinitionMapping> mappings)
{
    public TableKind Kind { get; } = kind;
    public string FileName => TableSchema.FileName(Kind);
    public string SourceLabel { get; } = sourceLabel;
    public string TargetLabel { get; } = targetLabel;
    public IReadOnlyList<TableDefinition> SourceDefinitions { get; } = sourceDefinitions;
    public IReadOnlyList<TableDefinition> TargetDefinitions { get; } = targetDefinitions;
    public IReadOnlyList<DefinitionMapping> Mappings { get; } = mappings;

    public IReadOnlyList<DuplicateKey> SourceDuplicates { get; } = FindDuplicates(sourceDefinitions);
    public IReadOnlyList<DuplicateKey> TargetDuplicates { get; } = FindDuplicates(targetDefinitions);

    public IEnumerable<DefinitionMapping> Matched => Mappings.Where(mapping => mapping.Status == MappingStatus.Matched);
    public IEnumerable<DefinitionMapping> Moved => Matched.Where(mapping => mapping.SaveIdChanged);
    public IEnumerable<DefinitionMapping> Changed => Matched.Where(mapping => mapping.FieldChanges.Count > 0);
    public IEnumerable<DefinitionMapping> Added => Mappings.Where(mapping => mapping.Status == MappingStatus.Added);
    public IEnumerable<DefinitionMapping> Removed => Mappings.Where(mapping => mapping.Status == MappingStatus.Removed);
    public IEnumerable<DefinitionMapping> Ambiguous => Mappings.Where(mapping => mapping.Status == MappingStatus.Ambiguous);

    public IEnumerable<DefinitionMapping> ForSourceKey(string key) => Mappings.Where(mapping =>
        mapping.Source is not null && mapping.Source.Key.Equals(key, StringComparison.OrdinalIgnoreCase));

    public DefinitionMapping? ForSourceSaveId(int saveId) =>
        Mappings.FirstOrDefault(mapping => mapping.Source?.SaveId == saveId);

    /// <summary>Source save id to target save id, for unambiguous matches only.</summary>
    public IReadOnlyDictionary<int, int> BuildSaveIdMap() => Matched
        .Where(mapping => mapping.Source!.SaveId is not null && mapping.Target!.SaveId is not null)
        .ToDictionary(mapping => mapping.Source!.SaveId!.Value, mapping => mapping.Target!.SaveId!.Value);

    public IReadOnlyList<SaveIdReassignment> FindSaveIdReassignments()
    {
        var targets = TargetDefinitions
            .Where(definition => definition is { SaveId: not null, IsPlaceholder: false })
            .GroupBy(definition => definition.SaveId!.Value)
            .ToDictionary(group => group.Key, group => group.First());
        return SourceDefinitions
            .Where(definition => definition is { SaveId: not null, IsPlaceholder: false })
            .Where(definition => targets.TryGetValue(definition.SaveId!.Value, out var target)
                && !target.Key.Equals(definition.Key, StringComparison.OrdinalIgnoreCase))
            .Select(definition => new SaveIdReassignment(definition.SaveId!.Value, definition, targets[definition.SaveId!.Value]))
            .ToList();
    }

    private static List<DuplicateKey> FindDuplicates(IReadOnlyList<TableDefinition> definitions) => definitions
        .Where(definition => !definition.IsPlaceholder)
        .GroupBy(definition => definition.Key, StringComparer.OrdinalIgnoreCase)
        .Where(group => group.Count() > 1)
        .Select(group => new DuplicateKey(group.Key, group.ToList()))
        .ToList();
}

public sealed class SnapshotComparison(GameDataSnapshot source, GameDataSnapshot target, IReadOnlyList<TableComparison> tables)
{
    public GameDataSnapshot Source { get; } = source;
    public GameDataSnapshot Target { get; } = target;
    public IReadOnlyList<TableComparison> Tables { get; } = tables;

    public TableComparison this[TableKind kind] => Tables.First(table => table.Kind == kind);
}
