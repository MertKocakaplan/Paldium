using Paldium.App.Services;
using Paldium.Core.Domain;
using Paldium.Core.Owned;

namespace Paldium.App.Tests;

public class PreferredPalsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pb-pref-" + Guid.NewGuid().ToString("N"));
    private readonly OwnedPalRepository _repo = new(PlannerStateServiceTests.GameDataForTests);

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }

    private static OwnedPal Pal(string uid) =>
        new(uid, null, "Anubis", Gender.Male, 10, 1, [], null, [], [], null, null, new PalLocation(LocationKind.Palbox, null, null, null),
            PalSourceKind.Manual, "manual", DateTimeOffset.UnixEpoch, false, false);

    [Fact]
    public void Toggle_AddsAndRemoves_AndNeverTwice()
    {
        var store = new SettingsStore(Path.Combine(_dir, "settings.json"));
        var preferred = new PreferredPals(store, _repo);
        var pal = Pal("p1");

        Assert.False(preferred.IsPreferred(pal));
        Assert.True(preferred.Toggle(pal));
        Assert.True(preferred.IsPreferred(pal));
        Assert.Single(store.Current.Planner.PreferredUids);

        Assert.False(preferred.Toggle(pal));
        Assert.Empty(store.Current.Planner.PreferredUids);

        // A list that already holds the key twice ends up without it after one removal.
        store.Update(s => s with { Planner = s.Planner with { PreferredUids = [pal.SelectionKey, pal.SelectionKey] } });
        Assert.False(preferred.Toggle(pal));
        Assert.Empty(store.Current.Planner.PreferredUids);
    }
}
