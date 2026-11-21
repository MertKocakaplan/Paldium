using Paldium.Core.Data;
using Paldium.Core.Domain;

namespace Paldium.Core.Planning;

public enum PresetVariant { Best, Easy }

/// <summary>Where a passive comes from (shown as a badge, T53).</summary>
public enum PassiveOrigin
{
    /// <summary>Can appear on wild Pals (<c>canAppearRandomly</c>).</summary>
    Wild,

    /// <summary>Only from a mutated egg (mutation passives).</summary>
    MutationOnly,

    /// <summary>A World Tree passive.</summary>
    WorldTree,

    /// <summary>Does not appear on wild Pals (Legend, Lucky, the +30 element passives ...); it is inherited or comes with certain species.</summary>
    NotWild,
}

/// <summary>A role set: four passives for one role, in one of two variants.</summary>
public sealed record RolePreset(string Role, PresetVariant Variant, IReadOnlyList<string> PassiveIds, IReadOnlyList<string> PenaltyPassives)
{
    public string Id => $"{Role}.{Variant}";
}

/// <summary>
/// Role sets (RESEARCH 12.2, T53). Every role has two variants: <see cref="PresetVariant.Best"/> is the fixed set of the research (data bundle
/// ids), <see cref="PresetVariant.Easy"/> is computed from the passives that can appear on wild Pals (<c>canAppearRandomly</c>) by the effect types of
/// the role, strongest first. The element role follows the element of the target species.
/// </summary>
public static class RolePresets
{
    public const string CombatBalanced = "CombatBalanced";
    public const string CombatDamage = "CombatDamage";
    public const string CombatTank = "CombatTank";
    public const string CombatElement = "CombatElement";
    public const string MountSpeed = "MountSpeed";
    public const string MountLong = "MountLong";
    public const string MountWater = "MountWater";
    public const string MountJump = "MountJump";
    public const string WorkerNight = "WorkerNight";
    public const string WorkerDay = "WorkerDay";
    public const string BreedingFarm = "BreedingFarm";

    public static IReadOnlyList<string> Roles { get; } =
    [
        CombatBalanced, CombatDamage, CombatTank, CombatElement, MountSpeed, MountLong, MountWater, MountJump, WorkerNight, WorkerDay, BreedingFarm,
    ];

    // The fixed "best" sets (RESEARCH 12.2). The element role is built from the target's element.
    private static readonly Dictionary<string, string[]> BestSets = new(StringComparer.Ordinal)
    {
        [CombatBalanced] = ["MutationPal_Immortal", "PAL_ALLAttack_up3", "Legend", "CoolTimeReduction_Up_1"],
        [CombatDamage] = ["WorldTree_ATK", "WorldTree_ATK_DEF", "PAL_ALLAttack_up3", "CoolTimeReduction_Up_1"],
        [CombatTank] = ["MutationPal_Immortal", "Deffence_up3", "Legend", "CoolTimeReduction_Up_1"],
        [MountSpeed] = ["WorldTree_MoveSpeed", "MoveSpeed_up_3", "Legend", "MoveSpeed_up_2"],
        [MountLong] = ["WorldTree_MoveSpeed", "MoveSpeed_up_3", "Legend", "Stamina_Up_3"],
        [MountWater] = ["WorldTree_MoveSpeed", "MoveSpeed_up_3", "SwimSpeed_up_3", "SwimSpeed_up_2"],
        [MountJump] = ["RideJumpCount_Increase2", "WorldTree_MoveSpeed", "MoveSpeed_up_3", "Legend"],
        [WorkerNight] = ["WorldTree_CraftSpeed", "CraftSpeed_up3", "CraftSpeed_up2", "PAL_CorporateSlave"],
        [WorkerDay] = ["WorldTree_CraftSpeed", "CraftSpeed_up3", "CraftSpeed_up2", "Nocturnal"],
        [BreedingFarm] = ["MutationPal_Babysitter", "Test_PalEgg_HatchingSpeed_Up", "CraftSpeed_up3", "CraftSpeed_up2"],
    };

    // The "easy" sets: one slot per passive, each slot an effect type (the strongest wild-appearing passive of it that is not taken yet), or
    // "=<id>" for one named passive. "@Element" is the element of the target species.
    private static readonly Dictionary<string, string[]> EasySlots = new(StringComparer.Ordinal)
    {
        [CombatBalanced] = ["ShotAttack", "ShotAttack", "Defense", "Defense"],
        [CombatDamage] = ["ShotAttack", "ShotAttack", "ShotAttack", "ShotAttack"],
        [CombatTank] = ["Defense", "Defense", "Defense", "Defense"],
        [CombatElement] = ["@Element", "ShotAttack", "ShotAttack", "Defense"],
        [MountSpeed] = ["MoveSpeed", "MoveSpeed", "MoveSpeed", "PalSP_Increase"],
        [MountLong] = ["MoveSpeed", "MoveSpeed", "PalSP_Increase", "PalSP_Increase"],
        [MountWater] = ["SwimSpeed", "SwimSpeed", "MoveSpeed", "MoveSpeed"],
        [MountJump] = ["RideJumpCount_Increase", "MoveSpeed", "MoveSpeed", "MoveSpeed"],
        [WorkerNight] = ["CraftSpeed", "CraftSpeed", "CraftSpeed", "CraftSpeed"],
        [WorkerDay] = ["CraftSpeed", "CraftSpeed", "CraftSpeed", "=Nocturnal"],
        [BreedingFarm] = ["BreedSpeed", "CraftSpeed", "CraftSpeed", "CraftSpeed"],
    };

    /// <summary>The set of a role, or null when it cannot be built (the element role without a target species, ids missing from the bundle).</summary>
    public static RolePreset? Build(IGameData data, string role, PresetVariant variant, PalSpecies? target = null)
    {
        var ids = variant == PresetVariant.Best ? Best(data, role, target) : Easy(data, role, target);
        return ids == null ? null : new RolePreset(role, variant, ids, PassivePresets.PenaltyOf(data, ids));
    }

    /// <summary>Every role in both variants (the element role only when a target is given).</summary>
    public static IReadOnlyList<RolePreset> All(IGameData data, PalSpecies? target = null) =>
        Roles.SelectMany(role => new[] { PresetVariant.Best, PresetVariant.Easy }.Select(v => Build(data, role, v, target))).OfType<RolePreset>().ToList();

    /// <summary>The element ids ("Fire", "Water", ...) match the game's <c>ElementBoost_&lt;element&gt;</c> effect types.</summary>
    private static string ElementOf(PalSpecies? target) => target?.Elements.FirstOrDefault() ?? string.Empty;

    private static IReadOnlyList<string>? Best(IGameData data, string role, PalSpecies? target)
    {
        List<string> ids;
        if (role == CombatElement)
        {
            var element = ElementOf(target);
            if (element.Length == 0) return null;
            var effect = "ElementBoost_" + element;
            // The +30 passive of the element alone, then a dual-element +30 passive that contains it, then Demon God and Legend.
            var single = data.Passives
                .Where(p => !p.CanAppearRandomly && p.Effects.Count(e => e.Value > 0 && e.Type.StartsWith("ElementBoost_", StringComparison.Ordinal)) == 1 &&
                            p.Effects.Any(e => e.Type == effect && e.Value >= 30))
                .OrderBy(p => p.Id, StringComparer.Ordinal).FirstOrDefault();
            var dual = data.Passives
                .Where(p => p.Effects.Count(e => e.Value >= 30 && e.Type.StartsWith("ElementBoost_", StringComparison.Ordinal)) == 2 &&
                            p.Effects.Any(e => e.Type == effect && e.Value >= 30))
                // A target with two elements prefers the dual passive that holds both of them (Dark + Ice: Witch, not Invader).
                .OrderByDescending(p => (target?.Elements ?? []).Count(e => p.Effects.Any(x => x.Type == "ElementBoost_" + e && x.Value >= 30)))
                .ThenByDescending(p => p.Rank).ThenBy(p => p.Id, StringComparer.Ordinal).FirstOrDefault();
            ids = [];
            if (single != null) ids.Add(single.Id);
            if (dual != null) ids.Add(dual.Id);
            ids.Add("PAL_ALLAttack_up3");
            ids.Add("Legend");
            if (ids.Count < PassivePresets.Size) ids.Add("CoolTimeReduction_Up_1");
        }
        else if (BestSets.TryGetValue(role, out var fixedIds)) ids = fixedIds.ToList();
        else return null;

        var set = ids.Take(PassivePresets.Size).ToList();
        return set.All(id => Exists(data, id)) ? set : null;
    }

    private static bool Exists(IGameData data, string id)
    {
        try { _ = data.Passive(id); return true; }
        catch (KeyNotFoundException) { return false; }
    }

    private static IReadOnlyList<string>? Easy(IGameData data, string role, PalSpecies? target)
    {
        if (!EasySlots.TryGetValue(role, out var slots)) return null;
        if (slots.Contains("@Element") && ElementOf(target).Length == 0) return null;
        var chosen = new List<PassiveSkill>();

        PassiveSkill? Pick(string slot)
        {
            if (slot.StartsWith('='))
                return data.Passives.FirstOrDefault(p => p.CanAppearRandomly && string.Equals(p.Id, slot[1..], StringComparison.Ordinal) && !chosen.Contains(p));
            if (slot == "@Element" && ElementOf(target).Length == 0) return null;
            var type = slot == "@Element" ? "ElementBoost_" + ElementOf(target) : slot;
            return data.Passives
                .Where(p => p.CanAppearRandomly && !p.MutationExclusive && !chosen.Contains(p))
                .Select(p => (Passive: p, Magnitude: p.Effects.Where(e => e.Type == type && e.Value > 0).Select(e => e.Value).DefaultIfEmpty(0).Max()))
                .Where(x => x.Magnitude > 0)
                // Strongest first; at equal strength a passive without a penalty, then the higher rank.
                .OrderByDescending(x => x.Magnitude).ThenBy(x => x.Passive.PenaltyTags.Count).ThenByDescending(x => x.Passive.Rank)
                .ThenBy(x => x.Passive.Id, StringComparer.Ordinal)
                .Select(x => x.Passive).FirstOrDefault();
        }

        foreach (var slot in slots)
        {
            if (Pick(slot) is { } passive) chosen.Add(passive);
        }

        // A slot without a candidate (not enough wild passives of that type) is filled from the slot types of the role, one after the other.
        foreach (var type in slots.Where(s => !s.StartsWith('=')).Distinct(StringComparer.Ordinal))
            while (chosen.Count < PassivePresets.Size && Pick(type) is { } more) chosen.Add(more);
        return chosen.Count == 0 ? null : chosen.Select(p => p.Id).ToList();
    }

    /// <summary>Where a passive comes from.</summary>
    public static PassiveOrigin OriginOf(PassiveSkill passive)
    {
        if (passive.MutationExclusive) return PassiveOrigin.MutationOnly;
        if (passive.Tags.Contains("WorldTree", StringComparer.Ordinal) || passive.Id.StartsWith("WorldTree_", StringComparison.Ordinal)) return PassiveOrigin.WorldTree;
        return passive.CanAppearRandomly ? PassiveOrigin.Wild : PassiveOrigin.NotWild;
    }

    /// <summary>Species that carry the passive from birth (<c>guaranteedPassives</c>).</summary>
    public static IReadOnlyList<PalSpecies> GuaranteedOn(IGameData data, string passiveId) =>
        data.Pals.Where(p => p.GuaranteedPassives.Contains(passiveId, StringComparer.OrdinalIgnoreCase)).ToList();
}
