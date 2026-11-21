using Paldium.Core.Data;
using Paldium.Core.Domain;

namespace Paldium.Core.Queries;

/// <summary>Search of owned Pals for the pickers (T64): species name, dex number, nickname and the names of the passives (English and Turkish).</summary>
public static class OwnedPalSearch
{
    public static bool Matches(IGameData data, OwnedPal pal, string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        var q = query.Trim();
        if (PalTextSearch.Matches(data.Pal(pal.SpeciesId), q)) return true;
        if (pal.Nickname != null && TextFold.Contains(pal.Nickname, q)) return true;
        return pal.Passives.Any(id => PassiveMatches(data, id, q));
    }

    private static bool PassiveMatches(IGameData data, string id, string query)
    {
        PassiveSkill passive;
        try { passive = data.Passive(id); }
        catch (KeyNotFoundException) { return TextFold.Contains(id, query); }
        return passive.Names.Languages.Any(lang => TextFold.Contains(passive.Names.Get(lang), query)) || TextFold.Contains(passive.Id, query);
    }
}
