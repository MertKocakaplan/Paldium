using System.Collections.Concurrent;
using System.Text.Json;
using Paldium.Core.Data;
using Paldium.Core.Data.Schema;
using Paldium.Core.Domain;

namespace Paldium.Core.Breeding;

/// <summary>Species-level breeding results (ARCHITECTURE sections 5 and 7.1).</summary>
public interface IBreedingCalculator
{
    /// <summary>
    /// Child species of two parents. Genders are only used for recipes that depend on them and only
    /// when both are given; otherwise the table default is returned. Unknown ids throw
    /// <see cref="KeyNotFoundException"/>.
    /// </summary>
    string GetChild(string a, string b, Gender? ga = null, Gender? gb = null);

    /// <summary>
    /// Every possible result of the pair: a single outcome with <see cref="GenderReq.Any"/> for most pairs,
    /// one outcome per gender combination for gender-dependent pairs.
    /// </summary>
    IReadOnlyList<ChildOutcome> GetChildOutcomes(string a, string b);

    /// <summary>All parent pairs producing the species (including the same-species pair when it applies).</summary>
    IReadOnlyList<ParentPair> GetParents(string childId);

    /// <summary>Male probability of the species, or 1 minus it for female.</summary>
    double ChildGenderProbability(string childId, Gender g);

    /// <summary>
    /// Fewest breeding steps for a tree containing <paramref name="fromSpecies"/> to reach the target,
    /// ignoring partner availability. 0 for the target itself, <see cref="int.MaxValue"/> when unreachable.
    /// </summary>
    int MinStepsToTarget(string fromSpecies, string targetSpecies);
}

public sealed class BreedingCalculator : IBreedingCalculator
{
    private readonly IGameData _data;
    private readonly string[] _palOrder;
    private readonly Dictionary<string, int> _index;
    private readonly int[] _matrix;
    private readonly IReadOnlyList<GenderSpecificDto> _genderRows;
    private readonly HashSet<int> _genderSlots = [];
    private readonly IReadOnlyList<ParentPair>[] _parents;
    private readonly int[][] _parentIndexes;
    private readonly ConcurrentDictionary<int, int[]> _distanceCache = new();

    public BreedingCalculator(IGameData data, BreedingFile table)
    {
        _data = data;
        _palOrder = table.PalOrder.ToArray();
        var n = _palOrder.Length;
        if (table.Children.Count != n * (n + 1) / 2)
            throw new InvalidDataException($"Breeding matrix has {table.Children.Count} slots, expected {n * (n + 1) / 2}.");

        _index = new Dictionary<string, int>(n, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < n; i++) _index[_palOrder[i]] = i;
        if (_index.Count != n) throw new InvalidDataException("Breeding table contains duplicate Pal ids.");
        foreach (var pal in data.Pals)
            if (!_index.ContainsKey(pal.Id))
                throw new InvalidDataException($"Breeding table has no entry for Pal '{pal.Id}'.");

        _matrix = table.Children.ToArray();
        if (_matrix.Any(c => c < 0 || c >= n))
            throw new InvalidDataException("Breeding matrix contains an out-of-range Pal index.");

        _genderRows = table.GenderSpecific;
        foreach (var row in _genderRows)
            _genderSlots.Add(Slot(Index(row.A), Index(row.B)));

        (_parents, _parentIndexes) = BuildParentIndex();
    }

    /// <summary>Loads <c>breeding.json</c> from the data folder.</summary>
    public static BreedingCalculator Load(IGameData data, string dataFolder)
    {
        var path = Path.Combine(dataFolder, "breeding.json");
        if (!File.Exists(path))
            throw new FileNotFoundException($"Breeding table not found: {path}", path);

        try
        {
            var table = JsonSerializer.Deserialize<BreedingFile>(File.ReadAllBytes(path), SchemaJson.Compact)
                        ?? throw new InvalidDataException($"Breeding table is empty: {path}");
            return new BreedingCalculator(data, table);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Breeding table is malformed: {path} ({ex.Message})", ex);
        }
    }

    private int Index(string id) =>
        _index.TryGetValue(id, out var i) ? i : throw new KeyNotFoundException($"Unknown Pal id '{id}'.");

    private int Slot(int i, int j)
    {
        if (i > j) (i, j) = (j, i);
        var n = _palOrder.Length;
        return i * n - i * (i - 1) / 2 + (j - i);
    }

    public string GetChild(string a, string b, Gender? ga = null, Gender? gb = null)
    {
        int ia = Index(a), ib = Index(b);
        if (ga.HasValue && gb.HasValue && _genderSlots.Contains(Slot(ia, ib)))
        {
            var ga1 = ga.Value.ToString();
            var gb1 = gb.Value.ToString();
            foreach (var row in _genderRows)
            {
                int ra = Index(row.A), rb = Index(row.B);
                if (ra == ia && rb == ib && row.AGender == ga1 && row.BGender == gb1) return row.Child;
                if (ra == ib && rb == ia && row.AGender == gb1 && row.BGender == ga1) return row.Child;
            }
        }

        return _palOrder[_matrix[Slot(ia, ib)]];
    }

    public IReadOnlyList<ChildOutcome> GetChildOutcomes(string a, string b)
    {
        int ia = Index(a), ib = Index(b);
        if (!_genderSlots.Contains(Slot(ia, ib)))
            return [new ChildOutcome(_palOrder[_matrix[Slot(ia, ib)]], GenderReq.Any, GenderReq.Any)];

        var outcomes = new List<ChildOutcome>();
        foreach (var row in _genderRows)
        {
            int ra = Index(row.A), rb = Index(row.B);
            if (ra == ia && rb == ib)
                outcomes.Add(new ChildOutcome(row.Child, ToReq(row.AGender), ToReq(row.BGender)));
            else if (ra == ib && rb == ia)
                outcomes.Add(new ChildOutcome(row.Child, ToReq(row.BGender), ToReq(row.AGender)));
        }

        return outcomes;
    }

    public IReadOnlyList<ParentPair> GetParents(string childId) => _parents[Index(childId)];

    public double ChildGenderProbability(string childId, Gender g)
    {
        var male = _data.Pal(childId).MaleProbability;
        return g == Gender.Male ? male : 1.0 - male;
    }

    public int MinStepsToTarget(string fromSpecies, string targetSpecies)
    {
        var from = Index(fromSpecies);
        var distances = _distanceCache.GetOrAdd(Index(targetSpecies), ComputeDistances);
        return distances[from];
    }

    // Backward breadth-first search from the target over the parent index: every parent of a species at
    // distance d is at distance d + 1 or less.
    private int[] ComputeDistances(int target)
    {
        var dist = new int[_palOrder.Length];
        Array.Fill(dist, int.MaxValue);
        dist[target] = 0;
        var queue = new Queue<int>();
        queue.Enqueue(target);
        while (queue.Count > 0)
        {
            var child = queue.Dequeue();
            foreach (var parent in _parentIndexes[child])
            {
                if (dist[parent] != int.MaxValue) continue;
                dist[parent] = dist[child] + 1;
                queue.Enqueue(parent);
            }
        }

        return dist;
    }

    private (IReadOnlyList<ParentPair>[] Pairs, int[][] ParentIndexes) BuildParentIndex()
    {
        var n = _palOrder.Length;
        var lists = new List<ParentPair>[n];
        var parentSets = new HashSet<int>[n];
        for (var i = 0; i < n; i++)
        {
            lists[i] = [];
            parentSets[i] = [];
        }

        for (var i = 0; i < n; i++)
        {
            for (var j = i; j < n; j++)
            {
                var slot = Slot(i, j);
                if (_genderSlots.Contains(slot)) continue; // covered by the gender-specific rows below
                var child = _matrix[slot];
                lists[child].Add(new ParentPair(_palOrder[i], _palOrder[j], GenderReq.Any, GenderReq.Any));
                parentSets[child].Add(i);
                parentSets[child].Add(j);
            }
        }

        foreach (var row in _genderRows)
        {
            var child = Index(row.Child);
            lists[child].Add(new ParentPair(row.A, row.B, ToReq(row.AGender), ToReq(row.BGender)));
            parentSets[child].Add(Index(row.A));
            parentSets[child].Add(Index(row.B));
        }

        return (lists.Select(l => (IReadOnlyList<ParentPair>)l.AsReadOnly()).ToArray(),
            parentSets.Select(s => s.Order().ToArray()).ToArray());
    }

    private static GenderReq ToReq(string gender) => gender switch
    {
        "Male" => GenderReq.Male,
        "Female" => GenderReq.Female,
        _ => GenderReq.Any,
    };
}
