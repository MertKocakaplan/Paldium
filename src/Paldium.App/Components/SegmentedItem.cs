namespace Paldium.App.Components;

/// <summary>One option rendered by <see cref="Segmented{TValue}"/>.</summary>
public sealed record SegmentedItem<TValue>(TValue Value, string Label, bool IsWord = false, bool Disabled = false);
