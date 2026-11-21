using Paldium.Core.Domain;

namespace Paldium.Core.Owned;

/// <summary>How the Pals of one species the user owns (and plans with) are made up. A Pal of unknown gender counts in the total only.</summary>
public sealed record OwnedSpeciesStats(int Total, int Male, int Female)
{
    public static OwnedSpeciesStats None { get; } = new(0, 0, 0);

    /// <summary>At least one male and one female: two of the same species can breed.</summary>
    public bool HasPair => Male > 0 && Female > 0;

    /// <summary>The gender badge of the Paldex: both, male only, female only, or none (nothing to show).</summary>
    public GenderState Genders => (Male > 0, Female > 0) switch
    {
        (true, true) => GenderState.Both,
        (true, false) => GenderState.MaleOnly,
        (false, true) => GenderState.FemaleOnly,
        _ => GenderState.None,
    };

    /// <summary>One or more are owned but a gender is missing. Not for species that have only one gender (<paramref name="maleProbability"/> 0 or 1).</summary>
    public bool IsMissingGender(double maleProbability) =>
        Total > 0 && maleProbability is > 0 and < 1 && !HasPair;

    /// <summary>Counts per species id. Species the user does not own are absent.</summary>
    public static IReadOnlyDictionary<string, OwnedSpeciesStats> Compute(IEnumerable<OwnedPal> pals)
    {
        var result = new Dictionary<string, OwnedSpeciesStats>(StringComparer.Ordinal);
        foreach (var pal in pals)
        {
            result.TryGetValue(pal.SpeciesId, out var current);
            current ??= None;
            result[pal.SpeciesId] = current with
            {
                Total = current.Total + 1,
                Male = current.Male + (pal.Gender == Gender.Male ? 1 : 0),
                Female = current.Female + (pal.Gender == Gender.Female ? 1 : 0),
            };
        }

        return result;
    }
}

public enum GenderState { None, MaleOnly, FemaleOnly, Both }
