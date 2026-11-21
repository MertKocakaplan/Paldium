namespace Paldium.App.Components;

/// <summary>The URLs the clickable chips and badges go to (T66).</summary>
public static class Links
{
    /// <summary>The Paldex with the detail drawer of a species open.</summary>
    public static string Pal(string palId) => "paldex?pal=" + Uri.EscapeDataString(palId);

    /// <summary>My Pals with the detail panel of an owned Pal open.</summary>
    public static string OwnedPal(string selectionKey) => "mypals?pal=" + Uri.EscapeDataString(selectionKey);

    /// <summary>Skills, Passives tab, opened at one passive.</summary>
    public static string Passive(string passiveId) => "skills?passive=" + Uri.EscapeDataString(passiveId);

    /// <summary>The Calculator, Pair to Child tab, with the two parents chosen.</summary>
    public static string CalculatorPair(string parentA, string parentB) =>
        $"calculator?a={Uri.EscapeDataString(parentA)}&b={Uri.EscapeDataString(parentB)}";
}
