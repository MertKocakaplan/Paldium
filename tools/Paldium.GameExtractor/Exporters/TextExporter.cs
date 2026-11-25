using System.Text.RegularExpressions;
using CUE4Parse.FileProvider;

namespace Paldium.GameExtractor.Exporters;

/// <summary>Exports the L10N text tables the app needs (en and tr) to <c>texts.json</c>.</summary>
public static partial class TextExporter
{
    public static readonly string[] Languages = ["en", "tr"];

    // Short name in texts.json -> L10N table name. Every table is optional (a missing one is reported, not fatal).
    public static readonly (string Name, string Table)[] Tables =
    [
        ("palName", "DT_PalNameText_Common"),
        ("skillName", "DT_SkillNameText_Common"),
        ("skillDesc", "DT_SkillDescText_Common"),
        ("partnerSkillDesc", "DT_PalFirstActivatedInfoText"),
        ("itemName", "DT_ItemNameText_Common"),
        ("uiCommon", "DT_UI_Common_Text_Common"),
        ("mapObjectName", "DT_MapObjectNameText_Common"),
        ("buildObjectCategory", "DT_BuildObjectCategoryText"),
        ("technologyName", "DT_TechnologyNameText_Common"),
        ("dungeonName", "DT_DungeonNameText"),
        ("humanName", "DT_HumanNameText_Common"),
        ("partnerSkillAppend", "DT_PartnerSkillAppendText"),
        ("worldMap", "DT_WorldMap_Common_Text_Common"),
        ("respawnText", "DT_MapRespawnPointInfoText"),
        ("uniqueNpcText", "DT_UniqueNPCText_Common"),
        ("noteDesc", "DT_NoteDescText"),
        ("namePrefix", "DT_NamePrefixText_Common"),
    ];

    /// <summary>language -> table short name -> key -> text.</summary>
    public static Dictionary<string, Dictionary<string, Dictionary<string, string>>> Load(
        DefaultFileProvider provider, List<string> warnings)
    {
        var result = new Dictionary<string, Dictionary<string, Dictionary<string, string>>>(StringComparer.Ordinal);
        foreach (var language in Languages)
        {
            var tables = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
            foreach (var (name, table) in Tables)
            {
                try
                {
                    tables[name] = GameTables.LoadTexts(provider, language, table);
                }
                catch (FileNotFoundException)
                {
                    warnings.Add($"Text table {table} not found for language {language}.");
                    tables[name] = [];
                }
            }

            result[language] = tables;
        }

        return result;
    }

    [GeneratedRegex(@"<(?<kind>characterName|itemName|mapObjectName|activeSkillName|uiCommon) id=\|(?<id>[^|]+)\|[^>]*/>", RegexOptions.IgnoreCase)]
    private static partial Regex ReferenceTag();

    [GeneratedRegex(@"<img [^>]*/>")]
    private static partial Regex ImageTag();

    [GeneratedRegex(@"<[A-Za-z_][A-Za-z0-9_]*>(?<text>.*?)</>", RegexOptions.Singleline)]
    private static partial Regex StyleTag();

    [GeneratedRegex(@" ([.,;:!?)%])|(?<=[(]) ")]
    private static partial Regex SpaceBeforePunctuation();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    // Reference tag kind -> (table short name, key prefix).
    private static readonly Dictionary<string, (string Table, string Prefix)> References = new(StringComparer.OrdinalIgnoreCase)
    {
        ["characterName"] = ("palName", "PAL_NAME_"),
        ["itemName"] = ("itemName", "ITEM_NAME_"),
        ["mapObjectName"] = ("mapObjectName", "MAPOBJECT_NAME_"),
        ["activeSkillName"] = ("skillName", "ACTION_SKILL_"),
        ["uiCommon"] = ("uiCommon", ""),
    };

    /// <summary>
    /// Turns the game's rich text into plain text: referenced names (Pals, items, map objects, skills, UI terms) are
    /// substituted, images dropped, style tags replaced by their content. <c>{Placeholder}</c> variables (effect
    /// values by rank) are left as they are. <paramref name="tables"/> is the table set of one language.
    /// </summary>
    public static string Plain(string text, IReadOnlyDictionary<string, Dictionary<string, string>> tables)
    {
        var result = ReferenceTag().Replace(text, m =>
        {
            var id = m.Groups["id"].Value;
            var (table, prefix) = References[m.Groups["kind"].Value];
            return tables.TryGetValue(table, out var texts) && texts.TryGetValue(prefix + id, out var name)
                ? name.Trim()
                : id;
        });
        // An icon image separates words in the game ("... saldırısını[icon]Toprak"); keep that as a space.
        result = ImageTag().Replace(result, " ");
        // Style tags can nest; unwrap until nothing changes.
        string previous;
        do
        {
            previous = result;
            result = StyleTag().Replace(result, "${text}");
        } while (result != previous);

        result = Whitespace().Replace(result, " ").Trim();
        // Remove spaces the image replacement left before punctuation or after an opening bracket.
        return SpaceBeforePunctuation().Replace(result, "$1");
    }
}