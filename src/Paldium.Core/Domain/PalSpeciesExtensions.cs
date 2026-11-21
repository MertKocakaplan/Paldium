namespace Paldium.Core.Domain;

public static class PalSpeciesExtensions
{
    /// <summary>
    /// Display name for lists and pickers. Variants always carry their dex label, e.g. "Gumoss (12B)",
    /// because variants can share the English name of the base species.
    /// </summary>
    public static string DisplayName(this PalSpecies pal, string lang)
    {
        var name = pal.Names.Get(lang);
        return pal.IsVariant ? $"{name} ({pal.DexLabel})" : name;
    }
}
