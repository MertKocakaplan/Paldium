using System.Globalization;
using System.Text.Json.Serialization;
using Paldium.Core.Breeding;
using Paldium.Core.Data;
using Paldium.Core.Domain;
using Paldium.Core.Owned;

namespace Paldium.Core.Queries;

/// <summary>Columns the Paldex can be sorted by.</summary>
public enum PalSortKey
{
    DexNo,
    Name,
    Rarity,
    BreedingPower,
    Hp,
    Attack,
    Defense,
    WalkSpeed,
    RunSpeed,
    RideSprintSpeed,
    TransportSpeed,
    Stamina,
    MaxFullStomach,
    FoodAmount,
    CraftSpeed,
    Price,

    /// <summary>Work suitability level of <see cref="PalSort.WorkType"/>.</summary>
    Work,

    /// <summary>How many Pals of the species the user owns (needs the counts given to <see cref="PalQuery.Run"/>; without them it sorts by dex number).</summary>
    OwnedCount,

    /// <summary>Bonus catches 0 to 5 of the species (T121; needs the capture record given to <see cref="PalQuery.Run"/>; without it this sorts by dex number). Uncapturable species always come last.</summary>
    CaptureBonus,
}

/// <summary>Capture-bonus filter of the Paldex (T121; needs the capture record; without it every value behaves like <see cref="Any"/>). Uncapturable species never match anything but <see cref="Any"/>.</summary>
public enum PalCaptureFilter
{
    Any,

    /// <summary>Bonus 0 to 4: not finished.</summary>
    Open,

    /// <summary>Bonus 1 to 4: started, not finished.</summary>
    Started,

    /// <summary>Bonus 0: never caught.</summary>
    None,

    /// <summary>Bonus 5: finished.</summary>
    Done,
}

/// <summary>Ownership filter of the Paldex (needs the owned counts; without them every value behaves like <see cref="Any"/>).</summary>
public enum PalOwnership
{
    Any,

    /// <summary>The user owns at least one.</summary>
    Owned,

    /// <summary>The user owns none.</summary>
    NotOwned,

    /// <summary>Owned, but a gender is missing (not for species with a single gender).</summary>
    MissingGender,

    /// <summary>Both a male and a female are owned.</summary>
    Pair,
}

/// <summary>Sort order of a Paldex query. Ties are always broken by dex number, then variant, then id.</summary>
public sealed record PalSort
{
    public PalSortKey Key { get; init; } = PalSortKey.DexNo;

    /// <summary>Work type id, only used when <see cref="Key"/> is <see cref="PalSortKey.Work"/>.</summary>
    public string? WorkType { get; init; }

    public bool Descending { get; init; }
}

/// <summary>
/// Paldex filter. Every part is optional; null or empty means "do not filter". Filters combine with AND.
/// The record is plain data so it can be stored in the user settings.
/// </summary>
public sealed record PalFilter
{
    /// <summary>Text search on names (all languages) and dex number, see <see cref="PalTextSearch"/>.</summary>
    public string? Text { get; init; }

    /// <summary>The Pal must have at least one of these elements.</summary>
    public IReadOnlyList<string> Elements { get; init; } = [];

    /// <summary>Work type id to minimum level; the Pal must reach every listed level.</summary>
    public IReadOnlyDictionary<string, int> MinWork { get; init; } = new Dictionary<string, int>();

    public int? MinRarity { get; init; }
    public int? MaxRarity { get; init; }

    /// <summary>true: only variants, false: only non-variants.</summary>
    public bool? Variant { get; init; }

    public bool? Nocturnal { get; init; }

    /// <summary>true: only species that can be bred solely from two of their own kind.</summary>
    public bool? SameSpeciesOnly { get; init; }

    /// <summary>true: only species that can be caught in the wild.</summary>
    public bool? WildCatchable { get; init; }

    /// <summary>
    /// Obtain methods (<c>obtain.methods</c>, e.g. "Wild", "AlphaBoss"): the Pal must have at least one of them.
    /// Species without obtain data never match.
    /// </summary>
    public IReadOnlyList<string> ObtainMethods { get; init; } = [];

    /// <summary>
    /// With <see cref="ObtainMethods"/>: the Pal may be obtained (other than by breeding) ONLY by the selected
    /// methods, e.g. AlphaBoss + this = "found only as an Alpha boss".
    /// </summary>
    public bool ObtainOnly { get; init; }

    /// <summary>Filter to species that learn this active skill by leveling up.</summary>
    public string? ActiveSkill { get; init; }

    /// <summary>Filter by what the user owns (see <see cref="PalOwnership"/>).</summary>
    public PalOwnership Ownership { get; init; } = PalOwnership.Any;

    /// <summary>Filter by the capture bonus progress (see <see cref="PalCaptureFilter"/>).</summary>
    public PalCaptureFilter Capture { get; init; } = PalCaptureFilter.Any;

    [JsonIgnore]
    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(Text) && Elements.Count == 0 && MinWork.Count == 0 &&
        MinRarity == null && MaxRarity == null && Variant == null && Nocturnal == null &&
        SameSpeciesOnly == null && WildCatchable == null && ObtainMethods.Count == 0 &&
        string.IsNullOrWhiteSpace(ActiveSkill) && Ownership == PalOwnership.Any && Capture == PalCaptureFilter.Any;
}

/// <summary>Filtering and sorting of the Pal list, independent of the UI.</summary>
public static class PalQuery
{
    /// <param name="owned">Owned counts per species id (<see cref="OwnedSpeciesStats.Compute"/>); null: ownership filters do nothing and the owned-count sort falls back to dex number.</param>
    /// <param name="capture">The capture record of the player (T121); null: the capture filter does nothing and the capture sort falls back to dex number.</param>
    public static IReadOnlyList<PalSpecies> Run(IGameData data, PalFilter filter, PalSort sort, string lang,
        IReadOnlyDictionary<string, OwnedSpeciesStats>? owned = null, CaptureRecord? capture = null)
    {
        var filtered = data.Pals.Where(p => Matches(data, p, filter, owned, capture));
        return Sort(filtered, sort, lang, owned, capture);
    }

    /// <summary>True when the species takes part in the capture bonus (it can be caught).</summary>
    public static bool CountsForCapture(PalSpecies pal) => !pal.IsUncapturable;

    public static bool MatchesCapture(PalSpecies pal, PalCaptureFilter filter, CaptureRecord capture)
    {
        if (filter == PalCaptureFilter.Any) return true;
        if (!CountsForCapture(pal)) return false;
        var bonus = capture.BonusOf(pal.Id);
        return filter switch
        {
            PalCaptureFilter.Open => bonus < CaptureRecord.MaxBonus,
            PalCaptureFilter.Started => bonus is >= 1 and < CaptureRecord.MaxBonus,
            PalCaptureFilter.None => bonus == 0,
            PalCaptureFilter.Done => bonus >= CaptureRecord.MaxBonus,
            _ => true,
        };
    }

    public static bool Matches(IGameData data, PalSpecies pal, PalFilter f, IReadOnlyDictionary<string, OwnedSpeciesStats>? owned = null, CaptureRecord? capture = null)
    {
        if (capture != null && !MatchesCapture(pal, f.Capture, capture)) return false;

        if (owned != null && f.Ownership != PalOwnership.Any)
        {
            var stats = owned.GetValueOrDefault(pal.Id) ?? OwnedSpeciesStats.None;
            var ok = f.Ownership switch
            {
                PalOwnership.Owned => stats.Total > 0,
                PalOwnership.NotOwned => stats.Total == 0,
                PalOwnership.MissingGender => stats.IsMissingGender(pal.MaleProbability),
                PalOwnership.Pair => stats.HasPair,
                _ => true,
            };
            if (!ok) return false;
        }

        if (!PalTextSearch.Matches(pal, f.Text)) return false;

        if (f.Elements.Count > 0 &&
            !pal.Elements.Any(e => f.Elements.Contains(e, StringComparer.OrdinalIgnoreCase)))
            return false;

        foreach (var (workType, minLevel) in f.MinWork)
        {
            if (!pal.Work.TryGetValue(workType, out var level) || level < minLevel) return false;
        }

        if (f.MinRarity is { } min && pal.Rarity < min) return false;
        if (f.MaxRarity is { } max && pal.Rarity > max) return false;
        if (f.Variant is { } variant && pal.IsVariant != variant) return false;
        if (f.Nocturnal is { } nocturnal && pal.Nocturnal != nocturnal) return false;
        if (f.SameSpeciesOnly is { } same && pal.BreedingFlags.SameSpeciesOnly != same) return false;
        if (f.WildCatchable is { } wild && data.IsWildCatchable(pal.Id) != wild) return false;
        if (f.ObtainMethods.Count > 0 && !MatchesObtain(pal, f)) return false;
        if (!string.IsNullOrWhiteSpace(f.ActiveSkill) &&
            !pal.AttackLearnset.Any(e => string.Equals(e.SkillId, f.ActiveSkill, StringComparison.OrdinalIgnoreCase)))
            return false;
        return true;
    }

    // Breeding-related entries of obtain.methods; they are not a way to get the species without breeding.
    private static readonly string[] BreedingMethods = ["SameSpeciesOnly", "Recipe"];

    private static bool MatchesObtain(PalSpecies pal, PalFilter f)
    {
        if (pal.Obtain == null) return false;
        var selected = f.ObtainMethods;
        if (!pal.Obtain.Methods.Any(m => selected.Contains(m, StringComparer.OrdinalIgnoreCase))) return false;
        if (!f.ObtainOnly) return true;
        return pal.Obtain.Methods
            .Where(m => !BreedingMethods.Contains(m, StringComparer.Ordinal))
            .All(m => selected.Contains(m, StringComparer.OrdinalIgnoreCase));
    }

    public static IReadOnlyList<PalSpecies> Sort(IEnumerable<PalSpecies> pals, PalSort sort, string lang,
        IReadOnlyDictionary<string, OwnedSpeciesStats>? owned = null, CaptureRecord? capture = null)
    {
        if (sort.Key == PalSortKey.OwnedCount && owned == null) sort = new PalSort { Key = PalSortKey.DexNo };
        if (sort.Key == PalSortKey.CaptureBonus && capture == null) sort = new PalSort { Key = PalSortKey.DexNo };
        IOrderedEnumerable<PalSpecies> ordered;
        if (sort.Key == PalSortKey.CaptureBonus)
        {
            // Species that cannot be caught are not part of the progress: they stay at the end in either direction.
            var byGroup = pals.OrderBy(p => CountsForCapture(p) ? 0 : 1);
            ordered = sort.Descending ? byGroup.ThenByDescending(p => capture!.BonusOf(p.Id)) : byGroup.ThenBy(p => capture!.BonusOf(p.Id));
        }
        else if (sort.Key == PalSortKey.Name)
        {
            var comparer = StringComparer.Create(CultureInfo.GetCultureInfo(lang), ignoreCase: true);
            ordered = sort.Descending
                ? pals.OrderByDescending(p => p.Names.Get(lang), comparer)
                : pals.OrderBy(p => p.Names.Get(lang), comparer);
        }
        else
        {
            Func<PalSpecies, int> key = sort.Key == PalSortKey.OwnedCount ? p => owned!.GetValueOrDefault(p.Id)?.Total ?? 0 : NumericKey(sort);
            ordered = sort.Descending ? pals.OrderByDescending(key) : pals.OrderBy(key);
        }

        return ordered
            .ThenBy(p => p.DexNo)
            .ThenBy(p => p.IsVariant)
            .ThenBy(p => p.Id, StringComparer.Ordinal)
            .ToList();
    }

    private static Func<PalSpecies, int> NumericKey(PalSort sort) => sort.Key switch
    {
        PalSortKey.DexNo => p => p.DexNo,
        PalSortKey.Rarity => p => p.Rarity,
        PalSortKey.BreedingPower => p => p.BreedingPower,
        PalSortKey.Hp => p => p.Stats.Hp,
        PalSortKey.Attack => p => p.Stats.Attack,
        PalSortKey.Defense => p => p.Stats.Defense,
        PalSortKey.WalkSpeed => p => p.Stats.WalkSpeed,
        PalSortKey.RunSpeed => p => p.Stats.RunSpeed,
        PalSortKey.RideSprintSpeed => p => p.Stats.RideSprintSpeed,
        PalSortKey.TransportSpeed => p => p.Stats.TransportSpeed,
        PalSortKey.Stamina => p => p.Stats.Stamina,
        PalSortKey.MaxFullStomach => p => p.Stats.MaxFullStomach,
        PalSortKey.FoodAmount => p => p.Stats.FoodAmount,
        PalSortKey.CraftSpeed => p => p.Stats.CraftSpeed,
        PalSortKey.Price => p => p.Price,
        PalSortKey.Work => WorkKey(sort.WorkType),
        PalSortKey.OwnedCount => _ => 0, // replaced by the owned counts in Sort
        PalSortKey.CaptureBonus => _ => 0, // replaced by the capture record in Sort
        _ => throw new ArgumentOutOfRangeException(nameof(sort), sort.Key, "Unsupported sort key."),
    };

    private static Func<PalSpecies, int> WorkKey(string? workType) =>
        p => workType != null && p.Work.TryGetValue(workType, out var level) ? level : 0;

    /// <summary>True when at least one species has element data (the element filter is hidden otherwise).</summary>
    public static bool HasElementData(IGameData data) => data.Pals.Any(p => p.Elements.Count > 0);

    /// <summary>
    /// Parent pairs of a species ordered the way the game picks the child: pairs whose average breeding power
    /// is closest to the child's own breeding power come first, then by the names of the parents.
    /// </summary>
    public static IReadOnlyList<ParentPair> RankParentPairs(
        IGameData data, PalSpecies child, IEnumerable<ParentPair> pairs, string lang)
    {
        var comparer = StringComparer.Create(CultureInfo.GetCultureInfo(lang), ignoreCase: true);
        return pairs
            .OrderBy(p => Math.Abs((data.Pal(p.A).BreedingPower + data.Pal(p.B).BreedingPower) / 2.0 - child.BreedingPower))
            .ThenBy(p => data.Pal(p.A).Names.Get(lang), comparer)
            .ThenBy(p => data.Pal(p.B).Names.Get(lang), comparer)
            .ToList();
    }
}