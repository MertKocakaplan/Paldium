namespace Paldium.App.Components;

/// <summary>
/// A page parameter that is applied once per value (<c>?species=</c>, <c>?passive=</c>): Blazor calls OnParametersSet again on every render of the
/// parent, and a filter the user cleared must not come back with it.
/// </summary>
public sealed class OnceParameter
{
    private string? _handled;

    /// <summary>True the first time <paramref name="value"/> is seen (also when it changes later); false while it stays the same.</summary>
    public bool IsNew(string? value)
    {
        if (string.Equals(value, _handled, StringComparison.Ordinal)) return false;
        _handled = value;
        return true;
    }
}
