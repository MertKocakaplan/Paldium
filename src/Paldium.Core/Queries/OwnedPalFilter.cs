using Paldium.Core.Data;
using Paldium.Core.Domain;

namespace Paldium.Core.Queries;

/// <summary>Filter state and clause evaluation for the My Pals list (T84).</summary>
public sealed record OwnedPalFilter
{
    public const string GenderAll = "all";
    public const string GenderMale = "male";
    public const string GenderFemale = "female";
    public const string GenderUnknown = "unknown";

    public const string ExpeditionLocationKey = "Expedition";
    public const string UnknownOwnerKey = "__unknown__";

    public const string SpeciesKey = "species";
    public const string PassiveKeyPrefix = "passive:";
    public const string GenderKey = "gender";
    public const string LocationKey = "location";
    public const string OwnerKey = "owner";
    public const string SourceKey = "source";
    public const string MinIvKey = "minIv";
    public const string BaseKey = "base";

    public static OwnedPalFilter Empty { get; } = new();

    public string SpeciesQuery { get; init; } = string.Empty;
    public string? SpeciesId { get; init; }
    public IReadOnlyList<string> Passives { get; init; } = [];
    public string Gender { get; init; } = GenderAll;
    public IReadOnlySet<string> Locations { get; init; } = new HashSet<string>(StringComparer.Ordinal);
    public IReadOnlySet<string> Owners { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlySet<string> Sources { get; init; } = new HashSet<string>(StringComparer.Ordinal);
    public int MinIvTotal { get; init; }

    /// <summary>Only the Pals that work at this base (<see cref="Owned.CaptureRecord.ObjectKey"/> of its id), from the Map page (T151).</summary>
    public string? BaseId { get; init; }

    public bool HasAny =>
        !string.IsNullOrWhiteSpace(SpeciesQuery)
        || !string.IsNullOrEmpty(SpeciesId)
        || Passives.Count > 0
        || (Gender is not (GenderAll or ""))
        || Locations.Count > 0
        || Owners.Count > 0
        || Sources.Count > 0
        || MinIvTotal > 0
        || !string.IsNullOrEmpty(BaseId);

    public IReadOnlyList<FilterClause<OwnedPal>> BuildClauses(IGameData data)
    {
        var clauses = new List<FilterClause<OwnedPal>>();

        if (!string.IsNullOrEmpty(SpeciesId))
        {
            var id = SpeciesId;
            clauses.Add(new(SpeciesKey, p => string.Equals(p.SpeciesId, id, StringComparison.OrdinalIgnoreCase)));
        }
        else if (!string.IsNullOrWhiteSpace(SpeciesQuery))
        {
            var q = SpeciesQuery.Trim();
            clauses.Add(new(SpeciesKey, p =>
                (data.TryNormalizePalId(p.SpeciesId, out var canonical) && PalTextSearch.Matches(data.Pal(canonical), q))
                || (p.Nickname != null && TextFold.Contains(p.Nickname, q))));
        }

        foreach (var passiveId in Passives)
        {
            var captured = passiveId;
            clauses.Add(new(PassiveKeyPrefix + captured, p => p.Passives.Contains(captured, StringComparer.OrdinalIgnoreCase)));
        }

        if (Gender is GenderMale or GenderFemale or GenderUnknown)
        {
            clauses.Add(new(GenderKey, Gender switch
            {
                GenderMale => static p => p.Gender == Domain.Gender.Male,
                GenderFemale => static p => p.Gender == Domain.Gender.Female,
                _ => static p => p.Gender == null,
            }));
        }

        if (Locations.Count > 0)
        {
            var selected = Locations;
            clauses.Add(new(LocationKey, p =>
                selected.Contains(p.Location.Kind.ToString())
                || (p.IsOnExpedition && selected.Contains(ExpeditionLocationKey))));
        }

        if (!string.IsNullOrEmpty(BaseId))
        {
            var key = BaseId;
            clauses.Add(new(BaseKey, p => p.Location.Kind == LocationKind.Base && p.Location.BaseId != null &&
                                          Owned.CaptureRecord.ObjectKey(p.Location.BaseId) == key));
        }

        if (Owners.Count > 0)
        {
            var selected = Owners;
            clauses.Add(new(OwnerKey, p =>
                p.Owner == null
                    ? selected.Contains(UnknownOwnerKey)
                    : selected.Contains(p.Owner.PlayerId)));
        }

        if (Sources.Count > 0)
        {
            var selected = Sources;
            clauses.Add(new(SourceKey, p => selected.Contains(p.Source.ToString())));
        }

        if (MinIvTotal > 0)
        {
            var min = MinIvTotal;
            clauses.Add(new(MinIvKey, p => OwnedPalSort.PassesMinIvTotal(p, min)));
        }

        return clauses;
    }

    public List<OwnedPal> Apply(
        IEnumerable<OwnedPal> source,
        IGameData data,
        OwnedSortKey sortKey = OwnedSortKey.Default,
        bool descending = true)
    {
        var clauses = BuildClauses(data);
        var filtered = clauses.Count == 0
            ? source
            : source.Where(p => MatchesAll(p, clauses));
        return OwnedPalSort.Sort(filtered, data, sortKey, descending);
    }

    public IReadOnlyList<FilterRemovalResult> ComputeRemovals(IReadOnlyList<OwnedPal> source, IGameData data) =>
        FilterRemovalHelper.ComputeRemovals(source, BuildClauses(data));

    public OwnedPalFilter WithoutClause(string key)
    {
        if (string.Equals(key, SpeciesKey, StringComparison.Ordinal))
            return this with { SpeciesQuery = string.Empty, SpeciesId = null };
        if (key.StartsWith(PassiveKeyPrefix, StringComparison.Ordinal))
        {
            var id = key[PassiveKeyPrefix.Length..];
            return this with { Passives = Passives.Where(p => !string.Equals(p, id, StringComparison.OrdinalIgnoreCase)).ToList() };
        }
        return key switch
        {
            GenderKey => this with { Gender = GenderAll },
            LocationKey => this with { Locations = new HashSet<string>(StringComparer.Ordinal) },
            BaseKey => this with { BaseId = null },
            OwnerKey => this with { Owners = new HashSet<string>(StringComparer.OrdinalIgnoreCase) },
            SourceKey => this with { Sources = new HashSet<string>(StringComparer.Ordinal) },
            MinIvKey => this with { MinIvTotal = 0 },
            _ => this,
        };
    }

    private static bool MatchesAll(OwnedPal pal, IReadOnlyList<FilterClause<OwnedPal>> clauses)
    {
        for (var i = 0; i < clauses.Count; i++)
        {
            if (!clauses[i].Predicate(pal)) return false;
        }
        return true;
    }
}
