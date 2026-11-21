using PalCalc.Model;
using Paldium.Core.Data;
using Paldium.Core.Domain;
using DomainLocation = Paldium.Core.Domain.PalLocation;

namespace Paldium.Import.Saves;

/// <summary>Converts PalCalc's save model to Paldium's domain model. PalCalc types stay inside this assembly.</summary>
internal sealed class PalCalcMapping
{
    private readonly IGameData _data;
    private readonly HashSet<string> _skippedSpecies = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _droppedPassives = new(StringComparer.OrdinalIgnoreCase);

    public PalCalcMapping(IGameData data)
    {
        _data = data;
    }

    public IReadOnlyList<string> SkippedUnknownSpecies => _skippedSpecies.Order(StringComparer.Ordinal).ToList();

    public IReadOnlyList<string> DroppedUnknownPassives => _droppedPassives.Order(StringComparer.Ordinal).ToList();

    public static LocationKind MapLocation(LocationType type) => type switch
    {
        LocationType.PlayerParty => LocationKind.Party,
        LocationType.Palbox => LocationKind.Palbox,
        LocationType.Base => LocationKind.Base,
        LocationType.ViewingCage => LocationKind.ViewingCage,
        LocationType.DimensionalPalStorage => LocationKind.DimensionalStorage,
        LocationType.GlobalPalStorage => LocationKind.GlobalStorage,
        _ => LocationKind.Unknown,
    };

    /// <summary>
    /// Maps one Pal. Returns null (and remembers the name) when the species is unknown to the game data.
    /// </summary>
    public OwnedPal? Map(
        PalInstance pal,
        string uid,
        PalSourceKind source,
        string sourceId,
        DateTimeOffset seenAtUtc,
        PalOwner? owner,
        string? baseId)
    {
        if (!_data.TryNormalizePalId(pal.Pal.InternalName, out var species))
        {
            _skippedSpecies.Add(pal.Pal.InternalName);
            return null;
        }

        var passives = new List<string>();
        foreach (var passive in pal.PassiveSkills ?? [])
        {
            if (_data.Passives.FirstOrDefault(p => string.Equals(p.Id, passive.InternalName, StringComparison.OrdinalIgnoreCase)) is { } known)
            {
                if (!passives.Contains(known.Id, StringComparer.Ordinal)) passives.Add(known.Id);
            }
            else
            {
                _droppedPassives.Add(passive.InternalName);
            }
        }

        return new OwnedPal(
            Uid: uid,
            InstanceId: pal.InstanceId,
            SpeciesId: species,
            Gender: pal.Gender switch
            {
                PalGender.MALE => Gender.Male,
                PalGender.FEMALE => Gender.Female,
                _ => null,
            },
            Level: Math.Max(1, pal.Level),
            Rank: Math.Clamp(pal.Rank, 1, 5),
            Passives: passives.Take(4).ToList(),
            Ivs: new Ivs(ClampIv(pal.IV_HP), ClampIv(pal.IV_Attack), ClampIv(pal.IV_Defense)),
            EquippedAttacks: (pal.EquippedActiveSkills ?? []).Select(a => a.InternalName).ToList(),
            LearnedAttacks: (pal.ActiveSkills ?? []).Select(a => a.InternalName).ToList(),
            Nickname: string.IsNullOrWhiteSpace(pal.NickName) ? null : pal.NickName,
            Owner: owner,
            Location: DomainLocation.FromRunningIndex(MapLocation(pal.Location.Type), pal.Location.ContainerId, pal.Location.Index, baseId),
            Source: source,
            SourceId: sourceId,
            SeenAtUtc: seenAtUtc,
            IsEdited: false,
            IsExcluded: false);
    }

    private static int ClampIv(int value) => Math.Clamp(value, 0, 100);
}
