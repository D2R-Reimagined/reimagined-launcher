using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace ReimaginedLauncher.SaveMigration;

/// <summary>One data row of a table, with the id a save file uses to reference it.</summary>
/// <param name="SaveId">Null for unique/set divider rows, which consume no id.</param>
/// <param name="Key">The semantic identity (Stat, index or name column). Empty for placeholders.</param>
/// <param name="CommentId">The raw <c>*ID</c> column. It is a comment column and is never used as the id.</param>
/// <param name="IsPlaceholder">A row without semantic identity (blank key or Expansion divider).</param>
public sealed record TableDefinition(
    TableKind Kind,
    int PhysicalIndex,
    int? SaveId,
    string Key,
    string CommentId,
    bool IsPlaceholder,
    TableRow Row)
{
    public string Field(string column) => Row.Text(column);

    public bool CommentIdDisagrees =>
        SaveId is { } saveId
        && int.TryParse(CommentId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var commentId)
        && commentId != saveId;

    public override string ToString() => $"{Key} (save id {SaveId?.ToString(CultureInfo.InvariantCulture) ?? "-"}, row {PhysicalIndex})";
}

public static class TableSchema
{
    public static readonly IReadOnlyList<TableKind> AllKinds =
        [TableKind.ItemStatCost, TableKind.UniqueItems, TableKind.SetItems, TableKind.MagicPrefix, TableKind.MagicSuffix];

    public static readonly IReadOnlyList<string> StatEncodingFields =
    [
        "Saved", "Signed", "Encode", "Save Bits", "Save Add", "Save Param Bits",
        "CSvSigned", "CSvBits", "CSvParam", "ValShift",
    ];

    private static readonly IReadOnlyList<string> UniquePropertyFields = [.. PropertyColumns("", 12, [""])];

    private static readonly IReadOnlyList<string> SetPropertyFields =
        [.. PropertyColumns("", 9, [""]), .. PropertyColumns("a", 5, ["a", "b"])];

    private static readonly IReadOnlyList<string> AffixModFields =
        [.. Enumerable.Range(1, 3).SelectMany(slot => new[] { $"mod{slot}code", $"mod{slot}param", $"mod{slot}min", $"mod{slot}max" })];

    private static readonly IReadOnlyList<string> AffixTypeFields =
        [.. Enumerable.Range(1, 7).Select(slot => $"itype{slot}"), .. Enumerable.Range(1, 5).Select(slot => $"etype{slot}")];

    private static readonly IReadOnlyList<string> AffixStructureFields =
        ["group", .. Enumerable.Range(1, 3).SelectMany(slot => new[] { $"mod{slot}code", $"mod{slot}param" }), .. AffixTypeFields];

    private static readonly IReadOnlyList<string> AffixDefinitionFields =
    [
        "version", "spawnable", "rare", "level", "maxlevel", "levelreq", "classspecific", "class",
        "classlevelreq", "frequency", "group", .. AffixModFields, .. AffixTypeFields, "multiply", "add",
    ];

    public static string FileName(TableKind kind) => kind switch
    {
        TableKind.ItemStatCost => "itemstatcost.txt",
        TableKind.UniqueItems => "uniqueitems.txt",
        TableKind.SetItems => "setitems.txt",
        TableKind.MagicPrefix => "magicprefix.txt",
        TableKind.MagicSuffix => "magicsuffix.txt",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    public static string KeyColumn(TableKind kind) => kind switch
    {
        TableKind.ItemStatCost => "Stat",
        TableKind.UniqueItems or TableKind.SetItems => "index",
        _ => "name",
    };

    public static string SaveIdRule(TableKind kind) => kind switch
    {
        TableKind.ItemStatCost => "zero-based physical row (header excluded, placeholders included)",
        TableKind.UniqueItems or TableKind.SetItems => "zero-based count of rows, skipping blank/Expansion divider rows",
        _ => "physical row + 1 (0 means no affix; blank/Expansion rows still consume an id)",
    };

    /// <summary>The base item code column used to tell same-named uniques/sets apart.</summary>
    public static string? CodeColumn(TableKind kind) => kind switch
    {
        TableKind.UniqueItems => "code",
        TableKind.SetItems => "item",
        _ => null,
    };

    public static IReadOnlyList<string> PropertyFields(TableKind kind) => kind switch
    {
        TableKind.UniqueItems => UniquePropertyFields,
        TableKind.SetItems => SetPropertyFields,
        _ => [],
    };

    /// <summary>Fields compared between matched definitions and reported as field changes.</summary>
    public static IReadOnlyList<string> ComparedFields(TableKind kind) => kind switch
    {
        TableKind.ItemStatCost => StatEncodingFields,
        TableKind.UniqueItems => ["code", "lvl", "lvl req", .. UniquePropertyFields],
        TableKind.SetItems => ["set", "item", "lvl", "lvl req", .. SetPropertyFields],
        _ => AffixDefinitionFields,
    };

    public static IReadOnlyList<string> AffixDefinition => AffixDefinitionFields;

    public static IReadOnlyList<string> AffixStructure => AffixStructureFields;

    public static IReadOnlyList<TableDefinition> Extract(TableKind kind, TableSnapshot table)
    {
        var keyColumn = KeyColumn(kind);
        if (!table.HasColumn(keyColumn))
        {
            throw new InvalidDataException($"The table '{table.FileName}' has no '{keyColumn}' column.");
        }

        var definitions = new List<TableDefinition>(table.Rows.Count);
        var runningId = -1;
        foreach (var row in table.Rows)
        {
            var key = row.Text(keyColumn);
            var isPlaceholder = key.Length == 0 || key.Equals("Expansion", StringComparison.OrdinalIgnoreCase);
            int? saveId = kind switch
            {
                TableKind.ItemStatCost => row.PhysicalIndex,
                TableKind.UniqueItems or TableKind.SetItems => isPlaceholder ? null : (int?)++runningId,
                _ => row.PhysicalIndex + 1,
            };

            definitions.Add(new TableDefinition(
                kind, row.PhysicalIndex, saveId, isPlaceholder ? string.Empty : key, row.Text("*ID"), isPlaceholder, row));
        }

        return definitions;
    }

    internal static string Fingerprint(TableDefinition definition, IEnumerable<string> fields) =>
        string.Join("\u001f", fields.Select(definition.Field));

    private static IEnumerable<string> PropertyColumns(string prefix, int slots, string[] halves) =>
        Enumerable.Range(1, slots).SelectMany(slot => halves.SelectMany(half => new[]
        {
            $"{prefix}prop{slot}{half}", $"{prefix}par{slot}{half}", $"{prefix}min{slot}{half}", $"{prefix}max{slot}{half}",
        }));
}
