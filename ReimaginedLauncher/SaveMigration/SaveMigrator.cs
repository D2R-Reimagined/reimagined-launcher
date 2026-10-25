using System;
using System.Buffers.Binary;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using D2SSharp;
using D2SSharp.Data;
using D2SSharp.Enums;
using D2SSharp.Model;

namespace ReimaginedLauncher.SaveMigration;

/// <summary>
/// Converts a character or shared stash from its historical source layout directly to the installed game data.
/// The input file is only read. Output goes to a separate directory and is reparsed with the target definitions.
/// </summary>
public sealed class SaveMigrator
{
    public const uint TargetVersion = 105;
    public const int RetailCharacterSizeLimit = 65535;
    private const uint SaveMagic = 0xAA55AA55;
    private const int StashTabHeaderSize = 64;
    private const int MaxListedProblems = 5;

    // Retail binary versions D2SSharp 0.0.22 is known to read; any other version is only attempted experimentally.
    private static readonly HashSet<uint> SupportedVersions = [97, 98, 99, 105];

    // D2SSharp writes these as fixed groups keyed by the vanilla ids, so they must keep their ids.
    private static readonly HashSet<StatId> PairedStats =
    [
        .. Enum.GetValues<StatId>().Where(id => id.GetPairedStats().Length > 0)
            .SelectMany(id => id.GetPairedStats().Append(id))
    ];

    private readonly IReadOnlyList<SaveDataDefinition> _sources;
    private readonly SaveDataDefinition _target;
    private readonly IReadOnlyList<SnapshotComparison> _comparisons;
    private readonly IReadOnlyList<ResolvedIdentityMapping> _identityMappings;
    private readonly IReadOnlyList<SaveDataDefinition> _newerLayouts;

    /// <param name="newerLayouts">Registered layouts that postdate the target. They are never converted from; a file they explain
    /// better than every source is refused as a downgrade, and one they explain as well is never verified.</param>
    public SaveMigrator(IReadOnlyList<SaveDataDefinition> sources, SaveDataDefinition target,
        IReadOnlyList<SnapshotComparison> comparisons,
        IReadOnlyList<ResolvedIdentityMapping>? identityMappings = null,
        IReadOnlyList<SaveDataDefinition>? newerLayouts = null)
    {
        _sources = sources;
        _target = target;
        _comparisons = comparisons;
        _identityMappings = identityMappings ?? [];
        _newerLayouts = newerLayouts ?? [];
    }

    /// <summary>A migrator from earlier frozen releases and compatible development layouts to the installed game data.</summary>
    public static SaveMigrator ForInstalledTarget(SaveCheckpointRegistry registry, InstalledGameData installedTarget)
    {
        var snapshots = new Dictionary<(string Label, string Path), GameDataSnapshot>();
        var targetTables = installedTarget.Snapshot;
        var targetStats = TableSchema.Extract(TableKind.ItemStatCost, targetTables[TableKind.ItemStatCost]);
        var matchingCheckpoint = registry.Checkpoints
            .Where(checkpoint => installedTarget.HasSameTables(registry.PathOf(checkpoint.Directory)))
            .MaxBy(checkpoint => checkpoint.Order);
        // Unrecognized data is only as new as the stats it defines prove; it may still take saves of the layout it is placed at.
        var targetOrder = matchingCheckpoint?.Order ?? registry.EstablishedOrderOf(targetStats) ?? int.MinValue;
        var sourceLimit = matchingCheckpoint is null ? targetOrder + 1 : targetOrder;
        var sources = registry.SourcesBefore(sourceLimit)
            .Select(checkpoint => Release(checkpoint))
            .ToList();

        // Development saves made before the newest release keep the previous release's stats with the target's appended ones.
        var previous = registry.Checkpoints.Where(checkpoint => checkpoint.Order < targetOrder)
            .MaxBy(checkpoint => checkpoint.Order);
        if (previous is not null && targetOrder >= registry.LatestCheckpoint!.Order)
        {
            var release = Snapshot(previous.Label, previous.Directory);
            if (targetStats.Count > TableSchema.Extract(TableKind.ItemStatCost, release[TableKind.ItemStatCost]).Count)
            {
                sources.Add(SaveDataDefinition.WithAppendedStats(
                        $"{previous.Label} with installed appended stats", release, targetTables)
                    .WithCharacterVersions(previous.CharacterVersions));
                sources.Add(SaveDataDefinition.WithAppendedStats(
                        $"Installed items with {previous.Label} stat encoding", release, targetTables, targetTables)
                    .WithCharacterVersions(previous.CharacterVersions));
            }
        }

        var newerLayouts = registry.Checkpoints.Where(checkpoint => checkpoint.Order > targetOrder)
            .Select(checkpoint => Release(checkpoint))
            .ToList();
        var baseCheckpoint = registry.Checkpoints.Where(checkpoint => checkpoint.Order <= targetOrder)
            .MaxBy(checkpoint => checkpoint.Order);
        foreach (var encoding in registry.StatEncodings)
        {
            var statPath = registry.PathOf(encoding);
            if (File.ReadAllBytes(statPath).AsSpan()
                .SequenceEqual(File.ReadAllBytes(Path.Combine(installedTarget.CopiedDirectory, "itemstatcost.txt"))))
            {
                continue;
            }

            var composedDirectory = Path.Combine(installedTarget.CopiedDirectory, "stat-encodings",
                Guid.NewGuid().ToString("N"), encoding.Id);
            var frozen = SaveDataDefinition.WithFrozenStatEncoding(
                    $"Installed items with {encoding.Label} stat encoding", statPath, targetTables, composedDirectory)
                .WithCharacterVersions(encoding.CharacterVersions);
            if (encoding.Order < sourceLimit)
            {
                sources.Add(frozen);
                continue;
            }

            newerLayouts.Add(frozen);
            if (baseCheckpoint is not null)
            {
                var release = Snapshot(baseCheckpoint.Label, baseCheckpoint.Directory);
                var encodingTables = GameDataSnapshot.Load(frozen.Label, composedDirectory);
                if (TableSchema.Extract(TableKind.ItemStatCost, encodingTables[TableKind.ItemStatCost]).Count
                    > TableSchema.Extract(TableKind.ItemStatCost, release[TableKind.ItemStatCost]).Count)
                {
                    newerLayouts.Add(SaveDataDefinition.WithAppendedStats(
                            $"{baseCheckpoint.Label} with {encoding.Label} appended stats", release, encodingTables)
                        .WithCharacterVersions(encoding.CharacterVersions));
                }
            }
        }

        var comparisons = sources
            .Select(source => source.ItemTables)
            .DistinctBy(tables => (tables.Label, tables.Directory))
            .Select(tables => TableComparer.Compare(tables, targetTables))
            .ToList();
        return new SaveMigrator(sources, SaveDataDefinition.FromSnapshot(targetTables), comparisons,
            registry.IdentityMappingsUpTo(targetOrder), newerLayouts);

        SaveDataDefinition Release(SaveCheckpoint checkpoint) => SaveDataDefinition
            .FromSnapshot(Snapshot(checkpoint.Label, checkpoint.Directory))
            .WithCharacterVersions(checkpoint.CharacterVersions);

        GameDataSnapshot Snapshot(string label, string directory)
        {
            var key = (label, registry.PathOf(directory).ToUpperInvariant());
            if (!snapshots.TryGetValue(key, out var snapshot))
            {
                snapshot = GameDataSnapshot.Load(label, registry.PathOf(directory));
                snapshots[key] = snapshot;
            }

            return snapshot;
        }
    }

    public SaveDataDefinition Target => _target;

    internal IReadOnlyList<string> SourceLayouts => [.. _sources.Select(source => source.Label)];

    internal IReadOnlyList<string> NewerLayouts => [.. _newerLayouts.Select(layout => layout.Label)];

    /// <summary>
    /// Writes <c>outputDirectory/&lt;file name&gt;</c> when no hard blocker or critical check failed and every reported uncertainty is
    /// either absent or accepted with <see cref="MigrationOverrides.Force"/>; any other serialized output gets a <c>.rejected</c> suffix.
    /// Nothing is written when the source cannot be decoded or the serializer fails.
    /// </summary>
    public SaveMigrationResult Migrate(string inputPath, string outputDirectory,
        MigrationOverrides? overrides = null) =>
        new Run(this, Path.GetFullPath(inputPath), Path.GetFullPath(outputDirectory),
            overrides ?? MigrationOverrides.None).Execute();

    public SaveMigrationResult Rebuild(string inputPath, string outputDirectory) =>
        Migrate(inputPath, outputDirectory, new MigrationOverrides(Force: true, RebuildWithTarget: true));

    private sealed record FieldDifference(string Path, string? Before, string? After)
    {
        public override string ToString() => $"{Path}: {Before ?? "(absent)"} -> {After ?? "(absent)"}";
    }

    private sealed class StatAccumulator(
        string scope,
        TableDefinition? source,
        TableDefinition? target,
        int sourceId,
        string sourceEncoding,
        string targetEncoding)
    {
        public int Uses { get; private set; }
        public long Min { get; private set; } = long.MaxValue;
        public long Max { get; private set; } = long.MinValue;
        public List<string> Problems { get; } = [];
        public int ProblemCount { get; private set; }

        public void Use(long value)
        {
            Uses++;
            Min = Math.Min(Min, value);
            Max = Math.Max(Max, value);
        }

        public void Problem(string problem)
        {
            ProblemCount++;
            if (Problems.Count < MaxListedProblems && !Problems.Contains(problem))
            {
                Problems.Add(problem);
            }
        }

        public StatResult ToResult() => new(
            scope,
            source?.Key ?? $"(undefined {sourceId})",
            target?.Key ?? "(none)",
            sourceId,
            target?.SaveId ?? sourceId,
            Uses,
            Min,
            Max,
            sourceEncoding,
            targetEncoding,
            ProblemCount > Problems.Count ? [.. Problems, $"... {ProblemCount - Problems.Count} more"] : Problems);
    }

    private sealed class Run(SaveMigrator owner, string input, string outputDirectory, MigrationOverrides overrides)
    {
        private readonly SaveFileKind _kind =
            Path.GetExtension(input).Equals(".d2i", StringComparison.OrdinalIgnoreCase)
                ? SaveFileKind.SharedStash
                : SaveFileKind.Character;

        private readonly List<string> _identification = [];
        private readonly List<ReferenceResult> _references = [];
        private readonly Dictionary<(string Scope, int Id), StatAccumulator> _stats = [];

        private readonly Dictionary<Item, (int Source, int Target)> _setUniqueIds =
            new(ReferenceEqualityComparer.Instance);

        private readonly List<string> _notes = [];
        private readonly List<string> _warnings = [];
        private readonly List<string> _blockers = [];
        private readonly List<string> _uncertain = [];
        private readonly List<MigrationCheck> _checks = [];
        private List<CustomPageItem> _customPageItems = [];
        private List<string> _ambiguousLayouts = [];
        private bool _chronologyUnknown;

        private SaveDataDefinition? _source;
        private SnapshotComparison _comparison = null!;
        private SaveInspector.ItemTypeIndex _sourceTypes = null!;
        private SaveInspector.ItemTypeIndex _targetTypes = null!;
        private IExternalData _sourceData = null!;
        private IExternalData _targetData = null!;
        private byte[] _sourceTrailer = [];
        private uint _sourceVersion;
        private int _changes;
        private int _itemsInspected;
        private int _itemsChanged;
        private int _rareNames;
        private int _runewords;
        private int _autoAffixes;
        private int _chronicleItems;

        private SaveDataDefinition Target => owner._target;

        public SaveMigrationResult Execute()
        {
            var fileName = Path.GetFileName(input);
            if (string.Equals(Path.GetDirectoryName(input), outputDirectory.TrimEnd(Path.DirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    "The output directory must differ from the input directory; saves are never converted in place.");
            }

            var data = File.ReadAllBytes(input);
            var repairable = new List<string>();
            var headerProblems = CheckHeader(data, _kind, out _sourceVersion, repairable);
            if (headerProblems.Count > 0)
            {
                _blockers.AddRange(headerProblems.Select(problem => $"Source rejected: {problem}."));
                return Result(null, null, null);
            }

            _uncertain.AddRange(repairable.Select(problem => $"Source header: {problem}."));
            if (_kind == SaveFileKind.Character && File.Exists(Path.ChangeExtension(input, ".d2rl")))
            {
                _warnings.Add(
                    "A D2RLoader .d2rl sidecar exists next to the input. It records the saved ItemStatCost schema and is not converted.");
            }

            _source = Identify();
            if (_source is null)
            {
                return Result(null, null, null);
            }

            _comparison = overrides.RebuildWithTarget
                ? TableComparer.Compare(Target.ItemTables, Target.ItemTables)
                : owner._comparisons.FirstOrDefault(comparison =>
                      comparison.Source.Label == _source.ItemTables.Label &&
                      comparison.Target.Label == Target.ItemTables.Label)
                  ?? throw new InvalidOperationException(
                      $"No Phase 1 comparison from {_source.ItemTables.Label} to {Target.ItemTables.Label}.");
            _sourceTypes = SaveInspector.ItemTypeIndex.Load(_source.ItemTables.Directory);
            _targetTypes = SaveInspector.ItemTypeIndex.Load(Target.ItemTables.Directory);
            _sourceData = _source.CreateExternalData(_sourceVersion);
            _targetData = Target.CreateExternalData(TargetVersion);

            var root = Parse(data, _sourceData);
            if (root is D2Save { TrailingData.Length: > 0 } withTrailer)
            {
                _sourceTrailer = withTrailer.TrailingData.ToArray();
                _warnings.Add(
                    $"{_sourceTrailer.Length} byte(s) of opaque extension data follow the standard sections ({DescribeTrailer(_sourceTrailer)}). "
                    + "They are carried over byte-for-byte; the launcher cannot interpret or migrate them.");
            }

            var before = Flatten(root);
            _customPageItems = CustomPageItems(root);
            MigrateRoot(root);
            var migrated = Flatten(root);
            var intended = Diff(before, migrated);
            _checks.Add(new MigrationCheck("Recorded id changes match the changed fields", intended.Count == _changes,
                $"{_changes} recorded change(s), {intended.Count} changed field(s)"));
            if (_blockers.Count > 0)
            {
                return Result(null, null, null);
            }

            byte[] bytes;
            try
            {
                bytes = Serialize(root);
            }
            catch (Exception exception)
            {
                _blockers.Add(
                    $"D2SSharp could not serialize the converted save with the {Target.Label} definitions: {exception.GetType().Name}: {exception.Message}");
                return Result(null, null, null);
            }

            var serialized = Flatten(root);
            var conversion = Diff(migrated, serialized).Where(difference => difference.Path != "$.Version").ToList();
            if (conversion.Count > 0)
            {
                var message =
                    $"D2SSharp's version {_sourceVersion} -> {TargetVersion} conversion changed {conversion.Count} field(s): "
                    + string.Join("; ", conversion.Take(12)) + (conversion.Count > 12 ? "; ..." : "");
                (_sourceVersion == TargetVersion || conversion.Any(difference =>
                    !difference.Path.StartsWith("$.Character.", StringComparison.Ordinal))
                    ? _warnings
                    : _notes).Add(message);
            }

            Verify(bytes, root, before, serialized, intended, conversion);

            _uncertain.AddRange(_checks.Where(check => check is { Passed: false, Critical: false })
                .Select(check => $"Check not passed, the difference is in the output: {check.Name} ({check.Detail})."));

            Directory.CreateDirectory(outputDirectory);
            var outputPath = Path.Combine(outputDirectory, fileName);
            var uncertain = _uncertain.Count > 0 || _references.Any(reference => reference.Blocking);
            var approved = !_checks.Any(check => check is { Passed: false, Critical: true }) &&
                           (!uncertain || overrides.Force);
            var writtenPath = approved ? outputPath : outputPath + ".rejected";
            File.WriteAllBytes(writtenPath, bytes);
            return approved ? Result(outputPath, null, TargetVersion) : Result(null, writtenPath, TargetVersion);
        }

        private SaveMigrationResult Result(string? outputPath, string? rejectedPath, uint? outputVersion)
        {
            if (_rareNames + _runewords + _autoAffixes + _chronicleItems > 0)
            {
                _notes.Add(
                    $"Preserved without lookup tables: {_rareNames} rare/crafted/tempered name pair(s), {_runewords} runeword id(s), "
                    + $"{_autoAffixes} automagic id(s), {_chronicleItems} chronicle item id(s).");
            }

            var stats = _stats.Values.Select(accumulator => accumulator.ToResult()).OrderBy(stat => stat.Scope)
                .ThenBy(stat => stat.SourceId).ToList();
            return new SaveMigrationResult(
                input, outputPath, rejectedPath, _kind, _sourceVersion, outputVersion, _source?.Label, Target.Label,
                _identification,
                _itemsInspected, _itemsChanged, _references, stats, _customPageItems, _notes, _warnings, _blockers,
                _checks, overrides, _ambiguousLayouts,
                _uncertain);
        }

        /// <summary>Length-independent description of an unknown trailer: its first bytes and any 4-character ASCII markers.</summary>
        private static string DescribeTrailer(byte[] trailer)
        {
            var markers = new List<string>();
            for (var index = 0; index + 4 <= trailer.Length && markers.Count < 8; index++)
            {
                if (trailer.AsSpan(index, 4).ToArray().All(value =>
                        value is >= (byte)'0' and <= (byte)'9' or >= (byte)'A' and <= (byte)'Z'))
                {
                    markers.Add(System.Text.Encoding.ASCII.GetString(trailer, index, 4));
                    index += 3;
                }
            }

            return $"starts {Convert.ToHexString(trailer.AsSpan(0, Math.Min(8, trailer.Length)))}"
                   + (markers.Count == 0 ? "" : $", markers {string.Join(", ", markers.Distinct())}");
        }

        private SaveDataDefinition? Identify()
        {
            if (overrides.RebuildWithTarget)
            {
                var targetAttempt = SaveInspector.Inspect(input, [Target], []).Attempts[0];
                if (!targetAttempt.Succeeded)
                {
                    _identification.Add($"{Target.Label}: rejected, target parse failed ({targetAttempt.Error})");
                    _blockers.Add(
                        $"The installed target definitions could not decode this file: {targetAttempt.Error}");
                    return null;
                }

                _identification.Add(
                    $"{Target.Label}: parses with installed target definitions for an experimental rebuild, "
                    + $"{targetAttempt.AllItems.Count()} item(s)");
                if (IsNewerThanTarget([(Target, targetAttempt, UndefinedStats(targetAttempt).Count)]))
                {
                    return null;
                }

                _identification.Add($"Selected source layout for experimental target rebuild: {Target.Label}");
                return Target;
            }

            var inspection = SaveInspector.Inspect(input, owner._sources, owner._comparisons);
            var decoded = Decode(owner._sources, inspection.Attempts, "");
            if (IsNewerThanTarget(decoded))
            {
                return null;
            }

            if (decoded.Count == 0)
            {
                var available = string.Join(", ", owner._sources.Select(source => source.Label));
                _blockers.Add($"No available source layout can decode this file (tried: {available}). "
                              + "It may require definitions outside the frozen 2.1.2/3.0.12/3.1.0 data and the installed Reimagined mod; no mapping was guessed.");
                return null;
            }

            if (overrides.SourceLayout is { } manual)
            {
                var selected = decoded.Select(candidate => candidate.Definition)
                    .FirstOrDefault(definition => definition.Label == manual);
                if (selected is null)
                {
                    _blockers.Add(
                        $"The manually selected source layout '{manual}' cannot decode this file; a manual choice cannot override a failed parse.");
                    return null;
                }

                _identification.Add(
                    $"Source layout selected manually: {manual} ({decoded.Count} layout(s) decode the file).");
                return selected;
            }

            // A layout that defines every decoded stat and leaves the least data unparsed explains the file better than one that does not.
            var cleanest = decoded.Min(candidate => Rank(candidate));
            var viable = decoded.Where(candidate => Rank(candidate) == cleanest)
                .Select(candidate => (candidate.Definition, candidate.Attempt)).ToList();
            if (viable.Count < decoded.Count)
            {
                _identification.Add($"{Labels(viable)} explain the file best: "
                                    + string.Join(", ", decoded.Where(candidate => Rank(candidate) != cleanest)
                                        .Select(candidate =>
                                            $"{candidate.Definition.Label} leaves {candidate.Undefined} stat id(s) undefined and {candidate.Attempt.TrailingBytes} trailing byte(s)"))
                                    + ".");
            }

            // The best id fit wins. Ties are only acceptable between layouts that decode identically with the same item tables.
            var best = viable.Min(candidate => Disagreements(candidate.Attempt));
            var tied = viable.Where(candidate => Disagreements(candidate.Attempt) == best).ToList();
            // Newer item tables under an older stat encoding are only needed when the encoding's own item tables cannot explain the
            // file as well: a tied layout that decodes the file identically with its own item tables keeps the save on those.
            var unneeded = tied.Where(candidate => candidate.Definition.UsesNewerItemTables
                                                   && tied.Any(other => !other.Definition.UsesNewerItemTables
                                                                        && Signature(other.Attempt) ==
                                                                        Signature(candidate.Attempt)))
                .ToList();
            if (unneeded.Count > 0)
            {
                tied = tied.Except(unneeded).ToList();
                _identification.Add(
                    $"{Labels(unneeded)}: not needed, the file decodes identically with the item tables of {Labels(tied)}.");
            }

            var others = viable.Except(tied).Except(unneeded).ToList();
            var chosen = tied.OrderBy(candidate => candidate.Definition.AppendedStats.Count).First().Definition;
            if (tied.Select(candidate => (Signature(candidate.Attempt), candidate.Definition.ItemTables.Label))
                    .Distinct().Count() > 1)
            {
                // A source choice is only offered between historical layouts, not when the file may postdate the target.
                _ambiguousLayouts = _chronologyUnknown ? [] : [.. tied.Select(candidate => candidate.Definition.Label)];
                _uncertain.Add(
                    $"Ambiguous source layout: {Labels(tied)} fit the saved ids equally well ({best} disagreement(s)) "
                    + $"but decode the file differently or use different item tables; converting as {chosen.Label} is a guess.");
            }

            if (others.Count > 0)
            {
                _identification.Add($"{Labels(tied)} fit the saved ids best ({best} disagreement(s)); "
                                    + $"{string.Join(", ", others.Select(other => $"{other.Definition.Label} has {Disagreements(other.Attempt)}"))}.");
            }

            if (tied.Count > 1)
            {
                _identification.Add(
                    $"{Labels(tied)} decode this file identically with the same item tables; using {chosen.Label}.");
            }

            _identification.Add($"Selected source layout: {chosen.Label}");
            return chosen;

            static string Labels(List<(SaveDataDefinition Definition, ParseAttempt Attempt)> group) =>
                string.Join(" = ", group.Select(candidate => candidate.Definition.Label));

            static (int Undefined, int Trailing) Rank(
                (SaveDataDefinition Definition, ParseAttempt Attempt, int Undefined) candidate) =>
                (candidate.Undefined, candidate.Attempt.TrailingBytes);
        }

        private List<(SaveDataDefinition Definition, ParseAttempt Attempt, int Undefined)> Decode(
            IReadOnlyList<SaveDataDefinition> definitions, IReadOnlyList<ParseAttempt> attempts, string role)
        {
            var decoded = new List<(SaveDataDefinition Definition, ParseAttempt Attempt, int Undefined)>();
            foreach (var (definition, attempt) in definitions.Zip(attempts))
            {
                if (_kind == SaveFileKind.Character && definition.CharacterVersions.Count > 0
                                                    && !definition.CharacterVersions.Contains(_sourceVersion))
                {
                    _identification.Add(
                        $"{definition.Label}{role}: rejected, binary version {_sourceVersion} is not a character version of this layout "
                        + $"({string.Join(", ", definition.CharacterVersions)})");
                    continue;
                }

                if (!attempt.Succeeded)
                {
                    _identification.Add($"{definition.Label}{role}: rejected, parse failed ({attempt.Error})");
                    continue;
                }

                var undefined = UndefinedStats(attempt);
                var references = attempt.IdValidations.Sum(validation => validation.ReferenceCount);
                _identification.Add($"{definition.Label}{role}: parses, {attempt.AllItems.Count()} item(s), "
                                    + $"{references - Disagreements(attempt)}/{references} saved ids fit their table rows"
                                    + (attempt.TrailingBytes > 0
                                        ? $", {attempt.TrailingBytes} trailing byte(s) after the standard sections"
                                        : "")
                                    + (undefined.Count > 0
                                        ? $", stat id(s) [{string.Join(", ", undefined)}] undefined in this layout"
                                        : ""));
                decoded.Add((definition, attempt, undefined.Count));
            }

            return decoded;
        }

        private static List<int> UndefinedStats(ParseAttempt attempt) =>
            attempt.CharacterStats.Concat(attempt.AllItems.SelectMany(item => item.Stats))
                .Where(stat => stat.Name.StartsWith('('))
                .Select(stat => stat.Id)
                .Distinct()
                .ToList();

        /// <summary>
        /// Saves are only converted upwards. A file that a layout newer than the target explains better than every
        /// <paramref name="older"/> candidate is refused, whatever the overrides; one it explains as well has an unknown chronology.
        /// </summary>
        private bool IsNewerThanTarget(
            IReadOnlyList<(SaveDataDefinition Definition, ParseAttempt Attempt, int Undefined)> older)
        {
            if (owner._newerLayouts.Count == 0)
            {
                return false;
            }

            var newer = Decode(owner._newerLayouts, SaveInspector.Inspect(input, owner._newerLayouts, []).Attempts,
                $" (newer than {Target.Label})");
            if (newer.Count == 0)
            {
                return false;
            }

            var newerFit = newer.Min(Fit);
            var newerLabels = string.Join(", ", newer.Where(candidate => Fit(candidate) == newerFit)
                .Select(candidate => candidate.Definition.Label));
            if (older.Count == 0 || newerFit.CompareTo(older.Min(Fit)) < 0)
            {
                _identification.Add($"{newerLabels} explain the file better than any layout up to {Target.Label}.");
                _blockers.Add(
                    $"This file was saved with newer game data ({newerLabels}) than the installed target {Target.Label}. "
                    + "Saves are only converted to newer game data, so it is not converted, even with Force Conversion.");
                return true;
            }

            if (newerFit.CompareTo(older.Min(Fit)) == 0)
            {
                _chronologyUnknown = true;
                _uncertain.Add(
                    $"Source version unknown: the file decodes as well with {newerLabels}, which is newer than the installed target "
                    + $"{Target.Label}, as with an older layout; converting it may downgrade a newer save.");
            }

            return false;

            static (int Undefined, int Trailing, int Disagreements) Fit(
                (SaveDataDefinition Definition, ParseAttempt Attempt, int Undefined) candidate) =>
                (candidate.Undefined, candidate.Attempt.TrailingBytes, Disagreements(candidate.Attempt));
        }

        private static int Disagreements(ParseAttempt attempt) =>
            attempt.IdValidations.Sum(validation => validation.ReferenceCount - validation.Hypotheses[0].Agreeing);

        private static string Signature(ParseAttempt attempt)
        {
            static string Stat(InspectedStat stat) => $"{stat.Id}:{stat.Name}:{stat.Layer}={stat.Value}";
            return string.Join("\n", attempt.CharacterStats.Select(Stat).Concat(attempt.AllItems.Select(item =>
                $"{item.Location}|{item.Code}|{item.Quality}|{string.Join(",", item.References)}|{item.RareName}|{item.RunewordId}|"
                + string.Join(",", item.Stats.Select(Stat)))));
        }

        private object Parse(byte[] data, IExternalData externalData) => _kind == SaveFileKind.SharedStash
            ? D2StashSave.Read(data, externalData)
            : D2Save.Read(data, externalData);

        private byte[] Serialize(object root) => root is D2Save save
            ? save.ToBytes(_targetData, TargetVersion)
            : ((D2StashSave)root).ToBytes(_targetData, TargetVersion);

        private void MigrateRoot(object root)
        {
            foreach (var (location, items) in Containers(root))
            {
                for (var index = 0; index < items.Count; index++)
                {
                    MigrateItem(items[index], ItemPath(location, index, items[index]));
                }
            }

            if (root is D2Save save)
            {
                MigratePlayerStats(save.Stats, "character");
                if (save.Demon?.DemonData is { } demon)
                {
                    MigratePlayerStats(demon.Stats, "demon");
                }

                MigratePreview(save);
            }
            else
            {
                ReportChronicles((D2StashSave)root);
            }

            foreach (var stat in _stats.Values.Where(stat => stat.ProblemCount > 0).Select(stat => stat.ToResult()))
            {
                _uncertain.Add(
                    $"{stat.Scope} stat '{stat.SourceName}' ({stat.SourceId} -> {stat.TargetId}): {string.Join("; ", stat.Problems)}");
            }
        }

        private void MigrateItem(Item item, string path)
        {
            _itemsInspected++;
            var changesBefore = _changes;
            var code = item.ItemCodeString;
            CheckBaseItem(item, path, code);

            switch (item.QualityData)
            {
                case SetUniqueQualityData setUnique when item.Quality is ItemQuality.Unique or ItemQuality.Set:
                    var kind = item.Quality == ItemQuality.Unique ? TableKind.UniqueItems : TableKind.SetItems;
                    var sourceId = (int)setUnique.SetUniqueFileIndex;
                    var targetId = MapReference(kind, sourceId, kind == TableKind.UniqueItems ? "unique" : "set", path,
                        code, 4095);
                    setUnique.SetUniqueFileIndex = (ushort)targetId;
                    _setUniqueIds[item] = (sourceId, targetId);
                    break;
                case MagicQualityData magic:
                    magic.PrefixId = MapAffix(TableKind.MagicPrefix, magic.PrefixId, "magic prefix", path, code);
                    magic.SuffixId = MapAffix(TableKind.MagicSuffix, magic.SuffixId, "magic suffix", path, code);
                    break;
                case NormalCharmQualityData charm:
                    charm.PrefixId = MapAffix(TableKind.MagicPrefix, charm.PrefixId, "charm prefix", path, code);
                    charm.SuffixId = MapAffix(TableKind.MagicSuffix, charm.SuffixId, "charm suffix", path, code);
                    break;
                case RareCraftQualityData rare:
                    _rareNames++;
                    var role = item.Quality == ItemQuality.Craft ? "crafted" : "rare";
                    for (var slot = 0; slot < rare.Prefixes.Length; slot++)
                    {
                        rare.Prefixes[slot] = MapAffix(TableKind.MagicPrefix, rare.Prefixes[slot],
                            $"{role} prefix {slot + 1}", path, code);
                    }

                    for (var slot = 0; slot < rare.Suffixes.Length; slot++)
                    {
                        rare.Suffixes[slot] = MapAffix(TableKind.MagicSuffix, rare.Suffixes[slot],
                            $"{role} suffix {slot + 1}", path, code);
                    }

                    break;
                case TemperedQualityData:
                    _rareNames++;
                    break;
            }

            _runewords += item.RunewordId.HasValue ? 1 : 0;
            _autoAffixes += item.AutoAffixId.HasValue ? 1 : 0;
            _chronicleItems += item.ChronicleId.HasValue ? 1 : 0;

            MigrateItemStats(item.Stats, path);
            for (var index = 0; index < item.SetBonusStats.Count; index++)
            {
                MigrateItemStats(item.SetBonusStats[index], $"{path} set bonus {index + 1}");
            }

            if (item.RunewordStats is { } runewordStats)
            {
                MigrateItemStats(runewordStats, $"{path} runeword");
            }

            if (_changes > changesBefore)
            {
                _itemsChanged++;
            }

            for (var index = 0; index < item.Sockets.Count; index++)
            {
                if (item.Sockets[index] is { } socket)
                {
                    MigrateItem(socket, $"{path} > socket {index + 1} '{socket.ItemCodeString}'");
                }
            }
        }

        private void CheckBaseItem(Item item, string path, string code)
        {
            var sourceIndex = _sourceData.GetItemIndex(item.ItemCode, _sourceVersion);
            var targetIndex = _targetData.GetItemIndex(item.ItemCode, TargetVersion);
            if (targetIndex < 0)
            {
                _uncertain.Add(
                    $"{path}: base item '{code}' is not defined in {Target.Label}; it keeps its code if the serializer can write it.");
                return;
            }

            if (sourceIndex >= 0
                && _sourceData.GetItemInfo(sourceIndex, _sourceVersion) is var sourceInfo
                && _targetData.GetItemInfo(targetIndex, TargetVersion) is var targetInfo
                && sourceInfo != targetInfo)
            {
                // Only the decoded-field checks in Verify can show whether a metadata change actually alters the written item.
                var changed = typeof(ItemTypeInfo).GetProperties()
                    .Where(property => !Equals(property.GetValue(sourceInfo), property.GetValue(targetInfo)))
                    .Select(property =>
                        $"{property.Name} {property.GetValue(sourceInfo)} -> {property.GetValue(targetInfo)}");
                AddOnce(_warnings,
                    $"Base item '{code.Trim()}' has different item-type metadata in {Target.Label} ({string.Join(", ", changed)}); "
                    + "the verification checks compare every decoded field of the written items.");
            }
        }

        private ushort MapAffix(TableKind kind, ushort sourceId, string role, string path, string code) =>
            sourceId == 0 ? (ushort)0 : (ushort)MapReference(kind, sourceId, role, path, code, 2047);

        private int MapReference(TableKind kind, int sourceId, string role, string path, string code, int maxId)
        {
            var table = _comparison[kind];
            var sourceRow = table.SourceDefinitions.FirstOrDefault(definition => definition.SaveId == sourceId);
            var rawTarget = table.TargetDefinitions.FirstOrDefault(definition => definition.SaveId == sourceId);
            var atRawId = rawTarget is null
                ? "the same id names no target row"
                : $"the same id names {rawTarget} in {Target.Label}";

            if (!SaveInspector.Agrees(kind, sourceRow, code, _sourceTypes))
            {
                return Record(sourceId, ReferenceOutcome.PreservedWithWarning, false,
                    $"does not fit the item under {_source!.Label} ({SaveInspector.Explain(kind, sourceId, sourceRow, code, _sourceTypes)}); "
                    + $"origin unknown, preserved unchanged; {atRawId}");
            }

            var mapping = table.ForSourceSaveId(sourceId);
            if (mapping is { Status: MappingStatus.Matched, Target.SaveId: { } targetId } &&
                mapping.Method != MatchMethod.DefinitionFingerprint)
            {
                if (targetId > maxId)
                {
                    return Record(sourceId, ReferenceOutcome.ManualAttention, true,
                        $"'{sourceRow!.Key}' -> {mapping.Target} id {targetId} does not fit the {(maxId == 4095 ? 12 : 11)}-bit save field; "
                        + $"preserved unchanged; {atRawId}");
                }

                var reason = $"'{sourceRow!.Key}' -> {mapping.Target} via {mapping.Method}";
                if (!SaveInspector.Agrees(kind, mapping.Target, code, _targetTypes))
                {
                    // The identity is established by the source row; only the target table moved the definition to another base.
                    reason +=
                        $"; {Target.Label} defines it on base '{mapping.Target.Field(TableSchema.CodeColumn(kind)!)}', the item keeps '{code}'";
                    _warnings.Add($"{path}: {role} {sourceId} -> {targetId} maps by identity, but {reason}.");
                }

                return Record(targetId,
                    targetId == sourceId ? ReferenceOutcome.AlreadyCompatible : ReferenceOutcome.Mapped, false, reason);
            }

            if (kind is TableKind.UniqueItems or TableKind.SetItems &&
                RegisteredIdentity(table, sourceRow!, sourceId, code) is ({ } registered, { } entry))
            {
                if (registered.SaveId is not { } registeredId || registeredId > maxId)
                {
                    return Record(sourceId, ReferenceOutcome.ManualAttention, true,
                        $"registered target '{registered.Key}' id {registered.SaveId} does not fit the 12-bit save field; preserved unchanged; {atRawId}");
                }

                AddOnce(_notes,
                    $"Registered identity mapping: {kind} '{sourceRow!.Key}' ({_source!.Label} id {sourceId}) -> '{registered.Key}' "
                    + $"({Target.Label} id {registeredId}); the item keeps its rolled properties. {entry.Mapping.Description}"
                        .TrimEnd());
                return Record(registeredId,
                    registeredId == sourceId ? ReferenceOutcome.AlreadyCompatible : ReferenceOutcome.Mapped, false,
                    $"'{sourceRow.Key}' -> {registered} via registered identity mapping ({entry.Mapping.SourceCheckpoint} -> {entry.Mapping.TargetCheckpoint})");
            }

            var affixRename = kind is TableKind.MagicPrefix or TableKind.MagicSuffix &&
                              rawTarget is { IsPlaceholder: false }
                ? RegisteredAffixRename(kind, sourceRow!, rawTarget, sourceId)
                : null;
            if (kind is TableKind.MagicPrefix or TableKind.MagicSuffix
                && rawTarget is { IsPlaceholder: false }
                && (affixRename is not null || rawTarget.Key.Equals(sourceRow!.Key, StringComparison.OrdinalIgnoreCase))
                && SaveInspector.Agrees(kind, rawTarget, code, _targetTypes))
            {
                var changedFields = TableSchema.AffixDefinition
                    .Where(field =>
                        !string.Equals(sourceRow!.Field(field), rawTarget.Field(field), StringComparison.Ordinal))
                    .ToList();
                var phase1 = $"Phase 1 left '{sourceRow!.Key}' {mapping?.Status.ToString() ?? "unmapped"}";
                var keeps = affixRename is null
                    ? "keeps its name, mods"
                    : $"is its registered rename '{rawTarget.Key}' ({affixRename.Mapping.SourceCheckpoint} -> {affixRename.Mapping.TargetCheckpoint}) and keeps its mods";
                return TableSchema.Fingerprint(rawTarget, TableSchema.AffixStructure) ==
                       TableSchema.Fingerprint(sourceRow, TableSchema.AffixStructure)
                    ? Record(sourceId, ReferenceOutcome.AlreadyCompatible, false,
                        $"{phase1}, but the row at the same id {keeps} and item types")
                    : Record(sourceId, ReferenceOutcome.PreservedWithWarning, false,
                        $"{phase1}; the same id still names '{rawTarget.Key}' and allows this item type, but its definition changed "
                        + $"[{string.Join(", ", changedFields)}]; the item's rolled properties are stored separately and are unaffected");
            }

            return Unresolved(mapping switch
            {
                null => "no Phase 1 mapping",
                { Method: MatchMethod.DefinitionFingerprint } =>
                    $"only a fingerprint rename match exists ({mapping.Reason}); not applied",
                _ => $"Phase 1 status {mapping.Status}: {mapping.Reason}",
            });

            int Unresolved(string why)
            {
                return Record(sourceId, ReferenceOutcome.ManualAttention, false,
                    $"'{sourceRow!.Key}' {why}; preserved unchanged; {atRawId}");
            }

            int Record(int targetId, ReferenceOutcome outcome, bool blocking, string reason)
            {
                _references.Add(new ReferenceResult(path, code, role, kind, sourceId, targetId, outcome, blocking,
                    reason,
                    sourceRow is { IsPlaceholder: false } ? sourceRow.Key : null,
                    rawTarget is { IsPlaceholder: false } ? rawTarget.Key : null));
                if (targetId != sourceId)
                {
                    _changes++;
                }

                return targetId;
            }
        }

        /// <summary>
        /// A registered rename applies only when the saved id still names the registered row and base, and the target has exactly
        /// one row with the registered name on that base that fits the item and is not already another row's Phase 1 match.
        /// </summary>
        private (TableDefinition Target, ResolvedIdentityMapping Entry)? RegisteredIdentity(TableComparison table,
            TableDefinition sourceRow, int sourceId, string code)
        {
            var entry = owner._identityMappings.FirstOrDefault(candidate => candidate.Mapping.Kind == table.Kind
                                                                            && candidate.Mapping.SourceId == sourceId
                                                                            && string.Equals(candidate.SourceDirectory,
                                                                                _source!.ItemTables.Directory,
                                                                                StringComparison.OrdinalIgnoreCase));
            if (entry is null)
            {
                return null;
            }

            var mapping = entry.Mapping;
            var codeColumn = TableSchema.CodeColumn(table.Kind)!;
            var candidates = table.TargetDefinitions
                .Where(definition => !definition.IsPlaceholder &&
                                     definition.Key.Equals(entry.TargetKey, StringComparison.OrdinalIgnoreCase))
                .ToList();
            // A Phase 1 fingerprint match of this same row confirms the registered rename rather than competing with it.
            var claimedBy = candidates.Count == 1
                ? table.Matched.FirstOrDefault(matched =>
                    ReferenceEquals(matched.Target, candidates[0]) && !ReferenceEquals(matched.Source, sourceRow))
                : null;
            var problem = !sourceRow.Key.Equals(mapping.SourceKey, StringComparison.OrdinalIgnoreCase)
                          || !sourceRow.Field(codeColumn).Equals(mapping.Code, StringComparison.OrdinalIgnoreCase)
                ? $"the saved id names {sourceRow} on '{sourceRow.Field(codeColumn)}', not '{mapping.SourceKey}' on '{mapping.Code}'"
                : candidates.Count != 1
                    ? $"{Target.Label} has {candidates.Count} row(s) named '{entry.TargetKey}'"
                    : !candidates[0].Field(codeColumn).Equals(mapping.Code, StringComparison.OrdinalIgnoreCase)
                        ? $"{Target.Label} defines '{entry.TargetKey}' on base '{candidates[0].Field(codeColumn)}'"
                        : !SaveInspector.Agrees(table.Kind, candidates[0], code, _targetTypes)
                            ? $"'{entry.TargetKey}' does not fit the saved base '{code}'"
                            : claimedBy is not null
                                ? $"'{entry.TargetKey}' is already the Phase 1 match of '{claimedBy.Source!.Key}'"
                                : null;
            if (problem is null)
            {
                return (candidates[0], entry);
            }

            AddOnce(_warnings,
                $"Registered identity mapping '{mapping.SourceKey}' -> '{entry.TargetKey}' was not applied: {problem}.");
            return null;
        }

        /// <summary>A registered affix rename keeps the saved id; it applies only when that id names the registered names on both sides.</summary>
        private ResolvedIdentityMapping? RegisteredAffixRename(TableKind kind, TableDefinition sourceRow,
            TableDefinition rawTarget, int sourceId) =>
            owner._identityMappings.FirstOrDefault(candidate => candidate.Mapping.Kind == kind
                                                                && candidate.Mapping.Covers(sourceId)
                                                                && string.Equals(candidate.SourceDirectory,
                                                                    _source!.ItemTables.Directory,
                                                                    StringComparison.OrdinalIgnoreCase)
                                                                && candidate.Mapping.SourceKey.Equals(sourceRow.Key,
                                                                    StringComparison.OrdinalIgnoreCase)
                                                                && candidate.TargetKey.Equals(rawTarget.Key,
                                                                    StringComparison.OrdinalIgnoreCase));

        private static void AddOnce(List<string> lines, string line)
        {
            if (!lines.Contains(line))
            {
                lines.Add(line);
            }
        }

        private (TableDefinition? Source, TableDefinition? Target, string? Problem) MapStat(int sourceId)
        {
            var source = _source!.Stat(sourceId);
            if (source is null or { IsPlaceholder: true })
            {
                return (source, null, $"stat id {sourceId} has no definition in {_source.Label}");
            }

            TableDefinition target;
            if (sourceId >= _source.Stats.Count - _source.AppendedStats.Count)
            {
                var candidates = Target.Stats
                    .Where(stat => stat.Key.Equals(source.Key, StringComparison.OrdinalIgnoreCase)).ToList();
                if (candidates.Count != 1)
                {
                    return (source, null,
                        $"appended stat '{source.Key}' has {candidates.Count} definition(s) in {Target.Label}");
                }

                target = candidates[0];
            }
            else
            {
                var mapping = _comparison[TableKind.ItemStatCost].ForSourceSaveId(sourceId);
                if (mapping is not { Status: MappingStatus.Matched, Target: { } matched })
                {
                    return (source, null,
                        $"'{source.Key}' is {mapping?.Status.ToString() ?? "unmapped"} in Phase 1 ({mapping?.Reason})");
                }

                target = matched;
            }

            return target.SaveId is { } id && id < (int)StatId.Terminator
                ? (source, target, null)
                : (source, target, $"target id {target.SaveId} does not fit the 9-bit stat id");
        }

        private StatAccumulator Accumulate(string scope, int sourceId, TableDefinition? source, TableDefinition? target,
            string sourceEncoding, string targetEncoding)
        {
            if (!_stats.TryGetValue((scope, sourceId), out var accumulator))
            {
                accumulator = new StatAccumulator(scope, source, target, sourceId, sourceEncoding, targetEncoding);
                _stats[(scope, sourceId)] = accumulator;
            }

            return accumulator;
        }

        private void MigrateItemStats(List<Stat> stats, string path)
        {
            var secondary = new HashSet<int>();
            var synthetic = new HashSet<int>();
            for (var index = 0; index < stats.Count; index++)
            {
                var paired = stats[index].Id.GetPairedStats();
                for (var offset = 0; offset < paired.Length && index + 1 + offset < stats.Count; offset++)
                {
                    if (stats[index + 1 + offset].Id == paired[offset])
                    {
                        secondary.Add(index + 1 + offset);
                    }
                }

                // D2SSharp appends a PoisonCount entry after each poison group on read and skips it on write.
                var poisonCount = index + paired.Length + 1;
                if (stats[index].Id == StatId.PoisonMinDamage && poisonCount < stats.Count &&
                    stats[poisonCount].Id == StatId.PoisonCount)
                {
                    synthetic.Add(poisonCount);
                }
            }

            for (var index = 0; index < stats.Count; index++)
            {
                if (synthetic.Contains(index))
                {
                    continue;
                }

                var stat = stats[index];
                var sourceId = (int)stat.Id;
                var (source, target, problem) = MapStat(sourceId);
                var sourceInfo = _sourceData.GetStatInfo(stat.Id, _sourceVersion);
                var targetInfo = target is null || problem is not null
                    ? default
                    : _targetData.GetStatInfo((StatId)target.SaveId!.Value, TargetVersion);
                var accumulator = Accumulate("item", sourceId, source, target, ItemEncoding(sourceInfo),
                    target is null || problem is not null ? "-" : ItemEncoding(targetInfo));
                accumulator.Use(stat.Value);
                if (problem is not null)
                {
                    accumulator.Problem($"{path}: {problem}; {KeptId(sourceId)}");
                    continue;
                }

                var targetId = target!.SaveId!.Value;
                if (stat.Id == StatId.PoisonCount)
                {
                    accumulator.Problem(
                        $"{path}: id {sourceId} is D2SSharp's synthetic poison count and would be dropped on write");
                }

                if (targetId != sourceId && (PairedStats.Contains(stat.Id) || PairedStats.Contains((StatId)targetId)))
                {
                    accumulator.Problem(
                        $"{path}: a paired damage stat cannot change id under D2SSharp's fixed pairing; {KeptId(sourceId)}");
                    targetId = sourceId;
                    targetInfo = _targetData.GetStatInfo(stat.Id, TargetVersion);
                }

                foreach (var valueProblem in ItemValueProblems(stat, targetInfo, secondary.Contains(index)))
                {
                    accumulator.Problem($"{path}: {valueProblem}");
                }

                if (targetId != sourceId)
                {
                    stats[index] = stat with { Id = (StatId)targetId };
                    _changes++;
                }
            }
        }

        /// <summary>The fallback for a stat whose target identity cannot be established: its saved id is written unchanged.</summary>
        private string KeptId(int sourceId) =>
            $"saved id {sourceId} is kept, which names "
            + (Target.Stat(sourceId) is { IsPlaceholder: false } same ? $"'{same.Key}'" : "no stat") +
            $" in {Target.Label}";

        private static IEnumerable<string> ItemValueProblems(Stat stat, StatInfo info, bool secondary)
        {
            if (info.SaveBits <= 0)
            {
                yield return "target Save Bits is 0, so D2SSharp would drop the property";
                yield break;
            }

            var shifted = stat.Value >> info.ValShift;
            if (shifted << info.ValShift != stat.Value)
            {
                yield return $"value {stat.Value} loses precision with ValShift {info.ValShift}";
            }

            var encoded = shifted + info.SaveAdd;
            var maxEncoded = (1L << info.SaveBits) - 1;
            if (encoded < 0 || encoded > maxEncoded)
            {
                yield return
                    $"value {stat.Value} needs raw {encoded}, outside 0..{maxEncoded} (Save Bits {info.SaveBits}, Save Add {info.SaveAdd})";
            }

            if (!secondary && shifted == 0)
            {
                yield return "zero value; D2SSharp's writer omits zero-valued properties";
            }

            if (info.SaveParamBits == 0 ? stat.Layer != 0 : stat.Layer < 0 || stat.Layer >= 1L << info.SaveParamBits)
            {
                yield return $"parameter {stat.Layer} does not fit Save Param Bits {info.SaveParamBits}";
            }
        }

        private void MigratePlayerStats(List<Stat> stats, string scope)
        {
            for (var index = 0; index < stats.Count; index++)
            {
                var stat = stats[index];
                var sourceId = (int)stat.Id;
                var (source, target, problem) = MapStat(sourceId);
                var sourceInfo = _sourceData.GetStatInfo(stat.Id, _sourceVersion);
                var targetInfo = target is null || problem is not null
                    ? default
                    : _targetData.GetStatInfo((StatId)target.SaveId!.Value, TargetVersion);
                var accumulator = Accumulate(scope, sourceId, source, target, PlayerEncoding(sourceInfo, scope),
                    target is null || problem is not null ? "-" : PlayerEncoding(targetInfo, scope));
                accumulator.Use(stat.Value);
                if (problem is not null)
                {
                    accumulator.Problem($"{problem}; {KeptId(sourceId)}");
                    continue;
                }

                foreach (var valueProblem in PlayerValueProblems(stat, targetInfo, scope == "demon"))
                {
                    accumulator.Problem(valueProblem);
                }

                if (scope == "character" && target!.Field("Saved") != "1")
                {
                    _warnings.Add(
                        $"Character stat '{target.Key}' = {stat.Value} is not marked Saved in {Target.Label}.");
                }

                var targetId = target!.SaveId!.Value;
                if (targetId != sourceId)
                {
                    stats[index] = stat with { Id = (StatId)targetId };
                    _changes++;
                }
            }
        }

        private static IEnumerable<string> PlayerValueProblems(Stat stat, StatInfo info, bool demon)
        {
            if (demon)
            {
                if (stat.Value is < 0 or > uint.MaxValue)
                {
                    yield return $"value {stat.Value} does not fit the 32-bit demon stat value";
                }
            }
            else if (info.CsvBits == 0)
            {
                if (stat.Value != 0)
                {
                    yield return $"value {stat.Value} cannot be stored because target CSvBits is 0";
                }
            }
            else
            {
                var (min, max) = info.CsvBits >= 32
                    ? (0L, (long)uint.MaxValue)
                    : info.CsvSigned
                        ? (-(1L << (info.CsvBits - 1)), (1L << (info.CsvBits - 1)) - 1)
                        : (0L, (1L << info.CsvBits) - 1);
                if (stat.Value < min || stat.Value > max)
                {
                    yield return
                        $"value {stat.Value} outside {min}..{max} (CSvBits {info.CsvBits}, CSvSigned {info.CsvSigned}); D2SSharp would clamp it";
                }
            }

            var layerFits = info.CsvParam == 0
                ? stat.Layer == 0
                : stat.Layer >= -(1L << (info.CsvParam - 1)) && stat.Layer < 1L << (info.CsvParam - 1);
            if (!layerFits)
            {
                yield return $"parameter {stat.Layer} does not fit CSvParam {info.CsvParam}";
            }
        }

        private static string ItemEncoding(StatInfo info) =>
            $"Save Bits {info.SaveBits}, Save Add {info.SaveAdd}, Save Param Bits {info.SaveParamBits}, ValShift {info.ValShift}";

        private static string PlayerEncoding(StatInfo info, string scope) => scope == "demon"
            ? $"32-bit value, CSvParam {info.CsvParam}"
            : $"CSvBits {info.CsvBits}, CSvSigned {(info.CsvSigned ? 1 : 0)}, CSvParam {info.CsvParam}";

        private void MigratePreview(D2Save save)
        {
            var preview = save.Character.Preview;
            var slots = new (string Name, PreviewItem Slot, BodyLocation[] Locations)[]
            {
                ("head", preview.Head, [BodyLocation.Head]),
                ("torso", preview.Torso, [BodyLocation.Torso]),
                ("left hand", preview.LeftHand, [BodyLocation.RightArm, BodyLocation.LeftArm]),
                ("right hand", preview.RightHand, [BodyLocation.RightArm, BodyLocation.LeftArm]),
            };
            foreach (var (name, slot, locations) in slots)
            {
                if (slot.Quality is not (ItemQuality.Unique or ItemQuality.Set))
                {
                    continue;
                }

                var targets = save.Items
                    .Where(item =>
                        item.Position.Mode == ItemMode.Equipped && locations.Contains(item.Position.BodyLocation) &&
                        item.ItemCode == slot.Code)
                    .Select(item =>
                        _setUniqueIds.TryGetValue(item, out var ids) ? ids : ((int Source, int Target)?)null)
                    .Where(ids => ids?.Source == slot.FileIndex)
                    .Select(ids => ids!.Value.Target)
                    .Distinct()
                    .ToList();
                if (targets.Count != 1)
                {
                    _notes.Add(
                        $"Preview {name} ({slot.CodeString} {slot.Quality} {slot.FileIndex}) has no single matching equipped item; left unchanged.");
                }
                else if (targets[0] != slot.FileIndex)
                {
                    _notes.Add(
                        $"Preview {name}: file index {slot.FileIndex} -> {targets[0]} to match the equipped item.");
                    slot.FileIndex = (ushort)targets[0];
                    _changes++;
                }
            }
        }

        private void ReportChronicles(D2StashSave stash)
        {
            for (var index = 0; index < stash.Count; index++)
            {
                if (stash[index].Chronicle is not { } chronicle)
                {
                    continue;
                }

                var entries = chronicle.SetEntries.Count + chronicle.UniqueEntries.Count +
                              chronicle.RunewordEntries.Count;
                if (entries == 0)
                {
                    _notes.Add($"Stash tab {index + 1}: empty chronicle section preserved.");
                    continue;
                }

                var renamed = Renamed(chronicle.SetEntries, TableKind.SetItems) +
                              Renamed(chronicle.UniqueEntries, TableKind.UniqueItems);
                (renamed > 0 ? _warnings : _notes).Add(
                    $"Stash tab {index + 1}: chronicle discoveries ({chronicle.SetEntries.Count} set, {chronicle.UniqueEntries.Count} unique, "
                    + $"{chronicle.RunewordEntries.Count} runeword) are preserved unchanged; {renamed} set/unique entr(ies) name a different "
                    + $"or no row in {Target.Label}; runeword entries have no lookup table to check.");
            }

            int Renamed(List<ChronicleEntry> entries, TableKind kind) => entries.Count(entry =>
                _comparison[kind].SourceDefinitions.FirstOrDefault(definition => definition.SaveId == (int)entry.ItemId)
                    is not { } source
                || _comparison[kind].TargetDefinitions
                    .FirstOrDefault(definition => definition.SaveId == (int)entry.ItemId) is not { } target
                || !source.Key.Equals(target.Key, StringComparison.OrdinalIgnoreCase));
        }

        private void Verify(byte[] bytes, object migratedRoot, Dictionary<string, string> before,
            Dictionary<string, string> serialized,
            List<FieldDifference> intended, List<FieldDifference> conversion)
        {
            // The output must meet every header rule, including the ones a source may break and still be recovered from.
            var headerProblems = CheckHeader(bytes, _kind, out var version, null);
            _checks.Add(new MigrationCheck("Header, declared size and checksum",
                headerProblems.Count == 0 && version == TargetVersion,
                headerProblems.Count == 0
                    ? $"version {version}, {bytes.Length} bytes"
                    : string.Join("; ", headerProblems), Critical: true));

            object reparsed;
            try
            {
                reparsed = Parse(bytes, _targetData);
            }
            catch (Exception exception)
            {
                _checks.Add(new MigrationCheck($"Reparse with {Target.Label} definitions", false,
                    $"{exception.GetType().Name}: {exception.Message}", Critical: true));
                return;
            }

            var trailer = reparsed is D2Save reparsedSave ? reparsedSave.TrailingData : [];
            _checks.Add(new MigrationCheck($"Reparse with {Target.Label} definitions", true,
                $"parses, {trailer.Length} trailing byte(s)", Critical: true));
            if (_sourceTrailer.Length > 0 || trailer.Length > 0)
            {
                _checks.Add(new MigrationCheck("Trailing extension data preserved byte-for-byte",
                    trailer.AsSpan().SequenceEqual(_sourceTrailer),
                    $"source {_sourceTrailer.Length} byte(s), output {trailer.Length} byte(s)"
                    + (trailer.AsSpan().SequenceEqual(_sourceTrailer) ? ", identical" : ", different")));
            }

            var after = Flatten(reparsed);
            if (overrides.RebuildWithTarget)
            {
                var modelDifferences = Diff(before, after)
                    .Where(difference => difference.Path is not ("$.FileSize" or "$.Checksum" or "$.Version"))
                    .ToList();
                if (modelDifferences.Count == 0)
                {
                    _notes.Add("Target-definition rebuild model comparison: no meaningful model differences.");
                }
                else
                {
                    _warnings.AddRange(modelDifferences.Select(difference =>
                        $"Target-definition rebuild model difference: {difference}"));
                }
            }

            var fidelity = Diff(serialized, after)
                .Where(difference => difference.Path is not ("$.FileSize" or "$.Checksum")).ToList();
            _checks.Add(new MigrationCheck("Reparsed output equals the migrated model", fidelity.Count == 0,
                Summarize(fidelity, "identical")));

            var missing = intended.Where(difference => after.GetValueOrDefault(difference.Path) != difference.After)
                .ToList();
            _checks.Add(new MigrationCheck("Intended id changes present in the output", missing.Count == 0,
                $"{intended.Count - missing.Count}/{intended.Count} present" +
                (missing.Count == 0 ? "" : $"; missing: {Summarize(missing, "")}")));

            var allowed = intended.Concat(conversion).Select(difference => difference.Path)
                .Concat(["$.Version", "$.FileSize", "$.Checksum"])
                .ToHashSet(StringComparer.Ordinal);
            var unexpected = Diff(before, after).Where(difference => !allowed.Contains(difference.Path)).ToList();
            _checks.Add(new MigrationCheck("No unintended changes against the source", unexpected.Count == 0,
                Summarize(unexpected, "none")));

            var sourceCounts = Count(migratedRoot);
            var outputCounts = Count(reparsed);
            _checks.Add(new MigrationCheck("Item, socket and property counts", sourceCounts == outputCounts,
                $"source {sourceCounts.Items} items / {sourceCounts.Sockets} socketed / {sourceCounts.Stats} properties, "
                + $"output {outputCounts.Items} / {outputCounts.Sockets} / {outputCounts.Stats}"));

            var outputCustom = CustomPageItems(reparsed);
            _checks.Add(new MigrationCheck("Custom inventory page items preserved",
                _customPageItems.SequenceEqual(outputCustom),
                _customPageItems.Count == 0
                    ? "no items on non-retail store pages"
                    : $"{outputCustom.Count}/{_customPageItems.Count} item(s) on page(s) "
                      + $"{string.Join(", ", _customPageItems.Select(item => item.Page + 1).Distinct())} (raw) with identical positions and stat counts"));

            if (migratedRoot is D2Save source && reparsed is D2Save output)
            {
                string[] sections =
                [
                    "$.Character.", "$.Quests.", "$.Waypoints.", "$.PlayerIntro.", "$.Skills.", "$.Corpses",
                    "$.MercItems", "$.IronGolem"
                ];
                var sectionChanges = unexpected.Where(difference =>
                    sections.Any(section => difference.Path.StartsWith(section, StringComparison.Ordinal))).ToList();
                _checks.Add(new MigrationCheck(
                    "Character, quests, waypoints, NPC intro, skills and mercenary unchanged",
                    sectionChanges.Count == 0,
                    $"'{DisplayName(source)}' {source.Character.Class} level {source.Character.Level} -> "
                    + $"'{DisplayName(output)}' {output.Character.Class} level {output.Character.Level}; {Summarize(sectionChanges, "no differences")}"));
            }
        }

        private static string DisplayName(D2Save save) =>
            string.IsNullOrWhiteSpace(save.Character.Preview.Name) ? save.Character.Name : save.Character.Preview.Name;

        private static string Summarize(List<FieldDifference> differences, string empty) => differences.Count == 0
            ? empty
            : $"{differences.Count} difference(s): {string.Join("; ", differences.Take(8))}" +
              (differences.Count > 8 ? "; ..." : "");

        private static IEnumerable<(string Location, IReadOnlyList<Item> Items)> Containers(object root)
        {
            if (root is D2StashSave stash)
            {
                for (var index = 0; index < stash.Count; index++)
                {
                    yield return ($"Stash tab {index + 1}", stash[index].Items);
                }

                yield break;
            }

            var save = (D2Save)root;
            yield return ("Player", save.Items);
            for (var index = 0; index < save.Corpses.Count; index++)
            {
                yield return ($"Corpse {index + 1}", save.Corpses[index].Items);
            }

            if (save.MercItems is { } mercItems)
            {
                yield return ("Mercenary", mercItems.Items);
            }

            if (save.IronGolem?.GolemItem is { } golemItem)
            {
                yield return ("Iron golem", [golemItem]);
            }
        }

        private static string ItemPath(string location, int index, Item item)
        {
            var position = item.Position;
            var place = position.IsOnGround
                ? $"ground {position.GroundX},{position.GroundY}"
                : $"{position.Mode} {(Enum.IsDefined(position.StorePage) ? position.StorePage.ToString() : $"page {(int)position.StorePage}")} "
                  + $"{position.InvX},{position.InvY}" +
                  (position.BodyLocation == BodyLocation.None ? "" : $" {position.BodyLocation}");
            return $"{location} #{index + 1} '{item.ItemCodeString}' ({place})";
        }

        /// <summary>Items stored on a page D2SSharp has no name for, such as the D2RLoader Charm Inventory page.</summary>
        private static List<CustomPageItem> CustomPageItems(object root) => Containers(root)
            .SelectMany(container =>
                container.Items.Select((item, index) => (container.Location, Item: item, Index: index)))
            .Where(entry => !entry.Item.Position.IsOnGround && !Enum.IsDefined(entry.Item.Position.StorePage))
            .Select(entry => new CustomPageItem(
                $"{entry.Location} #{entry.Index + 1}",
                entry.Item.ItemCodeString,
                (int)entry.Item.Position.StorePage,
                entry.Item.Position.InvX,
                entry.Item.Position.InvY,
                entry.Item.Stats.Count))
            .ToList();

        private static (int Items, int Sockets, int Stats) Count(object root)
        {
            var (items, sockets, stats) = (0, 0, 0);
            foreach (var item in Containers(root).SelectMany(container => container.Items))
            {
                Visit(item, false);
            }

            return (items, sockets, stats);

            void Visit(Item item, bool socketed)
            {
                if (socketed)
                {
                    sockets++;
                }
                else
                {
                    items++;
                }

                stats += item.Stats.Count + item.SetBonusStats.Sum(list => list.Count) +
                         (item.RunewordStats?.Count ?? 0);
                foreach (var socket in item.Sockets.OfType<Item>())
                {
                    Visit(socket, true);
                }
            }
        }
    }

    /// <summary>
    /// Returns the problems that make the file undecodable. Problems a decodable source can be recovered from (version label, declared size,
    /// checksum, size limit) go to <paramref name="recoverable"/>; when it is null, as for an output, every problem is returned.
    /// </summary>
    private static List<string> CheckHeader(byte[] data, SaveFileKind kind, out uint version, List<string>? recoverable)
    {
        var problems = new List<string>();
        var soft = recoverable ?? problems;
        version = 0;
        if (data.Length < 16 || BinaryPrimitives.ReadUInt32LittleEndian(data) != SaveMagic)
        {
            problems.Add("missing the 0xAA55AA55 save magic");
            return problems;
        }

        if (kind == SaveFileKind.Character)
        {
            version = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4));
            var declared = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8));
            if (!SupportedVersions.Contains(version))
            {
                soft.Add(
                    $"binary version {version} is not one of the retail versions D2SSharp is known to read ({string.Join(", ", SupportedVersions)}); "
                    + "it is decoded experimentally");
            }

            if (declared != data.Length)
            {
                soft.Add(
                    $"declared size {declared} differs from the file size {data.Length}; the output declares its real size");
            }

            if (data.Length > RetailCharacterSizeLimit)
            {
                soft.Add(
                    $"{data.Length} bytes exceeds the retail {RetailCharacterSizeLimit}-byte limit (D2RLoader extended save?)");
            }

            if (!ChecksumCalculator.Verify(data))
            {
                soft.Add(
                    "checksum mismatch; the output gets a recalculated checksum, which does not prove the source data is uncorrupted");
            }

            return problems;
        }

        var offset = 0;
        var tabs = 0;
        while (offset < data.Length)
        {
            if (data.Length - offset < StashTabHeaderSize ||
                BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset)) != SaveMagic)
            {
                soft.Add($"{data.Length - offset} unrecognized byte(s) after tab {tabs} at offset {offset}");
                break;
            }

            var stashFormat = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 4));
            var itemFormat = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 8));
            var size = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset + 16));
            version = tabs == 0 ? itemFormat : version;
            if (stashFormat is < 1 or > 2 || !SupportedVersions.Contains(itemFormat) || itemFormat != version)
            {
                soft.Add(
                    $"tab {tabs + 1}: stash format {stashFormat}, item format {itemFormat} is not a known retail layout; it is decoded experimentally");
            }

            if (size < StashTabHeaderSize || offset + size > data.Length)
            {
                soft.Add($"tab {tabs + 1}: declared size {size} does not fit the file");
                break;
            }

            offset += size;
            tabs++;
        }

        return problems;
    }

    private static Dictionary<string, string> Flatten(object root)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        Visit(root, "$", 0);
        return values;

        void Visit(object? value, string path, int depth)
        {
            if (depth > 32)
            {
                throw new InvalidOperationException($"Save model is nested too deeply at {path}.");
            }

            switch (value)
            {
                case null:
                    values[path] = "null";
                    return;
                case string text:
                    values[path] = text;
                    return;
                case byte[] bytes:
                    values[path] = Convert.ToHexString(bytes);
                    return;
                case Enum:
                    values[path] = value.ToString()!;
                    return;
                case IEnumerable sequence:
                    var count = 0;
                    foreach (var element in sequence)
                    {
                        Visit(element, $"{path}[{count++}]", depth + 1);
                    }

                    values[$"{path}.Count"] = count.ToString(CultureInfo.InvariantCulture);
                    return;
            }

            var type = value.GetType();
            if (type.IsPrimitive || value is decimal)
            {
                values[path] = Convert.ToString(value, CultureInfo.InvariantCulture)!;
                return;
            }

            // Settable properties only: computed getters (FileIndex, IsDead, ...) would double-count changes.
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.CanRead && property.CanWrite && property.GetIndexParameters().Length == 0)
                {
                    Visit(property.GetValue(value), $"{path}.{property.Name}", depth + 1);
                }
            }
        }
    }

    private static List<FieldDifference> Diff(Dictionary<string, string> before, Dictionary<string, string> after) =>
        before.Keys
            .Union(after.Keys)
            .Where(path => before.GetValueOrDefault(path) != after.GetValueOrDefault(path))
            .Order(StringComparer.Ordinal)
            .Select(path => new FieldDifference(path, before.GetValueOrDefault(path), after.GetValueOrDefault(path)))
            .ToList();
}