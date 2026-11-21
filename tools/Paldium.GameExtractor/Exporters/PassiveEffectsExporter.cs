using CUE4Parse.FileProvider;
using Newtonsoft.Json.Linq;

namespace Paldium.GameExtractor.Exporters;

/// <summary>Structural effects of every passive skill row (<c>DT_PassiveSkill_Main</c>). Output: <c>passive-effects.json</c>.</summary>
public static class PassiveEffectsExporter
{
    public const string Table = "Pal/Content/Pal/DataTable/PassiveSkill/DT_PassiveSkill_Main";

    public static JObject Export(DefaultFileProvider provider)
    {
        var rows = GameTables.LoadRows(provider, Table);
        var result = new JObject();
        foreach (var row in rows.Properties())
        {
            var p = (JObject)row.Value;
            var effects = new JArray();
            for (var slot = 1; slot <= 4; slot++)
            {
                var type = GameTables.Enum(p[$"EffectType{slot}"]);
                if (type == null) continue; // "no" / "None": unused slot

                effects.Add(new JObject
                {
                    ["type"] = type,
                    ["value"] = p[$"EffectValue{slot}"],
                    ["target"] = GameTables.Enum(p[$"TargetType{slot}"]) ?? "None",
                });
            }

            result[row.Name] = new JObject
            {
                ["rank"] = p["Rank"],
                ["effects"] = effects,
            };
        }

        return result;
    }
}
