using Paldium.Core.Breeding;
using Paldium.Core.Domain;

namespace Paldium.Core.Owned;

/// <summary>Can a parent pair be formed from the Pals the user owns?</summary>
public static class PairAvailability
{
    /// <summary>
    /// True when two different owned Pals fit the pair: each of the right species, and of the required gender
    /// where the recipe demands one (a Pal with unknown gender fits any requirement).
    /// </summary>
    public static bool CanForm(IReadOnlyList<OwnedPal> owned, ParentPair pair)
    {
        var first = Candidates(owned, pair.A, pair.AReq);
        if (first.Count == 0) return false;
        var second = Candidates(owned, pair.B, pair.BReq);
        if (second.Count == 0) return false;

        // The same physical Pal cannot be both parents (matters when A and B are the same species).
        return first.Any(a => second.Any(b => !string.Equals(a.Uid, b.Uid, StringComparison.Ordinal)));
    }

    /// <summary>Owned Pals of the species that satisfy the gender requirement.</summary>
    public static IReadOnlyList<OwnedPal> Candidates(IReadOnlyList<OwnedPal> owned, string speciesId, GenderReq requirement) =>
        owned
            .Where(p => string.Equals(p.SpeciesId, speciesId, StringComparison.OrdinalIgnoreCase) && Fits(p.Gender, requirement))
            .ToList();

    private static bool Fits(Gender? gender, GenderReq requirement) => requirement switch
    {
        GenderReq.Male => gender is null or Gender.Male,
        GenderReq.Female => gender is null or Gender.Female,
        _ => true,
    };
}
