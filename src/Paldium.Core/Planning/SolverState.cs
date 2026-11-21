using Paldium.Core.Domain;

namespace Paldium.Core.Planning;

/// <summary>Owned Pals that look the same to the solver (species, gender, wanted passives, unwanted ids, flags).</summary>
internal sealed class LeafGroup
{
    public required string SpeciesId { get; init; }
    public required Gender Gender { get; init; }
    public required int Have { get; init; }
    public required string[] UnwantedIds { get; init; }
    public required bool Start { get; init; }
    public required bool Preferred { get; init; }
    public required IReadOnlyList<OwnedPal> Pals { get; init; }

    /// <summary>Wild leaf (no Pals): the species is caught or bought.</summary>
    public int Wild { get; init; }


    /// <summary>Wild leaf: how many of the species' guaranteed passives are not wanted (T113); the unwanted count is at least this.</summary>
    public int GuaranteedUnwanted { get; init; }
    public string? ObtainMethod { get; init; }
}

/// <summary>How a derived state is produced: two parent states, the child's wanted passives and the cake.</summary>
internal sealed record Recipe(SolverState A, SolverState B, int ChildSpecies, int ChildGender, int Have, int Unwanted, bool Cake,
    double Probability, double GenderFactor, FlexInfo? Flex = null);

/// <summary>
/// Flexible gender (T105): one parent is a derived Pal whose gender does not matter because the other parent has a backup (a leaf of the same
/// species and the opposite gender). <paramref name="ParentsEggs"/> is the cost of both parents with the flexible one needing no gender
/// (1/p instead of 1/(p * P(gender))); <paramref name="StepEggs"/> is this step's expected eggs, weighted by the flexible parent's gender.
/// </summary>
internal sealed record FlexInfo(SolverState Alt, bool FlexIsA, double ParentsEggs, double StepEggs);

/// <summary>A node of the passive search: a (species, gender, wanted passives, unwanted count, flags) Pal with its cheapest way.</summary>
internal sealed class SolverState
{
    public int Species;
    public int Gender; // 0 male, 1 female
    public int Have;   // bit mask over the wanted passives
    public int Unwanted;
    public string[]? KnownUnwanted; // identities, only for owned leaves
    public bool Start;
    public bool Preferred;
    public int Steps;
    public int Wild;
    public double Eggs;
    public LeafGroup? Leaf;
    public int Group = -1;
    public Recipe? From;

    public int BucketKey => ((Species * 2 + Gender) * 2 + (Start ? 1 : 0)) * 2 + (Preferred ? 1 : 0);
}
