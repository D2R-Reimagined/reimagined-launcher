using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using D2SSharp.Data;
using D2SSharp.Enums;

namespace ReimaginedLauncher.SaveMigration;

/// <summary>The game data a save is read with: the stat layout D2SSharp decodes and the item tables saved ids point into.</summary>
public sealed class SaveDataDefinition
{
    private readonly Func<uint, IExternalData> _createExternalData;

    private SaveDataDefinition(
        string label,
        GameDataSnapshot itemTables,
        IReadOnlyList<TableDefinition> stats,
        IReadOnlyList<TableDefinition> appendedStats,
        Func<uint, IExternalData> createExternalData,
        bool usesNewerItemTables = false)
    {
        Label = label;
        ItemTables = itemTables;
        Stats = stats;
        AppendedStats = appendedStats;
        _createExternalData = createExternalData;
        UsesNewerItemTables = usesNewerItemTables;
    }

    public string Label { get; }
    public GameDataSnapshot ItemTables { get; }

    /// <summary>Stat definitions indexed by save id.</summary>
    public IReadOnlyList<TableDefinition> Stats { get; }

    /// <summary>Stats taken from an overlay snapshot past the end of the base stat table.</summary>
    public IReadOnlyList<TableDefinition> AppendedStats { get; }

    /// <summary>The item tables are not the stat encoding's own: they come from a newer snapshot.</summary>
    public bool UsesNewerItemTables { get; }

    public IExternalData CreateExternalData(uint saveVersion) => _createExternalData(saveVersion);

    public TableDefinition? Stat(int saveId) => saveId >= 0 && saveId < Stats.Count ? Stats[saveId] : null;

    public TableDefinition? Stat(string key) =>
        Stats.FirstOrDefault(stat => stat.Key.Equals(key, StringComparison.OrdinalIgnoreCase));

    public static SaveDataDefinition FromSnapshot(GameDataSnapshot snapshot) => new(
        snapshot.Label,
        snapshot,
        TableSchema.Extract(TableKind.ItemStatCost, snapshot[TableKind.ItemStatCost]),
        [],
        version => new TxtFileExternalData(snapshot.Directory, version));

    public static SaveDataDefinition WithFrozenStatEncoding(
        string label, string itemStatCostPath, GameDataSnapshot itemTablesSnapshot, string composedDirectory)
    {
        Directory.CreateDirectory(composedDirectory);
        foreach (var file in Directory.EnumerateFiles(itemTablesSnapshot.Directory, "*.txt"))
        {
            File.Copy(file, Path.Combine(composedDirectory, Path.GetFileName(file)), overwrite: true);
        }

        File.Copy(itemStatCostPath, Path.Combine(composedDirectory, TableSchema.FileName(TableKind.ItemStatCost)),
            overwrite: true);
        var frozenSnapshot = GameDataSnapshot.Load(label, composedDirectory);
        return new SaveDataDefinition(
            label,
            itemTablesSnapshot.WithStatsOf(label, frozenSnapshot),
            TableSchema.Extract(TableKind.ItemStatCost, frozenSnapshot[TableKind.ItemStatCost]),
            [],
            version => new TxtFileExternalData(composedDirectory, version),
            usesNewerItemTables: true);
    }

    /// <summary>
    /// The base snapshot unchanged, plus only the overlay stats whose ids lie past the end of the base stat table.
    /// Moves and encoding changes the overlay makes to existing ids are deliberately not applied.
    /// </summary>
    public static SaveDataDefinition WithAppendedStats(string label, GameDataSnapshot baseSnapshot, GameDataSnapshot overlaySnapshot) =>
        WithAppendedStats(label, baseSnapshot, overlaySnapshot, null);

    /// <summary>
    /// <see cref="WithAppendedStats(string, GameDataSnapshot, GameDataSnapshot)"/>, but with the item tables and base items of
    /// <paramref name="itemTablesSnapshot"/> when it is set: a layout that already had newer items but still the base stat encoding.
    /// </summary>
    public static SaveDataDefinition WithAppendedStats(
        string label, GameDataSnapshot baseSnapshot, GameDataSnapshot overlaySnapshot, GameDataSnapshot? itemTablesSnapshot)
    {
        var baseStats = TableSchema.Extract(TableKind.ItemStatCost, baseSnapshot[TableKind.ItemStatCost]);
        var appended = TableSchema.Extract(TableKind.ItemStatCost, overlaySnapshot[TableKind.ItemStatCost])
            .Skip(baseStats.Count)
            .ToList();
        // The item tables carry the base stat table, so Phase 1 maps the existing stat ids from the base encoding.
        var itemTables = itemTablesSnapshot?.WithStatsOf(label, baseSnapshot) ?? baseSnapshot;
        return new SaveDataDefinition(
            label,
            itemTables,
            [.. baseStats, .. appended],
            appended,
            version => new AppendedStatsExternalData(
                new TxtFileExternalData(baseSnapshot.Directory, version),
                new TxtFileExternalData(overlaySnapshot.Directory, version),
                new TxtFileExternalData(itemTables.Directory, version),
                baseStats.Count),
            itemTablesSnapshot is not null);
    }

    private sealed class AppendedStatsExternalData(IExternalData baseData, IExternalData overlayData, IExternalData itemData, int firstOverlayStatId)
        : IExternalData
    {
        public StatInfo GetStatInfo(StatId statId, uint saveVersion) => (int)statId >= firstOverlayStatId
            ? overlayData.GetStatInfo(statId, saveVersion)
            : baseData.GetStatInfo(statId, saveVersion);

        public int GetItemIndex(uint itemCode, uint saveVersion) => itemData.GetItemIndex(itemCode, saveVersion);

        public ItemTypeInfo GetItemInfo(int itemIndex, uint saveVersion) => itemData.GetItemInfo(itemIndex, saveVersion);

        public int GetQuantityBits(uint saveVersion) => itemData.GetQuantityBits(saveVersion);

        public int GetMagicPrefixOffset(uint saveVersion) => itemData.GetMagicPrefixOffset(saveVersion);

        public int GetAutoMagicOffset(uint saveVersion) => itemData.GetAutoMagicOffset(saveVersion);
    }
}