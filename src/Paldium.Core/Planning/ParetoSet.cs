namespace Paldium.Core.Planning;

/// <summary>
/// Settled states of one (species, gender, flags) bucket. A new state is dominated, and can be dropped, when an
/// earlier (cheaper) one has all its wanted passives, no more unwanted ones and no more steps and wild Pals.
/// Owned leaves are never dominated: their unwanted passives are known individually.
/// </summary>
internal sealed class ParetoSet
{
    private readonly bool _exhaustive;

    /// <param name="exhaustive">No leaf shortcut for empty states and no cap per set of wanted passives (T36).</param>
    public ParetoSet(bool exhaustive = false) => _exhaustive = exhaustive;

    private readonly List<SolverState> _states = [];

    public IReadOnlyList<SolverState> States => _states;

    /// <summary>True when a leaf (owned or wild Pal) of this kind is already settled.</summary>
    public bool HasLeaf { get; private set; }

    /// <summary>Most derived states kept per bucket (the cheapest ones, they settle first); leaves are always kept.</summary>
    public const int Beam = 8;

    // Derived states kept per exact set of wanted passives (the full set is the most expensive and must not be crowded out).
    // ... and per number of unwanted passives (T111): states that differ only in "w" must not crowd out the clean state of the same set.
    private readonly int[] _derivedByHave = new int[16 * 8];
    private static int Slot(int have, int w) => have * 8 + Math.Min(w, 7);

    public bool IsFull(int have, int w = 0) => !_exhaustive && _derivedByHave[Slot(have, w)] >= Beam;

    public bool IsDominated(SolverState s) =>
        s.KnownUnwanted == null && ((s.From != null && (IsFull(s.Have, s.Unwanted) || (!_exhaustive && s.Have == 0 && HasLeaf))) ||
                                    IsDominated(s.Have, s.Unwanted, s.Steps, s.Wild));

    public bool IsDominated(int have, int unwanted, int steps, int wild)
    {
        foreach (var t in _states)
        {
            if ((t.Have & have) == have && t.Unwanted <= unwanted && t.Steps <= steps && t.Wild <= wild) return true;
        }

        return false;
    }

    public void Add(SolverState s)
    {
        _states.Add(s);
        if (s.From == null) HasLeaf = true;
        else _derivedByHave[Slot(s.Have, s.Unwanted)]++;
    }
}
