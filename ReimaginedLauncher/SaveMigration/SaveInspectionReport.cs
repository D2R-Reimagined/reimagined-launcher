using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace ReimaginedLauncher.SaveMigration;

public static class SaveInspectionReport
{
    public static string Write(SaveInspection inspection)
    {
        var header = inspection.Header;
        var report = new StringBuilder();
        report.AppendLine($"=== {inspection.FileName} ===");
        report.AppendLine($"SHA-256: {inspection.Sha256}");
        report.AppendLine(header.Kind == SaveFileKind.SharedStash
            ? $"Shared stash: magic valid {header.MagicValid}, stash format {header.StashFormat}, embedded item format {header.Version}, {header.ActualSize} bytes"
            : $"Character: magic valid {header.MagicValid}, binary version {header.Version}, declared size {header.DeclaredSize} "
              + $"(actual {header.ActualSize}), checksum valid {header.ChecksumValid}");

        Section(report, "Parse attempts", inspection.Attempts.Select(attempt => attempt.Succeeded
            ? $"[OK] {attempt.DefinitionLabel}: {(attempt.Character is null ? "" : $"'{attempt.Character}' level {attempt.Level}; ")}"
              + $"{attempt.CharacterStats.Count} character stats, {attempt.Items.Count} items "
              + $"({attempt.AllItems.Count() - attempt.Items.Count} socketed), trailing bytes {attempt.TrailingBytes}"
            : $"[FAILED] {attempt.DefinitionLabel}: {attempt.Error}"));

        foreach (var attempt in inspection.Attempts.Where(attempt => attempt.Succeeded))
        {
            Section(report, $"Notes ({attempt.DefinitionLabel})", attempt.Notes);
            foreach (var validation in attempt.IdValidations)
            {
                Section(report,
                    $"Saved ids vs {TableSchema.FileName(validation.Kind)} ({validation.ItemTablesLabel}): {validation.ReferenceCount} reference(s), "
                    + $"{validation.AboveFirstDivider} after the first Expansion row [divider rows: {string.Join(", ", validation.DividerRows)}], "
                    + $"{validation.TierUpgrades} on an upgraded/downgraded base tier",
                    validation.Hypotheses.Select(hypothesis => $"{hypothesis.Hypothesis}: {hypothesis.Agreeing}/{hypothesis.Total} agree")
                        .Concat(validation.Phase1Mismatches.Select(mismatch => $"! Phase 1 mismatch: {mismatch}")));
            }
        }

        if (inspection.Parsed is not { } parsed)
        {
            return report.ToString();
        }

        Section(report, $"Character stats ({parsed.DefinitionLabel})",
            parsed.CharacterStats.Select(stat => $"{stat.Id} {stat.Name} = {stat.Value}{(stat.Layer == 0 ? "" : $" (layer {stat.Layer})")}"));
        Section(report, "Saved reference mapping through Phase 1 comparisons", inspection.ReferenceMappings.Select(mapping =>
            $"[{mapping.Comparison}] {mapping.Reference.Role} {mapping.Reference.SaveId} on '{mapping.ItemCode}': {mapping.Outcome}"
            + string.Concat(mapping.Concerns.Select(concern => $"{Environment.NewLine}      ! {concern}"))));
        Section(report, "Stat concerns", inspection.StatConcerns);
        Section(report, $"Items ({parsed.DefinitionLabel})", parsed.Items.SelectMany(item => DescribeItem(item, 0)));
        return report.ToString();
    }

    private static IEnumerable<string> DescribeItem(InspectedItem item, int depth)
    {
        var references = item.References.Select(reference => $"{reference.Role} {reference.SaveId}");
        if (item.RareName is not null)
        {
            references = references.Append(item.RareName);
        }

        if (item.RunewordId is { } runeword)
        {
            references = references.Append($"runeword {runeword}");
        }

        var stats = item.Stats.Select(stat =>
            $"{stat.Id}:{stat.Name}{(stat.Layer == 0 ? "" : $"[{stat.Layer}]")}={stat.Value}{(stat.Source == "item" ? "" : $" ({stat.Source})")}");
        yield return $"{new string(' ', depth * 2)}{item.Location} | {item.Code} {item.Quality} | "
                     + $"refs: {string.Join(", ", references)} | stats: {string.Join(", ", stats)}";
        foreach (var line in item.Sockets.SelectMany(socket => DescribeItem(socket, depth + 1)))
        {
            yield return line;
        }
    }

    private static void Section(StringBuilder report, string title, IEnumerable<string> lines)
    {
        var items = lines.ToList();
        if (items.Count == 0)
        {
            return;
        }

        report.AppendLine($"-- {title} ({items.Count})");
        foreach (var line in items)
        {
            report.AppendLine($"   {line}");
        }
    }
}
