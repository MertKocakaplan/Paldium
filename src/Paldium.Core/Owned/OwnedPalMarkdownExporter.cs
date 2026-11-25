using System.Globalization;
using System.Text;
using Paldium.Core.Data;
using Paldium.Core.Domain;

namespace Paldium.Core.Owned;

/// <summary>Metadata passed from the UI to <see cref="OwnedPalMarkdownExporter"/>.</summary>
public sealed record ExportContext(
    string Language,
    string SourceSummary,
    string? WorldName,
    string? ScopeSummary,
    string? ActiveFiltersSummary,
    DateTimeOffset ExportedAtLocal);

/// <summary>
/// Formats the user's owned Pal list as a self-contained Markdown document with bilingual species and passive names
/// and a passive glossary at the end (T136).
/// </summary>
public static class OwnedPalMarkdownExporter
{
    public static string Write(IReadOnlyList<OwnedPal> pals, IGameData data, ExportContext context)
    {
        var isTr = string.Equals(context.Language, "tr", StringComparison.OrdinalIgnoreCase);
        var lang = isTr ? "tr" : "en";
        var sb = new StringBuilder();

        var dateStr = context.ExportedAtLocal.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        sb.AppendLine(isTr ? $"# Pal listesi ({dateStr})" : $"# Pal list ({dateStr})");
        sb.AppendLine();

        WriteHeaderMeta(sb, isTr, context);
        WriteCountsSummary(sb, isTr, pals);
        WriteColumnLegend(sb, isTr);
        WriteCoverageNote(sb, isTr);
        WritePalTable(sb, isTr, lang, pals, data);
        WritePassiveGlossary(sb, isTr, pals, data);

        return sb.ToString();
    }

    private static void WriteHeaderMeta(StringBuilder sb, bool isTr, ExportContext context)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(context.SourceSummary))
            parts.Add(isTr ? $"Kaynak: {context.SourceSummary.Trim()}" : $"Source: {context.SourceSummary.Trim()}");
        if (!string.IsNullOrWhiteSpace(context.WorldName))
            parts.Add(isTr ? $"Dünya: {context.WorldName.Trim()}" : $"World: {context.WorldName.Trim()}");
        if (!string.IsNullOrWhiteSpace(context.ScopeSummary))
            parts.Add(isTr ? $"Kapsam: {context.ScopeSummary.Trim()}" : $"Scope: {context.ScopeSummary.Trim()}");
        if (!string.IsNullOrWhiteSpace(context.ActiveFiltersSummary))
            parts.Add(isTr ? $"Filtreler: {context.ActiveFiltersSummary.Trim()}" : $"Filters: {context.ActiveFiltersSummary.Trim()}");

        if (parts.Count > 0)
        {
            sb.AppendLine(string.Join(". ", parts) + ".");
            sb.AppendLine();
        }
    }

    private static void WriteCountsSummary(StringBuilder sb, bool isTr, IReadOnlyList<OwnedPal> pals)
    {
        var total = pals.Count;
        var locParts = new List<string>();
        void AddLoc(string label, int count)
        {
            if (count > 0) locParts.Add($"{label} {count.ToString(CultureInfo.InvariantCulture)}");
        }

        AddLoc(isTr ? "Parti" : "Party", pals.Count(p => p.Location.Kind == LocationKind.Party));
        AddLoc("Palbox", pals.Count(p => p.Location.Kind == LocationKind.Palbox));
        AddLoc(isTr ? "Boyutsal depo" : "Dimensional storage", pals.Count(p => p.Location.Kind == LocationKind.DimensionalStorage));
        AddLoc(isTr ? "Üs" : "Base", pals.Count(p => p.Location.Kind == LocationKind.Base));
        AddLoc(isTr ? "Kafes" : "Viewing cage", pals.Count(p => p.Location.Kind == LocationKind.ViewingCage));
        AddLoc(isTr ? "Global Pal Kutusu" : "Global Palbox", pals.Count(p => p.Location.Kind == LocationKind.GlobalStorage));
        AddLoc(isTr ? "Seferde" : "Expedition", pals.Count(p => p.IsOnExpedition));

        var ownerCounts = pals
            .GroupBy(p => string.IsNullOrWhiteSpace(p.Owner?.PlayerName)
                ? (string.IsNullOrWhiteSpace(p.Owner?.PlayerId) ? (isTr ? "Elle / sahipsiz" : "Manual / no owner") : p.Owner!.PlayerId)
                : p.Owner!.PlayerName!)
            .Select(g => new { Name = g.Key, Count = g.Count() })
            .OrderByDescending(g => g.Count)
            .ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => $"{g.Name} {g.Count.ToString(CultureInfo.InvariantCulture)}")
            .ToList();

        var line = isTr ? $"Toplam **{total}** Pal." : $"Total **{total}** Pals.";
        if (locParts.Count > 0)
            line += (isTr ? " Konuma göre: " : " By location: ") + string.Join(", ", locParts) + ".";
        if (ownerCounts.Count > 0)
            line += (isTr ? " Sahibe göre: " : " By owner: ") + string.Join(", ", ownerCounts) + ".";

        sb.AppendLine(line);
        sb.AppendLine();
    }

    private static void WriteColumnLegend(StringBuilder sb, bool isTr)
    {
        sb.AppendLine(isTr
            ? "**Sütunlar:** Cins. E = erkek, D = dişi. Yıldız = yoğunlaştırma (0–4). IV = can/saldırı/savunma (0–100, ? bilinmiyor). İş = türün temel çalışma seviyeleri (oyun verisi). Konum: s = sayfa. Alfa = yakalanmış alfa Pal."
            : "**Columns:** Gender M = male, F = female. Stars = condensation (0–4). IV = HP/ATK/DEF (0–100, ? unknown). Work = species base work levels (game data). Location: p = page. Alpha = caught alpha Pal.");
        sb.AppendLine();
    }

    private static void WriteCoverageNote(StringBuilder sb, bool isTr)
    {
        sb.AppendLine(isTr
            ? "**Not:** Okunmamış Palbox / boyutsal depo sayfaları ve ziyaret edilmemiş üsler listede yoktur."
            : "**Note:** Unread Palbox / dimensional storage pages and unvisited bases are not listed.");
        sb.AppendLine();
    }

    private static void WritePalTable(StringBuilder sb, bool isTr, string lang, IReadOnlyList<OwnedPal> pals, IGameData data)
    {
        sb.AppendLine(isTr
            ? "| # | Tür | Species | Alfa | Element | Cins. | Sv | Yıldız | Pasifler | IV | İş | Konum | Sahip | Takma ad | Planlamada |"
            : "| # | Species (TR) | Species (EN) | Alpha | Element | Gender | Lv | Stars | Passives | IV | Work | Location | Owner | Nickname | In plan |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|");

        // Keep input order, placing any unknown species (e.g. human/NPC) at the end with an explicit note.
        var known = new List<(OwnedPal Pal, PalSpecies Species)>();
        var unknown = new List<OwnedPal>();
        foreach (var pal in pals)
        {
            if (data.TryNormalizePalId(pal.SpeciesId, out var canonicalId))
                known.Add((pal, data.Pal(canonicalId)));
            else
                unknown.Add(pal);
        }

        var elementMap = data.Elements.ToDictionary(e => e.Id, StringComparer.OrdinalIgnoreCase);
        var workOrder = data.WorkTypes
            .Select((w, idx) => (w, idx))
            .ToDictionary(x => x.w.Id, x => x, StringComparer.OrdinalIgnoreCase);

        var rowNum = 0;
        foreach (var (pal, species) in known)
        {
            rowNum++;
            var trSpecies = species.Names.Get("tr");
            var enSpecies = species.Names.Get("en");
            var elements = string.Join("/", species.Elements.Select(e => elementMap.TryGetValue(e, out var info) ? info.Names.Get(lang) : e));
            var work = FormatWork(species, workOrder, lang);
            AppendRow(sb, rowNum, trSpecies, enSpecies, pal, elements, work, isTr, data);
        }

        var unknownTag = isTr ? "(insan/NPC, Paldex'te yok)" : "(human/NPC, not in Paldex)";
        foreach (var pal in unknown)
        {
            rowNum++;
            var label = $"{pal.SpeciesId} {unknownTag}";
            AppendRow(sb, rowNum, label, label, pal, string.Empty, string.Empty, isTr, data);
        }
    }

    private static void AppendRow(
        StringBuilder sb,
        int index,
        string speciesTr,
        string speciesEn,
        OwnedPal pal,
        string elements,
        string work,
        bool isTr,
        IGameData data)
    {
        var alpha = pal.IsAlpha ? (isTr ? "evet" : "yes") : string.Empty;
        var gender = pal.Gender switch
        {
            Gender.Male => isTr ? "E" : "M",
            Gender.Female => isTr ? "D" : "F",
            _ => string.Empty,
        };
        var stars = Math.Max(0, pal.Rank - 1);
        var passives = FormatPassives(pal.Passives, data);
        var ivs = pal.Ivs is { } i
            ? string.Create(CultureInfo.InvariantCulture, $"{i.Hp}/{i.Attack}/{i.Defense}")
            : "?";
        var location = FormatLocation(pal, isTr);
        var owner = pal.Owner?.PlayerName ?? pal.Owner?.PlayerId ?? string.Empty;
        var nickname = pal.Nickname ?? string.Empty;
        var inPlan = pal.IsExcluded
            ? (isTr ? "hayır" : "no")
            : (pal.IsOnExpedition ? (isTr ? "seferde" : "expedition") : (isTr ? "evet" : "yes"));

        sb.Append("| ")
            .Append(index.ToString(CultureInfo.InvariantCulture)).Append(" | ")
            .Append(Escape(speciesTr)).Append(" | ")
            .Append(Escape(speciesEn)).Append(" | ")
            .Append(Escape(alpha)).Append(" | ")
            .Append(Escape(elements)).Append(" | ")
            .Append(Escape(gender)).Append(" | ")
            .Append(pal.Level.ToString(CultureInfo.InvariantCulture)).Append(" | ")
            .Append(stars.ToString(CultureInfo.InvariantCulture)).Append(" | ")
            .Append(Escape(passives)).Append(" | ")
            .Append(Escape(ivs)).Append(" | ")
            .Append(Escape(work)).Append(" | ")
            .Append(Escape(location)).Append(" | ")
            .Append(Escape(owner)).Append(" | ")
            .Append(Escape(nickname)).Append(" | ")
            .Append(Escape(inPlan)).AppendLine(" |");
    }

    private static void WritePassiveGlossary(StringBuilder sb, bool isTr, IReadOnlyList<OwnedPal> pals, IGameData data)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var pal in pals)
        {
            foreach (var pid in pal.Passives.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var canonicalId = TryGetPassive(data, pid)?.Id ?? pid;
                counts[canonicalId] = counts.GetValueOrDefault(canonicalId) + 1;
            }
        }

        if (counts.Count == 0) return;

        sb.AppendLine();
        sb.AppendLine(isTr ? "## Pasif sözlüğü" : "## Passive glossary");
        sb.AppendLine();
        sb.AppendLine(isTr
            ? "| Pasif (TR) | Passive (EN) | Kademe | Etki | Kaç Pal'da |"
            : "| Passive (TR) | Passive (EN) | Tier | Effect | Pal count |");
        sb.AppendLine("|---|---|---|---|---|");

        var ordered = counts
            .Select(kv =>
            {
                var p = TryGetPassive(data, kv.Key);
                var tr = p?.Names.Get("tr") ?? kv.Key;
                var en = p?.Names.Get("en") ?? kv.Key;
                var rank = p?.Rank ?? 0;
                var effect = FormatEffects(p, isTr);
                return (tr, en, rank, effect, count: kv.Value);
            })
            .OrderByDescending(x => x.rank)
            .ThenByDescending(x => x.count)
            .ThenBy(x => x.en, StringComparer.OrdinalIgnoreCase);

        foreach (var row in ordered)
        {
            sb.Append("| ")
                .Append(Escape(row.tr)).Append(" | ")
                .Append(Escape(row.en)).Append(" | ")
                .Append(row.rank.ToString(CultureInfo.InvariantCulture)).Append(" | ")
                .Append(Escape(row.effect)).Append(" | ")
                .Append(row.count.ToString(CultureInfo.InvariantCulture)).AppendLine(" |");
        }
    }

    private static PassiveSkill? TryGetPassive(IGameData data, string id)
    {
        try { return data.Passive(id); }
        catch (KeyNotFoundException) { return null; }
    }

    private static string FormatPassives(IReadOnlyList<string> passives, IGameData data)
    {
        if (passives.Count == 0) return string.Empty;
        var parts = new List<string>(passives.Count);
        foreach (var id in passives)
        {
            var passive = TryGetPassive(data, id);
            parts.Add(passive == null ? id : $"{passive.Names.Get("tr")} ({passive.Names.Get("en")})");
        }
        return string.Join("; ", parts);
    }

    private static string FormatWork(
        PalSpecies species,
        IReadOnlyDictionary<string, (WorkTypeInfo Info, int Index)> workOrder,
        string lang)
    {
        if (species.Work.Count == 0) return string.Empty;
        var items = species.Work
            .Where(kv => kv.Value > 0)
            .OrderBy(kv => workOrder.TryGetValue(kv.Key, out var entry) ? entry.Index : int.MaxValue)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv =>
            {
                var name = workOrder.TryGetValue(kv.Key, out var entry) ? entry.Info.Names.Get(lang) : kv.Key;
                return $"{name} {kv.Value.ToString(CultureInfo.InvariantCulture)}";
            });
        return string.Join(", ", items);
    }

    private static string FormatLocation(OwnedPal pal, bool isTr)
    {
        var loc = pal.Location;
        var pageSuffix = loc.Page is { } page
            ? (isTr ? $" s{page + 1}" : $" p{page + 1}")
            : string.Empty;

        return loc.Kind switch
        {
            LocationKind.Party => isTr ? "Parti" : "Party",
            LocationKind.Palbox => "Palbox" + pageSuffix,
            LocationKind.DimensionalStorage => (isTr ? "Boyutsal depo" : "Dimensional storage") + pageSuffix,
            LocationKind.Base => string.IsNullOrWhiteSpace(loc.BaseName)
                ? (isTr ? "Üs" : "Base")
                : (isTr ? $"Üs ({loc.BaseName})" : $"Base ({loc.BaseName})"),
            LocationKind.ViewingCage => isTr ? "Kafes" : "Viewing cage",
            LocationKind.GlobalStorage => (isTr ? "Global Pal Kutusu" : "Global Palbox") + pageSuffix,
            _ => isTr ? "Bilinmiyor" : "Unknown",
        };
    }

    private static string FormatEffects(PassiveSkill? passive, bool isTr)
    {
        if (passive == null) return string.Empty;
        if (passive.Effects.Count == 0)
        {
            var desc = passive.Descriptions.Get(isTr ? "tr" : "en");
            return desc == passive.Id ? string.Empty : desc;
        }

        return string.Join(", ", passive.Effects.Select(e => FormatOneEffect(e, isTr)));
    }

    private static string FormatOneEffect(PassiveEffect effect, bool isTr)
    {
        var label = effect.Type switch
        {
            "ShotAttack" => isTr ? "saldırı" : "attack",
            "MeleeAttack" => isTr ? "yakın saldırı" : "melee attack",
            "Defense" => isTr ? "savunma" : "defense",
            "MaxHP" => isTr ? "can" : "max HP",
            "MoveSpeed" => isTr ? "hareket hızı" : "move speed",
            "SwimSpeed" => isTr ? "yüzme hızı" : "swim speed",
            "CraftSpeed" => isTr ? "çalışma hızı" : "work speed",
            "FullStomatch_Decrease" => isTr ? "açlık" : "hunger",
            "Sanity_Decrease" => isTr ? "akıl sağlığı düşüşü" : "SAN drop",
            "PlayerSP_DecreaseRate" => isTr ? "oyuncu dayanıklılık tüketimi" : "player stamina cost",
            "ShopSellPrice_Money_Increase" => isTr ? "satış fiyatı" : "sell price",
            "ShopBuyPrice_Money_Increase" => isTr ? "alış fiyatı" : "buy price",
            "BreedSpeed" or "BreedSpeed_InBaseCamp" => isTr ? "üreme hızı" : "breeding speed",
            "Logging" => isTr ? "kerestecilik" : "logging",
            "Mining" => isTr ? "madencilik" : "mining",
            "WorkSuitabilityAddRank_MonsterFarm" => isTr ? "Çiftçilik seviyesi" : "Farming level",
            "Nocturnal" => isTr ? "gece çalışır" : "works at night",
            "WorldTreeDecayImmunity" => isTr ? "Dünya Ağacı çürüme bağışıklığı" : "World Tree decay immunity",
            _ => effect.Type,
        };

        if (effect.Type is "Nocturnal" or "NightOwl" or "NonKilling"
            or "KnockbackInvalid_ForPassiveSkill" or "LeanBackInvalid_ForPassiveSkill"
            or "WorldTreeDecayImmunity")
        {
            return label;
        }

        var num = effect.Value;
        var sign = num >= 0 ? "+" : "";
        var valStr = Math.Abs(num % 1) < 1e-6
            ? ((int)Math.Round(num)).ToString(CultureInfo.InvariantCulture)
            : num.ToString("0.##", CultureInfo.InvariantCulture);

        return effect.Type == "WorkSuitabilityAddRank_MonsterFarm"
            ? $"{label} {sign}{valStr}"
            : $"{label} {sign}{valStr}%";
    }

    private static string Escape(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value
            .Replace("\r\n", " ", StringComparison.Ordinal)
            .Replace('\n', ' ')
            .Replace('\r', ' ')
            .Replace("|", "\\|", StringComparison.Ordinal);
    }
}
