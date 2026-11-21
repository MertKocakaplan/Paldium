namespace Paldium.App.Components;

/// <summary>One filter-removal row in <see cref="EmptyResult"/>.</summary>
public sealed record EmptyRemovalItem(string Key, string FilterLabel, string RemainingText, int RemainingCount);
