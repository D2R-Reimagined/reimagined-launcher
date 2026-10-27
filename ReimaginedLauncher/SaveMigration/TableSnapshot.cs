using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace ReimaginedLauncher.SaveMigration;

public sealed class TableRow(TableSnapshot table, int physicalIndex, IReadOnlyList<string> cells)
{
    public TableSnapshot Table { get; } = table;

    // Zero-based position among the lines after the header. Blank and placeholder lines keep their slot.
    public int PhysicalIndex { get; } = physicalIndex;

    public IReadOnlyList<string> Cells { get; } = cells;

    public bool IsBlank => Cells.All(string.IsNullOrWhiteSpace);

    public string Raw(string column) =>
        Table.TryGetColumn(column, out var index) && index < Cells.Count ? Cells[index] : string.Empty;

    public string Text(string column) => Raw(column).Trim();
}

public sealed class TableSnapshot
{
    private readonly Dictionary<string, int> _columnIndices = new(StringComparer.OrdinalIgnoreCase);

    private TableSnapshot(string fileName, string sourcePath, IReadOnlyList<string> columns)
    {
        FileName = fileName;
        SourcePath = sourcePath;
        Columns = columns;
        var duplicates = new List<string>();
        for (var index = 0; index < columns.Count; index++)
        {
            if (!_columnIndices.TryAdd(columns[index].Trim(), index))
            {
                duplicates.Add(columns[index]);
            }
        }

        DuplicateColumns = duplicates;
    }

    public string FileName { get; }
    public string SourcePath { get; }
    public IReadOnlyList<string> Columns { get; }

    // Column names that appear more than once; lookups by name resolve to the first occurrence.
    public IReadOnlyList<string> DuplicateColumns { get; }

    public IReadOnlyList<TableRow> Rows { get; private set; } = [];

    public bool HasColumn(string column) => _columnIndices.ContainsKey(column);

    public bool TryGetColumn(string column, out int index) => _columnIndices.TryGetValue(column, out index);

    public static TableSnapshot Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"The table '{Path.GetFileName(path)}' was not found at '{path}'.", path);
        }

        // Latin1 maps every byte to one char, so cell values round-trip exactly.
        var content = File.ReadAllText(path, Encoding.Latin1);
        return Parse(Path.GetFileName(path), content, Path.GetFullPath(path));
    }

    public static TableSnapshot Parse(string fileName, string content, string sourcePath = "")
    {
        var lines = content.Split('\n');
        var lineCount = lines.Length > 0 && lines[^1].Length == 0 ? lines.Length - 1 : lines.Length;
        if (lineCount == 0)
        {
            throw new InvalidDataException($"The table '{fileName}' has no header.");
        }

        var table = new TableSnapshot(fileName, sourcePath, SplitLine(lines[0]));
        var rows = new List<TableRow>(lineCount - 1);
        for (var line = 1; line < lineCount; line++)
        {
            rows.Add(new TableRow(table, line - 1, SplitLine(lines[line])));
        }

        table.Rows = rows;
        return table;
    }

    private static string[] SplitLine(string line) => line.TrimEnd('\r').Split('\t');
}
