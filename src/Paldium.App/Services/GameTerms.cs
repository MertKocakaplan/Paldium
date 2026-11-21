using Microsoft.Extensions.Localization;
using Paldium.App.Resources;
using Paldium.Core.Data;
using Paldium.Core.Domain;

namespace Paldium.App.Services;

/// <summary>
/// Game terms in the UI language: the game's own text from the data bundle (<c>terms</c>), then the <c>.resx</c>
/// entry <c>Term.&lt;termId&gt;</c> as a fallback, then the term id itself (GLOSSARY.md rule: a term the game has is
/// shown with the game's text).
/// </summary>
public sealed class GameTerms
{
    // Term id of each pal location (LocationKind values without a game term use the .resx entry "Location.<kind>").
    private static readonly IReadOnlyDictionary<LocationKind, string> LocationTerms = new Dictionary<LocationKind, string>
    {
        [LocationKind.Palbox] = "Palbox",
        [LocationKind.ViewingCage] = "ViewingCage",
        [LocationKind.DimensionalStorage] = "DimensionalPalStorage",
        [LocationKind.GlobalStorage] = "GlobalPalbox",
    };

    private readonly Func<IGameData?> _data;
    private readonly LanguageService _language;
    private readonly IStringLocalizer<Strings> _localizer;

    public GameTerms(Func<IGameData?> data, LanguageService language, IStringLocalizer<Strings> localizer)
    {
        _data = data;
        _language = language;
        _localizer = localizer;
    }

    /// <summary>The game term, the <c>Term.&lt;id&gt;</c> resource or the id.</summary>
    public string T(string termId) => TryT(termId) ?? termId;

    /// <summary>The game term or the <c>Term.&lt;id&gt;</c> resource; null when neither exists.</summary>
    public string? TryT(string termId)
    {
        if (_data()?.TryGetTerm(termId, out var text) == true && text.TryGet(_language.Current, out var value))
            return value;

        var resource = _localizer["Term." + termId];
        return resource.ResourceNotFound ? null : resource.Value;
    }

    /// <summary>Name of a pal location: Palbox, Viewing Cage, ... from the game; Party, Base, Unknown from the resources.</summary>
    public string Location(LocationKind kind) =>
        LocationTerms.TryGetValue(kind, out var id) && TryT(id) is { } name ? name : _localizer["Location." + kind].Value;

    /// <summary>Obtain method of <c>obtain.methods</c> in the UI language (game terms where the game has one).</summary>
    public string ObtainMethod(string method) => method switch
    {
        "AlphaBoss" => T("Alpha"),
        "Dungeon" => T("Dungeon"),
        "Raid" => T("Raid"),
        "TowerBoss" => T("TowerBoss"),
        "Fishing" => T("Fishing"),
        _ => _localizer["Obtain.Method." + method] is { ResourceNotFound: false } text ? text.Value : method,
    };

    /// <summary>
    /// The game's full egg name for a species egg (<c>Egg.&lt;element&gt;.&lt;size&gt;</c>, e.g. "Huge Rocky Egg"), or the
    /// plain size word from the resources when the element is unknown or the game has no such egg.
    /// </summary>
    public string EggName(string? element, string size)
    {
        var sizeKey = size is "Large" or "Huge" ? size : "Normal";
        if (element != null && TryT($"Egg.{element}.{sizeKey}") is { } name) return name;
        return _localizer["Egg." + sizeKey].Value;
    }
}
