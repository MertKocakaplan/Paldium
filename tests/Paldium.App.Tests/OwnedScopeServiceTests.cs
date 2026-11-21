using Paldium.App.Services;
using Paldium.Core.Data;
using Paldium.Core.Domain;
using Paldium.Core.Owned;

namespace Paldium.App.Tests;

public class OwnedScopeServiceTests : IDisposable
{
    private static readonly Lazy<GameData> Data = new(() =>
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Paldium.sln"))) dir = dir.Parent;
        return GameDataLoader.Load(Path.Combine(dir!.FullName, "data"));
    });

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pb-scope-" + Guid.NewGuid().ToString("N"));
    private readonly OwnedPalRepository _repo = new(Data.Value);
    private readonly SettingsStore _settings;
    private readonly OwnedScopeService _scope;

    public OwnedScopeServiceTests()
    {
        _settings = new SettingsStore(Path.Combine(_dir, "settings.json"));
        _scope = new OwnedScopeService(_repo, _settings);
    }

    public void Dispose()
    {
        _scope.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static OwnedPal MakePal(string uid, string owner, LocationKind location) =>
        new(uid, null, "CowPal", Gender.Female, 10, 1, [], null, [], [], null,
            new PalOwner(owner, "Player " + owner, null, null), new PalLocation(location, null, null, null),
            PalSourceKind.Save, "world", DateTimeOffset.UnixEpoch, false, false);

    private void Publish(params OwnedPal[] pals) =>
        _repo.PublishSourceSnapshot("world", pals, new SourceStatus("world", SourceState.Ok, null, pals.Length, null));

    [Fact]
    public void Mode_Me_WithoutPlayerId_BehavesLikeAll()
    {
        Assert.Equal(ScopeSettings.Me, _settings.Current.Scope.Mode);
        Assert.Null(_scope.Scope.PlayerIds);

        Publish(MakePal("a", "p1", LocationKind.Palbox), MakePal("b", "p2", LocationKind.Palbox));
        Assert.Equal(2, _scope.ScopedPals.Count);
    }

    [Fact]
    public void Mode_Me_WithPlayerId_OnlyCountsThatPlayer()
    {
        Publish(MakePal("a", "p1", LocationKind.Palbox), MakePal("b", "p2", LocationKind.Palbox));
        _settings.Update(s => s with { Scope = s.Scope with { MyPlayerId = "p2" } });

        Assert.Equal(["b"], _scope.ScopedPals.Select(p => p.Uid));
        Assert.True(_scope.IsInScope(_repo.Current.Pals.Single(p => p.Uid == "b")));
        Assert.False(_scope.IsInScope(_repo.Current.Pals.Single(p => p.Uid == "a")));
    }

    [Fact]
    public void Mode_Selected_UsesTheSelectedPlayers_AndAllIgnoresPlayers()
    {
        Publish(MakePal("a", "p1", LocationKind.Palbox), MakePal("b", "p2", LocationKind.Palbox), MakePal("c", "p3", LocationKind.Palbox));
        _settings.Update(s => s with { Scope = s.Scope with { Mode = ScopeSettings.Selected, SelectedPlayerIds = ["p1", "P3"] } });
        Assert.Equal(["a", "c"], _scope.ScopedPals.Select(p => p.Uid).Order());

        _settings.Update(s => s with { Scope = s.Scope with { Mode = ScopeSettings.All } });
        Assert.Equal(3, _scope.ScopedPals.Count);
    }

    [Fact]
    public void LocationKinds_LimitTheScope()
    {
        Publish(MakePal("a", "p1", LocationKind.Palbox), MakePal("b", "p1", LocationKind.Base), MakePal("c", "p1", LocationKind.GlobalStorage));
        _settings.Update(s => s with { Scope = s.Scope with { Locations = [LocationKind.Palbox, LocationKind.GlobalStorage] } });
        Assert.Equal(["a", "c"], _scope.ScopedPals.Select(p => p.Uid).Order());
    }

    [Fact]
    public void ExcludedPals_DoNotCount()
    {
        Publish(MakePal("a", "p1", LocationKind.Palbox), MakePal("b", "p1", LocationKind.Palbox));
        _repo.SetOverride("a", new PalOverride { IsExcluded = true });
        Assert.Equal(["b"], _scope.ScopedPals.Select(p => p.Uid));
    }

    [Fact]
    public void Changed_IsRaisedForRepositoryAndSettingsChanges_AndCacheFollows()
    {
        var raised = 0;
        _scope.Changed += (_, _) => raised++;

        Publish(MakePal("a", "p1", LocationKind.Palbox));
        var first = _scope.ScopedPals;
        Assert.Same(first, _scope.ScopedPals); // cached while nothing changes

        _settings.Update(s => s with { Scope = s.Scope with { Mode = ScopeSettings.All } });
        Publish(MakePal("a", "p1", LocationKind.Palbox), MakePal("b", "p1", LocationKind.Palbox));

        Assert.Equal(3, raised);
        Assert.Equal(2, _scope.ScopedPals.Count);
    }

    [Fact]
    public void ScopeSettings_SurviveARoundTripThroughTheSettingsFile()
    {
        _settings.Update(s => s with
        {
            Scope = new ScopeSettings
            {
                Mode = ScopeSettings.Selected,
                MyPlayerId = "me-1",
                SelectedPlayerIds = ["x", "y"],
                Locations = [LocationKind.Party, LocationKind.Base],
            },
        });

        var loaded = new SettingsStore(Path.Combine(_dir, "settings.json")).Current.Scope;
        Assert.Equal(ScopeSettings.Selected, loaded.Mode);
        Assert.Equal("me-1", loaded.MyPlayerId);
        Assert.Equal(["x", "y"], loaded.SelectedPlayerIds);
        Assert.Equal([LocationKind.Party, LocationKind.Base], loaded.Locations);
    }

    [Fact]
    public void SharedLocations_CountForMe_WhoeverOwnsThem_UnlessSwitchedOff()
    {
        Publish(MakePal("mine", "p1", LocationKind.Palbox), MakePal("hostPalbox", "p2", LocationKind.Palbox),
            MakePal("hostDps", "p2", LocationKind.DimensionalStorage), MakePal("hostBase", "p2", LocationKind.Base));
        _settings.Update(s => s with { Scope = s.Scope with { Mode = ScopeSettings.Me, MyPlayerId = "p1" } });

        Assert.Equal(["hostBase", "hostDps", "mine"], _scope.ScopedPals.Select(p => p.Uid).Order());

        _settings.Update(s => s with { Scope = s.Scope with { ShareGuildLocations = false } });
        Assert.Equal(["mine"], _scope.ScopedPals.Select(p => p.Uid));
    }}
