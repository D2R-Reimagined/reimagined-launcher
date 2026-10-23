using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace ReimaginedLauncher.SaveMigration;

public static class SaveMigrationReport
{
    public static string Write(SaveMigrationResult result)
    {
        var report = new StringBuilder();
        report.AppendLine($"=== {Path.GetFileName(result.InputPath)} ===");
        report.AppendLine($"Status: {result.Status switch
        {
            MigrationStatus.Verified => "VERIFIED - every check passed and every reference was mapped or already compatible",
            MigrationStatus.NeedsManualInspection => "WARNING - verified output, but entries below need manual inspection",
            MigrationStatus.Experimental => "EXPERIMENTAL - the output passed the critical structural checks, but the uncertainties below were "
                                           + "accepted by Force Conversion or a manual source choice and are NOT verified",
            _ when result.NeedsForce => "NOT CONVERTED - recoverable, but only by accepting the uncertainties below with Force Conversion",
            _ => "FAILED - no approved output",
        }}");
        report.AppendLine($"Input: {result.InputPath} ({result.Kind}, version {result.SourceVersion})");
        report.AppendLine($"Output: {result.OutputPath ?? "none"}{(result.OutputVersion is { } version ? $" (version {version})" : "")}");
        if (result.RejectedOutputPath is not null)
        {
            report.AppendLine($"Unapproved output kept for inspection: {result.RejectedOutputPath}");
        }

        report.AppendLine($"Layouts: {result.SourceLayout ?? "not identified"} -> {result.TargetLayout}");
        if (result.Overrides.Any)
        {
            report.AppendLine($"Overrides: Force Conversion {(result.Overrides.Force ? "yes" : "no")}, "
                              + $"manual source layout {result.Overrides.SourceLayout ?? "none"}");
            if (result.Overrides.RebuildWithTarget)
            {
                report.AppendLine("This is a best-effort round-trip attempt using the installed target definitions, not a historical migration.");
            }
        }

        report.AppendLine($"Items (including socketed): {result.ItemsInspected} inspected, {result.ItemsChanged} changed, {result.ItemsUnchanged} unchanged");
        report.AppendLine("A D2SSharp parse does not prove the game loads this file; no in-game test has been performed.");

        Section(report, "Blockers", result.Blockers);
        Section(report, result.Overrides.Force ? "Accepted uncertainties (changed, lost or unverified)" : "Uncertainties that need Force Conversion",
            result.Uncertainties.Concat(result.References.Where(reference => reference.Blocking).Select(reference => $"Unresolved identity: {reference}")));
        Section(report, "Warnings", result.Warnings);
        Section(report, "Verification", result.Checks.Select(check =>
            $"[{(check.Passed ? "PASS" : check.Critical ? "FAIL, critical" : "FAIL")}] {check.Name}: {check.Detail}"));
        Section(report, "Source identification", result.Identification);

        Section(report, "Id changes", result.References.Where(reference => reference.Outcome == ReferenceOutcome.Mapped).Select(reference => reference.ToString()));
        Section(report, "Preserved with warning", result.References.Where(reference => reference.Outcome == ReferenceOutcome.PreservedWithWarning).Select(reference => reference.ToString()));
        Section(report, "Ambiguous or incompatible (manual attention)", result.References.Where(reference => reference.Outcome == ReferenceOutcome.ManualAttention).Select(reference => reference.ToString()));
        Section(report, "Already compatible references", result.References
            .Where(reference => reference.Outcome == ReferenceOutcome.AlreadyCompatible)
            .GroupBy(reference => reference.Kind)
            .Select(group => $"{TableSchema.FileName(group.Key)}: {group.Count()}"));

        var changedStats = result.Stats.Where(stat => stat.IdChanged || stat.EncodingChanged || stat.Problems.Count > 0).ToList();
        Section(report, "Stat changes", changedStats.Select(stat =>
            $"[{stat.Scope}] {stat.SourceName} {stat.SourceId} -> {stat.TargetName} {stat.TargetId}, {stat.Uses} use(s), values {stat.MinValue}..{stat.MaxValue}"
            + (stat.EncodingChanged ? $"; {stat.SourceEncoding} -> {stat.TargetEncoding}" : "")
            + (stat.Problems.Count == 0 ? "; values fit and are preserved" : $"; PROBLEMS: {string.Join("; ", stat.Problems)}")));
        Section(report, "Stats with unchanged id and encoding", result.Stats
            .Where(stat => !changedStats.Contains(stat))
            .GroupBy(stat => stat.Scope)
            .Select(group => $"{group.Key}: {group.Count()} stat id(s), {group.Sum(stat => stat.Uses)} use(s)"));
        Section(report, "Custom inventory page items", result.CustomPageItems.Select(item => item.ToString()));
        Section(report, "Notes", result.Notes);
        return report.ToString();
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