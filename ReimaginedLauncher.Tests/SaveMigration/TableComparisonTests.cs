using ReimaginedLauncher.SaveMigration;
using Xunit;

namespace ReimaginedLauncher.Tests.SaveMigration;

public sealed class TableComparisonTests
{
    private static readonly Lazy<string> DataDirectory = new(FindDataDirectory);
    private static readonly Dictionary<KnownSnapshot, GameDataSnapshot> Snapshots = [];

    [Fact]
    public void LoaderKeepsBlankAndPlaceholderRowsInPhysicalOrder()
    {
        var source = TableSnapshot.Parse("itemstatcost.txt", "Stat\t*ID\r\nfirst\t0\r\n\t\r\nthird\t1\r\n");
        var target = TableSnapshot.Parse("itemstatcost.txt", "Stat\t*ID\r\nfirst\t0\r\nthird\t1\r\n");

        Assert.Equal(3, source.Rows.Count);
        Assert.True(source.Rows[1].IsBlank);
        var definitions = TableSchema.Extract(TableKind.ItemStatCost, source);
        Assert.True(definitions[1].IsPlaceholder);
        Assert.Equal(2, definitions[2].SaveId!.Value);
        Assert.True(definitions[2].CommentIdDisagrees);

        var comparison = TableComparer.CompareTable(TableKind.ItemStatCost, source, target);
        var third = Assert.Single(comparison.ForSourceKey("third"));
        Assert.Equal((2, 1), (third.Source!.SaveId!.Value, third.Target!.SaveId!.Value));

        var stats = TableSchema.Extract(TableKind.ItemStatCost, Load(KnownSnapshots.Reimagined212)[TableKind.ItemStatCost]);
        Assert.Equal(("unused203", 203), (stats[203].Key, stats[203].SaveId!.Value));
        var oncrit = stats.Single(stat => stat.Key == "passive_mastery_replenish_oncrit");
        Assert.Equal((207, "206"), (oncrit.SaveId!.Value, oncrit.CommentId));
        Assert.True(oncrit.CommentIdDisagrees);
    }

    [Fact]
    public void PullRequest1181StatMovesAndEncodingChangesAreDetected()
    {
        var stats = Compare(KnownSnapshots.Reimagined3012, KnownSnapshots.PullRequest1181)[TableKind.ItemStatCost];

        AssertMoved(stats, "hpregen_perlevel", 366, 432);
        AssertMoved(stats, "passive_dmg_pierce", 432, 366);
        Assert.Equal(2, stats.Moved.Count());
        Assert.Contains(stats.FindSaveIdReassignments(), r => r is { SaveId: 366, Source.Key: "hpregen_perlevel", Target.Key: "passive_dmg_pierce" });

        var maxHp = Assert.Single(stats.ForSourceKey("item_maxhp_percent"));
        Assert.Contains(new FieldChange("Save Bits", "6", "8"), maxHp.FieldChanges);
        Assert.Contains(new FieldChange("Save Add", "10", "100"), maxHp.FieldChanges);
        Assert.Contains(new FieldChange("Save Bits", "7", "16"), Assert.Single(stats.ForSourceKey("item_attackertakesdamage")).FieldChanges);
        Assert.Contains(stats.Added, added => added.Target is { Key: "extra_stbounce", SaveId: 441 });
        Assert.Empty(stats.Removed);
        Assert.Empty(stats.Ambiguous);

        AssertMoved(Compare(KnownSnapshots.Reimagined212, KnownSnapshots.PullRequest1181)[TableKind.ItemStatCost], "hpregen_perlevel", 366, 432);
    }

    [Fact]
    public void HistoricalUniqueAndSetMovesAndDuplicateNamesAreDetected()
    {
        var comparison = Compare(KnownSnapshots.Reimagined212, KnownSnapshots.Reimagined3012);
        var uniques = comparison[TableKind.UniqueItems];

        AssertMoved(uniques, "Talonrage", 401, 438);
        AssertMoved(comparison[TableKind.SetItems], "Jakira's Strike", 127, 140);
        var coldkill = Assert.Single(uniques.ForSourceKey("Coldkill"));
        Assert.Equal((129, 129), (coldkill.Source!.SaveId!.Value, coldkill.Target!.SaveId!.Value));

        Assert.Contains(uniques.SourceDuplicates, duplicate => duplicate.Key == "Medusa's Gaze");
        var medusa = uniques.ForSourceKey("Medusa's Gaze").ToList();
        Assert.Equal(2, medusa.Count);
        Assert.All(medusa, mapping =>
        {
            Assert.Equal(MappingStatus.Ambiguous, mapping.Status);
            Assert.Equal(349, Assert.Single(mapping.Candidates).SaveId!.Value);
        });

        var azurewrath = Assert.Single(uniques.ForSourceKey("Azurewrath"), mapping => mapping.Source!.Field("code") == "7cr");
        Assert.Equal((MappingStatus.Matched, MatchMethod.KeyAndCode, 301), (azurewrath.Status, azurewrath.Method, azurewrath.Target!.SaveId!.Value));
    }

    [Fact]
    public void SupportedComparisonsNeverMapAffixesByNameAloneAndWriteReports()
    {
        var reportDirectory = Path.Combine(AppContext.BaseDirectory, "SaveMigrationReports");
        Directory.CreateDirectory(reportDirectory);
        foreach (var (source, target) in KnownSnapshots.SupportedComparisons)
        {
            var comparison = Compare(source, target);
            foreach (var kind in new[] { TableKind.MagicPrefix, TableKind.MagicSuffix })
            {
                Assert.All(comparison[kind].Matched, mapping => Assert.NotEqual(MatchMethod.Key, mapping.Method));
            }

            foreach (var table in comparison.Tables)
            {
                var map = table.BuildSaveIdMap();
                Assert.Equal(map.Count, map.Values.Distinct().Count());
            }

            var report = ComparisonReport.Write(comparison);
            Assert.Contains("=== itemstatcost.txt ===", report);
            Assert.Contains("=== magicsuffix.txt ===", report);
            var fileName = $"{source.FolderName}_to_{target.FolderName}.txt".Replace(' ', '-');
            File.WriteAllText(Path.Combine(reportDirectory, fileName), report);
        }

        Assert.Contains(Compare(KnownSnapshots.Reimagined212, KnownSnapshots.Reimagined3012)[TableKind.MagicPrefix].SourceDuplicates,
            duplicate => duplicate.Definitions.Count > 1);
    }

    private static void AssertMoved(TableComparison table, string key, int sourceId, int targetId)
    {
        var mapping = Assert.Single(table.ForSourceKey(key));
        Assert.Equal(MappingStatus.Matched, mapping.Status);
        Assert.Equal((sourceId, targetId), (mapping.Source!.SaveId!.Value, mapping.Target!.SaveId!.Value));
        Assert.True(mapping.SaveIdChanged);
    }

    private static SnapshotComparison Compare(KnownSnapshot source, KnownSnapshot target) =>
        TableComparer.Compare(Load(source), Load(target));

    private static GameDataSnapshot Load(KnownSnapshot snapshot)
    {
        lock (Snapshots)
        {
            if (!Snapshots.TryGetValue(snapshot, out var loaded))
            {
                loaded = snapshot.Load(DataDirectory.Value);
                Snapshots[snapshot] = loaded;
            }

            return loaded;
        }
    }

    private static string FindDataDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ReimaginedLauncher.sln")))
            {
                return Path.Combine(directory.FullName, "ReimaginedLauncher", "Assets", "SaveMigrationData");
            }
        }

        throw new DirectoryNotFoundException("Could not locate ReimaginedLauncher.sln above the test output directory.");
    }
}
