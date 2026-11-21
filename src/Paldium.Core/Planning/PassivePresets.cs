using Paldium.Core.Data;
using Paldium.Core.Domain;

namespace Paldium.Core.Planning;

/// <summary>A named set of up to four passive skills.</summary>
/// <param name="Id">The role and variant for the built-in sets (see <see cref="RolePresets"/>), any other text for saved ones.</param>
/// <param name="PenaltyPassives">Passives of the set that worsen another category (<see cref="PassiveSkill.PenaltyTags"/>): the UI shows a warning badge.</param>
public sealed record PassivePreset(string Id, IReadOnlyList<string> PassiveIds, IReadOnlyList<string> PenaltyPassives);

/// <summary>Helpers shared by the passive sets (ARCHITECTURE 7.4, T19, T53).</summary>
public static class PassivePresets
{
    public const int Size = 4;

    /// <summary>The passives of a set that carry penalty tags.</summary>
    public static IReadOnlyList<string> PenaltyOf(IEnumerable<PassiveSkill> passives) =>
        passives.Where(p => p.PenaltyTags.Count > 0).Select(p => p.Id).ToList();

    public static IReadOnlyList<string> PenaltyOf(IGameData data, IEnumerable<string> passiveIds) =>
        PenaltyOf(passiveIds.Select(id =>
        {
            try { return data.Passive(id); }
            catch (KeyNotFoundException) { return null; }
        }).OfType<PassiveSkill>());
}
