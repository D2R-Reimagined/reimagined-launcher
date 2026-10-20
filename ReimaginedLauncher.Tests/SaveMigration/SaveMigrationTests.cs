using System.Security.Cryptography;
using D2SSharp;
using D2SSharp.Data;
using D2SSharp.Enums;
using D2SSharp.Model;
using ReimaginedLauncher.SaveMigration;
using Xunit;
using Xunit.Abstractions;

namespace ReimaginedLauncher.Tests.SaveMigration;

public sealed class SaveMigrationTests(ITestOutputHelper testOutput)
{
    private static readonly Lazy<string> RepositoryRoot = new(FindRepositoryRoot);
    private static readonly Lazy<SaveMigrator> Migrator = new(() =>
        SaveMigrator.ForTarget(SaveCheckpointRegistry.Load(DataDirectory), SaveCheckpointRegistry.PullRequest1181Id));
    private static readonly Lazy<(SaveMigrationResult Result, D2Save Save)> HistoricalRenames = new(ConvertHistoricalRenames);

    private static string DataDirectory => Path.Combine(RepositoryRoot.Value, "ReimaginedLauncher", "Assets", "SaveMigrationData");

    [Fact]
    public void PopsicleConvertsFrom3012AndReparsesWithPr1181()
    {
        var (result, save) = ConvertCharacter("Popsicle.d2s");

        Assert.Equal("3.0.12", result.SourceLayout);
        // Pre-3.0.12 unique ids are kept as they are and flagged instead of being guessed.
        Assert.Contains(result.References, reference =>
            reference is { Kind: TableKind.UniqueItems, SourceId: 1393, TargetId: 1393, Outcome: ReferenceOutcome.PreservedWithWarning });
        AssertLogicalValuesPreserved("Popsicle.d2s", SaveDataDefinition.FromSnapshot(KnownSnapshots.Reimagined3012.Load(DataDirectory)), save);
    }

    [Fact]
    public void BonesMcPimpConvertsFromIntermediateLayoutKeepingLabStatsAndCharmPage()
    {
        var (result, save) = ConvertCharacter("BonesMcPimp.d2s");

        Assert.Equal(SaveMigrator.Intermediate3013Label, result.SourceLayout);
        AssertLogicalValuesPreserved("BonesMcPimp.d2s", SaveDataDefinition.WithAppendedStats(SaveMigrator.Intermediate3013Label,
            KnownSnapshots.Reimagined3012.Load(DataDirectory), KnownSnapshots.PullRequest1181.Load(DataDirectory)), save);
        var labBossKill = Assert.Single(save.Stats, stat => (int)stat.Id == Migrator.Value.Target.Stat("lab_boss_kill")!.SaveId);
        Assert.Equal(84, labBossKill.Value);

        Assert.Equal(13, result.CustomPageItems.Count);
        Assert.All(result.CustomPageItems, item => Assert.Equal(7, item.Page + 1));
        var outputCharms = save.Items.Where(item => !Enum.IsDefined(item.Position.StorePage)).ToList();
        Assert.Equal(
            result.CustomPageItems.Select(item => (item.Code, item.Page, item.X, item.Y, item.StatCount)),
            outputCharms.Select(item => (item.ItemCodeString, (int)item.Position.StorePage, (int)item.Position.InvX, (int)item.Position.InvY, item.Stats.Count)));
    }

    [Fact]
    public void WereSorcConvertsFrom212Version99ToVersion105()
    {
        var (result, save) = ConvertCharacter("WereSorc.d2s");

        Assert.Equal("2.1.2", result.SourceLayout);
        Assert.Equal((99u, (uint?)105u), (result.SourceVersion, result.OutputVersion));
        Assert.Equal(105u, save.Version);
        AssertLogicalValuesPreserved("WereSorc.d2s", SaveDataDefinition.FromSnapshot(KnownSnapshots.Reimagined212.Load(DataDirectory)), save);
        Assert.All(result.Stats.Where(stat => stat.SourceName == "hpregen_perlevel"), stat => Assert.Equal((366, 432), (stat.SourceId, stat.TargetId)));
        Assert.All(result.Stats.Where(stat => stat is { Scope: "item", SourceName: "item_maxhp_percent" }), stat => Assert.True(stat.EncodingChanged));
    }

    [Fact]
    public void BasetolabprSwapsHpRegenPerLevelAndPassivePierceStatIds()
    {
        var (result, save) = ConvertCharacter("basetolabpr.d2s");
        testOutput.WriteLine($"Source layout: {result.SourceLayout}; {string.Join(" | ", result.Identification)}");

        var reimagined3012 = KnownSnapshots.Reimagined3012.Load(DataDirectory);
        var source = result.SourceLayout switch
        {
            "3.0.12" => SaveDataDefinition.FromSnapshot(reimagined3012),
            SaveMigrator.Intermediate3013Label => SaveDataDefinition.WithAppendedStats(
                SaveMigrator.Intermediate3013Label, reimagined3012, KnownSnapshots.PullRequest1181.Load(DataDirectory)),
            var other => throw new Xunit.Sdk.XunitException($"Unexpected source layout {other}"),
        };
        Assert.Equal(("hpregen_perlevel", "passive_dmg_pierce"), (source.Stat(366)!.Key, source.Stat(432)!.Key));
        Assert.Equal(("hpregen_perlevel", "passive_dmg_pierce"), (Migrator.Value.Target.Stat(432)!.Key, Migrator.Value.Target.Stat(366)!.Key));
        AssertLogicalValuesPreserved("basetolabpr.d2s", source, save);

        var data = File.ReadAllBytes(Path.Combine(RepositoryRoot.Value, "ReimaginedLauncher.Tests", "TestFixtures", "SaveMigration", "basetolabpr.d2s"));
        var input = D2Save.Read(data, source.CreateExternalData(BitConverter.ToUInt32(data, 4)));
        // Version 105 stores the name in the preview block; the legacy name field is empty.
        Assert.Equal(("basetolabpr", input.Character.Name), (save.Character.Preview.Name, save.Character.Name));
        Assert.Equal(input.Character.Preview.Name, save.Character.Preview.Name);
        var before = Walk(input).ToList();
        var after = Walk(save).ToList();
        Assert.Equal(before.Select(entry => entry.Path), after.Select(entry => entry.Path));

        var swapped = 0;
        foreach (var ((path, sourceItem), (_, outputItem)) in before.Zip(after))
        {
            Assert.Equal(sourceItem.ItemCodeString, outputItem.ItemCodeString);
            Assert.Equal(Location(sourceItem), Location(outputItem));
            Assert.Equal(sourceItem.Sockets.Count, outputItem.Sockets.Count);
            var sourceStats = StatLists(sourceItem).SelectMany(list => list.Stats.Select(stat => (list.Name, Stat: stat))).ToList();
            var outputStats = StatLists(outputItem).SelectMany(list => list.Stats.Select(stat => (list.Name, Stat: stat))).ToList();
            Assert.Equal(sourceStats.Count, outputStats.Count);
            foreach (var ((list, sourceStat), (_, outputStat)) in sourceStats.Zip(outputStats))
            {
                var (sourceId, outputId) = ((int)sourceStat.Id, (int)outputStat.Id);
                Assert.Equal((source.Stat(sourceId)!.Key, sourceStat.Layer, sourceStat.Value),
                    (Migrator.Value.Target.Stat(outputId)!.Key, outputStat.Layer, outputStat.Value));
                if (sourceId is 366 or 432)
                {
                    Assert.Equal(sourceId == 366 ? 432 : 366, outputId);
                    swapped++;
                    testOutput.WriteLine($"{path} '{sourceItem.ItemCodeString.Trim()}' {list}: {source.Stat(sourceId)!.Key} id {sourceId} -> {outputId}, "
                                         + $"param {sourceStat.Layer} -> {outputStat.Layer}, value {sourceStat.Value} -> {outputStat.Value}");
                }
                else if (sourceId != outputId)
                {
                    testOutput.WriteLine($"{path} '{sourceItem.ItemCodeString.Trim()}' {list}: other id move {source.Stat(sourceId)!.Key} {sourceId} -> {outputId}");
                }
            }
        }

        testOutput.WriteLine($"{swapped} swapped propert(ies); {before.Count} item(s) including sockets; status {result.Status}");
        Assert.True(swapped > 0, "Neither hpregen_perlevel nor passive_dmg_pierce occurs on any item.");

        static IEnumerable<(string Path, Item Item)> Walk(D2Save save) =>
            save.Items.Select((item, index) => ($"Player #{index + 1}", item))
                .Concat(save.Corpses.SelectMany((corpse, c) => corpse.Items.Select((item, index) => ($"Corpse {c + 1} #{index + 1}", item))))
                .Concat((save.MercItems?.Items ?? []).Select((item, index) => ($"Mercenary #{index + 1}", item)))
                .Concat(save.IronGolem?.GolemItem is { } golem ? new[] { ("Iron golem", golem) } : [])
                .SelectMany(entry => WithSockets(entry.Item1, entry.Item2));

        static IEnumerable<(string Path, Item Item)> WithSockets(string path, Item item) => item.Sockets.OfType<Item>()
            .SelectMany((socket, index) => WithSockets($"{path} > socket {index + 1}", socket))
            .Prepend((path, item));

        static IEnumerable<(string Name, IReadOnlyList<Stat> Stats)> StatLists(Item item) =>
        [
            ("item", item.Stats),
            .. item.SetBonusStats.Select((list, index) => ($"set bonus {index + 1}", (IReadOnlyList<Stat>)list)),
            ("runeword", item.RunewordStats ?? []),
        ];

        static string Location(Item item) =>
            $"{item.Position.Mode} {item.Position.StorePage} {item.Position.InvX},{item.Position.InvY} {item.Position.BodyLocation} "
            + $"ground {item.Position.IsOnGround} {item.Position.GroundX},{item.Position.GroundY}";
    }

    [Fact]
    public void BruhIIMapsTheRenamedLifeAndDeathUniqueFrom1349To1386()
    {
        var (result, save) = ConvertCharacter("BruhII.d2s");
        testOutput.WriteLine(SaveMigrationReport.Write(result));

        Assert.Equal("2.1.2", result.SourceLayout);
        var lifeAndDeath = Assert.Single(result.References, reference => reference is { Kind: TableKind.UniqueItems, SourceId: 1349 });
        Assert.Equal((1386, ReferenceOutcome.Mapped, "Life & Death"), (lifeAndDeath.TargetId, lifeAndDeath.Outcome, lifeAndDeath.SourceKey));
        Assert.StartsWith("Player #41 'cm2", lifeAndDeath.ItemPath);
        Assert.Single(result.References, reference => reference.Reason.Contains("registered identity mapping"));
        Assert.DoesNotContain(result.References, reference => reference.Blocking);
        // 73 items including socketed ones: 48 items and 25 socketed, carrying 256 properties.
        Assert.Equal(73, result.ItemsInspected);
        Assert.Contains(result.Checks, check => check is { Name: "Item, socket and property counts", Passed: true }
                                                && check.Detail == "source 48 items / 25 socketed / 256 properties, output 48 / 25 / 256");

        var item = save.Items[40];
        Assert.Equal(("cm2", ItemQuality.Unique, (ushort)1386),
            (item.ItemCodeString.Trim(), item.Quality, ((SetUniqueQualityData)item.QualityData!).SetUniqueFileIndex));
        AssertLogicalValuesPreserved("BruhII.d2s", SaveDataDefinition.FromSnapshot(KnownSnapshots.Reimagined212.Load(DataDirectory)), save);

        // Every other unique and set reference keeps the definition it named before.
        var source = KnownSnapshots.Reimagined212.Load(DataDirectory);
        foreach (var reference in result.References.Where(reference => reference != lifeAndDeath
                     && reference is { Kind: TableKind.UniqueItems or TableKind.SetItems, Outcome: ReferenceOutcome.Mapped or ReferenceOutcome.AlreadyCompatible }))
        {
            var before = TableSchema.Extract(reference.Kind, source[reference.Kind]).Single(definition => definition.SaveId == reference.SourceId);
            var after = TableSchema.Extract(reference.Kind, Migrator.Value.Target.ItemTables[reference.Kind]).Single(definition => definition.SaveId == reference.TargetId);
            Assert.Equal(before.Key, after.Key, ignoreCase: true);
        }

        Assert.Equal("Life and Death", TableSchema.Extract(TableKind.UniqueItems, Migrator.Value.Target.ItemTables[TableKind.UniqueItems])
            .Single(definition => definition.SaveId == 1386).Key);
    }

    [Fact]
    public void T9SplashCharmMapsToCollinsDestructionThroughTheRegisteredIdentity()
    {
        var (result, save) = HistoricalRenames.Value;

        var charm = Assert.Single(result.References, reference => reference is { Kind: TableKind.UniqueItems, SourceId: 1392 });
        Assert.Equal((1409, ReferenceOutcome.Mapped, false, "t9 Splash Charm"), (charm.TargetId, charm.Outcome, charm.Blocking, charm.SourceKey));
        Assert.Contains("registered identity mapping", charm.Reason);
        Assert.Equal("Collin's Destruction", TargetRow(TableKind.UniqueItems, 1409).Key);
        AssertRewrittenItem(save, 21, "cm5", ItemQuality.Unique, 1409);
    }

    [Fact]
    public void TyrialsGriefMapsToTyraelsGriefThroughTheRegisteredIdentity()
    {
        var (result, save) = HistoricalRenames.Value;

        var ring = Assert.Single(result.References, reference => reference is { Kind: TableKind.SetItems, SourceId: 207 });
        Assert.Equal((220, ReferenceOutcome.Mapped, false, "Tyrial's Grief"), (ring.TargetId, ring.Outcome, ring.Blocking, ring.SourceKey));
        Assert.Contains("registered identity mapping", ring.Reason);
        Assert.Equal(("Tyrael's Grief", "Forsaken Divinity"), (TargetRow(TableKind.SetItems, 220).Key, TargetRow(TableKind.SetItems, 220).Field("set")));
        AssertRewrittenItem(save, 41, "rin", ItemQuality.Set, 220);
    }

    [Fact]
    public void RenamedGnosticsPrefixesKeepTheirOwnIdsAndDefinitions()
    {
        var (result, save) = HistoricalRenames.Value;

        var mapping = Assert.Single(SaveCheckpointRegistry.Load(DataDirectory).IdentityMappings, mapping => mapping.Kind == TableKind.MagicPrefix);
        Assert.Equal(("gnostics1", "Gnostics", 301, (int?)328), (mapping.SourceKey, mapping.TargetKey, mapping.SourceId, mapping.LastSourceId));
        var source = TableSchema.Extract(TableKind.MagicPrefix, KnownSnapshots.Reimagined212.Load(DataDirectory)[TableKind.MagicPrefix]);
        foreach (var (itemIndex, prefixId) in new[] { (39, 301), (42, 328) })
        {
            var prefix = Assert.Single(result.References, reference => reference is { Kind: TableKind.MagicPrefix } && reference.SourceId == prefixId);
            Assert.Equal((prefixId, ReferenceOutcome.AlreadyCompatible, false, "gnostics1", "Gnostics"),
                (prefix.TargetId, prefix.Outcome, prefix.Blocking, prefix.SourceKey, prefix.SameIdTargetKey));
            var (before, after) = (source.Single(definition => definition.SaveId == prefixId), TargetRow(TableKind.MagicPrefix, prefixId));
            Assert.Equal(TableSchema.AffixDefinition.Select(before.Field), TableSchema.AffixDefinition.Select(after.Field));
            Assert.Equal(prefixId, ((MagicQualityData)save.Items[itemIndex].QualityData!).PrefixId);
        }

        // The Amazon +2 row and the Assassin +5 row stay distinct definitions.
        Assert.NotEqual(TargetRow(TableKind.MagicPrefix, 301).Field("mod1min"), TargetRow(TableKind.MagicPrefix, 328).Field("mod1min"));
        Assert.DoesNotContain(result.Warnings, warning => warning.Contains("identity", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result.References, reference => reference.Blocking);
    }

    [Fact]
    public void ModernSharedStashConvertsAndReparsesWithPr1181()
    {
        var result = Convert("ModernSharedStashSoftCoreV2.d2i");

        var output = File.ReadAllBytes(result.OutputPath!);
        var stash = D2StashSave.Read(output, Migrator.Value.Target.CreateExternalData(SaveMigrator.TargetVersion));
        Assert.Equal(result.ItemsInspected, stash.Sum(tab => CountWithSockets(tab.Items)));
        Assert.Contains(result.References, reference =>
            reference is { Kind: TableKind.MagicPrefix, SourceId: 2047, Outcome: ReferenceOutcome.PreservedWithWarning });
    }

    /// <summary>BruhII (2.1.2) with four items rewritten to carry 2.1.2 identities that later releases renamed.</summary>
    private static (SaveMigrationResult Result, D2Save Save) ConvertHistoricalRenames()
    {
        var source = SaveDataDefinition.FromSnapshot(KnownSnapshots.Reimagined212.Load(DataDirectory));
        var data = File.ReadAllBytes(FixturePath("BruhII.d2s"));
        var version = BitConverter.ToUInt32(data, 4);
        var externalData = source.CreateExternalData(version);
        var save = D2Save.Read(data, externalData);
        Rewrite(save.Items[21], "cm5", ItemQuality.Unique, new SetUniqueQualityData { SetUniqueFileIndex = 1392 });
        Rewrite(save.Items[41], "rin", ItemQuality.Set, new SetUniqueQualityData { SetUniqueFileIndex = 207 });
        Rewrite(save.Items[39], "rin", ItemQuality.Magic, new MagicQualityData { PrefixId = 301 });
        Rewrite(save.Items[42], "amu", ItemQuality.Magic, new MagicQualityData { PrefixId = 328 });

        var directory = Path.Combine(RepositoryRoot.Value, "ReimaginedLauncher.Tests", "TestOutput", "SaveMigration", "Input");
        Directory.CreateDirectory(directory);
        var input = Path.Combine(directory, "HistoricalRenames.d2s");
        File.WriteAllBytes(input, save.ToBytes(externalData, version));
        var converted = ConvertCharacterFile(input);

        // The rewritten items keep every rolled property.
        var reparsed = D2Save.Read(File.ReadAllBytes(input), externalData);
        foreach (var index in new[] { 21, 39, 41, 42 })
        {
            Assert.Equal(reparsed.Items[index].Stats.Select(stat => (source.Stat((int)stat.Id)!.Key, stat.Layer, stat.Value)),
                converted.Save.Items[index].Stats.Select(stat => (Migrator.Value.Target.Stat((int)stat.Id)!.Key, stat.Layer, stat.Value)));
        }

        return converted;

        static void Rewrite(Item item, string code, ItemQuality quality, ItemQualityData qualityData)
        {
            item.ItemCode = ItemTypeCodes.MakeCode(code);
            item.Quality = quality;
            item.QualityData = qualityData;
            item.SetItemMask = quality == ItemQuality.Set ? 0 : null;
        }
    }

    private static TableDefinition TargetRow(TableKind kind, int saveId) =>
        TableSchema.Extract(kind, Migrator.Value.Target.ItemTables[kind]).Single(definition => definition.SaveId == saveId);

    private static void AssertRewrittenItem(D2Save save, int index, string code, ItemQuality quality, int fileIndex)
    {
        var item = save.Items[index];
        Assert.Equal((code, quality, (ushort)fileIndex),
            (item.ItemCodeString.Trim(), item.Quality, ((SetUniqueQualityData)item.QualityData!).SetUniqueFileIndex));
    }

    private static string FixturePath(string fixture) =>
        Path.Combine(RepositoryRoot.Value, "ReimaginedLauncher.Tests", "TestFixtures", "SaveMigration", fixture);

    private static (SaveMigrationResult Result, D2Save Save) ConvertCharacter(string fixture) => ConvertCharacterFile(FixturePath(fixture));

    private static (SaveMigrationResult Result, D2Save Save) ConvertCharacterFile(string input)
    {
        var result = ConvertFile(input);
        var output = File.ReadAllBytes(result.OutputPath!);
        Assert.True(ChecksumCalculator.Verify(output));
        Assert.Equal((uint)output.Length, BitConverter.ToUInt32(output, 8));

        var save = D2Save.Read(output, Migrator.Value.Target.CreateExternalData(SaveMigrator.TargetVersion));
        Assert.Empty(save.TrailingData);
        var items = CountWithSockets(save.Items) + save.Corpses.Sum(corpse => CountWithSockets(corpse.Items))
                    + CountWithSockets(save.MercItems?.Items ?? []) + (save.IronGolem?.GolemItem is { } golem ? CountWithSockets([golem]) : 0);
        Assert.Equal(result.ItemsInspected, items);
        return (result, save);
    }

    private static SaveMigrationResult Convert(string fixture) => ConvertFile(FixturePath(fixture));

    private static SaveMigrationResult ConvertFile(string input)
    {
        var fixture = Path.GetFileName(input);
        var outputDirectory = Path.Combine(RepositoryRoot.Value, "ReimaginedLauncher.Tests", "TestOutput", "SaveMigration");
        Directory.CreateDirectory(outputDirectory);
        foreach (var stale in new[] { "", ".rejected", ".report.txt" }.Select(suffix => Path.Combine(outputDirectory, fixture + suffix)))
        {
            File.Delete(stale);
        }

        var hash = SHA256.HashData(File.ReadAllBytes(input));
        var result = Migrator.Value.Migrate(input, outputDirectory);
        File.WriteAllText(Path.Combine(outputDirectory, fixture + ".report.txt"), SaveMigrationReport.Write(result));

        Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(input)));
        Assert.True(result.Status != MigrationStatus.Failed, SaveMigrationReport.Write(result));
        Assert.All(result.Checks, check => Assert.True(check.Passed, $"{check.Name}: {check.Detail}"));
        Assert.Equal(Path.Combine(outputDirectory, fixture), result.OutputPath);
        Assert.DoesNotContain(result.Stats, stat => stat.Problems.Count > 0);
        return result;
    }

    /// <summary>Every item property and character stat, by stat name, decoded from the fixture and from the output.</summary>
    private static void AssertLogicalValuesPreserved(string fixture, SaveDataDefinition source, D2Save output)
    {
        var data = File.ReadAllBytes(Path.Combine(RepositoryRoot.Value, "ReimaginedLauncher.Tests", "TestFixtures", "SaveMigration", fixture));
        var input = D2Save.Read(data, source.CreateExternalData(BitConverter.ToUInt32(data, 4)));
        Assert.Equal(Values(input, source), Values(output, Migrator.Value.Target));

        static List<string> Values(D2Save save, SaveDataDefinition definition)
        {
            string Describe(Stat stat) => $"{definition.Stat((int)stat.Id)!.Key}[{stat.Layer}]={stat.Value}";
            IEnumerable<string> ItemValues(Item item) => item.Stats
                .Concat(item.SetBonusStats.SelectMany(list => list))
                .Concat(item.RunewordStats ?? [])
                .Select(stat => $"{item.ItemCodeString} {Describe(stat)}")
                .Concat(item.Sockets.OfType<Item>().SelectMany(ItemValues));
            return
            [
                .. save.Stats.Select(Describe),
                .. save.Items.Concat(save.Corpses.SelectMany(corpse => corpse.Items)).Concat(save.MercItems?.Items ?? []).SelectMany(ItemValues),
            ];
        }
    }

    private static int CountWithSockets(IEnumerable<Item> items) =>
        items.Sum(item => 1 + CountWithSockets(item.Sockets.OfType<Item>()));

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ReimaginedLauncher.sln")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate ReimaginedLauncher.sln above the test output directory.");
    }
}
