using CUE4Parse.FileProvider;
using Newtonsoft.Json.Linq;

namespace Paldium.GameExtractor.Exporters;

/// <summary>
/// Data of the child species rules (ARCHITECTURE 7.5): every <c>DT_PalMonsterParameter</c> row in game order and the special
/// recipes of <c>DT_PalCombiUnique</c> in table order. Output: <c>breeding-rules.json</c>.
/// </summary>
public static class BreedingRulesExporter
{
    public const string RecipeTable = "Pal/Content/Pal/DataTable/Character/DT_PalCombiUnique";

    public static JObject Export(DefaultFileProvider provider, JArray species, List<string> warnings)
    {
        var rows = new JArray();
        var order = 0;
        foreach (var s in species)
        {
            rows.Add(new JObject
            {
                ["id"] = s["id"],
                ["tribe"] = s["tribe"],
                ["combiRank"] = s["combiRank"],
                ["combiDuplicatePriority"] = s["combiDuplicatePriority"],
                ["isBoss"] = s["isBoss"],
                ["ignoreCombi"] = s["ignoreCombi"],
                ["isTowerBoss"] = s["isTowerBoss"],
                ["isRaidBoss"] = s["isRaidBoss"],
                ["order"] = order++,
            });
        }

        var recipes = new JArray();
        foreach (var row in GameTables.LoadRows(provider, RecipeTable).Properties())
        {
            var p = (JObject)row.Value;
            recipes.Add(new JObject
            {
                ["row"] = row.Name,
                ["parentA"] = GameTables.Enum(p["ParentTribeA"]),
                ["genderA"] = GameTables.Enum(p["ParentGenderA"]),
                ["parentB"] = GameTables.Enum(p["ParentTribeB"]),
                ["genderB"] = GameTables.Enum(p["ParentGenderB"]),
                ["child"] = GameTables.Name(p["ChildCharacterID"]),
            });
        }

        warnings.Add($"breeding rules: {rows.Count} species rows, {recipes.Count} recipes.");
        return new JObject { ["rows"] = rows, ["recipes"] = recipes };
    }
}
