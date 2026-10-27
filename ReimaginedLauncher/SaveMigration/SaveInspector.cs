using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using D2SSharp;
using D2SSharp.Enums;
using D2SSharp.Model;

namespace ReimaginedLauncher.SaveMigration;

public enum SaveFileKind
{
    Character,
    SharedStash,
}

/// <param name="Version">Character binary version, or the item format of the first stash tab.</param>
/// <param name="StashFormat">The stash tab format, for shared stashes only.</param>
public sealed record SaveHeader(
    SaveFileKind Kind, bool MagicValid, uint Version, uint? StashFormat, uint? DeclaredSize, int ActualSize, bool? ChecksumValid);

public sealed record SavedItemReference(TableKind Kind, int SaveId, string Role);

public sealed record InspectedStat(int Id, string Name, int Layer, long Value, string Source);

public sealed record InspectedItem(
    string Location,
    string Code,
    ItemQuality Quality,
    IReadOnlyList<SavedItemReference> References,
    string? RareName,
    int? RunewordId,
    IReadOnlyList<InspectedStat> Stats,
    IReadOnlyList<InspectedItem> Sockets)
{
    public IEnumerable<InspectedItem> SelfAndSockets() => Sockets.SelectMany(socket => socket.SelfAndSockets()).Prepend(this);
}

public sealed record IdHypothesisResult(string Hypothesis, int Agreeing, int Total);

public sealed record IdMismatch(SavedItemReference Reference, string ItemCode, string Location, string Resolved, string Reason)
{
    public override string ToString() =>
        $"{Reference.Role} id {Reference.SaveId} on '{ItemCode}' ({Location}) resolves to {Resolved}; {Reason}";
}

/// <summary>How well each id convention explains the saved references into one table.</summary>
/// <param name="AboveFirstDivider">References whose Phase 1 row lies after the first Expansion divider row.</param>
/// <param name="TierUpgrades">Phase 1 agreements whose saved base is another tier (normcode/ubercode/ultracode) of the table base.</param>
public sealed record IdTableValidation(
    TableKind Kind,
    string ItemTablesLabel,
    int ReferenceCount,
    IReadOnlyList<int> DividerRows,
    int AboveFirstDivider,
    int TierUpgrades,
    IReadOnlyList<IdHypothesisResult> Hypotheses,
    IReadOnlyList<IdMismatch> Phase1Mismatches);

public sealed record ParseAttempt(
    string DefinitionLabel,
    bool Succeeded,
    string? Error,
    string? Character,
    int? Level,
    int TrailingBytes,
    IReadOnlyList<string> Notes,
    IReadOnlyList<InspectedStat> CharacterStats,
    IReadOnlyList<InspectedItem> Items,
    IReadOnlyList<IdTableValidation> IdValidations)
{
    public IEnumerable<InspectedItem> AllItems => Items.SelectMany(item => item.SelfAndSockets());

    public long? CharacterStat(string name) =>
        CharacterStats.FirstOrDefault(stat => stat.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value;
}

public sealed record ReferenceMapping(
    SavedItemReference Reference, string ItemCode, string Comparison, string Outcome, IReadOnlyList<string> Concerns);

public sealed record SaveInspection(
    string FileName,
    string Sha256,
    SaveHeader Header,
    IReadOnlyList<ParseAttempt> Attempts,
    IReadOnlyList<ReferenceMapping> ReferenceMappings,
    IReadOnlyList<string> StatConcerns)
{
    public ParseAttempt? Parsed => Attempts.FirstOrDefault(attempt => attempt.Succeeded);
}

/// <summary>Read-only inspection of a save: it never writes, converts or serializes the file it reads.</summary>
public static class SaveInspector
{
    private const uint SaveMagic = 0xAA55AA55;

    private static readonly (string Name, Func<IReadOnlyList<TableDefinition>, int, TableDefinition?> Resolve)[] Hypotheses =
    [
        ("Phase 1 save id", (definitions, id) => definitions.FirstOrDefault(definition => definition.SaveId == id)),
        ("physical row, zero-based, dividers counted", (definitions, id) => definitions.ElementAtOrDefault(id)),
        ("physical row, one-based, dividers counted", (definitions, id) => id < 1 ? null : definitions.ElementAtOrDefault(id - 1)),
        ("data row, zero-based, dividers skipped",
            (definitions, id) => definitions.Where(definition => !definition.IsPlaceholder).ElementAtOrDefault(id)),
        ("data row, one-based, dividers skipped",
            (definitions, id) => id < 1 ? null : definitions.Where(definition => !definition.IsPlaceholder).ElementAtOrDefault(id - 1)),
    ];

    public static SaveInspection Inspect(
        string path, IReadOnlyList<SaveDataDefinition> candidates, IReadOnlyList<SnapshotComparison> comparisons)
    {
        var data = File.ReadAllBytes(path);
        var header = ReadHeader(path, data);
        var attempts = candidates.Select(candidate => Attempt(data, header, candidate)).ToList();

        var parsedIndex = attempts.FindIndex(attempt => attempt.Succeeded);
        var mappings = new List<ReferenceMapping>();
        var statConcerns = new List<string>();
        if (parsedIndex >= 0)
        {
            var definition = candidates[parsedIndex];
            var applicable = comparisons.Where(comparison => comparison.Source.Label == definition.ItemTables.Label).ToList();
            mappings.AddRange(MapReferences(attempts[parsedIndex], applicable));
            statConcerns.AddRange(FindStatConcerns(attempts[parsedIndex], definition, applicable));
        }

        return new SaveInspection(
            Path.GetFileName(path), Convert.ToHexStringLower(SHA256.HashData(data)), header, attempts, mappings, statConcerns);
    }

    private static SaveHeader ReadHeader(string path, byte[] data)
    {
        var kind = Path.GetExtension(path).Equals(".d2i", StringComparison.OrdinalIgnoreCase)
            ? SaveFileKind.SharedStash
            : SaveFileKind.Character;
        if (data.Length < 16)
        {
            return new SaveHeader(kind, false, 0, null, null, data.Length, null);
        }

        var magicValid = BinaryPrimitives.ReadUInt32LittleEndian(data) == SaveMagic;
        return kind == SaveFileKind.SharedStash
            ? new SaveHeader(kind, magicValid, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8)),
                BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4)), null, data.Length, null)
            : new SaveHeader(kind, magicValid, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4)), null,
                BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8)), data.Length, ChecksumCalculator.Verify(data));
    }

    private static ParseAttempt Attempt(byte[] data, SaveHeader header, SaveDataDefinition definition)
    {
        try
        {
            var externalData = definition.CreateExternalData(header.Version);
            var notes = new List<string>();
            if (definition.AppendedStats.Count > 0)
            {
                notes.Add("Appended stats: " + string.Join(", ", definition.AppendedStats.Select(stat => stat.ToString())));
            }

            string? character = null;
            int? level = null;
            var trailingBytes = 0;
            var characterStats = new List<InspectedStat>();
            var items = new List<InspectedItem>();

            if (header.Kind == SaveFileKind.SharedStash)
            {
                var stash = D2StashSave.Read(data, externalData);
                foreach (var (tab, index) in stash.Select((tab, index) => (tab, index)))
                {
                    var location = $"Stash tab {index + 1} ({tab.TabType})";
                    notes.Add($"{location}: stash format {tab.StashFormat}, item format {tab.ItemFormat}, gold {tab.Gold}, "
                              + $"{tab.Items.Count} items{(tab.Chronicle is null ? "" : ", chronicle section present")}");
                    items.AddRange(tab.Items.Select(item => Describe(item, location, definition)));
                }
            }
            else
            {
                var save = D2Save.Read(data, externalData);
                character = string.IsNullOrWhiteSpace(save.Character.Preview.Name) ? save.Character.Name : save.Character.Preview.Name;
                level = save.Character.Level;
                trailingBytes = save.TrailingData.Length;
                characterStats.AddRange(save.Stats.Select(stat => Describe(stat, "character", definition)));
                items.AddRange(save.Items.Select(item => Describe(item, "Player", definition)));
                foreach (var (corpse, index) in save.Corpses.Select((corpse, index) => (corpse, index)))
                {
                    items.AddRange(corpse.Items.Select(item => Describe(item, $"Corpse {index + 1}", definition)));
                }

                if (save.MercItems is { } mercItems)
                {
                    items.AddRange(mercItems.Items.Select(item => Describe(item, "Mercenary", definition)));
                }

                if (save.IronGolem?.GolemItem is { } golemItem)
                {
                    items.Add(Describe(golemItem, "Iron golem", definition));
                }

                if (save.Version != header.Version)
                {
                    notes.Add($"D2SSharp reports version {save.Version}, header says {header.Version}.");
                }
            }

            var attempt = new ParseAttempt(
                definition.Label, true, null, character, level, trailingBytes, notes, characterStats, items, []);
            return attempt with { IdValidations = ValidateIds(attempt, definition) };
        }
        catch (Exception exception)
        {
            return new ParseAttempt(
                definition.Label, false, $"{exception.GetType().FullName}: {exception.Message}", null, null, 0, [], [], [], []);
        }
    }

    private static InspectedItem Describe(Item item, string location, SaveDataDefinition definition)
    {
        var references = new List<SavedItemReference>();
        string? rareName = null;

        void AddAffix(TableKind kind, int id, string role)
        {
            if (id != 0)
            {
                references.Add(new SavedItemReference(kind, id, role));
            }
        }

        switch (item.QualityData)
        {
            case SetUniqueQualityData setUnique when item.Quality == ItemQuality.Unique:
                references.Add(new SavedItemReference(TableKind.UniqueItems, setUnique.SetUniqueFileIndex, "unique"));
                break;
            case SetUniqueQualityData setUnique when item.Quality == ItemQuality.Set:
                references.Add(new SavedItemReference(TableKind.SetItems, setUnique.SetUniqueFileIndex, "set"));
                break;
            case MagicQualityData magic:
                AddAffix(TableKind.MagicPrefix, magic.PrefixId, "magic prefix");
                AddAffix(TableKind.MagicSuffix, magic.SuffixId, "magic suffix");
                break;
            case NormalCharmQualityData charm:
                AddAffix(TableKind.MagicPrefix, charm.PrefixId, "charm prefix");
                AddAffix(TableKind.MagicSuffix, charm.SuffixId, "charm suffix");
                break;
            case RareCraftQualityData rare:
                var role = item.Quality == ItemQuality.Craft ? "crafted" : "rare";
                rareName = $"rareprefix {rare.RarePrefixId}, raresuffix {rare.RareSuffixId}";
                for (var slot = 0; slot < rare.Prefixes.Length; slot++)
                {
                    AddAffix(TableKind.MagicPrefix, rare.Prefixes[slot], $"{role} prefix {slot + 1}");
                }

                for (var slot = 0; slot < rare.Suffixes.Length; slot++)
                {
                    AddAffix(TableKind.MagicSuffix, rare.Suffixes[slot], $"{role} suffix {slot + 1}");
                }

                break;
            case TemperedQualityData tempered:
                rareName = $"rareprefix {tempered.RarePrefixId}, raresuffix {tempered.RareSuffixId}";
                break;
        }

        var stats = item.Stats.Select(stat => Describe(stat, "item", definition))
            .Concat(item.SetBonusStats.SelectMany((list, index) => list.Select(stat => Describe(stat, $"set bonus {index + 1}", definition))))
            .Concat((item.RunewordStats ?? []).Select(stat => Describe(stat, "runeword", definition)))
            .ToList();
        var sockets = item.Sockets.OfType<Item>().Select(socket => Describe(socket, $"{location} > socket", definition)).ToList();
        return new InspectedItem(
            location, item.ItemCodeString.Trim(), item.Quality, references, rareName, item.RunewordId, stats, sockets);
    }

    private static InspectedStat Describe(Stat stat, string source, SaveDataDefinition definition)
    {
        var id = (int)stat.Id;
        var name = definition.Stat(id) switch
        {
            null => $"(undefined {id})",
            { IsPlaceholder: true } => $"(placeholder {id})",
            var known => known.Key,
        };
        return new InspectedStat(id, name, stat.Layer, stat.Value, source);
    }

    private static List<IdTableValidation> ValidateIds(ParseAttempt attempt, SaveDataDefinition definition)
    {
        var types = ItemTypeIndex.Load(definition.ItemTables.Directory);
        var references = attempt.AllItems
            .SelectMany(item => item.References.Select(reference => (Item: item, Reference: reference)))
            .ToList();
        var validations = new List<IdTableValidation>();
        foreach (var group in references.GroupBy(pair => pair.Reference.Kind).OrderBy(group => group.Key))
        {
            var kind = group.Key;
            var definitions = TableSchema.Extract(kind, definition.ItemTables[kind]);
            var keyColumn = TableSchema.KeyColumn(kind);
            var dividerRows = definitions
                .Where(row => row.Row.Text(keyColumn).Equals("Expansion", StringComparison.OrdinalIgnoreCase))
                .Select(row => row.PhysicalIndex)
                .ToList();

            var hypotheses = Hypotheses
                .Select(hypothesis => new IdHypothesisResult(
                    hypothesis.Name,
                    group.Count(pair => Agrees(kind, hypothesis.Resolve(definitions, pair.Reference.SaveId), pair.Item.Code, types)),
                    group.Count()))
                .ToList();
            var phase1 = Hypotheses[0].Resolve;
            var aboveDivider = dividerRows.Count == 0
                ? 0
                : group.Count(pair => phase1(definitions, pair.Reference.SaveId)?.PhysicalIndex > dividerRows[0]);
            var resolved = group
                .Select(pair => (pair.Item, pair.Reference, Resolved: phase1(definitions, pair.Reference.SaveId)))
                .ToList();
            var tierUpgrades = TableSchema.CodeColumn(kind) is { } codeColumn
                ? resolved.Count(entry => Agrees(kind, entry.Resolved, entry.Item.Code, types)
                                          && !entry.Resolved!.Field(codeColumn).Equals(entry.Item.Code, StringComparison.OrdinalIgnoreCase))
                : 0;
            var mismatches = resolved
                .Where(entry => !Agrees(kind, entry.Resolved, entry.Item.Code, types))
                .Select(entry => new IdMismatch(
                    entry.Reference,
                    entry.Item.Code,
                    entry.Item.Location,
                    entry.Resolved?.ToString() ?? "no row",
                    Explain(kind, entry.Reference.SaveId, entry.Resolved, entry.Item.Code, types)))
                .ToList();
            validations.Add(new IdTableValidation(
                kind, definition.ItemTables.Label, group.Count(), dividerRows, aboveDivider, tierUpgrades, hypotheses, mismatches));
        }

        return validations;
    }

    internal static bool Agrees(TableKind kind, TableDefinition? resolved, string itemCode, ItemTypeIndex types) =>
        resolved is { IsPlaceholder: false } && kind switch
        {
            TableKind.UniqueItems or TableKind.SetItems => types.SameBaseFamily(itemCode, resolved.Field(TableSchema.CodeColumn(kind)!)),
            _ => types.AllowsAffix(itemCode, resolved) == true,
        };

    internal static string Explain(TableKind kind, int saveId, TableDefinition? resolved, string itemCode, ItemTypeIndex types) => resolved switch
    {
        null when kind is TableKind.MagicPrefix or TableKind.MagicSuffix && saveId == 2047 =>
            "all-ones 11-bit affix id (2047), not a table row",
        null => "no definition at this id",
        { IsPlaceholder: true } => "row is a blank/Expansion placeholder",
        _ when kind is TableKind.UniqueItems or TableKind.SetItems =>
            $"table base code '{resolved.Field(TableSchema.CodeColumn(kind)!)}' is not the saved base code or another tier of it",
        _ => types.AllowsAffix(itemCode, resolved) is null
            ? $"base code '{itemCode}' is not in armor/weapons/misc"
            : $"affix item types [{string.Join(",", ItemTypeIndex.AffixTypes(resolved, "itype"))}] "
              + $"excluded [{string.Join(",", ItemTypeIndex.AffixTypes(resolved, "etype"))}] do not allow item types "
              + $"[{string.Join(",", types.TypesOf(itemCode))}]",
    };

    private static IEnumerable<ReferenceMapping> MapReferences(ParseAttempt attempt, IReadOnlyList<SnapshotComparison> comparisons)
    {
        foreach (var comparison in comparisons)
        {
            var label = $"{comparison.Source.Label} -> {comparison.Target.Label}";
            var saveIdMaps = TableSchema.AllKinds.ToDictionary(kind => kind, kind => comparison[kind].BuildSaveIdMap());
            foreach (var item in attempt.AllItems)
            {
                foreach (var reference in item.References)
                {
                    var table = comparison[reference.Kind];
                    var mapping = table.ForSourceSaveId(reference.SaveId);
                    var concerns = new List<string>();
                    string outcome;
                    if (mapping is null)
                    {
                        outcome = "no source definition at this save id";
                        concerns.Add("unmatched: the saved id names no data row in the source table");
                    }
                    else
                    {
                        var source = mapping.Source!;
                        outcome = mapping.Status switch
                        {
                            MappingStatus.Matched =>
                                $"'{source.Key}' -> '{mapping.Target!.Key}' id {mapping.Target.SaveId} via {mapping.Method}",
                            _ => $"'{source.Key}' {mapping.Status}: {mapping.Reason}",
                        };
                        if (mapping.Status != MappingStatus.Matched)
                        {
                            concerns.Add($"{mapping.Status.ToString().ToLowerInvariant()}: not in BuildSaveIdMap");
                        }

                        if (table.SourceDuplicates.Any(duplicate => duplicate.Key.Equals(source.Key, StringComparison.OrdinalIgnoreCase)))
                        {
                            concerns.Add($"duplicate name '{source.Key}' in {comparison.Source.Label}");
                        }

                        if (mapping.Method == MatchMethod.DefinitionFingerprint)
                        {
                            concerns.Add($"fingerprint rename match: {mapping.Reason}");
                        }

                        if (mapping.Method == MatchMethod.KeyAndModStructure)
                        {
                            concerns.Add("redefined affix (same mods, changed ranges/levels): "
                                         + string.Join("; ", mapping.FieldChanges.Select(Describe)));
                        }

                        if (TableSchema.CodeColumn(reference.Kind) is { } codeColumn
                            && mapping.FieldChanges.FirstOrDefault(change => change.Column == codeColumn) is { } codeChange)
                        {
                            concerns.Add($"base item code changed: {Describe(codeChange)}");
                        }

                        if (mapping.Status == MappingStatus.Matched
                            && saveIdMaps[reference.Kind].GetValueOrDefault(reference.SaveId, -1) != mapping.Target!.SaveId)
                        {
                            concerns.Add("BuildSaveIdMap disagrees with the matched mapping");
                        }
                    }

                    yield return new ReferenceMapping(reference, item.Code, label, outcome, concerns);
                }
            }
        }
    }

    private static IEnumerable<string> FindStatConcerns(
        ParseAttempt attempt, SaveDataDefinition definition, IReadOnlyList<SnapshotComparison> comparisons)
    {
        foreach (var stat in attempt.CharacterStats.DistinctBy(stat => stat.Id))
        {
            if (definition.Stat(stat.Id) is { IsPlaceholder: false } known && known.Field("Saved") != "1")
            {
                yield return $"character stat {stat.Id} '{stat.Name}' = {stat.Value} was decoded but is not marked Saved in {definition.Label}";
            }
        }

        var used = attempt.CharacterStats.Concat(attempt.AllItems.SelectMany(item => item.Stats))
            .GroupBy(stat => stat.Id)
            .OrderBy(group => group.Key)
            .ToList();
        foreach (var group in used)
        {
            var sample = group.First();
            if (definition.Stat(group.Key) is null or { IsPlaceholder: true })
            {
                yield return $"stat id {group.Key} decoded {group.Count()} time(s) but has no definition in {definition.Label}";
            }

            foreach (var comparison in comparisons)
            {
                var mapping = comparison[TableKind.ItemStatCost].ForSourceSaveId(group.Key);
                var label = $"{comparison.Source.Label} -> {comparison.Target.Label}";
                if (mapping is null)
                {
                    yield return $"stat {group.Key} '{sample.Name}' ({group.Count()} use(s)) has no definition in {comparison.Source.Label}; {label} cannot map it";
                }
                else if (mapping.Status != MappingStatus.Matched)
                {
                    yield return $"stat {group.Key} '{sample.Name}' is {mapping.Status} in {label}: {mapping.Reason}";
                }
                else
                {
                    if (mapping.SaveIdChanged)
                    {
                        yield return $"stat '{sample.Name}' ({group.Count()} use(s)) moves {group.Key} -> {mapping.Target!.SaveId} in {label}";
                    }

                    if (mapping.FieldChanges.Count > 0)
                    {
                        yield return $"stat '{sample.Name}' ({group.Count()} use(s)) changes encoding in {label}: "
                                     + string.Join("; ", mapping.FieldChanges.Select(Describe));
                    }
                }
            }
        }
    }

    private static string Describe(FieldChange change) =>
        $"{change.Column} '{change.Source}' -> '{change.Target}'";

    internal sealed class ItemTypeIndex
    {
        private readonly Dictionary<string, string[]> _baseTypes = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string[]> _equivalents = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _tierFamilies = new(StringComparer.OrdinalIgnoreCase);

        public static ItemTypeIndex Load(string directory)
        {
            var index = new ItemTypeIndex();
            foreach (var file in new[] { "armor.txt", "weapons.txt", "misc.txt" })
            {
                foreach (var row in TableSnapshot.Load(Path.Combine(directory, file)).Rows)
                {
                    var code = row.Text("code");
                    if (code.Length > 0)
                    {
                        index._baseTypes.TryAdd(code, NonEmpty(row.Text("type"), row.Text("type2")));
                        if (row.Text("normcode") is { Length: > 0 } family)
                        {
                            index._tierFamilies.TryAdd(code, family);
                        }
                    }
                }
            }

            foreach (var row in TableSnapshot.Load(Path.Combine(directory, "itemtypes.txt")).Rows)
            {
                var code = row.Text("Code");
                if (code.Length > 0)
                {
                    index._equivalents.TryAdd(code, NonEmpty(row.Text("Equiv1"), row.Text("Equiv2")));
                }
            }

            return index;
        }

        public static IReadOnlyList<string> AffixTypes(TableDefinition affix, string prefix) =>
            NonEmpty([.. Enumerable.Range(1, prefix == "itype" ? 7 : 5).Select(slot => affix.Field(prefix + slot.ToString(CultureInfo.InvariantCulture)))]);

        public IReadOnlyCollection<string> TypesOf(string itemCode)
        {
            var types = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pending = new Stack<string>(_baseTypes.GetValueOrDefault(itemCode) ?? []);
            while (pending.TryPop(out var type))
            {
                if (types.Add(type))
                {
                    foreach (var equivalent in _equivalents.GetValueOrDefault(type) ?? [])
                    {
                        pending.Push(equivalent);
                    }
                }
            }

            return types;
        }

        public bool SameBaseFamily(string itemCode, string tableCode) =>
            itemCode.Equals(tableCode, StringComparison.OrdinalIgnoreCase)
            || (_tierFamilies.TryGetValue(itemCode, out var itemFamily)
                && _tierFamilies.TryGetValue(tableCode, out var tableFamily)
                && itemFamily.Equals(tableFamily, StringComparison.OrdinalIgnoreCase));

        /// <summary>Null when the base code is unknown to these tables.</summary>
        public bool? AllowsAffix(string itemCode, TableDefinition affix)
        {
            if (!_baseTypes.ContainsKey(itemCode))
            {
                return null;
            }

            var types = TypesOf(itemCode);
            return AffixTypes(affix, "itype").Any(types.Contains) && !AffixTypes(affix, "etype").Any(types.Contains);
        }

        private static string[] NonEmpty(params string[] values) => values.Where(value => value.Length > 0).ToArray();
    }
}
