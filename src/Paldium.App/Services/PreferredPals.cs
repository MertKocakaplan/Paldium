using Paldium.Core.Domain;
using Paldium.Core.Owned;

namespace Paldium.App.Services;

/// <summary>
/// The Planner's preferred Pals (<see cref="PlannerSettings.PreferredUids"/>), the same list the Planner shelf marks. My Pals adds and removes
/// Pals here; the Planner reads the list when it opens.
/// </summary>
public sealed class PreferredPals(SettingsStore store, IOwnedPalRepository repository)
{
    public bool IsPreferred(OwnedPal pal) =>
        store.Current.Planner.PreferredUids.Any(k => Matches(k, pal));

    /// <summary>Adds the Pal when it is not preferred, removes it otherwise (the key is never added twice). True when it is preferred afterwards.</summary>
    public bool Toggle(OwnedPal pal)
    {
        var preferred = !IsPreferred(pal);
        store.Update(s =>
        {
            var kept = s.Planner.PreferredUids.Where(k => !Matches(k, pal)).ToList();
            if (preferred) kept.Add(pal.SelectionKey);
            return s with { Planner = s.Planner with { PreferredUids = kept } };
        });
        return preferred;
    }

    // A stored key may be an older id of the same Pal (the shelf resolves keys through the repository the same way).
    private bool Matches(string key, OwnedPal pal) =>
        string.Equals(key, pal.SelectionKey, StringComparison.Ordinal)
        || string.Equals(repository.Current.Find(key)?.SelectionKey, pal.SelectionKey, StringComparison.Ordinal);
}
