using Paldium.Core.Domain;
using Paldium.Core.Owned;

namespace Paldium.App.Services;

/// <summary>
/// Turns the ownership-scope settings and the owned-Pal repository into "the Pals that count for planning".
/// Raises <see cref="Changed"/> when either the Pals or the settings change; the event can come from any thread.
/// </summary>
public sealed class OwnedScopeService : IDisposable
{
    private readonly IOwnedPalRepository _repository;
    private readonly SettingsStore _settings;
    private readonly object _gate = new();
    private (long Version, ScopeSettings Scope)? _cacheKey;
    private IReadOnlyList<OwnedPal> _cache = [];

    public OwnedScopeService(IOwnedPalRepository repository, SettingsStore settings)
    {
        _repository = repository;
        _settings = settings;
        _repository.Changed += OnRepositoryChanged;
        _settings.Changed += OnSettingsChanged;
    }

    public event EventHandler? Changed;

    /// <summary>The current scope (owners and location kinds that count; excluded Pals do not count).</summary>
    public OwnerScope Scope => ToScope(_settings.Current.Scope);

    /// <summary>Pals that count for planning right now.</summary>
    public IReadOnlyList<OwnedPal> ScopedPals
    {
        get
        {
            var snapshot = _repository.Current;
            var settings = _settings.Current.Scope;
            lock (_gate)
            {
                if (_cacheKey is { } key && key.Version == snapshot.Version && ReferenceEquals(key.Scope, settings))
                    return _cache;
                _cache = OwnedPalRepository.Filter(snapshot, ToScope(settings));
                _cacheKey = (snapshot.Version, settings);
                return _cache;
            }
        }
    }

    /// <summary>True when the Pal is inside the ownership scope (ignoring the exclusion switch).</summary>
    public bool IsInScope(OwnedPal pal)
    {
        var scope = ToScope(_settings.Current.Scope);
        return scope.IncludedLocations.Contains(pal.Location.Kind) &&
               (scope.PlayerIds == null || pal.Owner == null ||
                scope.PlayerIds.Contains(pal.Owner.PlayerId, StringComparer.OrdinalIgnoreCase) ||
                (scope.OwnerFreeLocations?.Contains(pal.Location.Kind) ?? false));
    }

    /// <summary>Locations the whole guild shares: their Pals count for "me" whoever owns them (ARCHITECTURE 6.2).</summary>
    public static IReadOnlyList<LocationKind> SharedLocations { get; } = [LocationKind.Base, LocationKind.DimensionalStorage];

    public static OwnerScope ToScope(ScopeSettings s)
    {
        IReadOnlyList<string>? players = s.Mode switch
        {
            ScopeSettings.Selected => s.SelectedPlayerIds,
            ScopeSettings.Me when !string.IsNullOrEmpty(s.MyPlayerId) => [s.MyPlayerId],
            _ => null,
        };
        return new OwnerScope(players, s.Locations, OwnerFreeLocations: s.ShareGuildLocations ? SharedLocations : null);
    }

    private void OnRepositoryChanged(object? sender, OwnedPalSnapshot snapshot) => Changed?.Invoke(this, EventArgs.Empty);

    private void OnSettingsChanged(object? sender, AppSettings settings) => Changed?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        _repository.Changed -= OnRepositoryChanged;
        _settings.Changed -= OnSettingsChanged;
    }
}
