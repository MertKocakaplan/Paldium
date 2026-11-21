using Paldium.Core.Domain;
using Paldium.Core.Queries;

namespace Paldium.App.Pages.GuidesParts;

/// <summary>One line of the Mounts table: a rideable Pal and its mount data (T124).</summary>
public sealed record MountRow(PalSpecies Pal, MountInfo Mount);

/// <summary>A speed passive the user can switch on: its percentage in one of the three kinds.</summary>
public sealed record MountPassive(PassiveSkill Skill, string Group, int Percent);

/// <summary>
/// What the user picked above the table (T124): the star level of the Pal Essence Condenser (partner skill rank, 0 to 4) and the speed passives that are on.
/// </summary>
public sealed record MountSelection(int Stars, IReadOnlyList<string> PassiveIds)
{
    public static MountSelection None { get; } = new(0, []);
}

/// <summary>The numbers of one row under a selection: the final values and the percentages that were added to the base.</summary>
public sealed record MountValues(
    int Walk, int Run, int Sprint, int? Swim, int? SwimSprint, int Stamina, int MovePercent, int SwimPercent, int StaminaPercent);

/// <summary>
/// The Mounts reference table (screens/guides.md "Binekler: kim en hızlı"): every rideable Pal with its speeds and stamina, worked out for a star level and
/// a set of speed passives. The percentages of one kind are added and applied to the base value (the community sites do the same; the game was not
/// measured, user decision 2026-11-20). Pure functions over the data bundle, tested without the page.
/// </summary>
public static class MountModel
{
    public const string GroupMove = "Move", GroupSwim = "Swim", GroupStamina = "Stamina";

    public const string SortPal = "pal", SortSaddle = "saddle", SortWalk = "walk", SortRun = "run", SortSprint = "sprint", SortSwim = "swim",
        SortSwimSprint = "swimsprint", SortStamina = "stamina", SortOwned = "owned";

    public const int MaxStars = 4;

    /// <summary>The rideable Pals of the data bundle.</summary>
    public static IReadOnlyList<MountRow> Rows(IEnumerable<PalSpecies> all) =>
        all.Where(p => p.Mount != null).Select(p => new MountRow(p, p.Mount!)).ToList();

    /// <summary>
    /// The speed passives of the game data that can be switched on: the ones with a positive MoveSpeed, SwimSpeed or PalSP_Increase effect. Movement passives
    /// first, then swimming, then stamina; the smallest bonus first.
    /// </summary>
    public static IReadOnlyList<MountPassive> Passives(IEnumerable<PassiveSkill> passives)
    {
        var list = new List<MountPassive>();
        foreach (var passive in passives)
        {
            foreach (var (type, group) in new[] { ("MoveSpeed", GroupMove), ("SwimSpeed", GroupSwim), ("PalSP_Increase", GroupStamina) })
            {
                var total = passive.Effects.Where(e => e.Type == type && e.Target == "ToSelf" && e.Value > 0).Sum(e => e.Value);
                if (total > 0) list.Add(new MountPassive(passive, group, (int)Math.Round(total)));
            }
        }

        return list.OrderBy(p => p.Group == GroupMove ? 0 : p.Group == GroupSwim ? 1 : 2).ThenBy(p => p.Percent).ThenBy(p => p.Skill.Id, StringComparer.Ordinal).ToList();
    }

    /// <summary>The passives that can still be switched on: none once <paramref name="max"/> are on.</summary>
    public static bool CanAdd(MountSelection selection, int max) => selection.PassiveIds.Count < max;

    /// <summary>Switches a passive on or off; switching on is refused (the selection stays as it is) when <paramref name="max"/> are already on.</summary>
    public static MountSelection Toggle(MountSelection selection, string passiveId, int max)
    {
        if (selection.PassiveIds.Contains(passiveId, StringComparer.Ordinal))
            return selection with { PassiveIds = selection.PassiveIds.Where(x => !string.Equals(x, passiveId, StringComparison.Ordinal)).ToList() };
        return CanAdd(selection, max) ? selection with { PassiveIds = [.. selection.PassiveIds, passiveId] } : selection;
    }

    /// <summary>The final numbers of a row: <c>base × (1 + percent / 100)</c>, rounded.</summary>
    public static MountValues Compute(MountRow row, MountSelection selection, IReadOnlyList<MountPassive> passives)
    {
        int Sum(string group) => passives.Where(p => p.Group == group && selection.PassiveIds.Contains(p.Skill.Id, StringComparer.Ordinal)).Sum(p => p.Percent);
        var move = Sum(GroupMove) + row.Mount.MoveBonus(selection.Stars);
        var swim = Sum(GroupSwim) + row.Mount.SwimBonus(selection.Stars);
        var stamina = Sum(GroupStamina);
        var stats = row.Pal.Stats;
        return new MountValues(
            Apply(stats.WalkSpeed, move), Apply(stats.RunSpeed, move), Apply(stats.RideSprintSpeed, move),
            row.Mount.Is(MountKind.Water) ? Apply(row.Mount.SwimSpeed, swim) : null,
            row.Mount.Is(MountKind.Water) ? Apply(row.Mount.SwimDashSpeed, swim) : null,
            Apply(stats.Stamina, stamina), move, swim, stamina);
    }

    public static int Apply(int value, int percent) => (int)Math.Round(value * (1 + percent / 100.0), MidpointRounding.AwayFromZero);

    /// <summary>Search by Pal name (letter case and Turkish characters do not matter), the kinds (one is enough; none selected means all) and "only the ones you have".</summary>
    public static IReadOnlyList<MountRow> Filter(IEnumerable<MountRow> rows, string query, string lang, IReadOnlyCollection<string> kinds, bool onlyOwned, Func<string, int> ownedOf)
    {
        var q = TextFold.Fold(query).Trim();
        return rows.Where(r => (kinds.Count == 0 || kinds.Any(r.Mount.Is))
                               && (q.Length == 0 || TextFold.Fold(r.Pal.Names.Get(lang)).Contains(q, StringComparison.Ordinal))
                               && (!onlyOwned || ownedOf(r.Pal.Id) > 0)).ToList();
    }

    /// <summary>
    /// Sorted by a column of the computed table. The default of the page is sprint, high first. A row without a value in the column ("-": swim of a
    /// Pal that does not swim, an unknown saddle level) stays at the end in either direction; every sort breaks ties by sprint, then Pal number.
    /// </summary>
    public static IReadOnlyList<MountRow> Sort(IEnumerable<MountRow> rows, string key, bool descending, string lang, Func<string, int> ownedOf,
        MountSelection selection, IReadOnlyList<MountPassive> passives)
    {
        var culture = System.Globalization.CultureInfo.GetCultureInfo(lang);
        var items = rows.Select(r => (Row: r, Values: Compute(r, selection, passives))).ToList();
        int? Key((MountRow Row, MountValues Values) x) => key switch
        {
            SortSaddle => x.Row.Mount.SaddleLevel,
            SortWalk => x.Values.Walk,
            SortRun => x.Values.Run,
            SortSwim => x.Values.Swim,
            SortSwimSprint => x.Values.SwimSprint,
            SortStamina => x.Values.Stamina,
            SortOwned => ownedOf(x.Row.Pal.Id),
            SortPal => x.Row.Pal.DexNo,
            _ => x.Values.Sprint,
        };

        var ordered = items.OrderBy(x => Key(x).HasValue ? 0 : 1);
        ordered = descending ? ordered.ThenByDescending(x => Key(x) ?? 0) : ordered.ThenBy(x => Key(x) ?? 0);
        if (key != SortPal) ordered = ordered.ThenByDescending(x => x.Values.Sprint);
        return ordered.ThenBy(x => x.Row.Pal.DexNo).ThenBy(x => x.Row.Pal.IsVariant).ThenBy(x => x.Row.Pal.Names.Get(lang), StringComparer.Create(culture, ignoreCase: true))
            .Select(x => x.Row).ToList();
    }

    /// <summary>The direction a column starts with when it is chosen: numbers high first, the Pal column by number up.</summary>
    public static bool DefaultDescending(string key) => key != SortPal;

    /// <summary>
    /// The partner skill's speed bonus by star as a tip ("1★ +10% · 2★ +12% · ..."); null when the species has none. <paramref name="swim"/> picks the swim bonus.
    /// </summary>
    public static IReadOnlyList<(int Stars, int Percent)>? StarBonuses(MountInfo mount, bool swim = false)
    {
        var values = swim ? mount.SwimByStar : mount.MoveByStar;
        var list = values.Select((v, i) => (Stars: i, Percent: v)).Where(x => x.Stars >= 1 && x.Percent > 0).ToList();
        return list.Count == 0 ? null : list;
    }

    /// <summary>The conditional (not counted) bonuses by star, or null.</summary>
    public static IReadOnlyList<(int Stars, int Percent)>? ConditionalBonuses(MountInfo mount)
    {
        if (mount.Conditional == null) return null;
        var length = Math.Max(mount.Conditional.MoveByStar.Count, mount.Conditional.SwimByStar.Count);
        var list = Enumerable.Range(0, length)
            .Select(i => (Stars: i, Percent: (i < mount.Conditional.MoveByStar.Count ? mount.Conditional.MoveByStar[i] : 0) + (i < mount.Conditional.SwimByStar.Count ? mount.Conditional.SwimByStar[i] : 0)))
            .Where(x => x.Percent > 0).ToList();
        return list.Count == 0 ? null : list;
    }
}
