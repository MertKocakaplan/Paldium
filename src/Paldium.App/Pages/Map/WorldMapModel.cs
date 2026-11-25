using System.Globalization;
using System.Text.RegularExpressions;
using Paldium.Core.Data;
using Paldium.Core.Domain;
using Paldium.Core.Map;

namespace Paldium.App.Pages.MapParts;

/// <summary>Pure parts of the Map page (screens/map.md, T138): categories, counts, titles and texts, tested without the page.</summary>
public static partial class WorldMapModel
{
    public const string Main = "MainMap";
    public const string Tree = "Tree";

    private static readonly string[] GroupOrder = ["Locations", "Npc", "Collectibles", "Items", "Enemies", "Quests", "Materials"];

    /// <summary>The map of the <c>map=</c> URL parameter (<c>main</c> or <c>tree</c>); null for anything else.</summary>
    public static string? MapId(string? value) => value?.ToLowerInvariant() switch
    {
        "main" or "mainmap" => Main,
        "tree" => Tree,
        _ => null,
    };

    public static string MapParameter(string mapId) => mapId == Tree ? "tree" : "main";

    /// <summary>The category list in the order of the spec, each group in the data's order; empty groups are left out.</summary>
    public static IReadOnlyList<(string Group, IReadOnlyList<MapCategory> Categories)> Groups(IEnumerable<MapCategory> categories)
    {
        var all = categories.ToList();
        return GroupOrder.Select(g => (g, (IReadOnlyList<MapCategory>)all.Where(c => c.Group == g).ToList()))
            .Where(g => g.Item2.Count > 0).ToList();
    }

    /// <summary>The name of a marker: its own name, the Pal or effigy it stands for, else the category.</summary>
    public static string Title(MapMarker point, string categoryName, IGameData data, string lang)
    {
        if (point.Names is { } names) return names.Get(lang);
        if (point.Category == "effigy" && data.Relics.FirstOrDefault(r => r.Type == point.Extra) is { } relic)
            return relic.ItemNames.Get(lang);
        if (point.RefKind == "pal" && point.RefId != null && data.Pals.FirstOrDefault(p => p.Id == point.RefId) is { } pal)
            return pal.DisplayName(lang);
        return categoryName;
    }

    /// <summary>The ring colour of a category group, on the map and in the list.</summary>
    public static string Color(string group) => group switch
    {
        "Locations" => "#5aa9ff",
        "Npc" => "#c58cff",
        "Collectibles" => "#5fd38a",
        "Items" => "#f2c14e",
        "Enemies" => "#ff6b6b",
        "Quests" => "#7fd8e8",
        _ => "#b9a48c",
    };

    /// <summary>The letter of a category without an icon.</summary>
    public static string Letter(string name) => name.Length == 0 ? "?" : name[..1].ToUpperInvariant();

    /// <summary>The level in a marker's extra text ("Lv 55"); null when it carries none.</summary>
    public static int? Level(string? extra) =>
        extra != null && extra.StartsWith("Lv ", StringComparison.Ordinal) && int.TryParse(extra.AsSpan(3), NumberStyles.None, CultureInfo.InvariantCulture, out var level)
            ? level : null;

    [GeneratedRegex("grade_?0*([1-9][0-9]*)", RegexOptions.IgnoreCase)]
    private static partial Regex GradePattern();

    /// <summary>The grade written in a chest or egg tag (<c>Grass_Grade_02</c>); null when the tag names none.</summary>
    public static int? Grade(string? tag) =>
        tag != null && GradePattern().Match(tag) is { Success: true } m ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : null;

    /// <summary>"(−1.280, −626)" with the grouping of the language; null where the map shows no coordinates.</summary>
    public static string? Coordinates(int? mapX, int? mapY, string lang)
    {
        if (mapX is not { } x || mapY is not { } y) return null;
        var culture = CultureInfo.GetCultureInfo(lang);
        string Number(int v) => (v < 0 ? "−" : "") + Math.Abs(v).ToString("N0", culture);
        return $"({Number(x)}, {Number(y)})";
    }
}
