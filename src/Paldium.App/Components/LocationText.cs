using Microsoft.Extensions.Localization;
using Paldium.App.Resources;
using Paldium.App.Services;
using Paldium.Core.Domain;

namespace Paldium.App.Components;

/// <summary>
/// The one place that writes where a Pal is (My Pals, plan steps, the owned-target note): "Pal Box page 3 (2,4)",
/// "Dimensional Pal Storage (Alice) page 5 (1,2)", "Base", "Party, slot 2". The page and slot come from <see cref="PalLocation.Page"/> and
/// <see cref="PalLocation.SlotIndex"/>, never from parsing <see cref="PalLocation.ContainerId"/>.
/// </summary>
public static class LocationText
{
    public const int Columns = 6;

    public static string Format(OwnedPal pal, GameTerms terms, IStringLocalizer<Strings> l)
    {
        var text = Place(pal, terms, l);
        // A Pal on an expedition is counted but is not where the save says until it is back (T11).
        return pal.IsOnExpedition ? text + " · " + l["Pal.OnExpedition"].Value : text;
    }

    /// <summary>Compact two-word location for shelf rows and card footers ("Kutu 3", "Alice 1", "Üs", "Takım").</summary>
    public static string ShortPlace(OwnedPal pal, GameTerms terms, IStringLocalizer<Strings> l)
    {
        var loc = pal.Location;
        return loc.Kind switch
        {
            LocationKind.Palbox => loc.Page is { } p
                ? l["Location.ShortPalboxPaged", p + 1].Value
                : l["Location.ShortPalbox"].Value,
            LocationKind.DimensionalStorage => !string.IsNullOrEmpty(loc.BaseName)
                ? (loc.Page is { } p ? $"{loc.BaseName} {p + 1}" : loc.BaseName)
                : (loc.Page is { } p2 ? l["Location.ShortStoragePaged", p2 + 1].Value : l["Location.ShortStorage"].Value),
            LocationKind.GlobalStorage => loc.Page is { } p
                ? l["Location.ShortGlobalPaged", p + 1].Value
                : l["Location.ShortGlobal"].Value,
            LocationKind.Base => !string.IsNullOrEmpty(loc.BaseName) ? loc.BaseName : terms.Location(LocationKind.Base),
            LocationKind.ViewingCage => l["Location.ShortCage"].Value,
            _ => terms.Location(loc.Kind),
        };
    }

    /// <summary>Two-line location for tree nodes and inspection panels ("Pal Kutusu, 1. sayfa" / "3. satır, 3. sütun").</summary>
    public static (string Title, string Subtitle) TwoLine(OwnedPal pal, GameTerms terms, IStringLocalizer<Strings> l)
    {
        var loc = pal.Location;
        var name = ContainerName(pal, terms, l);
        var title = Paldium.Core.Domain.PalLocation.IsPaged(loc.Kind) && loc.Page is { } page
            ? l["Location.BoxPageTitle", name, page + 1].Value
            : name;
        var subtitle = FormatSubtitle(pal, l);
        return (title, subtitle);
    }

    /// <summary>Three-line location for Glance cards ("Pal Kutusu" / "1. sayfa" / "3. satır, 3. sütun").</summary>
    public static (string Container, string Page, string Slot) ThreeLine(OwnedPal pal, GameTerms terms, IStringLocalizer<Strings> l)
    {
        var loc = pal.Location;
        var container = ContainerName(pal, terms, l);
        var pageText = Paldium.Core.Domain.PalLocation.IsPaged(loc.Kind) && loc.Page is { } page
            ? l["Location.PageOnly", page + 1].Value
            : string.Empty;
        var slotText = FormatSubtitle(pal, l);
        return (container, pageText, slotText);
    }

    private static string ContainerName(OwnedPal pal, GameTerms terms, IStringLocalizer<Strings> l)
    {
        var loc = pal.Location;
        var name = terms.Location(loc.Kind);
        if (loc.Kind == LocationKind.DimensionalStorage)
        {
            if (!string.IsNullOrEmpty(loc.BaseName))
                name += $" ({loc.BaseName})";
            else if (pal.Source == PalSourceKind.ModBridge && !string.IsNullOrEmpty(loc.ContainerId))
                name += $" ({OwnedDisplay.BoxLabel(loc.ContainerId, l["Sources.CoverageUnknownOwner"].Value)})";
        }
        else if (loc.Kind == LocationKind.Base && !string.IsNullOrEmpty(loc.BaseName))
        {
            name += $" ({loc.BaseName})";
        }
        return name;
    }

    private static string FormatSubtitle(OwnedPal pal, IStringLocalizer<Strings> l)
    {
        var loc = pal.Location;
        var subtitle = string.Empty;
        if (Paldium.Core.Domain.PalLocation.IsPaged(loc.Kind))
        {
            if (loc.SlotIndex is { } slot and >= 0)
                subtitle = l["Location.RowCol", slot / Columns + 1, slot % Columns + 1].Value;
        }
        else if (loc.SlotIndex is { } other && other >= 0 && loc.Kind != LocationKind.Unknown)
        {
            subtitle = l["Location.SlotSubtitle", other + 1].Value;
        }

        if (pal.IsOnExpedition)
            subtitle = string.IsNullOrEmpty(subtitle)
                ? l["Pal.OnExpedition"].Value
                : $"{subtitle} · {l["Pal.OnExpedition"].Value}";

        return subtitle;
    }

    private static string Place(OwnedPal pal, GameTerms terms, IStringLocalizer<Strings> l)
    {
        var location = pal.Location;
        var name = ContainerName(pal, terms, l);

        if (Paldium.Core.Domain.PalLocation.IsPaged(location.Kind) && location.Page is { } page && location.SlotIndex is { } slot and >= 0)
            return l["Plan.PalboxSlot", name, page + 1, slot % Columns + 1, slot / Columns + 1].Value;

        if (location.SlotIndex is { } other && location.Kind != LocationKind.Unknown)
            return l["Plan.SlotInLocation", name, other + 1].Value;
        return name;
    }
}
