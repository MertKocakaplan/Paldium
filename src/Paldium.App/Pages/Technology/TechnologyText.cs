using Microsoft.Extensions.Localization;
using Paldium.App.Resources;
using Paldium.Core.Domain;

namespace Paldium.App.Pages.TechnologyParts;

/// <summary>The prerequisite lines of a technology ("Unlock first: ...", "Tower boss: ...", "Research: ...") in the UI language.</summary>
public static class TechnologyText
{
    public static IEnumerable<string> Requirements(TechnologyInfo tech, IReadOnlyDictionary<string, TechnologyInfo> byId, string lang, IStringLocalizer<Strings> l)
    {
        if (tech.Requires is not { } r) yield break;
        if (r.TechnologyId != null)
            yield return l["Tech.Requires", byId.TryGetValue(r.TechnologyId, out var before) ? before.Names.Get(lang) : r.TechnologyId].Value;
        if (r.TowerBossType != null)
            yield return l["Tech.Boss", r.TowerBossName?.Get(lang) ?? r.TowerBossType].Value;
        if (r.ResearchId != null)
            yield return l["Tech.Research", r.ResearchName?.Get(lang) ?? r.ResearchId].Value;
    }
}
