using System.Text.RegularExpressions;
using CUE4Parse.FileProvider;
using Newtonsoft.Json.Linq;

namespace Paldium.GameExtractor.Exporters;

/// <summary>
/// Normalized per-row data of <c>DT_PalMonsterParameter</c> (every row: normal species, BOSS_/GYM_/RAID_ variants...)
/// plus partner skill and base drops. Output: <c>species.json</c>.
/// </summary>
public static class SpeciesExporter
{
    public const string MonsterTable = "Pal/Content/Pal/DataTable/Character/DT_PalMonsterParameter";
    public const string PartnerSkillTable = "Pal/Content/Pal/DataTable/PassiveSkill/DT_PartnerSkillParameter";
    public const string DropTable = "Pal/Content/Pal/DataTable/Character/DT_PalDropItem";
    public const string ItemTable = "Pal/Content/Pal/DataTable/Item/DT_ItemDataTable";
    public const string TechnologyTable = "Pal/Content/Pal/DataTable/Technology/DT_TechnologyRecipeUnlock";

    /// <summary>Text keys: partner skill name = <c>PARTNERSKILL_&lt;Tribe&gt;</c> in DT_SkillNameText_Common,
    /// description = <c>PAL_FIRST_SPAWN_DESC_&lt;Tribe&gt;</c> in DT_PalFirstActivatedInfoText (T21 finding);
    /// the row's Override*TextID wins when it is set.</summary>
    public const string NameKeyPattern = "PARTNERSKILL_<Tribe> (DT_SkillNameText_Common)";
    public const string DescriptionKeyPattern = "PAL_FIRST_SPAWN_DESC_<Tribe> (DT_PalFirstActivatedInfoText)";

    public static JArray Export(
        DefaultFileProvider provider,
        Dictionary<string, Dictionary<string, Dictionary<string, string>>> texts,
        List<string> warnings)
    {
        var monsters = GameTables.LoadRows(provider, MonsterTable);
        var partner = GameTables.LoadRows(provider, PartnerSkillTable);
        var drops = GameTables.LoadRows(provider, DropTable);
        var passiveRows = GameTables.LoadRows(provider, PassiveEffectsExporter.Table);
        var itemRows = GameTables.LoadRows(provider, ItemTable);
        var technologyRows = GameTables.LoadRows(provider, TechnologyTable);

        // Base (level 0) drop row per character.
        var baseDrops = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in drops.Properties())
        {
            var character = GameTables.Name(row.Value["CharacterID"]);
            if (character != null && row.Value["Level"]?.Value<int>() == 0)
                baseDrops.TryAdd(character, (JObject)row.Value);
        }

        var result = new JArray();
        foreach (var row in monsters.Properties())
        {
            var p = (JObject)row.Value;
            var tribe = GameTables.Enum(p["Tribe"]) ?? row.Name;

            var work = new JObject();
            foreach (var property in p.Properties().Where(x => x.Name.StartsWith("WorkSuitability_", StringComparison.Ordinal)))
            {
                var level = property.Value.Value<int>();
                if (level > 0) work[property.Name["WorkSuitability_".Length..]] = level;
            }

            var entry = new JObject
            {
                ["id"] = row.Name,
                ["tribe"] = tribe,
                ["zukanIndex"] = p["ZukanIndex"],
                ["zukanSuffix"] = GameTables.Name(p["ZukanIndexSuffix"]) ?? (p["ZukanIndexSuffix"]?.Value<string>() is { Length: > 0 } s ? s : null),
                ["rarity"] = p["Rarity"],
                ["size"] = GameTables.Enum(p["Size"]),
                ["element1"] = GameTables.Enum(p["ElementType1"]),
                ["element2"] = GameTables.Enum(p["ElementType2"]),
                ["isBoss"] = p["IsBoss"],
                ["isTowerBoss"] = p["IsTowerBoss"],
                ["isRaidBoss"] = p["IsRaidBoss"],
                ["isUncapturable"] = p["IsUncapturable"],
                ["nocturnal"] = p["Nocturnal"],
                ["combiRank"] = p["CombiRank"],
                ["combiDuplicatePriority"] = p["CombiDuplicatePriority"],
                ["ignoreCombi"] = p["IgnoreCombi"],
                ["price"] = p["Price"],
                ["maleProbability"] = p["MaleProbability"],
                ["hp"] = p["Hp"],
                ["meleeAttack"] = p["MeleeAttack"],
                ["shotAttack"] = p["ShotAttack"],
                ["defense"] = p["Defense"],
                ["craftSpeed"] = p["CraftSpeed"],
                ["work"] = work,
            };

            entry["partnerSkill"] = BuildPartnerSkill(row.Name, tribe, p, partner, passiveRows, texts, warnings);
            entry["drops"] = BuildDrops(row.Name, baseDrops, texts);
            entry["mount"] = BuildMount(row.Name, p, entry["partnerSkill"], itemRows, technologyRows, passiveRows);
            result.Add(entry);
        }

        var mounts = result.Where(e => e["mount"]?.Type == JTokenType.Object).ToList();
        warnings.Add($"mounts: {mounts.Count} species (ground {mounts.Count(e => e["mount"]!["kinds"]!.Any(k => k.Value<string>() == "Ground"))}, " +
                     $"flying {mounts.Count(e => e["mount"]!["kinds"]!.Any(k => k.Value<string>() == "Flying"))}, " +
                     $"water {mounts.Count(e => e["mount"]!["kinds"]!.Any(k => k.Value<string>() == "Water"))}).");
        var withPartnerRow = result.Count(e => e["partnerSkill"]?.Type == JTokenType.Object);
        warnings.Add($"species: {result.Count} rows, {withPartnerRow} with a partner skill row.");
        return result;
    }

    private static readonly Regex RideText = new(@"\bridden\b|flying mount|as a mount", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex UnconditionalMoveSpeed = new(@"^MoveSpeed_up_PartnerSkill_(Ride_)?\d+$", RegexOptions.Compiled);
    private static readonly Regex UnconditionalSwimSpeed = new(@"^SwimSpeed_up_PartnerSkill_\d+$", RegexOptions.Compiled);

    /// <summary>
    /// T124: whether the species is a mount and what the game gives for it. A mount is a species whose partner skill text says it can be ridden and that has a saddle or is an ordinary capturable one (Whalaska and the Terraria Pals have no saddle item; the saddle is the Pal gear item <c>SkillUnlock_&lt;Id&gt;</c>, whose
    /// technology row holds the saddle's level cap) whose partner skill text says it can be ridden; the kind comes from that text (ground, flying, or water for "travel on water"), plus water for any mount whose swim speed is above its run speed, because
    /// no structural field was found (the Pal's blueprint only has the riding marker on some of them). Speed bonuses of the partner skill are summed per star
    /// (rank 1..5 = 0..4 stars) from the passive rows: the unconditional ones apply to the table, the others (a night, a terrain, an element in the party)
    /// are kept apart.
    /// </summary>
    private static JToken BuildMount(string rowName, JObject monster, JToken? partnerSkill, JObject itemRows, JObject technologyRows, JObject passiveRows)
    {
        if (partnerSkill is not JObject ps) return JValue.CreateNull();
        var description = ps["descriptions"]?["en"]?.Value<string>() ?? string.Empty;
        if (!RideText.IsMatch(description)) return JValue.CreateNull();
        // Row names are FNames: letter case does not count (ThunderDog_Ice has the item SkillUnlock_Thunderdog_Ice).
        var saddleItem = itemRows.Properties().FirstOrDefault(r => string.Equals(r.Name, "SkillUnlock_" + rowName, StringComparison.OrdinalIgnoreCase));
        var summoned = monster["IsBoss"]?.Value<bool>() == true || monster["IsRaidBoss"]?.Value<bool>() == true || monster["IsTowerBoss"]?.Value<bool>() == true ||
                       monster["IsUncapturable"]?.Value<bool>() == true || rowName.EndsWith("_Oilrig", StringComparison.Ordinal) ||
                       rowName.StartsWith("GYM_", StringComparison.Ordinal) || rowName.StartsWith("SUMMON_", StringComparison.Ordinal);
        // Without a saddle item (Whalaska, the Terraria Pals) the species counts when it is an ordinary capturable one.
        if (saddleItem == null && summoned) return JValue.CreateNull();

        var kinds = new JArray();
        if (description.Contains("flying mount", StringComparison.OrdinalIgnoreCase)) kinds.Add("Flying");
        else if (description.Contains("travel on water", StringComparison.OrdinalIgnoreCase)) kinds.Add("Water");
        else kinds.Add("Ground");
        // A mount that swims faster than it runs is also a water mount (the water list of the community sites has the same 20 species; checked in T124).
        if (!kinds.Any(k => k.Value<string>() == "Water") && (monster["SwimSpeed"]?.Value<int>() ?? 0) > (monster["RunSpeed"]?.Value<int>() ?? 0)) kinds.Add("Water");

        int? saddleLevel = saddleItem == null ? null
            : technologyRows.Properties().FirstOrDefault(r => string.Equals(r.Name, saddleItem.Name, StringComparison.OrdinalIgnoreCase))?.Value["LevelCap"]?.Value<int>();

        var moveByStar = new int[Ranks];
        var swimByStar = new int[Ranks];
        var conditionalMove = new int[Ranks];
        var conditionalSwim = new int[Ranks];
        var anyConditional = false;
        var idsByRank = ps["passiveIdsByRank"] as JArray ?? [];
        for (var rank = 0; rank < Math.Min(Ranks, idsByRank.Count); rank++)
        {
            foreach (var id in idsByRank[rank] as JArray ?? [])
            {
                var key = id.Value<string>() ?? string.Empty;
                if (passiveRows[key] is not JObject passive) continue;
                for (var slot = 1; slot <= 4; slot++)
                {
                    var type = GameTables.Enum(passive[$"EffectType{slot}"]);
                    var value = passive[$"EffectValue{slot}"]?.Value<double>() ?? 0;
                    if (type == "MoveSpeed")
                    {
                        if (UnconditionalMoveSpeed.IsMatch(key)) moveByStar[rank] += (int)Math.Round(value);
                        else { conditionalMove[rank] += (int)Math.Round(value); anyConditional = true; }
                    }
                    else if (type == "SwimSpeed")
                    {
                        if (UnconditionalSwimSpeed.IsMatch(key)) swimByStar[rank] += (int)Math.Round(value);
                        else { conditionalSwim[rank] += (int)Math.Round(value); anyConditional = true; }
                    }
                }
            }
        }

        return new JObject
        {
            ["kinds"] = kinds,
            ["saddleLevel"] = saddleLevel,
            ["swimSpeed"] = monster["SwimSpeed"],
            ["swimDashSpeed"] = monster["SwimDashSpeed"],
            ["moveByStar"] = new JArray(moveByStar),
            ["swimByStar"] = new JArray(swimByStar),
            ["conditional"] = anyConditional
                ? new JObject { ["moveByStar"] = new JArray(conditionalMove), ["swimByStar"] = new JArray(conditionalSwim) }
                : JValue.CreateNull(),
        };
    }

    private static JToken BuildPartnerSkill(
        string rowName, string tribe, JObject monster, JObject partnerRows, JObject passiveRows,
        Dictionary<string, Dictionary<string, Dictionary<string, string>>> texts, List<string> warnings)
    {
        if (!partnerRows.TryGetValue(rowName, out var rowToken) || rowToken is not JObject row) return JValue.CreateNull();

        var passivesByRank = new JArray();
        foreach (var reference in row["TextReferencePassiveSkills"] as JArray ?? [])
        {
            var ids = new JArray();
            foreach (var id in reference["PassiveSkillIds"] as JArray ?? [])
                if (GameTables.Name(id["Key"]) is { } key) ids.Add(key);
            passivesByRank.Add(ids);
        }

        var nameKey = GameTables.Name(monster["OverridePartnerSkillNameTextID"]) ?? "PARTNERSKILL_" + tribe;
        var descKey = GameTables.Name(monster["OverridePartnerSkillDescTextID"]) ?? "PAL_FIRST_SPAWN_DESC_" + tribe;

        var names = new JObject();
        var descriptions = new JObject();
        foreach (var language in TextExporter.Languages)
        {
            var languageTexts = texts[language];
            if (nameKey != "PARTNERSKILL_DUMMY_None" && languageTexts["skillName"].TryGetValue(nameKey, out var name))
                names[language] = name.Trim();
            if (languageTexts["partnerSkillDesc"].TryGetValue(descKey, out var description))
                descriptions[language] = TextExporter.Plain(description, languageTexts);
        }

        var passiveIdsByRank = new JArray();
        foreach (var rank in row["PassiveSkills"] as JArray ?? [])
        {
            var ids = new JArray();
            foreach (var entry in rank["SkillAndParametersArray"] as JArray ?? [])
                if (GameTables.Name(entry["SkillName"]?["Key"]) is { } key) ids.Add(key);
            passiveIdsByRank.Add(ids);
        }

        var (numbers, textsByRank) = ResolvePlaceholders(rowName, row, passiveRows, texts, descriptions, warnings);

        return new JObject
        {
            ["nameKey"] = nameKey,
            ["descriptionKey"] = descKey,
            ["names"] = names,
            ["descriptions"] = descriptions,
            ["skillName"] = row["ActiveSkill"]?["SkillName"],
            ["wazaId"] = GameTables.Enum(row["ActiveSkill"]?["WazaID"]),
            ["mainValueByRank"] = row["ActiveSkill"]?["ActiveSkill_MainValueByRank"],
            ["passivesByRank"] = passivesByRank,
            ["passiveIdsByRank"] = passiveIdsByRank,
            ["numbersByRank"] = numbers,
            ["textsByRank"] = textsByRank,
        };
    }

    private static readonly Regex PlaceholderPattern = new(@"\{([A-Za-z0-9_]+)\}", RegexOptions.Compiled);
    private static readonly Regex PassivePlaceholder = new(@"^(Reference)?Passive(\d+)_EffectValue(\d+)$", RegexOptions.Compiled);

    private const int Ranks = 5;

    /// <summary>
    /// Values of the <c>{placeholder}</c> variables in the partner skill descriptions, per rank (1..5):
    /// Passive&lt;N&gt;_EffectValue&lt;M&gt; (PassiveSkills[rank], N-th passive), ReferencePassive&lt;N&gt;_EffectValue&lt;M&gt;
    /// (TextReferencePassiveSkills[rank]), ActiveSkillMainValueByRank / ActiveSkillOverWriteEffectTime (ActiveSkill arrays)
    /// and ReferenceMsgId_&lt;X&gt; (DT_PartnerSkillAppendText key &lt;X&gt;_Rank_&lt;rank&gt;, text per language).
    /// </summary>
    private static (JObject Numbers, JObject Texts) ResolvePlaceholders(
        string rowName, JObject row, JObject passiveRows,
        Dictionary<string, Dictionary<string, Dictionary<string, string>>> texts, JObject descriptions, List<string> warnings)
    {
        var names = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var description in descriptions.Properties())
            foreach (Match match in PlaceholderPattern.Matches(description.Value.Value<string>() ?? string.Empty))
                names.Add(match.Groups[1].Value);

        var numbers = new JObject();
        var textResults = new JObject();
        foreach (var name in names)
        {
            if (name.StartsWith("ReferenceMsgId_", StringComparison.Ordinal))
            {
                var key = name["ReferenceMsgId_".Length..];
                var perLanguage = new JObject();
                foreach (var language in TextExporter.Languages)
                {
                    var values = new JArray();
                    for (var rank = 1; rank <= Ranks; rank++)
                    {
                        var table = texts[language]["partnerSkillAppend"];
                        values.Add(table.TryGetValue($"{key}_Rank_{rank}", out var text)
                            ? TextExporter.Plain(text, texts[language])
                            : null);
                    }

                    perLanguage[language] = values;
                }

                textResults[name] = perLanguage;
                continue;
            }

            var numberValues = new JArray();
            for (var rank = 0; rank < Ranks; rank++) numberValues.Add(NumberFor(name, rank, row, passiveRows));
            numbers[name] = numberValues;
            if (numberValues.Any(v => v.Type == JTokenType.Null))
                warnings.Add($"partner skill {rowName}: placeholder {{{name}}} has no value for every rank.");
        }

        return (numbers, textResults);
    }

    private static JToken? At(JToken? array, int index) => array is JArray a && index < a.Count ? a[index] : null;

    private static JToken NumberFor(string name, int rank, JObject row, JObject passiveRows)
    {
        var match = PassivePlaceholder.Match(name);
        if (match.Success)
        {
            var index = int.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture) - 1;
            var slot = match.Groups[3].Value;
            var id = match.Groups[1].Success
                ? GameTables.Name(At(At(row["TextReferencePassiveSkills"], rank)?["PassiveSkillIds"], index)?["Key"])
                : GameTables.Name(At(At(row["PassiveSkills"], rank)?["SkillAndParametersArray"], index)?["SkillName"]?["Key"]);
            return id != null && passiveRows.TryGetValue(id, out var passive) && passive[$"EffectValue{slot}"] is { } value
                ? value
                : JValue.CreateNull();
        }

        return name switch
        {
            "ActiveSkillMainValueByRank" => At(row["ActiveSkill"]?["ActiveSkill_MainValueByRank"], rank) ?? JValue.CreateNull(),
            "ActiveSkillOverWriteEffectTime" => At(row["ActiveSkill"]?["ActiveSkill_OverWriteEffectTimeByRank"], rank) ?? JValue.CreateNull(),
            _ => JValue.CreateNull(),
        };
    }

    private static JArray BuildDrops(
        string rowName, Dictionary<string, JObject> baseDrops,
        Dictionary<string, Dictionary<string, Dictionary<string, string>>> texts)
    {
        var result = new JArray();
        if (!baseDrops.TryGetValue(rowName, out var row)) return result;

        for (var slot = 1; slot <= 10; slot++)
        {
            var itemId = GameTables.Name(row[$"ItemId{slot}"]);
            var rate = row[$"Rate{slot}"]?.Value<double>() ?? 0;
            if (itemId == null || rate <= 0) continue;

            var names = new JObject();
            foreach (var language in TextExporter.Languages)
                if (texts[language]["itemName"].TryGetValue("ITEM_NAME_" + itemId, out var name))
                    names[language] = name.Trim();

            result.Add(new JObject
            {
                ["itemId"] = itemId,
                ["names"] = names,
                ["rate"] = Math.Round(rate / 100.0, 6),
                ["min"] = row[$"min{slot}"],
                ["max"] = row[$"Max{slot}"],
                ["minLevel"] = 0,
            });
        }

        return result;
    }
}
