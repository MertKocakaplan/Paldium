using Paldium.Core.Data;
using Paldium.Core.Domain;

namespace Paldium.Core.Owned;

/// <summary>The single place that knows which Pals the user owns (ARCHITECTURE section 5, 6.4).</summary>
public interface IOwnedPalRepository
{
    /// <summary>Immutable: all sources merged, duplicates removed, corrections applied.</summary>
    OwnedPalSnapshot Current { get; }

    /// <summary>Raised once per change, after <see cref="Current"/> has been replaced.</summary>
    event EventHandler<OwnedPalSnapshot> Changed;

    /// <summary>Replaces everything known about one source (called by the importers).</summary>
    void PublishSourceSnapshot(string sourceId, IReadOnlyList<OwnedPal> pals, SourceStatus status);

    /// <summary>Forgets a source and its Pals (the user's corrections stay).</summary>
    void RemoveSource(string sourceId);

    /// <summary>Records a source's state (e.g. an error) without touching the Pals read earlier.</summary>
    void SetSourceStatus(string sourceId, SourceStatus status);

    OwnedPal AddManual(OwnedPalDraft draft);
    void UpdateManual(string uid, OwnedPalDraft draft);
    void RemoveManual(string uid);

    /// <summary>Stores or, with null, removes the correction for a Pal.</summary>
    void SetOverride(string uid, PalOverride? patch);

    /// <summary>The Pal as its source gave it, without any correction (the visible copy of the Pal, or another copy of the same in-game Pal); null when unknown.</summary>
    OwnedPal? RawPal(string uid);
}

public sealed class OwnedPalRepository : IOwnedPalRepository
{
    public const string ManualSourceId = "manual";

    public const int MaxPassives = 4;
    public const int MaxLevel = 100;
    public const int MaxRank = 5;

    private sealed record SourceData(IReadOnlyList<OwnedPal> Pals, SourceStatus Status);

    private readonly object _gate = new();
    private readonly IGameData _data;
    private readonly UserDataStore? _store;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Action<string, Exception?>? _warn;

    private readonly Dictionary<string, SourceData> _sources = new(StringComparer.Ordinal);
    private readonly List<OwnedPal> _manual;
    private readonly Dictionary<string, PalOverride> _overrides;
    private OwnedPalSnapshot _current;

    public OwnedPalRepository(
        IGameData data,
        UserDataStore? store = null,
        Func<DateTimeOffset>? clock = null,
        Action<string, Exception?>? warn = null)
    {
        _data = data;
        _store = store;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _warn = warn;
        _manual = store?.LoadManualPals() ?? [];
        _overrides = store?.LoadOverrides() ?? new Dictionary<string, PalOverride>(StringComparer.Ordinal);
        _current = Rebuild(0);
    }

    public OwnedPalSnapshot Current
    {
        get { lock (_gate) return _current; }
    }

    public event EventHandler<OwnedPalSnapshot>? Changed;

    public void PublishSourceSnapshot(string sourceId, IReadOnlyList<OwnedPal> pals, SourceStatus status)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourceId);
        Mutate(() => _sources[sourceId] = new SourceData(pals.ToList(), status));
    }

    public void RemoveSource(string sourceId) => Mutate(() => _sources.Remove(sourceId));

    public void SetSourceStatus(string sourceId, SourceStatus status)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourceId);
        Mutate(() =>
        {
            _sources[sourceId] = _sources.TryGetValue(sourceId, out var existing)
                ? existing with { Status = status }
                : new SourceData([], status);
        });
    }

    public OwnedPal AddManual(OwnedPalDraft draft)
    {
        OwnedPal pal = null!;
        Mutate(() =>
        {
            pal = BuildManual($"manual:{Guid.NewGuid():N}", draft);
            _manual.Add(pal);
            SaveManual();
        });
        return pal;
    }

    public void UpdateManual(string uid, OwnedPalDraft draft) =>
        Mutate(() =>
        {
            var index = _manual.FindIndex(p => string.Equals(p.Uid, uid, StringComparison.Ordinal));
            if (index < 0) throw new OwnedPalValidationException(OwnedPalValidationException.NotManual, uid);
            _manual[index] = BuildManual(uid, draft);
            SaveManual();
        });

    public void RemoveManual(string uid) =>
        Mutate(() =>
        {
            if (_manual.RemoveAll(p => string.Equals(p.Uid, uid, StringComparison.Ordinal)) == 0)
                throw new OwnedPalValidationException(OwnedPalValidationException.NotManual, uid);
            SaveManual();
            if (_overrides.Remove(uid)) SaveOverrides();
        });

    public void SetOverride(string uid, PalOverride? patch)
    {
        ArgumentException.ThrowIfNullOrEmpty(uid);
        PalOverride? normalized = null;
        if (patch is { IsEmpty: false }) normalized = Normalize(patch);

        Mutate(() =>
        {
            // The same in-game Pal read from a save and from the mod has one correction on every copy: a correction that stayed on a hidden
            // copy would otherwise come back after it was removed from the visible one.
            var changed = false;
            foreach (var copy in CopiesOf(uid))
            {
                if (normalized == null) changed |= _overrides.Remove(copy);
                else { _overrides[copy] = normalized; changed = true; }
            }

            if (changed) SaveOverrides();
        });
    }

    public OwnedPal? RawPal(string uid)
    {
        lock (_gate)
        {
            var all = _sources.Values.SelectMany(s => s.Pals).Concat(_manual).ToList();
            var pal = all.FirstOrDefault(p => string.Equals(p.Uid, uid, StringComparison.Ordinal));
            if (pal != null) return pal;
            // The visible copy of a Pal is the newest one; a stored selection may point to another copy of it.
            var instance = uid[(uid.LastIndexOf(':') + 1)..];
            return instance.Length == 0 ? null : all.Where(p => string.Equals(p.InstanceId, instance, StringComparison.OrdinalIgnoreCase)).OrderByDescending(p => p.SeenAtUtc).FirstOrDefault();
        }
    }

    // The uid and the uids of every other copy of the same in-game Pal (same instance id) in any source.
    private List<string> CopiesOf(string uid)
    {
        var all = _sources.Values.SelectMany(s => s.Pals).Concat(_manual).ToList();
        var pal = all.FirstOrDefault(p => string.Equals(p.Uid, uid, StringComparison.Ordinal));
        var copies = new List<string> { uid };
        if (pal == null || string.IsNullOrEmpty(pal.InstanceId)) return copies;
        copies.AddRange(all.Where(p => string.Equals(p.InstanceId, pal.InstanceId, StringComparison.OrdinalIgnoreCase)).Select(p => p.Uid));
        // A stored correction of a copy that is gone from its source (instance id embedded in the uid) goes with the others.
        copies.AddRange(_overrides.Keys.Where(k => k.EndsWith(":" + pal.InstanceId, StringComparison.OrdinalIgnoreCase)));
        return copies.Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>The Pals of a snapshot that count under the scope (owner, location kind, exclusion switch).</summary>
    public static IReadOnlyList<OwnedPal> Filter(OwnedPalSnapshot snapshot, OwnerScope scope) => Filter(snapshot.Pals, scope);

    /// <summary>
    /// The one scope filter of the app: My Pals, Paldex, the calculator and both planners use it (a Pal in a location that is
    /// owner-free counts whoever owns it, so "planned with" on screen and in the planner never differ).
    /// </summary>
    public static IReadOnlyList<OwnedPal> Filter(IEnumerable<OwnedPal> pals, OwnerScope scope)
    {
        HashSet<string>? players = scope.PlayerIds == null
            ? null
            : new HashSet<string>(scope.PlayerIds, StringComparer.OrdinalIgnoreCase);
        var locations = scope.IncludedLocations.ToHashSet();
        var ownerFree = (scope.OwnerFreeLocations ?? []).ToHashSet();

        return pals
            .Where(p => (scope.IncludeExcluded || !p.IsExcluded) &&
                        locations.Contains(p.Location.Kind) &&
                        (players == null || p.Owner == null || players.Contains(p.Owner.PlayerId) || ownerFree.Contains(p.Location.Kind)))
            .ToList();
    }

    // ---- internals -------------------------------------------------------------------------------------------

    // Runs a change under the lock, publishes the new snapshot and raises Changed once, outside the lock.
    private void Mutate(Action change)
    {
        OwnedPalSnapshot snapshot;
        lock (_gate)
        {
            change();
            snapshot = _current = Rebuild(_current.Version + 1);
        }

        Changed?.Invoke(this, snapshot);
    }

    private void SaveManual()
    {
        try { _store?.SaveManualPals(_manual); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _warn?.Invoke("Manual Pals could not be saved.", ex);
        }
    }

    private void SaveOverrides()
    {
        try { _store?.SaveOverrides(_overrides); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _warn?.Invoke("Pal corrections could not be saved.", ex);
        }
    }

    private OwnedPalSnapshot Rebuild(long version)
    {
        var all = _sources.Values.SelectMany(s => s.Pals).Concat(_manual).ToList();

        // The same in-game Pal can come from several sources (save + mod bridge): the newest copy wins.
        var visible = new List<OwnedPal>();
        var hiddenUidsByWinner = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var duplicates = 0;
        foreach (var group in all.GroupBy(p => string.IsNullOrEmpty(p.InstanceId) ? p.Uid : "i:" + p.InstanceId,
                     StringComparer.OrdinalIgnoreCase))
        {
            var ordered = group.OrderByDescending(p => p.SeenAtUtc).ThenBy(p => p.Uid, StringComparer.Ordinal).ToList();
            visible.Add(ordered[0]);
            if (ordered.Count > 1)
            {
                duplicates += ordered.Count - 1;
                hiddenUidsByWinner[ordered[0].Uid] = ordered.Skip(1).Select(p => p.Uid).ToList();
            }
        }

        var known = new HashSet<string>(visible.Select(p => p.Uid), StringComparer.Ordinal);
        foreach (var uids in hiddenUidsByWinner.Values) known.UnionWith(uids);

        var result = new List<OwnedPal>(visible.Count);
        var effective = new Dictionary<string, PalOverride>(_overrides, StringComparer.Ordinal);
        foreach (var pal in visible.OrderBy(p => p.Uid, StringComparer.Ordinal))
        {
            // A correction made on a hidden duplicate still applies to the visible copy (and is shown as the visible copy's own).
            var patch = Lookup(pal.Uid);
            if (patch == null && hiddenUidsByWinner.TryGetValue(pal.Uid, out var hidden))
            {
                patch = hidden.Select(Lookup).FirstOrDefault(o => o != null);
                if (patch != null) effective[pal.Uid] = patch;
            }

            result.Add(patch == null ? pal : Apply(pal, patch));
        }

        var orphans = _overrides.Keys.Where(uid => !known.Contains(uid)).Order(StringComparer.Ordinal).ToList();
        var statuses = _sources.Values.Select(s => s.Status).OrderBy(s => s.SourceId, StringComparer.Ordinal).ToList();
        return new OwnedPalSnapshot(
            result,
            statuses,
            effective,
            orphans,
            duplicates,
            version);

        PalOverride? Lookup(string uid) => _overrides.GetValueOrDefault(uid);
    }

    private static OwnedPal Apply(OwnedPal pal, PalOverride patch) => pal with
    {
        Gender = patch.ClearGender ? null : patch.Gender ?? pal.Gender,
        Passives = patch.Passives ?? pal.Passives,
        Ivs = patch.ClearIvs ? null : patch.Ivs ?? pal.Ivs,
        Level = patch.Level ?? pal.Level,
        IsExcluded = patch.IsExcluded ?? pal.IsExcluded,
        IsEdited = pal.IsEdited || patch.HasCorrections,
    };

    private OwnedPal BuildManual(string uid, OwnedPalDraft draft)
    {
        var species = ValidateSpecies(draft.SpeciesId);
        ValidateLevelAndRank(draft.Level, draft.Rank);
        var passives = ValidatePassives(draft.Passives);
        ValidateIvs(draft.Ivs);

        return new OwnedPal(
            Uid: uid,
            InstanceId: null,
            SpeciesId: species,
            Gender: draft.Gender,
            Level: draft.Level,
            Rank: draft.Rank,
            Passives: passives,
            Ivs: draft.Ivs,
            EquippedAttacks: draft.EquippedAttacks.ToList(),
            LearnedAttacks: draft.LearnedAttacks.ToList(),
            Nickname: string.IsNullOrWhiteSpace(draft.Nickname) ? null : draft.Nickname.Trim(),
            Owner: null,
            Location: draft.Location,
            Source: PalSourceKind.Manual,
            SourceId: ManualSourceId,
            SeenAtUtc: _clock(),
            IsEdited: false,
            IsExcluded: draft.IsExcluded);
    }

    private PalOverride Normalize(PalOverride patch)
    {
        if (patch.Level is { } level) ValidateLevelAndRank(level, 1);
        ValidateIvs(patch.Ivs);
        return patch with
        {
            Gender = patch.ClearGender ? null : patch.Gender,
            Ivs = patch.ClearIvs ? null : patch.Ivs,
            Passives = patch.Passives == null ? null : ValidatePassives(patch.Passives),
            Note = string.IsNullOrWhiteSpace(patch.Note) ? null : patch.Note.Trim(),
        };
    }

    private string ValidateSpecies(string id)
    {
        if (!_data.TryNormalizePalId(id, out var species))
            throw new OwnedPalValidationException(OwnedPalValidationException.UnknownSpecies, id);
        return species;
    }

    private IReadOnlyList<string> ValidatePassives(IReadOnlyList<string> ids)
    {
        if (ids.Count > MaxPassives)
            throw new OwnedPalValidationException(OwnedPalValidationException.TooManyPassives, ids.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));

        var result = new List<string>(ids.Count);
        foreach (var id in ids)
        {
            string canonical;
            try { canonical = _data.Passive(id).Id; }
            catch (KeyNotFoundException)
            {
                throw new OwnedPalValidationException(OwnedPalValidationException.UnknownPassive, id);
            }

            if (result.Contains(canonical, StringComparer.Ordinal))
                throw new OwnedPalValidationException(OwnedPalValidationException.DuplicatePassive, canonical);
            result.Add(canonical);
        }

        return result;
    }

    private static void ValidateLevelAndRank(int level, int rank)
    {
        if (level is < 1 or > MaxLevel)
            throw new OwnedPalValidationException(OwnedPalValidationException.LevelRange, level.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (rank is < 1 or > MaxRank)
            throw new OwnedPalValidationException(OwnedPalValidationException.RankRange, rank.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private static void ValidateIvs(Ivs? ivs)
    {
        if (ivs == null) return;
        foreach (var value in new[] { ivs.Hp, ivs.Attack, ivs.Defense })
        {
            if (value is < 0 or > 100)
                throw new OwnedPalValidationException(OwnedPalValidationException.IvRange, value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }
}
