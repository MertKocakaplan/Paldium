using System.Globalization;
using System.Text.RegularExpressions;
using Paldium.Core.Domain;

namespace Paldium.Core.Text;

/// <summary>Fills the <c>{placeholder}</c> variables of a partner skill description with the values of a rank.</summary>
public static partial class PartnerSkillText
{
    public const int MinRank = 1;
    public const int MaxRank = 5;

    [GeneratedRegex(@"\{([A-Za-z0-9_]+)\}")]
    private static partial Regex Placeholder();

    /// <summary>
    /// The description of <paramref name="pal"/>'s partner skill in <paramref name="lang"/> for <paramref name="rank"/>
    /// (1..5, clamped). A placeholder with no known value is left as it is. Null when the species has no partner skill.
    /// </summary>
    public static string? Render(PalSpecies pal, string lang, int rank)
    {
        if (pal.PartnerSkill is not { } skill) return null;
        return Render(skill, lang, rank);
    }

    public static string Render(PartnerSkillInfo skill, string lang, int rank)
    {
        var index = Math.Clamp(rank, MinRank, MaxRank) - 1;
        var text = skill.Descriptions.Get(lang);
        var result = Placeholder().Replace(text, m =>
        {
            var name = m.Groups[1].Value;
            if (skill.NumbersByRank.TryGetValue(name, out var numbers) && index < numbers.Count && numbers[index] is { } number)
                return FormatNumber(number);

            if (skill.TextsByRank.TryGetValue(name, out var perLanguage) && TryLanguage(perLanguage, lang, out var texts) &&
                index < texts.Count && texts[index] is { } replacement)
                return replacement;

            return m.Value;
        });

        // Empty replacements (rank 1 has no extra text) leave stray spaces behind.
        return Regex.Replace(result, @"\s+", " ").Trim();
    }

    /// <summary>True when the text still contains an unresolved <c>{placeholder}</c>.</summary>
    public static bool HasUnresolved(string text) => Placeholder().IsMatch(text);

    private static bool TryLanguage(
        IReadOnlyDictionary<string, IReadOnlyList<string?>> perLanguage, string lang, out IReadOnlyList<string?> texts)
    {
        if (perLanguage.TryGetValue(lang, out texts!)) return true;
        var dash = lang.IndexOf('-');
        if (dash > 0 && perLanguage.TryGetValue(lang[..dash], out texts!)) return true;
        return perLanguage.TryGetValue("en", out texts!);
    }

    private static string FormatNumber(double value) =>
        Math.Round(value, 2, MidpointRounding.AwayFromZero).ToString("0.##", CultureInfo.InvariantCulture);
}
