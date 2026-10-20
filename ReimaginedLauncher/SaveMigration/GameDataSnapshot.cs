using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ReimaginedLauncher.SaveMigration;

public enum TableKind
{
    ItemStatCost,
    UniqueItems,
    SetItems,
    MagicPrefix,
    MagicSuffix,
}

public sealed class GameDataSnapshot
{
    private readonly IReadOnlyDictionary<TableKind, TableSnapshot> _tables;

    private GameDataSnapshot(string label, string directory, IReadOnlyDictionary<TableKind, TableSnapshot> tables)
    {
        Label = label;
        Directory = directory;
        _tables = tables;
    }

    public string Label { get; }
    public string Directory { get; }

    public TableSnapshot this[TableKind kind] => _tables[kind];

    public static GameDataSnapshot Load(string label, string directory)
    {
        var missing = TableSchema.AllKinds
            .Select(TableSchema.FileName)
            .Where(name => !File.Exists(Path.Combine(directory, name)))
            .ToList();
        if (missing.Count > 0)
        {
            throw new FileNotFoundException(
                $"Snapshot '{label}' at '{directory}' is missing: {string.Join(", ", missing)}.");
        }

        var tables = TableSchema.AllKinds.ToDictionary(
            kind => kind,
            kind => TableSnapshot.Load(Path.Combine(directory, TableSchema.FileName(kind))));
        return new GameDataSnapshot(label, Path.GetFullPath(directory), tables);
    }
}

public sealed record KnownSnapshot(string Label, string FolderName)
{
    public GameDataSnapshot Load(string dataDirectory) =>
        GameDataSnapshot.Load(Label, Path.Combine(dataDirectory, FolderName));
}

public static class KnownSnapshots
{
    public static readonly KnownSnapshot Reimagined212 = new("2.1.2", "D2RR 2.1.2");
    public static readonly KnownSnapshot Reimagined3012 = new("3.0.12", "D2RR 3.0.12");

    // The proposed PR #1181 tables are stored in the "D2RR 3.0.13" folder.
    public static readonly KnownSnapshot PullRequest1181 = new("PR #1181", "D2RR 3.0.13");

    public static readonly IReadOnlyList<(KnownSnapshot Source, KnownSnapshot Target)> SupportedComparisons =
    [
        (Reimagined212, Reimagined3012),
        (Reimagined3012, PullRequest1181),
        (Reimagined212, PullRequest1181),
    ];
}
