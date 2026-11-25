using Paldium.Core.Data;
using Paldium.Core.Domain;
using Paldium.Core.Queries;

namespace Paldium.App.Components;

/// <summary>Formatting and query matching helpers for <see cref="PalPicker"/> in Owned mode (T139).</summary>
public static class PalPickerFormat
{
    /// <summary>
    /// Builds a single-sentence accessible label for an owned Pal row in the picker:
    /// e.g. "Anubis, Erkek, Sv 42, Palbox s3, pasifler: Çalışkan, Ciddi".
    /// </summary>
    public static string BuildOwnedAriaLabel(
        OwnedPal pal,
        IGameData data,
        string lang,
        string locationShort,
        string levelLabel,
        string maleLabel,
        string femaleLabel,
        string passivesTemplate,
        string noPassivesText)
    {
        var name = !string.IsNullOrWhiteSpace(pal.Nickname)
            ? pal.Nickname!
            : data.TryNormalizePalId(pal.SpeciesId, out var cid)
                ? data.Pal(cid).DisplayName(lang)
                : pal.SpeciesId;

        var parts = new List<string>(5) { name };
        if (pal.Gender == Gender.Male) parts.Add(maleLabel);
        else if (pal.Gender == Gender.Female) parts.Add(femaleLabel);

        parts.Add($"{levelLabel} {pal.Level}");
        if (!string.IsNullOrWhiteSpace(locationShort)) parts.Add(locationShort);

        if (pal.Passives.Count == 0)
        {
            parts.Add(noPassivesText);
        }
        else
        {
            var passiveNames = new List<string>(pal.Passives.Count);
            foreach (var pid in pal.Passives)
            {
                try { passiveNames.Add(data.Passive(pid).Names.Get(lang)); }
                catch (KeyNotFoundException) { passiveNames.Add(pid); }
            }
            parts.Add(string.Format(passivesTemplate, string.Join(", ", passiveNames)));
        }

        return string.Join(", ", parts);
    }

    /// <summary>True when <paramref name="query"/> matches the passive's id or localized name (Turkish/case-folded).</summary>
    public static bool PassiveMatchesQuery(IGameData data, string passiveId, string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return false;
        var q = query.Trim();
        try
        {
            var passive = data.Passive(passiveId);
            return passive.Names.Languages.Any(l => TextFold.Contains(passive.Names.Get(l), q)) ||
                   TextFold.Contains(passive.Id, q);
        }
        catch (KeyNotFoundException)
        {
            return TextFold.Contains(passiveId, q);
        }
    }
}
