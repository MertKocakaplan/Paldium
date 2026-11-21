using Paldium.Core.Owned;

namespace Paldium.App.Components;

/// <summary>Text for the owned counts of a species ("3 (♂2 ♀1)") and its gender badge.</summary>
public static class OwnedDisplay
{
    public const string NoneText = "—";

    /// <summary>
    /// What to show for a box key of a bridge location (<c>uid</c>, <c>name:&lt;player&gt;</c> or <c>unknown-&lt;n&gt;</c>): the player name, the end of a
    /// uid, or <paramref name="unknownText"/> when the owner is not known.
    /// </summary>
    public static string BoxLabel(string boxId, string unknownText)
    {
        if (boxId.StartsWith("name:", StringComparison.Ordinal)) return boxId["name:".Length..];
        if (boxId.StartsWith("unknown", StringComparison.Ordinal)) return unknownText;
        return "…" + boxId[^Math.Min(8, boxId.Length)..];
    }

    /// <summary>"3 (♂2 ♀1)"; genders that are zero are left out; "—" when none is owned. A Pal of unknown gender is in the total only.</summary>
    public static string Label(OwnedSpeciesStats? stats)
    {
        if (stats == null || stats.Total == 0) return NoneText;
        var genders = new List<string>();
        if (stats.Male > 0) genders.Add("♂" + stats.Male);
        if (stats.Female > 0) genders.Add("♀" + stats.Female);
        return genders.Count == 0 ? stats.Total.ToString() : $"{stats.Total} ({string.Join(' ', genders)})";
    }

    /// <summary>" (♂2 ♀1)" with a leading space; empty when no gender is known.</summary>
    public static string GenderSuffix(OwnedSpeciesStats stats)
    {
        var genders = new List<string>();
        if (stats.Male > 0) genders.Add("♂" + stats.Male);
        if (stats.Female > 0) genders.Add("♀" + stats.Female);
        return genders.Count == 0 ? string.Empty : $" ({string.Join(" ", genders)})";
    }

    /// <summary>"♂♀" (a pair: same-species breeding is possible), "♂" or "♀"; empty when none.</summary>
    public static string Badge(OwnedSpeciesStats? stats) => stats?.Genders switch
    {
        GenderState.Both => "♂♀",
        GenderState.MaleOnly => "♂",
        GenderState.FemaleOnly => "♀",
        _ => string.Empty,
    };
}
