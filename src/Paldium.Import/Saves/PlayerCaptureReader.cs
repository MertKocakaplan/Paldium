using PalCalc.SaveReader;
using PalCalc.SaveReader.FArchive;
using PalCalc.SaveReader.SaveFile;
using Paldium.Core.Data;
using Paldium.Core.Owned;

namespace Paldium.Import.Saves;

/// <summary>
/// Reads the player's record from a <c>Players/&lt;id&gt;.sav</c> file: the capture counts and effigies (<c>SaveData.RecordData</c>), what was taken,
/// opened and defeated there, and the quest lists of the save data (T144, T148–T150). PalCalc code is only used, never changed.
/// </summary>
public static class PlayerCaptureReader
{
    private const string BonusPath = ".SaveData.RecordData.PalCaptureBonusCount";
    private const string CountPath = ".SaveData.RecordData.PalCaptureCount";

    /// <summary>
    /// The record of a copied player save. Null when the file has no capture record (older saves). With <paramref name="data"/> the species names are
    /// turned into the ids of the game data and unknown ones are logged and left out.
    /// </summary>
    public static CaptureRecord? Read(string playerSavePath, IGameData? data = null, Action<string, Exception?>? log = null)
    {
        var source = new SingleFileSource(playerSavePath);
        var file = new PlayersDpsSaveFile(source);   // only its GVAS reading is used
        var gvas = file.ParseGvas(true);
        if (gvas?.Properties == null) return null;

        Dictionary<string, int>? bonus = null, count = null, have = null, legacyFlags = null;
        Dictionary<string, int>? collectedByType = null;
        var taken = new HashSet<string>(StringComparer.Ordinal);
        var notes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var towers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, int>? active = null;
        HashSet<string>? done = null;
        int? predators = null;
        void Look(IProperty prop)
        {
            if (prop is ArrayProperty array)
            {
                if (string.Equals(array.TypedMeta.Path, FlagsByTypePath, StringComparison.OrdinalIgnoreCase)) collectedByType = ReadFlagsByType(array, taken);
                return;
            }

            if (prop is not MapProperty map) return;
            var path = map.TypedMeta.Path;
            if (string.Equals(path, BonusPath, StringComparison.OrdinalIgnoreCase)) bonus = ToMap(map);
            else if (string.Equals(path, CountPath, StringComparison.OrdinalIgnoreCase)) count = ToMap(map);
            else if (string.Equals(path, PossessPath, StringComparison.OrdinalIgnoreCase)) have = ToMap(map);
            else if (string.Equals(path, LegacyFlagsPath, StringComparison.OrdinalIgnoreCase))
            {
                legacyFlags = new Dictionary<string, int> { [CapturePowerType] = CountTrue(map) };
                AddTrueKeys(map, taken);
            }
            else if (string.Equals(path, PickupFlagsPath, StringComparison.OrdinalIgnoreCase)) AddTrueKeys(map, taken);
            else if (string.Equals(path, FastTravelFlagsPath, StringComparison.OrdinalIgnoreCase)) AddTrueKeys(map, taken);
            else if (string.Equals(path, NoteFlagsPath, StringComparison.OrdinalIgnoreCase)) AddTrueNames(map, notes);
            else if (string.Equals(path, TowerBossFlagsPath, StringComparison.OrdinalIgnoreCase)) AddTrueNames(map, towers);
        }

        foreach (var (key, value) in gvas.Properties)
        {
            if (!string.Equals(key, "SaveData", StringComparison.OrdinalIgnoreCase)) continue;
            var saveFields = value is StructProperty saveStruct ? saveStruct.Value as IDictionary<string, object> : value as IDictionary<string, object>;
            if (saveFields == null) continue;
            foreach (var (subKey, subVal) in saveFields)
            {
                // The quest lists lie beside the record data.
                if (subVal is ArrayProperty quests)
                {
                    if (string.Equals(subKey, OrderedQuestsName, StringComparison.OrdinalIgnoreCase)) active = ReadOrderedQuests(quests);
                    else if (string.Equals(subKey, CompletedQuestsName, StringComparison.OrdinalIgnoreCase)) done = ReadNames(quests);
                    continue;
                }

                if (!string.Equals(subKey, "RecordData", StringComparison.OrdinalIgnoreCase)) continue;
                var recordFields = subVal is StructProperty recordStruct ? recordStruct.Value as IDictionary<string, object> : subVal as IDictionary<string, object>;
                if (recordFields == null) continue;
                foreach (var (name, recordProp) in recordFields)
                {
                    if (string.Equals(name, PredatorName, StringComparison.OrdinalIgnoreCase)) predators = ToInt(recordProp);
                    else if (recordProp is IProperty prop) Look(prop);
                }
            }
        }

        // The per-type flags are the newer record; saves without them only have the old map, which holds the Lifmunk effigies.
        var collected = collectedByType is { Count: > 0 } ? collectedByType : legacyFlags ?? collectedByType;
        var relics = BuildRelics(have, collected);
        var progress = new WorldProgress
        {
            Objects = WorldProgress.Set(taken),
            Notes = notes,
            Towers = towers,
            Quests = active == null && done == null ? null
                : new QuestProgress(active ?? new Dictionary<string, int>(), done ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase)),
            PredatorDefeats = predators,
        };
        if (bonus == null && count == null && relics == null && progress.IsEmpty) return null;
        return Normalize(new CaptureRecord(bonus ?? [], count ?? []) { Relics = relics, Progress = progress.IsEmpty ? null : progress }, data, log);
    }

    private const string PossessPath = ".SaveData.RecordData.RelicPossessNumMap";
    private const string FlagsByTypePath = ".SaveData.RecordData.RelicObtainForInstanceFlagByType";
    private const string LegacyFlagsPath = ".SaveData.RecordData.RelicObtainForInstanceFlag";
    private const string CapturePowerType = "CapturePower";

    // The field names are those of the game's type mappings (PalPlayerRecordData, PalWorldPlayerSaveData).
    private const string PickupFlagsPath = ".SaveData.RecordData.ItemPickupObtainForInstanceFlag";   // ancient towers
    private const string FastTravelFlagsPath = ".SaveData.RecordData.FastTravelPointUnlockFlag";     // fast travel points and watchtowers
    private const string NoteFlagsPath = ".SaveData.RecordData.NoteObtainForInstanceFlag";           // journal row names
    private const string TowerBossFlagsPath = ".SaveData.RecordData.TowerBossDefeatFlag";
    private const string PredatorName = "PredatorDefeatCount";
    private const string OrderedQuestsName = "OrderedQuestArray_FullRelease";
    private const string CompletedQuestsName = "CompletedQuestArray_FullRelease";

    private static void AddTrueNames(MapProperty map, HashSet<string> names)
    {
        foreach (var (key, value) in map.Value)
        {
            if (!IsTrue(value)) continue;
            var name = Text(key);
            if (!string.IsNullOrEmpty(name)) names.Add(name);
        }
    }

    private static string? Text(object? value) => value switch
    {
        null => null,
        LiteralProperty { Value: { } v } => v.ToString(),
        _ => value.ToString(),
    };

    /// <summary><c>OrderedQuestArray_FullRelease</c>: entries { QuestName, BlockIndex, ... }.</summary>
    private static Dictionary<string, int> ReadOrderedQuests(ArrayProperty array)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (array.Value is not object[] items) return result;
        foreach (var item in items.OfType<IDictionary<string, object>>())
        {
            if (item.TryGetValue("QuestName", out var name) && Text(name) is { Length: > 0 } id)
                result[id] = item.TryGetValue("BlockIndex", out var block) ? ToInt(block) : 0;
        }

        return result;
    }

    private static HashSet<string> ReadNames(ArrayProperty array)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (array.Value is not System.Collections.IEnumerable items) return result;
        foreach (var item in items)
        {
            if (Text(item) is { Length: > 0 } name) result.Add(name);
        }

        return result;
    }

    /// <summary>The effigies by type from the held counts and the collected counts; null when the save has neither.</summary>
    public static Dictionary<string, RelicCount>? BuildRelics(IReadOnlyDictionary<string, int>? have, IReadOnlyDictionary<string, int>? collected)
    {
        if (have == null && collected == null) return null;
        static int LookupOrZero(IReadOnlyDictionary<string, int> map, string key)
        {
            if (map.TryGetValue(key, out var val)) return val;
            foreach (var (k, v) in map)
                if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) return v;
            return 0;
        }

        var result = new Dictionary<string, RelicCount>(StringComparer.OrdinalIgnoreCase);
        foreach (var type in (have?.Keys ?? Enumerable.Empty<string>()).Concat(collected?.Keys ?? Enumerable.Empty<string>()).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            // A type that is not in the held map is 0 when the map exists (the game leaves out the types the player never had).
            int? h = have == null ? null : LookupOrZero(have, type);
            int? c = collected == null ? null : LookupOrZero(collected, type);
            result[type] = new RelicCount(h, c);
        }

        return result;
    }

    /// <summary>The number of entries with the value true.</summary>
    private static int CountTrue(MapProperty map) => map.Value.Values.Count(v => IsTrue(v));

    /// <summary>The keys with the value true, as object keys (T144).</summary>
    private static void AddTrueKeys(MapProperty map, HashSet<string> taken)
    {
        foreach (var (key, value) in map.Value)
        {
            if (!IsTrue(value)) continue;
            var id = Text(key);
            if (!string.IsNullOrEmpty(id)) taken.Add(CaptureRecord.ObjectKey(id));
        }
    }

    private static bool IsTrue(object? value) => value switch
    {
        bool b => b,
        LiteralProperty { Value: bool b } => b,
        LiteralProperty { Value: { } v } => string.Equals(v.ToString(), "True", StringComparison.OrdinalIgnoreCase),
        _ => string.Equals(value?.ToString(), "True", StringComparison.OrdinalIgnoreCase),
    };

    /// <summary><c>RelicObtainForInstanceFlagByType</c>: an array of { Type (enum), Flags (map: effigy id, collected) }; gives the collected count per type.</summary>
    private static Dictionary<string, int> ReadFlagsByType(ArrayProperty array, HashSet<string> taken)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (array.Value is not object[] items) return result;
        foreach (var item in items)
        {
            if (item is not IDictionary<string, object> entry) continue;
            if (!entry.TryGetValue("Flags", out var flags) || flags is not MapProperty map) continue;
            string? name;
            if (entry.TryGetValue("Type", out var type))
                name = type is EnumProperty e ? e.EnumValue : type?.ToString();
            else
                name = CapturePowerType; // UE GVAS omits enum default 0 (CapturePower).
            if (string.IsNullOrEmpty(name)) continue;
            result[RelicTypeName(name)] = CountTrue(map);
            AddTrueKeys(map, taken);
        }

        return result;
    }

    /// <summary><c>EPalRelicType::MoveSpeed</c> to <c>MoveSpeed</c>.</summary>
    public static string RelicTypeName(string name)
    {
        var cut = name.LastIndexOf("::", StringComparison.Ordinal);
        return cut >= 0 ? name[(cut + 2)..] : name;
    }

    /// <summary>Turns species names into game data ids (also when only the letter case differs) and drops, with one log line, the ones the bundle does not know.</summary>
    public static CaptureRecord Normalize(CaptureRecord record, IGameData? data, Action<string, Exception?>? log)
    {
        if (data == null) return record;
        var unknown = new SortedSet<string>(StringComparer.Ordinal);
        // The record spells some names in another letter case than the bundle ("Sheepball" and "SheepBall").
        var byFolded = data.Pals.GroupBy(p => p.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Id, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, int> Map(IReadOnlyDictionary<string, int> source)
        {
            var result = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var (name, value) in source)
            {
                if (data.TryNormalizePalId(name, out var id) || byFolded.TryGetValue(name, out id)) result[id] = Math.Max(result.GetValueOrDefault(id), value);
                else unknown.Add(name);
            }

            return result;
        }

        var normalized = new CaptureRecord(Map(record.Bonus), Map(record.Count)) { Relics = record.Relics, Progress = record.Progress };
        if (unknown.Count > 0) log?.Invoke($"Capture record: species the data bundle does not know were left out: {string.Join(", ", unknown)}.", null);
        return normalized;
    }

    private static Dictionary<string, int> ToMap(MapProperty map)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in map.Value)
        {
            var name = key is LiteralProperty { Value: { } k } ? k.ToString() : key?.ToString();
            if (string.IsNullOrEmpty(name)) continue;
            // Some saves keep the name as "EPalMonsterId::Name"-style text; the species id is what follows the last "::".
            var cut = name.LastIndexOf("::", StringComparison.Ordinal);
            if (cut >= 0) name = name[(cut + 2)..];
            result[name] = ToInt(value);
        }

        return result;
    }

    private static int ToInt(object? value) => value switch
    {
        null => 0,
        LiteralProperty { Value: { } v } => ToInt(v),
        int i => i,
        long l => (int)Math.Clamp(l, int.MinValue, int.MaxValue),
        uint u => (int)Math.Min(u, int.MaxValue),
        byte b => b,
        ushort s => s,
        _ => int.TryParse(value.ToString(), out var n) ? n : 0,
    };
}
