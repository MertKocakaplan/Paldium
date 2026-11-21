namespace Paldium.App.Services;

/// <summary>Widths of the resizable left panels (shelf, filters) per page, kept in the settings (T102).</summary>
public sealed class PanelWidths(SettingsStore store)
{
    public const int Default = 288;
    public const int Min = 200;
    public const int Max = 480;

    /// <summary>The content to the right of the panel keeps at least this many pixels.</summary>
    public const int MinContent = 480;

    public int Get(string key) =>
        store.Current.PanelWidths.TryGetValue(key, out var width) ? Clamp(width) : Default;

    /// <summary>Stores the width (clamped to <see cref="Min"/>..<see cref="Max"/>) and returns what was stored.</summary>
    public int Set(string key, int width)
    {
        var clamped = Clamp(width);
        store.Update(s => s with
        {
            PanelWidths = new Dictionary<string, int>(s.PanelWidths) { [key] = clamped },
        });
        return clamped;
    }

    public static int Clamp(int width) => Math.Clamp(width, Min, Max);
}
