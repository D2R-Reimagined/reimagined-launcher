using System.Security.Cryptography;
using ReimaginedLauncher.SaveMigration;
using Xunit;

namespace ReimaginedLauncher.Tests.SaveMigration;

public sealed class SaveInspectionTests
{
    private static readonly Lazy<string> RepositoryRoot = new(FindRepositoryRoot);
    private static readonly Dictionary<KnownSnapshot, GameDataSnapshot> Snapshots = [];

    [Fact]
    public void WereSorcParsesWith212DataAndSavedIdsMatchPhase1()
    {
        var inspection = InspectAndReport(
            "WereSorc.d2s",
            [SaveDataDefinition.FromSnapshot(Load(KnownSnapshots.Reimagined212))],
            [
                Compare(KnownSnapshots.Reimagined212, KnownSnapshots.Reimagined3012),
                Compare(KnownSnapshots.Reimagined212, InstalledSnapshot)
            ]);

        Assert.Equal((SaveFileKind.Character, 99u, (bool?)true),
            (inspection.Header.Kind, inspection.Header.Version, inspection.Header.ChecksumValid));
        AssertPhase1Ids(AssertParsedWith(inspection, "2.1.2"));
    }

    [Fact]
    public void PopsicleParsesWith3012DataAndSavedIdsMatchPhase1()
    {
        var inspection = InspectAndReport(
            "Popsicle.d2s",
            [SaveDataDefinition.FromSnapshot(Load(KnownSnapshots.Reimagined3012))],
            [Compare(KnownSnapshots.Reimagined3012, InstalledSnapshot)]);

        Assert.Equal((SaveFileKind.Character, 105u, (bool?)true),
            (inspection.Header.Kind, inspection.Header.Version, inspection.Header.ChecksumValid));
        // 1393 is Cold Rupture's 2.1.2 id carried over unmigrated; the z01 quiver keeps its pre-3.0.12 base code.
        AssertPhase1Ids(AssertParsedWith(inspection, "3.0.12"), (TableKind.UniqueItems, 1393, "cm3"),
            (TableKind.UniqueItems, 1423, "z01"));
    }

    [Fact]
    public void BonesMcPimpDecodesLabBossKillWithIntermediateStats()
    {
        var inspection = InspectAndReport(
            "BonesMcPimp.d2s",
            [
                NewItemsPreSwapStats(), SaveDataDefinition.FromSnapshot(InstalledSnapshot),
                SaveDataDefinition.FromSnapshot(Load(KnownSnapshots.Reimagined3012))
            ],
            [Compare(KnownSnapshots.Reimagined3012, InstalledSnapshot)]);

        Assert.Equal((SaveFileKind.Character, 105u, (bool?)true),
            (inspection.Header.Kind, inspection.Header.Version, inspection.Header.ChecksumValid));
        var parsed = AssertParsedWith(inspection, NewItemsPreSwapStatsLabel);
        Assert.Equal((long?)84, parsed.CharacterStat("lab_boss_kill"));
        AssertPhase1Ids(parsed, (TableKind.MagicPrefix, 2047, "rin"), (TableKind.MagicSuffix, 2047, "rin"));
    }

    [Fact]
    public void ModernSharedStashParsesAndSavedIdsMatchPhase1()
    {
        var inspection = InspectAndReport(
            "ModernSharedStashSoftCoreV2.d2i",
            [
                SaveDataDefinition.FromSnapshot(Load(KnownSnapshots.Reimagined3012)), Intermediate3013(),
                NewItemsPreSwapStats(), SaveDataDefinition.FromSnapshot(InstalledSnapshot)
            ],
            [Compare(KnownSnapshots.Reimagined3012, InstalledSnapshot)]);

        Assert.Equal((SaveFileKind.SharedStash, 105u, (uint?)2u),
            (inspection.Header.Kind, inspection.Header.Version, inspection.Header.StashFormat));
        var parsed = Assert.IsType<ParseAttempt>(inspection.Parsed);
        AssertPhase1Ids(parsed,
        [
            .. Enumerable.Repeat((TableKind.MagicPrefix, 2047, "rin"), 5),
            .. Enumerable.Repeat((TableKind.MagicSuffix, 2047, "rin"), 5)
        ]);
    }

    private const string Intermediate3013Label = "3.0.12 with installed appended stats";
    private const string NewItemsPreSwapStatsLabel = "Installed items with 3.0.12 stat encoding";

    private static SaveDataDefinition Intermediate3013() => SaveDataDefinition.WithAppendedStats(
        Intermediate3013Label, Load(KnownSnapshots.Reimagined3012), InstalledSnapshot);

    private static SaveDataDefinition NewItemsPreSwapStats() => SaveDataDefinition.WithAppendedStats(
        NewItemsPreSwapStatsLabel, Load(KnownSnapshots.Reimagined3012), InstalledSnapshot, InstalledSnapshot);

    private static SaveInspection InspectAndReport(
        string fixture, IReadOnlyList<SaveDataDefinition> candidates, IReadOnlyList<SnapshotComparison> comparisons)
    {
        var path = Path.Combine(RepositoryRoot.Value, "ReimaginedLauncher.Tests", "TestFixtures", "SaveMigration",
            fixture);
        var inspection = SaveInspector.Inspect(path, candidates, comparisons);

        var reportDirectory = Path.Combine(AppContext.BaseDirectory, "SaveMigrationReports");
        Directory.CreateDirectory(reportDirectory);
        File.WriteAllText(Path.Combine(reportDirectory, $"{fixture}.inspection.txt"),
            SaveInspectionReport.Write(inspection));

        Assert.True(inspection.Header.MagicValid);
        Assert.Equal(inspection.Sha256, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))));
        return inspection;
    }

    private static ParseAttempt AssertParsedWith(SaveInspection inspection, string label)
    {
        var attempt = Assert.Single(inspection.Attempts, attempt => attempt.DefinitionLabel == label);
        Assert.True(attempt.Succeeded, attempt.Error);
        Assert.Equal(0, attempt.TrailingBytes);
        return attempt;
    }

    private static void AssertPhase1Ids(ParseAttempt parsed,
        params (TableKind Kind, int SaveId, string Code)[] expectedMismatches)
    {
        Assert.All(parsed.IdValidations, validation =>
            Assert.Equal(validation.Hypotheses.Max(hypothesis => hypothesis.Agreeing),
                validation.Hypotheses[0].Agreeing));
        var mismatches = parsed.IdValidations
            .SelectMany(validation => validation.Phase1Mismatches)
            .Select(mismatch => (mismatch.Reference.Kind, mismatch.Reference.SaveId, mismatch.ItemCode))
            .Order()
            .ToList();
        Assert.Equal(expectedMismatches.Order(), mismatches);
    }

    private static SnapshotComparison Compare(KnownSnapshot source, KnownSnapshot target) =>
        TableComparer.Compare(Load(source), Load(target));

    private static SnapshotComparison Compare(KnownSnapshot source, GameDataSnapshot target) =>
        TableComparer.Compare(Load(source), target);

    private static GameDataSnapshot InstalledSnapshot => SaveMigrationFixtures.InstalledTarget.Snapshot;

    private static GameDataSnapshot Load(KnownSnapshot snapshot)
    {
        lock (Snapshots)
        {
            if (!Snapshots.TryGetValue(snapshot, out var loaded))
            {
                loaded = snapshot.Load(Path.Combine(RepositoryRoot.Value, "ReimaginedLauncher", "Assets",
                    "SaveMigrationData"));
                Snapshots[snapshot] = loaded;
            }

            return loaded;
        }
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ReimaginedLauncher.sln")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException(
            "Could not locate ReimaginedLauncher.sln above the test output directory.");
    }
}