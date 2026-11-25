using Paldium.Core.Domain;

namespace Paldium.Core.Data;

/// <summary>Read-only access to the game data bundle (ARCHITECTURE section 5).</summary>
public interface IGameData
{
    GameDataMeta Meta { get; }
    MechanicsConfig Mechanics { get; }
    IReadOnlyList<PalSpecies> Pals { get; }
    IReadOnlyList<PassiveSkill> Passives { get; }
    IReadOnlyList<ActiveSkill> Actives { get; }
    IReadOnlyList<ElementInfo> Elements { get; }
    IReadOnlyList<WorkTypeInfo> WorkTypes { get; }

    /// <summary>The Pal Effigy enhancements (T126); empty for data without them.</summary>
    IReadOnlyList<RelicInfo> Relics { get; }

    /// <summary>The technology tree (T128); empty for data without it.</summary>
    IReadOnlyList<TechnologyInfo> Technologies { get; }

    /// <summary>Merchants and Pal shops (T137); empty for data without them.</summary>
    IReadOnlyList<ShopInfo> Shops { get; }

    /// <summary>Unknown id throws <see cref="KeyNotFoundException"/>. Lookup is case-insensitive.</summary>
    PalSpecies Pal(string id);
    PassiveSkill Passive(string id);
    ActiveSkill Active(string id);

    /// <summary>"BOSS_CowPal" becomes "CowPal"; case-insensitive; false when the species is unknown.</summary>
    bool TryNormalizePalId(string raw, out string id);

    /// <summary>
    /// True when the species can be obtained without breeding (ARCHITECTURE 7.3): <c>obtain.methods</c> has one of
    /// Wild, AlphaBoss, Imprisoned, Dungeon, DungeonBoss, Cage, Merchant, Fishing, Raid and the species is capturable.
    /// Data without <c>obtain</c> (schema 1 or no extractor) falls back to "has a wild level range".
    /// </summary>
    bool IsWildCatchable(string palId);

    /// <summary>A game term (schema 2 <c>terms</c>), e.g. "BreedingFarm", "Cake02", "Element.Fire"; false when absent.</summary>
    bool TryGetTerm(string termId, out LocalizedText text);
}

/// <summary>In-memory implementation built by <see cref="GameDataLoader"/>.</summary>
public sealed class GameData : IGameData
{
    // Prefixes the game puts in front of species ids for special spawns (case-insensitive).
    private static readonly string[] IdPrefixes = ["BOSS_", "RAID_", "GYM_"];

    private readonly Dictionary<string, PalSpecies> _pals;
    private readonly Dictionary<string, PassiveSkill> _passives;
    private readonly Dictionary<string, ActiveSkill> _actives;

    public GameData(
        GameDataMeta meta, MechanicsConfig mechanics, IReadOnlyList<PalSpecies> pals,
        IReadOnlyList<PassiveSkill> passives, IReadOnlyList<ActiveSkill> actives,
        IReadOnlyList<ElementInfo> elements, IReadOnlyList<WorkTypeInfo> workTypes,
        IReadOnlyDictionary<string, LocalizedText>? terms = null)
    {
        Terms = terms ?? new Dictionary<string, LocalizedText>(StringComparer.Ordinal);
        Meta = meta;
        Mechanics = mechanics;
        Pals = pals;
        Passives = passives;
        Actives = actives;
        Elements = elements;
        WorkTypes = workTypes;
        _pals = pals.ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);
        _passives = passives.ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);
        _actives = actives.ToDictionary(a => a.Id, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Game terms by id (empty for data without a <c>terms</c> section).</summary>
    public IReadOnlyDictionary<string, LocalizedText> Terms { get; }

    public bool TryGetTerm(string termId, out LocalizedText text) => Terms.TryGetValue(termId, out text!);

    public GameDataMeta Meta { get; }
    public MechanicsConfig Mechanics { get; }
    public IReadOnlyList<PalSpecies> Pals { get; }
    public IReadOnlyList<PassiveSkill> Passives { get; }
    public IReadOnlyList<ActiveSkill> Actives { get; }
    public IReadOnlyList<ElementInfo> Elements { get; }
    public IReadOnlyList<WorkTypeInfo> WorkTypes { get; }

    public IReadOnlyList<RelicInfo> Relics { get; init; } = [];

    public IReadOnlyList<TechnologyInfo> Technologies { get; init; } = [];

    public IReadOnlyList<ShopInfo> Shops { get; init; } = [];

    public PalSpecies Pal(string id) =>
        _pals.TryGetValue(id, out var p) ? p : throw new KeyNotFoundException($"Unknown Pal id '{id}'.");

    public PassiveSkill Passive(string id) =>
        _passives.TryGetValue(id, out var p) ? p : throw new KeyNotFoundException($"Unknown passive id '{id}'.");

    public ActiveSkill Active(string id) =>
        _actives.TryGetValue(id, out var a) ? a : throw new KeyNotFoundException($"Unknown active skill id '{id}'.");

    public bool TryNormalizePalId(string raw, out string id)
    {
        id = string.Empty;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        var candidate = raw.Trim();
        while (true)
        {
            // Try the id as given first, so a species whose real id starts with a prefix still resolves.
            if (_pals.TryGetValue(candidate, out var pal))
            {
                id = pal.Id;
                return true;
            }

            var prefix = IdPrefixes.FirstOrDefault(p => candidate.StartsWith(p, StringComparison.OrdinalIgnoreCase));
            if (prefix == null) return false;
            candidate = candidate[prefix.Length..];
        }
    }

    /// <summary>Obtain methods that give the species without breeding.</summary>
    public static readonly IReadOnlyList<string> NonBreedingMethods =
        ["Wild", "AlphaBoss", "Imprisoned", "Dungeon", "DungeonBoss", "Cage", "Merchant", "Fishing", "Raid"];

    public bool IsWildCatchable(string palId)
    {
        if (!_pals.TryGetValue(palId, out var p)) return false;
        if (p.Obtain == null) return p.WildLevel != null;
        return !p.IsUncapturable && NonBreedingMethods.Any(p.Obtain.HasMethod);
    }
}
