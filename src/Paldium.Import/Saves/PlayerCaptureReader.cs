using PalCalc.SaveReader;
using PalCalc.SaveReader.FArchive;
using PalCalc.SaveReader.SaveFile;
using Paldium.Core.Data;
using Paldium.Core.Owned;

namespace Paldium.Import.Saves;

/// <summary>Reads <c>SaveData.RecordData.PalCaptureBonusCount</c> and <c>PalCaptureCount</c> from a <c>Players/&lt;id&gt;.sav</c> file. PalCalc code is only used, never changed.</summary>
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
        void Look(IProperty prop)
        {
            if (prop is ArrayProperty array)
            {
                if (string.Equals(array.TypedMeta.Path, FlagsByTypePath, StringComparison.OrdinalIgnoreCase)) collectedByType = ReadFlagsByType(array);
                return;
            }

            if (prop is not MapProperty map) return;
            var path = map.TypedMeta.Path;
            if (string.Equals(path, BonusPath, StringComparison.OrdinalIgnoreCase)) bonus = ToMap(map);
            else if (string.Equals(path, CountPath, StringComparison.OrdinalIgnoreCase)) count = ToMap(map);
            else if (string.Equals(path, PossessPath, StringComparison.OrdinalIgnoreCase)) have = ToMap(map);
            else if (string.Equals(path, LegacyFlagsPath, StringComparison.OrdinalIgnoreCase)) legacyFlags = new Dictionary<string, int> { [CapturePowerType] = CountTrue(map) };
        }

        foreach (var value in gvas.Properties.Values)
        {
            if (value is not IProperty prop) continue;
            Look(prop);
            prop.Traverse(Look);
        }

        // The per-type flags are the newer record; saves without them only have the old map, which holds the Lifmunk effigies.
        var collected = collectedByType ?? legacyFlags;
        var relics = BuildRelics(have, collected);
        if (bonus == null && count == null && relics == null) return null;
        return Normalize(new CaptureRecord(bonus ?? [], count ?? []) { Relics = relics }, data, log);
    }

    private const string PossessPath = ".SaveData.RecordData.RelicPossessNumMap";
    private const string FlagsByTypePath = ".SaveData.RecordData.RelicObtainForInstanceFlagByType";
    private const string LegacyFlagsPath = ".SaveData.RecordData.RelicObtainForInstanceFlag";
    private const string CapturePowerType = "CapturePower";

    /// <summary>The effigies by type from the held counts and the collected counts; null when the save has neither.</summary>
    public static Dictionary<string, RelicCount>? BuildRelics(IReadOnlyDictionary<string, int>? have, IReadOnlyDictionary<string, int>? collected)
    {
        if (have == null && collected == null) return null;
        var result = new Dictionary<string, RelicCount>(StringComparer.Ordinal);
        foreach (var type in (have?.Keys ?? Enumerable.Empty<string>()).Concat(collected?.Keys ?? Enumerable.Empty<string>()).Distinct(StringComparer.Ordinal))
        {
            // A type that is not in the held map is 0 when the map exists (the game leaves out the types the player never had).
            int? h = have == null ? null : have.GetValueOrDefault(type);
            int? c = collected == null ? null : collected.GetValueOrDefault(type);
            result[type] = new RelicCount(h, c);
        }

        return result;
    }

    /// <summary>The number of entries with the value true.</summary>
    private static int CountTrue(MapProperty map) => map.Value.Values.Count(v => IsTrue(v));

    private static bool IsTrue(object? value) => value switch
    {
        bool b => b,
        LiteralProperty { Value: bool b } => b,
        LiteralProperty { Value: { } v } => string.Equals(v.ToString(), "True", StringComparison.OrdinalIgnoreCase),
        _ => string.Equals(value?.ToString(), "True", StringComparison.OrdinalIgnoreCase),
    };

    /// <summary><c>RelicObtainForInstanceFlagByType</c>: an array of { Type (enum), Flags (map: effigy id, collected) }; gives the collected count per type.</summary>
    private static Dictionary<string, int> ReadFlagsByType(ArrayProperty array)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        if (array.Value is not object[] items) return result;
        foreach (var item in items)
        {
            if (item is not IDictionary<string, object> entry) continue;
            if (!entry.TryGetValue("Type", out var type) || !entry.TryGetValue("Flags", out var flags) || flags is not MapProperty map) continue;
            var name = type is EnumProperty e ? e.EnumValue : type?.ToString();
            if (string.IsNullOrEmpty(name)) continue;
            result[RelicTypeName(name)] = CountTrue(map);
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

        var normalized = new CaptureRecord(Map(record.Bonus), Map(record.Count)) { Relics = record.Relics };
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
