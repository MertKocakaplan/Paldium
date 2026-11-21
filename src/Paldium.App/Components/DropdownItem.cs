namespace Paldium.App.Components;

/// <summary>An option row for <see cref="Dropdown{TValue}"/>.</summary>
public sealed record DropdownItem<TValue>(TValue Value, string Label, string? Meta = null, bool Disabled = false);
