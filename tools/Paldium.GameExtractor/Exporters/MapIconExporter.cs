using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports.Texture;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Paldium.GameExtractor.Exporters;

/// <summary>
/// Extracts 32px UI compass icons from game textures into <c>icons/map/</c>.
/// Missing or unreadable textures are logged as warnings; UI falls back to clean glyphs.
/// </summary>
public static class MapIconExporter
{
    private const int IconSize = 32;

    private static readonly (string Id, string Asset)[] CompassIcons =
    [
        ("fast_travel", "Pal/Content/Pal/Texture/UI/InGame/T_icon_compass_FTtower"),
        ("boss_tower", "Pal/Content/Pal/Texture/UI/InGame/T_icon_compass_tower"),
        ("dungeon", "Pal/Content/Pal/Texture/UI/InGame/T_icon_compass_dungeon"),
        ("sealed_realm", "Pal/Content/Pal/Texture/UI/InGame/T_icon_compass_KeyBoss"),
        ("enemy_camp", "Pal/Content/Pal/Texture/UI/InGame/T_icon_compass_EnemyCamp"),
        ("oil_rig", "Pal/Content/Pal/Texture/UI/InGame/T_icon_compass_Oilrig"),
        ("alpha", "Pal/Content/Pal/Texture/UI/InGame/T_icon_compass_boss"),
        ("bounty", "Pal/Content/Pal/Texture/UI/InGame/T_icon_compass_Bounty"),
        ("quest", "Pal/Content/Pal/Texture/UI/InGame/T_icon_Compass_Quest_1"),
        ("quest_giver", "Pal/Content/Pal/Texture/UI/InGame/T_icon_Compass_Quest_0"),
        // The shrine shaped marker of the game's icon set: the ancient (item pickup) towers.
        ("pickup_tower", "Pal/Content/Pal/Texture/UI/InGame/T_icon_compass_04"),
        ("treasure_map", "Pal/Content/Pal/Texture/UI/InGame/T_icon_compass_Search_Treasure"),
        ("supply", "Pal/Content/Pal/Texture/UI/InGame/T_icon_compass_16"),
        ("respawn", "Pal/Content/Pal/Texture/UI/InGame/T_icon_compass_camp"),
        ("arena", "Pal/Content/Pal/Texture/UI/InGame/T_icon_compass_BossGate"),
        ("warp", "Pal/Content/Pal/Texture/UI/InGame/T_icon_compass_Teleport"),
    ];

    public static void Export(DefaultFileProvider provider, string outFolder, List<string> warnings)
    {
        var iconFolder = Path.Combine(outFolder, "icons", "map");
        Directory.CreateDirectory(iconFolder);
        var exported = 0;

        foreach (var (id, asset) in CompassIcons)
        {
            if (SaveIcon(provider, asset, Path.Combine(iconFolder, id + ".png"), warnings))
                exported++;
        }

        // Effigy icon from Lifmunk Effigy item texture
        try
        {
            var items = GameTables.LoadRows(provider, SpeciesExporter.ItemTable);
            var iconTable = GameTables.LoadRows(provider, "Pal/Content/Pal/DataTable/Item/DT_ItemIconDataTable");
            var iconKey = GameTables.Name(items["Relic"]?["IconName"]) ?? "Relic";
            var assetPath = (iconTable[iconKey] ?? iconTable["Relic"])?["Icon"]?["AssetPathName"]?.Value<string>();
            if (!string.IsNullOrEmpty(assetPath) && assetPath != "None")
            {
                var gamePath = assetPath.StartsWith("/Game/", StringComparison.Ordinal)
                    ? "Pal/Content/" + assetPath[6..].Split('.')[0]
                    : assetPath.Split('.')[0];
                if (SaveIcon(provider, gamePath, Path.Combine(iconFolder, "effigy.png"), warnings))
                    exported++;
            }
        }
        catch (Exception ex)
        {
            warnings.Add($"map icon effigy: {ex.Message}");
        }

        exported += ExportStateIcons(provider, iconFolder, warnings);

        // The base camp marker of the map filter ("Camp").
        if (SaveIcon(provider, "Pal/Content/Pal/Texture/UI/InGame/T_icon_compass_camp", Path.Combine(iconFolder, "base.png"), warnings)) exported++;

        warnings.Add($"map icons: {exported} PNGs exported.");
    }

    /// <summary>
    /// The fast travel and watchtower markers as the game's map shows them (T144, T148): its widget draws the compass texture tinted with one
    /// colour when the place is unlocked and another when it is not. Both colours are read from the widget's bytecode.
    /// </summary>
    private static int ExportStateIcons(DefaultFileProvider provider, string iconFolder, List<string> warnings)
    {
        const string widget = "Pal/Content/Pal/Blueprint/UI/UserInterface/Map/WBP_Map_IconFTTower.uasset";
        var colors = ReadSelectColors(provider, widget, warnings);
        if (colors == null)
        {
            warnings.Add("map icons: the unlocked/locked colours of the fast travel widget were not found.");
            return 0;
        }

        var count = 0;
        foreach (var (name, asset) in new[]
                 {
                     ("fast_travel", "Pal/Content/Pal/Texture/UI/InGame/T_icon_compass_FTtower"),
                     ("watchtower", "Pal/Content/Pal/Texture/UI/InGame/T_icon_compass_FTUnlockMap"),
                 })
        {
            if (SaveIcon(provider, asset, Path.Combine(iconFolder, name + "_open.png"), warnings, colors.Value.Open)) count++;
            if (SaveIcon(provider, asset, Path.Combine(iconFolder, name + "_locked.png"), warnings, colors.Value.Locked)) count++;
        }
        // The watchtower without a known state looks like the other compass markers.
        if (SaveIcon(provider, "Pal/Content/Pal/Texture/UI/InGame/T_icon_compass_FTUnlockMap", Path.Combine(iconFolder, "watchtower.png"), warnings)) count++;
        warnings.Add(string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"map icons: fast travel widget colours (linear RGB): unlocked {string.Join("/", colors.Value.Open)}, locked {string.Join("/", colors.Value.Locked)}."));
        return count;
    }

    /// <summary>The two colours of the widget's <c>SelectColor(unlocked, locked, flag)</c> call.</summary>
    private static (float[] Open, float[] Locked)? ReadSelectColors(DefaultFileProvider provider, string package, List<string> warnings)
    {
        var script = provider.ReadScriptData;
        try
        {
            provider.ReadScriptData = true;
            var exports = JArray.Parse(JsonConvert.SerializeObject(provider.LoadPackage(package).GetExports().ToList()));
            foreach (var parameters in exports.SelectTokens("$..Parameters").OfType<JArray>())
            {
                if (parameters.Parent?.Parent is not JObject call || parameters.Count < 2 ||
                    !call.Properties().Any(p => p.Name != "Parameters" && p.Value.ToString().Contains("KismetMathLibrary:SelectColor", StringComparison.Ordinal))) continue;
                float[]? Color(JToken token) => (token["Properties"] as JArray)?.Select(v => v["Value"]!.Value<float>()).ToArray() is { Length: >= 3 } rgb ? rgb[..3] : null;
                if (Color(parameters[0]) is { } open && Color(parameters[1]) is { } locked) return (open, locked);
            }
        }
        catch (Exception ex)
        {
            warnings.Add($"map icons: {package} could not be read: {ex.Message}");
        }
        finally
        {
            provider.ReadScriptData = script;
        }
        return null;
    }

    /// <summary>The picture multiplied by a linear colour, as the engine tints a brush.</summary>
    private static void Tint(byte[] rgba, float[] color)
    {
        static double ToLinear(double c) => c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        static double ToSrgb(double c) => c <= 0.0031308 ? c * 12.92 : 1.055 * Math.Pow(c, 1 / 2.4) - 0.055;
        for (var i = 0; i < rgba.Length; i += 4)
        {
            for (var channel = 0; channel < 3; channel++)
                rgba[i + channel] = (byte)Math.Clamp(Math.Round(ToSrgb(ToLinear(rgba[i + channel] / 255.0) * color[channel]) * 255), 0, 255);
        }
    }

    /// <summary>The item icons (<c>icons/items/&lt;key&gt;.png</c>) the material and Dog Coin layers of the map use.</summary>
    public static void ExportItemIcons(DefaultFileProvider provider, string outFolder, IEnumerable<string> itemIds, List<string> warnings)
    {
        var items = GameTables.LoadRows(provider, SpeciesExporter.ItemTable);
        var iconTable = GameTables.LoadRows(provider, "Pal/Content/Pal/DataTable/Item/DT_ItemIconDataTable");
        var folder = Path.Combine(outFolder, "icons", "items");
        Directory.CreateDirectory(folder);
        foreach (var itemId in itemIds)
        {
            var key = GameTables.Name(items[itemId]?["IconName"]) is { } name && iconTable[name] != null ? name : itemId;
            var asset = iconTable[key]?["Icon"]?["AssetPathName"]?.Value<string>();
            var target = Path.Combine(folder, itemId + ".png");
            if (string.IsNullOrEmpty(asset) || asset == "None" || (!File.Exists(target) && !RanchExporter.SaveIcon(provider, asset, target, warnings)))
                warnings.Add($"map icon of the item {itemId}: not found.");
        }
    }

    private static bool SaveIcon(DefaultFileProvider provider, string assetPath, string target, List<string> warnings, float[]? tint = null)
    {
        try
        {
            var objectPath = assetPath + "." + Path.GetFileName(assetPath);
            var texture = provider.LoadPackageObject<UTexture2D>(objectPath);
            var mip = texture.GetFirstMip();
            if (mip?.BulkData?.Data == null)
            {
                warnings.Add($"map icon {assetPath}: no pixel data.");
                return false;
            }

            var rgba = TextureDecoder.ToRgba(texture.Format, mip.BulkData.Data, mip.SizeX, mip.SizeY);
            if (rgba == null)
            {
                warnings.Add($"map icon {assetPath}: unsupported format {texture.Format}.");
                return false;
            }

            var (small, w, h) = TextureDecoder.Downscale(rgba, mip.SizeX, mip.SizeY, IconSize);
            if (tint != null)
            {
                small = (byte[])small.Clone();
                Tint(small, tint);
            }
            File.WriteAllBytes(target, PngWriter.Write(small, w, h));
            return true;
        }
        catch (Exception ex)
        {
            warnings.Add($"map icon {assetPath}: {ex.Message}");
            return false;
        }
    }
}
