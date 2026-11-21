using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Localization;
using Paldium.App.Resources;
using Paldium.App.Services;
using Paldium.Core.Data;
using Paldium.Core.Domain;

namespace Paldium.App.Pages.SkillsParts;

internal static class SkillsText
{
    private static readonly Regex NegativeNumberRegex = new(@"-\d", RegexOptions.Compiled);

    public static readonly int[] RankOrder = [5, 4, 3, 2, 1, -1, -2, -3];

    public static string[] SplitEffects(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static bool IsNegativeEffect(string line) => NegativeNumberRegex.IsMatch(line);

    public static IReadOnlyList<(string Tag, int Count)> OrderedTags(IGameData data)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in data.Passives)
        {
            foreach (var tag in p.Tags)
            {
                counts.TryGetValue(tag, out var n);
                counts[tag] = n + 1;
            }
        }

        // Fallback order breaks ties deterministically matching the mockup's rail order.
        var tieOrder = new[]
        {
            "ElementDamage", "Attack", "Negative", "Defense", "Mount", "Base", "ElementResist",
            "Movement", "WorkSpeed", "WorldTree", "Other", "Mutation", "Hunger", "Sanity",
            "Stamina", "Breeding",
        };
        int TieIndex(string tag)
        {
            var idx = Array.FindIndex(tieOrder, t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase));
            return idx >= 0 ? idx : int.MaxValue;
        }

        return counts
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => TieIndex(kv.Key))
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => (kv.Key, kv.Value))
            .ToList();
    }

    public static string TagLabel(string tag, IStringLocalizer<Strings> l, GameTerms terms)
    {
        if (string.Equals(tag, "Breeding", StringComparison.OrdinalIgnoreCase))
            return terms.T("Breeding");
        var text = l["Tag." + tag];
        return text.ResourceNotFound ? tag : text.Value;
    }

    public static IEnumerable<string> VisibleTags(PassiveSkill passive) =>
        passive.Tags.Where(t => !string.Equals(t, "Negative", StringComparison.OrdinalIgnoreCase));

    public static string RankTitle(int rank, IStringLocalizer<Strings> l, GameTerms terms, bool withWorldTree) => rank switch
    {
        5 when withWorldTree => $"{l["Skill.RankLabel", 5].Value} · {TagLabel("WorldTree", l, terms)}",
        < 0 => l["Skill.NegativeRankLabel", -rank].Value,
        _ => l["Skill.RankLabel", rank].Value,
    };

    public static string RankGroupClass(int rank) => rank switch
    {
        5 => "r5",
        4 => "r4",
        3 => "r3",
        2 => "r2",
        1 => "r1",
        _ => "rn",
    };

    public static string SurgeryItemName(PassiveSkill passive, IStringLocalizer<Strings> l, GameTerms terms, string lang)
    {
        var item = passive.Surgery.RequiredItem;
        if (string.IsNullOrEmpty(item)) return string.Empty;
        if (terms.TryT(item) is { } name) return name;
        return string.Equals(item, "PalPassiveSkillChange_" + passive.Id, StringComparison.OrdinalIgnoreCase)
            ? l["Skills.SurgeryItemOf", passive.Names.Get(lang)].Value
            : item;
    }

    public static string FormatCost(int cost, string lang) =>
        cost.ToString("N0", CultureInfo.GetCultureInfo(lang));

    public static string? WeightRelativeHint(PassiveSkill passive, IGameData data, IStringLocalizer<Strings> l)
    {
        if (!passive.CanAppearRandomly) return null;
        var peers = data.Passives
            .Where(p => p.CanAppearRandomly && p.Rank == passive.Rank)
            .Select(p => p.RandomWeight)
            .Distinct()
            .Order()
            .ToList();
        if (peers.Count <= 1) return null;

        var rankWord = passive.Rank < 0
            ? l["Skill.NegativeRankLabel", -passive.Rank].Value.ToLower(CultureInfo.GetCultureInfo(CultureInfo.CurrentUICulture.Name))
            : l["Skill.RankLabel", passive.Rank].Value.ToLower(CultureInfo.GetCultureInfo(CultureInfo.CurrentUICulture.Name));
        if (passive.RandomWeight <= peers[0])
            return l["Skills.WeightLowAmong", rankWord].Value;
        if (passive.RandomWeight >= peers[^1])
            return l["Skills.WeightHighAmong", rankWord].Value;
        return l["Skills.WeightMidAmong", rankWord].Value;
    }
}
