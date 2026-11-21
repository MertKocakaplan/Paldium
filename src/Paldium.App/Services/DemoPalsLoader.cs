#if DEBUG
using System.Globalization;
using System.IO;
using System.Text.Json;
using Paldium.Core.Data;
using Paldium.Core.Domain;
using Paldium.Core.Owned;
using Paldium.Import.Sync;

namespace Paldium.App.Services;

/// <summary>Debug-only loader for <c>--demo-pals &lt;path&gt;</c> (IMPLEMENTATION_ORDER verification).</summary>
internal sealed class DemoSyncService : ISyncService
{
    public const string DemoSourceId = "demo";

    private readonly string _path;
    private readonly AppState _state;
    private readonly IOwnedPalRepository _repository;
    private readonly CaptureProgressStore? _captures;
    private readonly object _gate = new();
    private List<SourceStatus> _statuses = [];

    public DemoSyncService(string path, AppState state, IOwnedPalRepository repository, CaptureProgressStore? captures = null)
    {
        _captures = captures;
        _path = path;
        _state = state;
        _repository = repository;
    }

    public IReadOnlyList<SourceStatus> Statuses
    {
        get { lock (_gate) return [.. _statuses]; }
    }

    public event EventHandler? StatusesChanged;

    public void LoadInitial()
    {
        if (!_state.IsReady || _state.Data == null) return;
        Apply(_path, _state.Data, _repository, out var status);
        PublishDemoCapture(_state.Data);
        lock (_gate) _statuses = [status];
        StatusesChanged?.Invoke(this, EventArgs.Empty);
    }

    public Task RefreshNowAsync(string? sourceId = null)
    {
        LoadInitial();
        return Task.CompletedTask;
    }

    public void ApplyConfig(IReadOnlyList<SourceConfig> sources)
    {
        // Demo mode ignores persisted sources so user data stays untouched.
    }

    public void Dispose()
    {
    }

    // Made-up capture progress for screenshots (the real record comes from a save file or the mod): the species in dex order get 5, 4, 3, 2, 1, 0 bonus catches in turn.
    private void PublishDemoCapture(IGameData data)
    {
        if (_captures == null) return;
        var bonus = new Dictionary<string, int>(StringComparer.Ordinal);
        var count = new Dictionary<string, int>(StringComparer.Ordinal);
        var i = 0;
        foreach (var pal in data.Pals.Where(p => !p.IsUncapturable).OrderBy(p => p.DexNo).ThenBy(p => p.IsVariant).ThenBy(p => p.Id, StringComparer.Ordinal))
        {
            var b = 5 - (i++ % 6);
            if (b > 0) { bonus[pal.Id] = b; count[pal.Id] = b + (b == 5 ? 2 : 0); }
        }

        // Made-up effigies as well (T133): a rank in progress, a finished type, an untouched one, one with enough in hand for the next rank, and Mimog.
        var relics = new Dictionary<string, RelicCount>(StringComparer.Ordinal)
        {
            ["CapturePower"] = new(2, 66), ["HungerReduction"] = new(6, 26), ["SwimSpeed"] = new(3, 14), ["JumpPower"] = new(0, 7),
            ["SphereHoming"] = new(2, 3), ["MoveSpeed"] = new(1, null),
        };
        _captures.Publish(DemoSourceId, [new PlayerCapture("demo-player", new CaptureRecord(bonus, count) { Relics = relics }, DateTimeOffset.UtcNow)]);
    }

    private static void Apply(string rawPath, IGameData data, IOwnedPalRepository repository, out SourceStatus status)
    {
        var path = ResolvePath(rawPath);
        var loadedAt = DateTimeOffset.UtcNow;
        try
        {
            var json = File.ReadAllText(path);
            var doc = JsonSerializer.Deserialize<DemoFile>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                      ?? throw new InvalidDataException($"Demo Pal file is empty: {path}");

            var pals = new List<OwnedPal>(doc.Pals.Count);
            for (var i = 0; i < doc.Pals.Count; i++)
            {
                var entry = doc.Pals[i];
                var speciesId = data.Pal(entry.SpeciesId).Id;
                Gender? gender = entry.Gender?.ToLowerInvariant() switch
                {
                    "male" => Gender.Male,
                    "female" => Gender.Female,
                    _ => null,
                };
                var passives = (entry.Passives ?? []).Select(p => data.Passive(p).Id).Take(OwnedPalRepository.MaxPassives).ToList();
                var ivs = entry.Iv is { } iv ? new Ivs(Math.Clamp(iv.Hp, 0, 100), Math.Clamp(iv.Attack, 0, 100), Math.Clamp(iv.Defense, 0, 100)) : null;

                var locKind = Enum.TryParse<LocationKind>(entry.Location?.Kind, true, out var k) ? k : LocationKind.Palbox;
                var page = entry.Location?.Page is { } p ? Math.Max(0, p - 1) : (int?)null;
                // The mockup data writes an unknown owner as a dash: such a Pal has no owner.
                var ownerName = entry.Owner is null or "" or "—" ? null : entry.Owner;
                var baseName = entry.Location?.BaseName ?? (locKind == LocationKind.DimensionalStorage ? ownerName : null);
                var containerId = locKind == LocationKind.DimensionalStorage && !string.IsNullOrEmpty(ownerName) ? "name:" + ownerName : null;
                var location = new PalLocation(locKind, containerId, entry.Location?.Slot, null, page, baseName);

                var owner = !string.IsNullOrEmpty(ownerName) ? new PalOwner(ownerName, ownerName, null, null) : null;
                var source = Enum.TryParse<PalSourceKind>(entry.Source, true, out var src) ? src : PalSourceKind.Manual;

                pals.Add(new OwnedPal(
                    Uid: $"demo-{(i + 1).ToString(CultureInfo.InvariantCulture)}",
                    InstanceId: null,
                    SpeciesId: speciesId,
                    Gender: gender,
                    Level: Math.Clamp(entry.Level, 1, OwnedPalRepository.MaxLevel),
                    Rank: Math.Clamp(entry.CondenseRank + 1, 1, OwnedPalRepository.MaxRank),
                    Passives: passives,
                    Ivs: ivs,
                    EquippedAttacks: [],
                    LearnedAttacks: [],
                    Nickname: string.IsNullOrWhiteSpace(entry.Nickname) ? null : entry.Nickname,
                    Owner: owner,
                    Location: location,
                    Source: source,
                    SourceId: DemoSourceId,
                    SeenAtUtc: loadedAt.AddMinutes(-2),
                    IsEdited: entry.Edited,
                    IsExcluded: !entry.UseInPlanning,
                    IsOnExpedition: entry.OnExpedition));
            }

            status = new SourceStatus(DemoSourceId, SourceState.Ok, loadedAt.AddMinutes(-2), pals.Count, null);
            repository.PublishSourceSnapshot(DemoSourceId, pals, status);
        }
        catch (Exception ex)
        {
            status = new SourceStatus(DemoSourceId, SourceState.Error, null, 0, ex.Message);
            repository.SetSourceStatus(DemoSourceId, status);
        }
    }

    private static string ResolvePath(string path)
    {
        if (Path.IsPathRooted(path) || File.Exists(path)) return Path.GetFullPath(path);
        var fromBase = Path.Combine(AppContext.BaseDirectory, path);
        if (File.Exists(fromBase)) return fromBase;
        return Path.GetFullPath(path);
    }

    private sealed class DemoFile
    {
        public List<DemoPal> Pals { get; set; } = [];
    }

    private sealed class DemoPal
    {
        public string SpeciesId { get; set; } = "";
        public string? Gender { get; set; }
        public int Level { get; set; } = 1;
        public int CondenseRank { get; set; }
        public List<string>? Passives { get; set; }
        public DemoIv? Iv { get; set; }
        public DemoLocation? Location { get; set; }
        public string? Owner { get; set; }
        public string? Source { get; set; }
        public string? Nickname { get; set; }
        public bool Edited { get; set; }
        public bool OnExpedition { get; set; }
        public bool UseInPlanning { get; set; } = true;
    }

    private sealed class DemoIv
    {
        public int Hp { get; set; }
        public int Attack { get; set; }
        public int Defense { get; set; }
    }

    private sealed class DemoLocation
    {
        public string? Kind { get; set; }
        public int? Page { get; set; }
        public int? Slot { get; set; }
        public string? BaseName { get; set; }
    }
}
#endif
