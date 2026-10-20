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
        Func<uint, IExternalData> createExternalData)
    {
        Label = label;
        ItemTables = itemTables;
        Stats = stats;
        AppendedStats = appendedStats;
        _createExternalData = createExternalData;
    }

    public string Label { get; }
    public GameDataSnapshot ItemTables { get; }

    /// <summary>Stat definitions indexed by save id.</summary>
    public IReadOnlyList<TableDefinition> Stats { get; }

    /// <summary>Stats taken from an overlay snapshot past the end of the base stat table.</summary>
    public IReadOnlyList<TableDefinition> AppendedStats { get; }

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

    /// <summary>
    /// The base snapshot unchanged, plus only the overlay stats whose ids lie past the end of the base stat table.
    /// Moves and encoding changes the overlay makes to existing ids are deliberately not applied.
    /// </summary>
    public static SaveDataDefinition WithAppendedStats(string label, GameDataSnapshot baseSnapshot, GameDataSnapshot overlaySnapshot)
    {
        var baseStats = TableSchema.Extract(TableKind.ItemStatCost, baseSnapshot[TableKind.ItemStatCost]);
        var appended = TableSchema.Extract(TableKind.ItemStatCost, overlaySnapshot[TableKind.ItemStatCost])
            .Skip(baseStats.Count)
            .ToList();
        return new SaveDataDefinition(
            label,
            baseSnapshot,
            [.. baseStats, .. appended],
            appended,
            version => new AppendedStatsExternalData(
                new TxtFileExternalData(baseSnapshot.Directory, version),
                new TxtFileExternalData(overlaySnapshot.Directory, version),
                baseStats.Count));
    }

    private sealed class AppendedStatsExternalData(IExternalData baseData, IExternalData overlayData, int firstOverlayStatId)
        : IExternalData
    {
        public StatInfo GetStatInfo(StatId statId, uint saveVersion) => (int)statId >= firstOverlayStatId
            ? overlayData.GetStatInfo(statId, saveVersion)
            : baseData.GetStatInfo(statId, saveVersion);

        public int GetItemIndex(uint itemCode, uint saveVersion) => baseData.GetItemIndex(itemCode, saveVersion);

        public ItemTypeInfo GetItemInfo(int itemIndex, uint saveVersion) => baseData.GetItemInfo(itemIndex, saveVersion);

        public int GetQuantityBits(uint saveVersion) => baseData.GetQuantityBits(saveVersion);

        public int GetMagicPrefixOffset(uint saveVersion) => baseData.GetMagicPrefixOffset(saveVersion);

        public int GetAutoMagicOffset(uint saveVersion) => baseData.GetAutoMagicOffset(saveVersion);
    }
}
