using System.Text.Json;

namespace Paldium.DataBuilder;

/// <summary>
/// Assigns passive tags from passive-tags.json (exact id, then longest prefix).
/// <c>tags</c> are categories the passive improves, <c>penaltyTags</c> categories it worsens (ARCHITECTURE 4.4).
/// </summary>
public sealed class PassiveTagger
{
    private sealed record Mapping(string[] Tags, string[] Penalty);

    private readonly Dictionary<string, Mapping> _exact;
    private readonly List<KeyValuePair<string, Mapping>> _prefixes;

    public PassiveTagger(string jsonPath)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
        _exact = Read(doc.RootElement.GetProperty("exact"));
        _prefixes = Read(doc.RootElement.GetProperty("prefixes"))
            .OrderByDescending(kv => kv.Key.Length)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .ToList();
    }

    private static Dictionary<string, Mapping> Read(JsonElement obj) =>
        obj.EnumerateObject().ToDictionary(p => p.Name, p => ReadMapping(p.Value), StringComparer.Ordinal);

    // An entry is either a plain array (tags only) or { "tags": [...], "penalty": [...] }.
    private static Mapping ReadMapping(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Array) return new Mapping(Strings(e), []);
        var tags = e.TryGetProperty("tags", out var t) ? Strings(t) : [];
        var penalty = e.TryGetProperty("penalty", out var p) ? Strings(p) : [];
        return new Mapping(tags, penalty);
    }

    private static string[] Strings(JsonElement array) => array.EnumerateArray().Select(x => x.GetString()!).ToArray();

    /// <summary>Returns tags and penalty tags; <c>Mapped</c> is false when no mapping existed.</summary>
    public (IReadOnlyList<string> Tags, IReadOnlyList<string> PenaltyTags, bool Mapped) Tag(string id, int rank)
    {
        Mapping? mapping = null;
        if (_exact.TryGetValue(id, out var e)) mapping = e;
        else
        {
            foreach (var kv in _prefixes)
            {
                if (id.StartsWith(kv.Key, StringComparison.Ordinal)) { mapping = kv.Value; break; }
            }
        }

        var mapped = mapping != null;
        var tags = mapping?.Tags ?? ["Other"];
        var penalty = mapping?.Penalty ?? [];

        if (rank < 0)
        {
            // Negative passives: tags = [Negative]; everything they touch is a penalty.
            var affected = tags.Concat(penalty).Where(t => t != "Negative").Distinct().ToList();
            return (["Negative"], affected, mapped);
        }

        return (tags.Distinct().ToList(), penalty.Distinct().ToList(), mapped);
    }
}
