using System.Globalization;
using System.Text.Json;
using Paldium.Core.Breeding;
using Paldium.Core.Data.Schema;

namespace Paldium.DataBuilder;

/// <summary>
/// Reads the normalized GameExtractor output (<c>species.json</c>, <c>passive-effects.json</c>) and merges it into the
/// PalCalc based bundle. The game table wins where both sources disagree; every difference is counted and listed.
/// </summary>
public sealed class ExtractorSource
{
    /// <summary>Game work suitability name -> data bundle work type id (TASKS T21).</summary>
    public static readonly IReadOnlyDictionary<string, string> WorkMap = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["EmitFlame"] = "Kindling",
        ["Watering"] = "Watering",
        ["Seeding"] = "Planting",
        ["GenerateElectricity"] = "GenerateElectricity",
        ["Handcraft"] = "Handiwork",
        ["Collection"] = "Gathering",
        ["Deforest"] = "Lumbering",
        ["Mining"] = "Mining",
        ["ProductMedicine"] = "MedicineProduction",
        ["Cool"] = "Cooling",
        ["Transport"] = "Transporting",
        ["MonsterFarm"] = "Farming",
    };

    private readonly Dictionary<string, JsonElement> _species = new(StringComparer.Ordinal);
    private readonly Dictionary<string, JsonElement> _passives = new(StringComparer.Ordinal);

    /// <summary>Human readable cross-check lines, in the order they were found.</summary>
    public List<string> Differences { get; } = [];

    /// <summary>Species with a different value per compared field.</summary>
    public SortedDictionary<string, int> DifferenceCounts { get; } = new(StringComparer.Ordinal);

    public List<string> PartnerSkillMissingNames { get; } = [];
    public List<string> OilExtractionSpecies { get; } = [];
    public List<string> PenaltyTagMismatches { get; } = [];
    public int MatchedSpecies { get; private set; }

    private readonly string _dir;
    private ObtainBuilder? _obtainBuilder;

    /// <summary>Joined obtain data; null until <see cref="PrepareObtain"/> ran or when obtain.json is missing.</summary>
    public ObtainBuilder? ObtainData => _obtainBuilder;

    private readonly string? _regionMapPath;

    /// <param name="dir">Folder written by <c>GameExtractor export</c>.</param>
    /// <param name="regionMapPath">Hand-written region-map.json (region key to REGION_* text id); optional.</param>
    public ExtractorSource(string dir, string? regionMapPath = null)
    {
        _dir = dir;
        _regionMapPath = regionMapPath;
        var speciesDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "species.json")));
        foreach (var e in speciesDoc.RootElement.EnumerateArray())
            _species[e.GetProperty("id").GetString()!] = e;
        var passivesDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "passive-effects.json")));
        foreach (var p in passivesDoc.RootElement.EnumerateObject())
            _passives[p.Name] = p.Value;
    }

    /// <summary>Builds the per-species obtain data from obtain.json (skipped when the file is absent).</summary>
    public void PrepareObtain(IEnumerable<string> palIds)
    {
        var path = Path.Combine(_dir, "obtain.json");
        if (!File.Exists(path)) return;
        using var obtain = JsonDocument.Parse(File.ReadAllText(path));
        using var texts = JsonDocument.Parse(File.ReadAllText(Path.Combine(_dir, "texts.json")));
        var areas = obtain.RootElement.GetProperty("dungeonAreas");

        SortedDictionary<string, string> DungeonNames(string area)
        {
            var names = new SortedDictionary<string, string>(StringComparer.Ordinal);
            if (!areas.TryGetProperty(area, out var key)) return names;
            foreach (var language in texts.RootElement.EnumerateObject())
            {
                var table = language.Value.GetProperty("dungeonName");
                foreach (var entry in table.EnumerateObject())
                {
                    if (!string.Equals(entry.Name, key.GetString(), StringComparison.OrdinalIgnoreCase)) continue;
                    var text = entry.Value.GetString()!.Trim();
                    // The game ships placeholders ("-", "???", "en Text") for unfinished names.
                    if (text.Length > 0 && text is not ("-" or "???") && !text.EndsWith(" Text", StringComparison.Ordinal))
                        names[language.Name] = text;
                }
            }

            return names;
        }

        var builder = new ObtainBuilder(palIds, LoadRegionNames(texts.RootElement));
        builder.AddTowerBosses(_species.Values
            .Where(s => s.GetProperty("isTowerBoss").GetBoolean())
            .Select(s => s.GetProperty("tribe").GetString()!));
        builder.AddSpawns(obtain.RootElement, DungeonNames);
        builder.AddOther(obtain.RootElement);
        _obtainBuilder = builder;
    }

    // region key (lower case) -> names; regions without an entry stay unmapped.
    private Dictionary<string, SortedDictionary<string, string>> LoadRegionNames(JsonElement texts)
    {
        var result = new Dictionary<string, SortedDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        if (_regionMapPath == null || !File.Exists(_regionMapPath)) return result;
        using var map = JsonDocument.Parse(File.ReadAllText(_regionMapPath));
        foreach (var entry in map.RootElement.GetProperty("regions").EnumerateObject())
        {
            var names = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var language in texts.EnumerateObject())
            {
                if (!language.Value.TryGetProperty("worldMap", out var table)) continue;
                foreach (var text in table.EnumerateObject())
                {
                    if (!string.Equals(text.Name, entry.Value.GetString(), StringComparison.OrdinalIgnoreCase)) continue;
                    var value = text.Value.GetString()!.Trim();
                    if (value.Length > 0 && value is not ("-" or "???") && !value.EndsWith(" Text", StringComparison.Ordinal))
                        names[language.Name] = value;
                }
            }

            if (names.Count > 0) result[entry.Name] = names;
        }

        return result;
    }

    /// <summary>The game's species rows and special recipes (breeding-rules.json); null when the file is absent.</summary>
    public BreedingRulesFile? BreedingRules()
    {
        var path = Path.Combine(_dir, "breeding-rules.json");
        if (!File.Exists(path)) return null;
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var rows = doc.RootElement.GetProperty("rows").EnumerateArray().Select(r => new RuleRow(
            r.GetProperty("id").GetString()!, r.GetProperty("tribe").GetString()!, r.GetProperty("combiRank").GetInt32(),
            r.GetProperty("combiDuplicatePriority").GetInt32(), r.GetProperty("isBoss").GetBoolean(), r.GetProperty("ignoreCombi").GetBoolean(),
            r.GetProperty("isTowerBoss").GetBoolean(), r.GetProperty("isRaidBoss").GetBoolean(), r.GetProperty("order").GetInt32())).ToList();
        string? Nullable(JsonElement e, string name) => e.GetProperty(name).ValueKind == JsonValueKind.String ? e.GetProperty(name).GetString() : null;
        var recipes = doc.RootElement.GetProperty("recipes").EnumerateArray().Select(r => new RuleRecipe(
            r.GetProperty("row").GetString()!, r.GetProperty("parentA").GetString()!, Nullable(r, "genderA"),
            r.GetProperty("parentB").GetString()!, Nullable(r, "genderB"), r.GetProperty("child").GetString()!)).ToList();
        return new BreedingRulesFile(rows, recipes);
    }

    /// <summary>Game terms (terms.json) as termId to language to text; empty when the file is absent.</summary>
    public SortedDictionary<string, SortedDictionary<string, string>> Terms()
    {
        var result = new SortedDictionary<string, SortedDictionary<string, string>>(StringComparer.Ordinal);
        var path = Path.Combine(_dir, "terms.json");
        if (!File.Exists(path)) return result;
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var term in doc.RootElement.GetProperty("terms").EnumerateObject())
        {
            var names = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var p in term.Value.EnumerateObject())
                if (p.Name != "source") names[p.Name] = p.Value.GetString()!;
            result[term.Name] = names;
        }

        return result;
    }

    public bool TryGetSpecies(string id, out JsonElement row) => _species.TryGetValue(id, out row);

    /// <summary>The game's values for the fields that PalCalc also carries; counts and lists the differences.</summary>
    public GameOverrides Overrides(string id, int rarity, int hp, int attack, int defense, double maleProbability,
        SortedDictionary<string, int> work)
    {
        var row = _species[id];
        MatchedSpecies++;

        var gameRarity = row.GetProperty("rarity").GetInt32();
        var gameHp = row.GetProperty("hp").GetInt32();
        var gameAttack = row.GetProperty("shotAttack").GetInt32();
        var gameDefense = row.GetProperty("defense").GetInt32();
        var gameMale = Math.Round(row.GetProperty("maleProbability").GetDouble() / 100.0, 6);

        var gameWork = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var w in row.GetProperty("work").EnumerateObject())
        {
            if (WorkMap.TryGetValue(w.Name, out var mapped)) gameWork[mapped] = w.Value.GetInt32();
            else if (w.Name == "OilExtraction") OilExtractionSpecies.Add(id);
            else Differences.Add($"{id}: unmapped game work type {w.Name}");
        }

        Compare(id, "rarity", rarity, gameRarity);
        Compare(id, "hp", hp, gameHp);
        Compare(id, "attack", attack, gameAttack);
        Compare(id, "defense", defense, gameDefense);
        Compare(id, "maleProbability", maleProbability, gameMale);
        var workText = (SortedDictionary<string, int> d) => string.Join(",", d.Select(kv => $"{kv.Key}={kv.Value}"));
        if (workText(work) != workText(gameWork))
        {
            DifferenceCounts["work"] = DifferenceCounts.GetValueOrDefault("work") + 1;
            Differences.Add($"{id}: work PalCalc [{workText(work)}] game [{workText(gameWork)}]");
        }

        var elements = new List<string>();
        foreach (var name in new[] { "element1", "element2" })
            if (row.GetProperty(name).ValueKind == JsonValueKind.String) elements.Add(row.GetProperty(name).GetString()!);

        return new GameOverrides(gameRarity, gameHp, gameAttack, gameDefense, gameMale, gameWork, elements,
            row.GetProperty("isUncapturable").GetBoolean(), BuildPartnerSkill(id, row), BuildDrops(row), BuildMount(row));
    }

    /// <summary>T124: the extractor's <c>mount</c> object (null for species that are not mounts or files made before T124).</summary>
    private static MountDto? BuildMount(JsonElement row)
    {
        if (!row.TryGetProperty("mount", out var m) || m.ValueKind != JsonValueKind.Object) return null;
        List<int> Ints(JsonElement e) => e.EnumerateArray().Select(x => x.GetInt32()).ToList();
        MountConditionalDto? conditional = m.GetProperty("conditional") is { ValueKind: JsonValueKind.Object } c
            ? new MountConditionalDto(Ints(c.GetProperty("moveByStar")), Ints(c.GetProperty("swimByStar")))
            : null;
        return new MountDto(
            m.GetProperty("kinds").EnumerateArray().Select(x => x.GetString()!).ToList(),
            m.GetProperty("saddleLevel").ValueKind == JsonValueKind.Number ? m.GetProperty("saddleLevel").GetInt32() : null,
            m.GetProperty("swimSpeed").GetInt32(), m.GetProperty("swimDashSpeed").GetInt32(),
            Ints(m.GetProperty("moveByStar")), Ints(m.GetProperty("swimByStar")), conditional);
    }

    private void Compare<T>(string id, string field, T palcalc, T game) where T : IEquatable<T>
    {
        if (palcalc.Equals(game)) return;
        DifferenceCounts[field] = DifferenceCounts.GetValueOrDefault(field) + 1;
        Differences.Add(string.Create(CultureInfo.InvariantCulture, $"{id}: {field} PalCalc {palcalc} game {game}"));
    }

    private PartnerSkillDto? BuildPartnerSkill(string id, JsonElement row)
    {
        if (row.GetProperty("partnerSkill").ValueKind != JsonValueKind.Object) return null;
        var ps = row.GetProperty("partnerSkill");

        SortedDictionary<string, string> Texts(string name)
        {
            var d = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var p in ps.GetProperty(name).EnumerateObject())
                if (!string.IsNullOrWhiteSpace(p.Value.GetString())) d[p.Name] = p.Value.GetString()!;
            return d;
        }

        var names = Texts("names");
        var descriptions = Texts("descriptions");
        if (!names.ContainsKey("en") || !names.ContainsKey("tr"))
            PartnerSkillMissingNames.Add($"{id} (keys {ps.GetProperty("nameKey").GetString()}, " +
                                         $"en {(names.ContainsKey("en") ? "ok" : "missing")}, tr {(names.ContainsKey("tr") ? "ok" : "missing")})");

        var ranks = ps.GetProperty("passivesByRank").EnumerateArray()
            .Select(r => (IReadOnlyList<string>)r.EnumerateArray().Select(x => x.GetString()!).ToList())
            .ToList();
        var idsByRank = ps.GetProperty("passiveIdsByRank").EnumerateArray()
            .Select(r => (IReadOnlyList<string>)r.EnumerateArray().Select(x => x.GetString()!).ToList())
            .ToList();
        var numbers = new SortedDictionary<string, IReadOnlyList<double?>>(StringComparer.Ordinal);
        foreach (var n in ps.GetProperty("numbersByRank").EnumerateObject())
            numbers[n.Name] = n.Value.EnumerateArray()
                .Select(v => v.ValueKind == JsonValueKind.Number ? (double?)v.GetDouble() : null).ToList();
        var texts = new SortedDictionary<string, SortedDictionary<string, IReadOnlyList<string?>>>(StringComparer.Ordinal);
        foreach (var t in ps.GetProperty("textsByRank").EnumerateObject())
        {
            var perLanguage = new SortedDictionary<string, IReadOnlyList<string?>>(StringComparer.Ordinal);
            foreach (var l in t.Value.EnumerateObject())
                perLanguage[l.Name] = l.Value.EnumerateArray()
                    .Select(v => v.ValueKind == JsonValueKind.String ? v.GetString() : null).ToList();
            texts[t.Name] = perLanguage;
        }

        return new PartnerSkillDto(names, descriptions, ranks, idsByRank, numbers, texts);
    }

    /// <summary>T126: the Pal Effigy enhancements from <c>relics.json</c> (null when the file is missing).</summary>
    public IReadOnlyList<RelicDto>? Relics()
    {
        var path = Path.Combine(_dir, "relics.json");
        if (!File.Exists(path)) return null;
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        SortedDictionary<string, string> Texts(JsonElement e)
        {
            var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var p in e.EnumerateObject()) result[p.Name] = p.Value.GetString()!;
            return result;
        }

        return doc.RootElement.GetProperty("types").EnumerateArray().Select(t => new RelicDto(
            t.GetProperty("type").GetString()!, t.GetProperty("index").GetInt32(), t.GetProperty("itemId").GetString()!,
            Texts(t.GetProperty("itemNames")), Texts(t.GetProperty("names")), Texts(t.GetProperty("descriptions")),
            t.GetProperty("icon").ValueKind == JsonValueKind.String ? t.GetProperty("icon").GetString() : null,
            t.GetProperty("ranks").EnumerateArray().Select(r => new RelicRankDto(
                r.GetProperty("rank").GetInt32(), r.GetProperty("required").GetInt32(), r.GetProperty("effect").GetDouble())).ToList())).ToList();
    }

    /// <summary>T128: the technology tree from <c>technologies.json</c> (null when the file is missing).</summary>
    public IReadOnlyList<TechnologyDto>? Technologies()
    {
        var path = Path.Combine(_dir, "technologies.json");
        if (!File.Exists(path)) return null;
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        SortedDictionary<string, string> Texts(JsonElement e)
        {
            var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
            if (e.ValueKind != JsonValueKind.Object) return result;
            foreach (var p in e.EnumerateObject()) result[p.Name] = p.Value.GetString()!;
            return result;
        }

        string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        return doc.RootElement.GetProperty("technologies").EnumerateArray().Select(t =>
        {
            TechnologyRequiresDto? requires = null;
            if (t.TryGetProperty("requires", out var r) && r.ValueKind == JsonValueKind.Object)
            {
                TechnologyBossDto? boss = r.TryGetProperty("towerBoss", out var b) ? new TechnologyBossDto(b.GetProperty("type").GetString()!, Texts(b.GetProperty("name"))) : null;
                TechnologyResearchDto? research = r.TryGetProperty("research", out var s) ? new TechnologyResearchDto(s.GetProperty("id").GetString()!, Texts(s.GetProperty("name"))) : null;
                requires = new TechnologyRequiresDto(Str(r, "technologyId"), boss, research);
            }

            return new TechnologyDto(
                t.GetProperty("id").GetString()!, t.GetProperty("index").GetInt32(), t.GetProperty("level").GetInt32(), t.GetProperty("cost").GetInt32(),
                t.GetProperty("ancient").GetBoolean(), Texts(t.GetProperty("name")), Texts(t.GetProperty("description")), Str(t, "icon"), requires,
                t.GetProperty("unlocks").EnumerateArray().Select(u => new TechnologyUnlockDto(
                    u.GetProperty("kind").GetString()!, u.GetProperty("id").GetString()!, Texts(u.GetProperty("name")), Str(u, "icon"))).ToList());
        }).ToList();
    }

    private static List<DropDto> BuildDrops(JsonElement row) =>
        row.GetProperty("drops").EnumerateArray().Select(d =>
        {
            var names = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var p in d.GetProperty("names").EnumerateObject()) names[p.Name] = p.Value.GetString()!;
            return new DropDto(d.GetProperty("itemId").GetString()!, names, d.GetProperty("rate").GetDouble(),
                d.GetProperty("min").GetInt32(), d.GetProperty("max").GetInt32(), d.GetProperty("minLevel").GetInt32());
        }).ToList();

    /// <summary>
    /// T108: Ranch drops per species from <c>ranch.json</c> (null when the file is missing): a Pal's rank 1..10 names a lottery, the lottery lists
    /// weighted items (one slot, one draw); <c>Chance</c> is the weight over the total weight.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<RanchRankDto>>? RanchByPal()
    {
        var path = Path.Combine(_dir, "ranch.json");
        if (!File.Exists(path)) return null;
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var lotteries = doc.RootElement.GetProperty("lotteries");
        var items = doc.RootElement.GetProperty("items");
        var result = new Dictionary<string, IReadOnlyList<RanchRankDto>>(StringComparer.Ordinal);
        foreach (var pal in doc.RootElement.GetProperty("pals").EnumerateObject())
        {
            var ranks = new List<RanchRankDto>();
            foreach (var rank in pal.Value.GetProperty("byRank").EnumerateObject().OrderBy(r => int.Parse(r.Name, CultureInfo.InvariantCulture)))
            {
                var rows = lotteries.GetProperty(rank.Value.GetString()!).EnumerateArray().ToList();
                var total = rows.Sum(r => r.GetProperty("weight").GetDouble());
                var drops = rows.Select(r =>
                {
                    var id = r.GetProperty("item").GetString()!;
                    var names = new SortedDictionary<string, string>(StringComparer.Ordinal);
                    string? icon = null;
                    if (items.TryGetProperty(id, out var item))
                    {
                        foreach (var n in item.GetProperty("names").EnumerateObject()) names[n.Name] = n.Value.GetString()!;
                        if (item.TryGetProperty("icon", out var i)) icon = i.GetString();
                    }

                    var weight = r.GetProperty("weight").GetDouble();
                    return new RanchDropDto(id, names, icon, weight, total <= 0 ? 0 : Math.Round(weight / total, 6),
                        r.GetProperty("min").GetInt32(), r.GetProperty("max").GetInt32());
                }).OrderByDescending(d => d.Weight).ThenBy(d => d.ItemId, StringComparer.Ordinal).ToList();
                ranks.Add(new RanchRankDto(int.Parse(rank.Name, CultureInfo.InvariantCulture), drops));
            }

            result[pal.Name] = ranks;
        }

        return result;
    }

    public IReadOnlyList<EffectDto>? Effects(string passiveId)
    {
        if (!_passives.TryGetValue(passiveId, out var row)) return null;
        return row.GetProperty("effects").EnumerateArray()
            .Select(e => new EffectDto(e.GetProperty("type").GetString()!, e.GetProperty("value").GetDouble(),
                e.GetProperty("target").GetString()!))
            .ToList();
    }

    // Effect types whose category is unambiguous; used only to cross-check the hand written penaltyTags.
    private static string? CategoryOf(string effectType) => effectType switch
    {
        "ShotAttack" or "MeleeAttack" => "Attack",
        "Defense" => "Defense",
        "CraftSpeed" => "WorkSpeed",
        "MoveSpeed" => "Movement",
        _ => null,
    };

    /// <summary>Lists standard passives with a negative effect whose category is missing from penaltyTags (nothing is changed).</summary>
    public void CheckPenaltyTags(IEnumerable<PassiveDto> passives)
    {
        foreach (var passive in passives.Where(p => p.IsStandard))
        {
            foreach (var effect in passive.Effects ?? [])
            {
                var category = CategoryOf(effect.Type);
                if (category == null || effect.Value >= 0) continue;
                if (!(passive.PenaltyTags ?? []).Contains(category, StringComparer.Ordinal))
                    PenaltyTagMismatches.Add(string.Create(CultureInfo.InvariantCulture,
                        $"{passive.Id}: effect {effect.Type} {effect.Value} but penaltyTags [{string.Join(",", passive.PenaltyTags ?? [])}]"));
            }
        }
    }
}

/// <summary>Game values that replace or extend the PalCalc based species data.</summary>
public sealed record GameOverrides(
    int Rarity, int Hp, int Attack, int Defense, double MaleProbability,
    SortedDictionary<string, int> Work, IReadOnlyList<string> Elements, bool IsUncapturable,
    PartnerSkillDto? PartnerSkill, IReadOnlyList<DropDto> Drops, MountDto? Mount = null);
