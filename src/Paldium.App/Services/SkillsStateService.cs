using Paldium.Core.Data;
using Paldium.Core.Domain;
using Paldium.Core.Queries;

namespace Paldium.App.Services;

/// <summary>
/// In-memory filter, sort and selected-inspector state for Skills while the app is open (skills.md).
/// </summary>
public sealed class SkillsStateService
{
    public const string TabPassives = "passives";
    public const string TabActives = "actives";

    private string? _lastQueryKey;

    public string ActiveTab { get; set; } = TabPassives;

    public PassiveFilter PassiveFilter { get; set; } = new() { TagsMatchAll = true };
    public bool PassiveOwnedOnly { get; set; }
    public PassiveSort PassiveSort { get; set; } = new() { Key = PassiveSortKey.Rank, Descending = true };
    public string? SelectedPassiveId { get; set; }

    public ActiveFilter ActiveFilter { get; set; } = new();
    public ActiveSort ActiveSort { get; set; } = new() { Key = ActiveSortKey.Power, Descending = true };

    public bool HasPassiveFilters =>
        !string.IsNullOrWhiteSpace(PassiveFilter.Text) ||
        PassiveFilter.Ranks.Count > 0 ||
        PassiveFilter.MinRank != null ||
        PassiveFilter.MaxRank != null ||
        PassiveFilter.Tags.Count > 0 ||
        PassiveFilter.CanAppearRandomly != null ||
        PassiveFilter.SurgerySupported != null ||
        PassiveFilter.MutationOnly != null ||
        PassiveOwnedOnly;

    public bool HasActiveFilters =>
        !string.IsNullOrWhiteSpace(ActiveFilter.Text) ||
        ActiveFilter.Elements.Count > 0 ||
        ActiveFilter.MinPower != null ||
        ActiveFilter.MaxPower != null ||
        ActiveFilter.Inheritable != null ||
        ActiveFilter.HasSkillFruit != null;

    public void ClearPassiveFilters()
    {
        PassiveFilter = new PassiveFilter { TagsMatchAll = true };
        PassiveOwnedOnly = false;
    }

    public void ClearActiveFilters()
    {
        ActiveFilter = new ActiveFilter();
    }

    public static string NormalizeTab(string? raw) => (raw ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        TabActives or "aktifler" => TabActives,
        TabPassives or "pasifler" => TabPassives,
        _ => TabPassives,
    };

    /// <summary>
    /// Applies <c>?tab=</c> and <c>?passive=</c> once per unique navigation signature.
    /// If the requested passive is hidden by existing filters, clears conflicting passive filters so it appears in the table.
    /// </summary>
    public bool ApplyQuery(
        string? tab,
        string? passive,
        IGameData data,
        IReadOnlySet<string>? ownedPassiveIds = null)
    {
        var hasTab = !string.IsNullOrWhiteSpace(tab);
        var hasPassive = !string.IsNullOrWhiteSpace(passive);
        if (!hasTab && !hasPassive) return false;

        var key = $"{tab}|{passive}";
        if (string.Equals(key, _lastQueryKey, StringComparison.Ordinal)) return false;
        _lastQueryKey = key;

        if (hasPassive)
        {
            var match = data.Passives.FirstOrDefault(p =>
                string.Equals(p.Id, passive, StringComparison.OrdinalIgnoreCase));
            if (match != null)
            {
                ActiveTab = TabPassives;
                SelectedPassiveId = match.Id;
                var hiddenByOwned = PassiveOwnedOnly && (ownedPassiveIds == null || !ownedPassiveIds.Contains(match.Id));
                if (!SkillQuery.Matches(match, PassiveFilter) || hiddenByOwned)
                    ClearPassiveFilters();
                return true;
            }
        }

        if (hasTab)
        {
            ActiveTab = NormalizeTab(tab);
            return true;
        }

        return false;
    }

    public bool MatchesPassiveWithOwned(PassiveSkill passive, IReadOnlySet<string> ownedIds) =>
        SkillQuery.Matches(passive, PassiveFilter) &&
        (!PassiveOwnedOnly || ownedIds.Contains(passive.Id));
}
