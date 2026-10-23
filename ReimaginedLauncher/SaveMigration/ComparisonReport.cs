using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace ReimaginedLauncher.SaveMigration;

public static class ComparisonReport
{
    public static string Write(SnapshotComparison comparison, int maxLinesPerSection = 200)
    {
        var report = new StringBuilder();
        report.AppendLine($"Save migration table comparison: {comparison.Source.Label} -> {comparison.Target.Label}");
        report.AppendLine($"Source: {comparison.Source.Directory}");
        report.AppendLine($"Target: {comparison.Target.Directory}");
        foreach (var table in comparison.Tables)
        {
            report.AppendLine();
            WriteTable(report, table, maxLinesPerSection);
        }

        return report.ToString();
    }

    private static void WriteTable(StringBuilder report, TableComparison table, int maxLines)
    {
        var moved = table.Moved.ToList();
        var changed = table.Changed.ToList();
        report.AppendLine($"=== {table.FileName} ===");
        report.AppendLine($"Save id rule: {TableSchema.SaveIdRule(table.Kind)}");
        report.AppendLine($"Rows (header excluded): {table.SourceDefinitions.Count} -> {table.TargetDefinitions.Count}");
        report.AppendLine(
            $"Matched: {table.Matched.Count()} (save id changed: {moved.Count}, fields changed: {changed.Count}) | " +
            $"Added: {table.Added.Count()} | Removed: {table.Removed.Count()} | Ambiguous: {table.Ambiguous.Count()}");
        report.AppendLine("Match methods: " + string.Join(", ", table.Matched
            .GroupBy(mapping => mapping.Method)
            .Select(group => $"{group.Key} {group.Count()}")));

        Section(report, "Placeholder rows (source)", table.SourceDefinitions.Where(d => d.IsPlaceholder).Select(Describe), maxLines);
        Section(report, "Placeholder rows (target)", table.TargetDefinitions.Where(d => d.IsPlaceholder).Select(Describe), maxLines);
        Section(report, "*ID column disagrees with save id (source)", CommentIdMismatches(table.SourceDefinitions), maxLines);
        Section(report, "*ID column disagrees with save id (target)", CommentIdMismatches(table.TargetDefinitions), maxLines);
        Section(report, "Duplicate keys (source)", table.SourceDuplicates.Select(DescribeDuplicate), maxLines);
        Section(report, "Duplicate keys (target)", table.TargetDuplicates.Select(DescribeDuplicate), maxLines);

        if (table.Kind == TableKind.ItemStatCost)
        {
            Section(report, "Save id changes", moved.Select(m => $"{m.Source!.Key}: {Id(m.Source)} -> {Id(m.Target)}"), maxLines);
            Section(report, "Save ids that now name a different stat", table.FindSaveIdReassignments()
                .Select(r => $"{r.SaveId}: {r.Source.Key} -> {r.Target.Key}"), maxLines);
            Section(report, "Encoding changes", changed.Select(m => $"{m.Source!.Key} ({Id(m.Source)} -> {Id(m.Target)}): {Changes(m)}"), maxLines);
        }
        else
        {
            Section(report, "Save id shifts (contiguous runs of matched definitions)", ShiftRuns(table), maxLines);
            Section(report, "Matched by renamed definition fingerprint", table.Matched
                .Where(m => m.Method == MatchMethod.DefinitionFingerprint)
                .Select(m => $"{Describe(m.Source!)} -> {Describe(m.Target!)}"), maxLines);
            Section(report, "Field changes", changed.Select(m => $"{m.Source!.Key} ({Id(m.Source)} -> {Id(m.Target)}): {Changes(m)}"), maxLines);
        }

        Section(report, "Ambiguous", table.Ambiguous.Select(m =>
            $"{Describe(m.Source!)}; candidates: [{string.Join(", ", m.Candidates.Select(Describe))}]; {m.Reason}"), maxLines);
        Section(report, "Removed", table.Removed.Select(m => Describe(m.Source!)), maxLines);
        Section(report, "Added", table.Added.Select(m => m.Target!).Select(t => table.Kind == TableKind.ItemStatCost
            ? $"{Describe(t)} [Save Bits '{t.Field("Save Bits")}', Save Add '{t.Field("Save Add")}', CSvBits '{t.Field("CSvBits")}']"
            : Describe(t)), maxLines);
    }

    private static IEnumerable<string> ShiftRuns(TableComparison table)
    {
        var matched = table.Matched
            .Where(m => m.Source!.SaveId is not null && m.Target!.SaveId is not null)
            .OrderBy(m => m.Source!.SaveId)
            .ToList();
        var index = 0;
        while (index < matched.Count)
        {
            var delta = matched[index].Target!.SaveId!.Value - matched[index].Source!.SaveId!.Value;
            var end = index;
            while (end + 1 < matched.Count
                && matched[end + 1].Target!.SaveId!.Value - matched[end + 1].Source!.SaveId!.Value == delta)
            {
                end++;
            }

            if (delta != 0)
            {
                var first = matched[index];
                var last = matched[end];
                yield return end == index
                    ? $"{first.Source!.Key}: {Id(first.Source)} -> {Id(first.Target)} ({delta:+0;-0})"
                    : $"{Id(first.Source)}..{Id(last.Source)} -> {Id(first.Target)}..{Id(last.Target)} ({delta:+0;-0}, {end - index + 1} definitions, '{first.Source!.Key}' .. '{last.Source!.Key}')";
            }

            index = end + 1;
        }
    }

    private static IEnumerable<string> CommentIdMismatches(IEnumerable<TableDefinition> definitions) => definitions
        .Where(d => d.CommentIdDisagrees)
        .Select(d => $"{Describe(d)} has *ID {d.CommentId}");

    private static void Section(StringBuilder report, string title, IEnumerable<string> lines, int maxLines)
    {
        var items = lines.ToList();
        if (items.Count == 0)
        {
            return;
        }

        report.AppendLine($"-- {title} ({items.Count})");
        foreach (var line in items.Take(maxLines))
        {
            report.AppendLine($"   {line}");
        }

        if (items.Count > maxLines)
        {
            report.AppendLine($"   ... {items.Count - maxLines} more in the structured results.");
        }
    }

    private static string Changes(DefinitionMapping mapping) =>
        string.Join("; ", mapping.FieldChanges.Select(c => $"{c.Column} '{c.Source}' -> '{c.Target}'"));

    private static string DescribeDuplicate(DuplicateKey duplicate) =>
        $"'{duplicate.Key}' x{duplicate.Definitions.Count}: {string.Join(", ", duplicate.Definitions.Select(d => Id(d)))}";

    private static string Describe(TableDefinition definition) =>
        $"{(definition.IsPlaceholder ? $"<{definition.Row.Text(TableSchema.KeyColumn(definition.Kind))}>" : definition.Key)} " +
        $"[id {Id(definition)}, row {definition.PhysicalIndex}{CodeSuffix(definition)}]";

    private static string CodeSuffix(TableDefinition definition) =>
        TableSchema.CodeColumn(definition.Kind) is { } column && definition.Field(column).Length > 0
            ? $", {definition.Field(column)}"
            : string.Empty;

    private static string Id(TableDefinition? definition) =>
        definition?.SaveId?.ToString(CultureInfo.InvariantCulture) ?? "-";
}
