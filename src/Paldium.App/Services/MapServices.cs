using System.IO;
using Microsoft.Extensions.Logging;
using Paldium.Core.Map;

namespace Paldium.App.Services;

/// <summary>The markers of the Map page (<c>data/map/map-points.json</c>), read once when the page is first opened (T138).</summary>
public sealed class MapDataService(string dataFolder, string pictureFolder, ILogger<MapDataService>? log = null)
{
    private readonly object _gate = new();
    private Task<MapData?>? _load;

    /// <summary>Null when the file is missing or broken (the page shows a notice and keeps the picture and the Pal search).</summary>
    public Task<MapData?> LoadAsync()
    {
        lock (_gate)
        {
            // A failed read is tried again the next time the page opens.
            if (_load == null || (_load.IsCompletedSuccessfully && _load.Result == null)) _load = Task.Run(Read);
            return _load;
        }
    }

    private MapData? Read()
    {
        if (MapDataLoader.TryLoad(dataFolder, out var data, out var error)) return data;
        log?.LogWarning("{Error}", error);
        return null;
    }

    /// <summary>True when the map picture is among the app's web files (<c>wwwroot/gamedata/map</c>).</summary>
    public bool HasPicture(string file) => File.Exists(Path.Combine(pictureFolder, file));
}

/// <summary>What the Map page keeps while the app is open: the search text, the open card, the quest filter and the folded groups.</summary>
public sealed class MapStateService
{
    public string Query { get; set; } = "";
    public string? SelectedPoint { get; set; }

    /// <summary>The open quest card (T145), shown instead of the marker card; <see cref="QuestFrom"/> is the marker card it was opened from.</summary>
    public string? SelectedQuest { get; set; }
    public string? QuestFrom { get; set; }

    public string QuestFilter { get; set; } = "";

    public bool EffigyTypesOpen { get; set; }
    public bool PredatorTypesOpen { get; set; }

    /// <summary>The state filter of the Quests tab (T150), kept as its enum name.</summary>
    public string QuestShow { get; set; } = "All";

    public HashSet<string> FoldedGroups { get; } = new(StringComparer.Ordinal) { "Items", "Quests", "Materials" };
}
