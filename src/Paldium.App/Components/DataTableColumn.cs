namespace Paldium.App.Components;

/// <summary>Column definition for <see cref="DataTable{TItem}"/>.</summary>
public sealed record DataTableColumn(
    string Label,
    string ColClass,
    string? SortKey = null,
    string Align = "l",
    bool HiddenHeader = false,
    string? IconSrc = null,
    string? Tip = null);
