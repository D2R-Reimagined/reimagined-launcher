using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ReimaginedLauncher.SaveMigration;

public static class TableComparer
{
    private sealed record Stage(MatchMethod Method, Func<TableDefinition, string> Refine);

    public static SnapshotComparison Compare(GameDataSnapshot source, GameDataSnapshot target) => new(
        source,
        target,
        TableSchema.AllKinds
            .Select(kind => CompareTable(kind, source[kind], target[kind], source.Label, target.Label))
            .ToList());

    public static TableComparison CompareTable(
        TableKind kind, TableSnapshot source, TableSnapshot target, string sourceLabel = "source", string targetLabel = "target")
    {
        var sourceDefinitions = TableSchema.Extract(kind, source);
        var targetDefinitions = TableSchema.Extract(kind, target);
        var mappings = new List<DefinitionMapping>();
        var leftoverSources = new List<TableDefinition>();
        var leftoverTargets = new List<TableDefinition>();

        var targetGroups = targetDefinitions
            .Where(definition => !definition.IsPlaceholder)
            .GroupBy(definition => definition.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);
        var sourceGroups = sourceDefinitions
            .Where(definition => !definition.IsPlaceholder)
            .GroupBy(definition => definition.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var group in sourceGroups)
        {
            var targets = targetGroups.GetValueOrDefault(group.Key) ?? [];
            MatchGroup(kind, group.Key, group.ToList(), targets, mappings, leftoverSources, leftoverTargets);
        }

        var sourceKeys = sourceGroups.Select(group => group.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        leftoverTargets.AddRange(targetGroups.Where(pair => !sourceKeys.Contains(pair.Key)).SelectMany(pair => pair.Value));

        if (TableSchema.CodeColumn(kind) is { } codeColumn)
        {
            MatchRenamed(kind, codeColumn, mappings, leftoverSources, leftoverTargets);
        }

        mappings.AddRange(leftoverSources.Select(definition => new DefinitionMapping(
            MappingStatus.Removed, MatchMethod.None, definition, null, [], [], "No target definition with this identity.")));
        mappings.AddRange(leftoverTargets.Select(definition => new DefinitionMapping(
            MappingStatus.Added, MatchMethod.None, null, definition, [], [])));

        var ordered = mappings
            .OrderBy(mapping => mapping.Source is null ? 1 : 0)
            .ThenBy(mapping => (mapping.Source ?? mapping.Target)!.PhysicalIndex)
            .ToList();
        return new TableComparison(kind, sourceLabel, targetLabel, sourceDefinitions, targetDefinitions, ordered);
    }

    private static IReadOnlyList<Stage> Stages(TableKind kind)
    {
        var codeColumn = TableSchema.CodeColumn(kind);
        var properties = TableSchema.PropertyFields(kind);
        return kind switch
        {
            TableKind.ItemStatCost => [new Stage(MatchMethod.Key, _ => string.Empty)],
            TableKind.UniqueItems or TableKind.SetItems =>
            [
                new Stage(MatchMethod.Key, _ => string.Empty),
                new Stage(MatchMethod.KeyAndCode, definition => definition.Field(codeColumn!)),
                new Stage(MatchMethod.KeyAndDefinition,
                    definition => TableSchema.Fingerprint(definition, [codeColumn!, .. properties])),
            ],
            // Affix names repeat across tiers, so a name alone never produces a mapping.
            _ =>
            [
                new Stage(MatchMethod.KeyAndDefinition, definition => TableSchema.Fingerprint(definition, TableSchema.AffixDefinition)),
                new Stage(MatchMethod.KeyAndModStructure, definition => TableSchema.Fingerprint(definition, TableSchema.AffixStructure)),
            ],
        };
    }

    private static void MatchGroup(
        TableKind kind,
        string key,
        List<TableDefinition> sources,
        List<TableDefinition> targets,
        List<DefinitionMapping> mappings,
        List<TableDefinition> leftoverSources,
        List<TableDefinition> leftoverTargets)
    {
        var remainingSources = sources.ToList();
        var remainingTargets = targets.ToList();
        foreach (var stage in Stages(kind))
        {
            var targetsByRefinement = remainingTargets.GroupBy(stage.Refine).ToDictionary(group => group.Key, group => group.ToList());
            foreach (var refined in remainingSources.GroupBy(stage.Refine).ToList())
            {
                if (refined.Count() != 1
                    || !targetsByRefinement.TryGetValue(refined.Key, out var refinedTargets)
                    || refinedTargets.Count != 1)
                {
                    continue;
                }

                var source = refined.First();
                var target = refinedTargets[0];
                mappings.Add(Match(kind, stage.Method, source, target));
                remainingSources.Remove(source);
                remainingTargets.Remove(target);
            }
        }

        if (remainingSources.Count == 0 || remainingTargets.Count == 0)
        {
            leftoverSources.AddRange(remainingSources);
            leftoverTargets.AddRange(remainingTargets);
            return;
        }

        var reason = kind switch
        {
            TableKind.ItemStatCost =>
                $"Stat '{key}' is defined {remainingSources.Count} time(s) in the source and {remainingTargets.Count} time(s) in the target.",
            TableKind.UniqueItems or TableKind.SetItems =>
                $"Key '{key}' has {remainingSources.Count} unresolved source and {remainingTargets.Count} target definitions that code and properties cannot tell apart.",
            _ =>
                $"Name '{key}' has {remainingSources.Count} unresolved source and {remainingTargets.Count} target definitions whose mods differ; affixes are not mapped by name alone.",
        };
        mappings.AddRange(remainingSources.Select(source => new DefinitionMapping(
            MappingStatus.Ambiguous, MatchMethod.None, source, null, remainingTargets, [], reason)));
    }

    private static void MatchRenamed(
        TableKind kind,
        string codeColumn,
        List<DefinitionMapping> mappings,
        List<TableDefinition> leftoverSources,
        List<TableDefinition> leftoverTargets)
    {
        string Fingerprint(TableDefinition definition) =>
            TableSchema.Fingerprint(definition, [codeColumn, .. TableSchema.PropertyFields(kind)]);

        var targetsByFingerprint = leftoverTargets
            .Where(definition => definition.Field(codeColumn).Length > 0)
            .GroupBy(Fingerprint)
            .ToDictionary(group => group.Key, group => group.ToList());
        foreach (var group in leftoverSources.Where(definition => definition.Field(codeColumn).Length > 0).GroupBy(Fingerprint).ToList())
        {
            if (group.Count() != 1 || !targetsByFingerprint.TryGetValue(group.Key, out var targets) || targets.Count != 1)
            {
                continue;
            }

            var source = group.First();
            var target = targets[0];
            var mapping = Match(kind, MatchMethod.DefinitionFingerprint, source, target);
            mappings.Add(mapping with
            {
                FieldChanges = [new FieldChange(TableSchema.KeyColumn(kind), source.Key, target.Key), .. mapping.FieldChanges],
                Reason = $"Renamed '{source.Key}' -> '{target.Key}' with identical code and properties.",
            });
            leftoverSources.Remove(source);
            leftoverTargets.Remove(target);
        }
    }

    private static DefinitionMapping Match(TableKind kind, MatchMethod method, TableDefinition source, TableDefinition target)
    {
        var changes = TableSchema.ComparedFields(kind)
            .Where(column => !string.Equals(source.Field(column), target.Field(column), StringComparison.Ordinal))
            .Select(column => new FieldChange(column, source.Field(column), target.Field(column)))
            .ToList();
        return new DefinitionMapping(MappingStatus.Matched, method, source, target, [], changes);
    }
}
