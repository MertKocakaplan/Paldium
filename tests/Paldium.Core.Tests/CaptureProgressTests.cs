using Paldium.Core.Data;
using Paldium.Core.Domain;
using Paldium.Core.Owned;
using Paldium.Core.Queries;

namespace Paldium.Core.Tests;

public class CaptureProgressTests
{
    private static readonly Lazy<GameData> Real = new(() => GameDataLoader.Load(TestPaths.DataFolder));
    private static IGameData Data => Real.Value;

    private static CaptureRecord Record(params (string Id, int Bonus)[] entries) =>
        new(entries.ToDictionary(e => e.Id, e => e.Bonus), entries.ToDictionary(e => e.Id, e => e.Bonus + (e.Bonus == 5 ? 2 : 0)));

    // A record that gives the first species in dex order 5, 4, 3, 2, 1, 0 bonus catches in turn.
    private static CaptureRecord Spread()
    {
        var ids = Data.Pals.OrderBy(p => p.DexNo).ThenBy(p => p.IsVariant).ThenBy(p => p.Id, StringComparer.Ordinal).Select(p => p.Id).ToList();
        return Record(ids.Select((id, i) => (id, 5 - i % 6)).ToArray());
    }

    // ---- the store ---------------------------------------------------------------------------------------------

    [Fact]
    public void Store_WithoutRecords_HasNoData()
    {
        var store = new CaptureProgressStore();
        Assert.Equal(CaptureAvailability.NoData, store.Resolve(null).State);
        Assert.Equal(CaptureAvailability.NoData, store.Resolve("p1").State);
    }

    [Fact]
    public void Store_TheOnlyPlayerIsMe_WhenNobodyWasPicked_ButSeveralPlayersNeedAChoice()
    {
        var store = new CaptureProgressStore();
        store.Publish("a", [new PlayerCapture("P1", Record(("Sheepball", 5)), DateTimeOffset.UnixEpoch)]);
        Assert.Equal(CaptureAvailability.Available, store.Resolve(null).State);

        store.Publish("b", [new PlayerCapture("P2", Record(("Sheepball", 1)), DateTimeOffset.UnixEpoch)]);
        Assert.Equal(CaptureAvailability.NoPlayer, store.Resolve(null).State);
        Assert.Equal(1, store.Resolve("p2").Record!.BonusOf("Sheepball"));
        Assert.Equal(CaptureAvailability.NoData, store.Resolve("P3").State);
    }

    [Fact]
    public void Store_IdsAreComparedWithoutDashesAndCase_AndTheNewerSourceWins()
    {
        var store = new CaptureProgressStore();
        var older = DateTimeOffset.UnixEpoch;
        store.Publish("save", [new PlayerCapture("AABBCCDD-0000-0000-0000-000000000001", Record(("Sheepball", 3)), older)]);
        store.Publish("mod", [new PlayerCapture("aabbccdd000000000000000000000001", Record(("Sheepball", 4)), older.AddHours(1))]);

        Assert.Equal(4, store.Resolve("AABBCCDD-0000-0000-0000-000000000001").Record!.BonusOf("Sheepball"));

        store.Remove("mod");
        Assert.Equal(3, store.Resolve(null).Record!.BonusOf("Sheepball"));
    }

    [Fact]
    public void Store_RaisesChanged_OnPublishAndOnlyForRealRemovals()
    {
        var store = new CaptureProgressStore();
        var raised = 0;
        store.Changed += (_, _) => raised++;
        store.Publish("a", [new PlayerCapture("P1", Record(("Sheepball", 5)), DateTimeOffset.UnixEpoch)]);
        store.Remove("nothing");
        store.Remove("a");
        Assert.Equal(2, raised);
    }

    // ---- filter and sort ---------------------------------------------------------------------------------------

    [Fact]
    public void CaptureFilter_CountsAddUp_AndOpenIsStartedPlusNever()
    {
        var record = Spread();
        int Count(PalCaptureFilter f) => PalQuery.Run(Data, new PalFilter { Capture = f }, new PalSort(), "en", null, record).Count;

        Assert.Equal(Data.Pals.Count, Count(PalCaptureFilter.Any));
        Assert.Equal(Count(PalCaptureFilter.Open), Count(PalCaptureFilter.Started) + Count(PalCaptureFilter.None));
        Assert.Equal(Data.Pals.Count, Count(PalCaptureFilter.Open) + Count(PalCaptureFilter.Done));
        Assert.All(PalQuery.Run(Data, new PalFilter { Capture = PalCaptureFilter.Done }, new PalSort(), "en", null, record), p => Assert.Equal(5, record.BonusOf(p.Id)));
        Assert.All(PalQuery.Run(Data, new PalFilter { Capture = PalCaptureFilter.None }, new PalSort(), "en", null, record), p => Assert.Equal(0, record.BonusOf(p.Id)));
        Assert.False(new PalFilter { Capture = PalCaptureFilter.Done }.IsEmpty);
    }

    [Fact]
    public void CaptureFilter_WithoutARecord_DoesNothing()
    {
        var all = PalQuery.Run(Data, new PalFilter { Capture = PalCaptureFilter.Done }, new PalSort(), "en", null, null);
        Assert.Equal(Data.Pals.Count, all.Count);
    }

    [Fact]
    public void CaptureSort_GoesBothWays_TiesByDexNumber_AndFallsBackWithoutARecord()
    {
        var record = Spread();
        var up = PalQuery.Run(Data, new PalFilter(), new PalSort { Key = PalSortKey.CaptureBonus }, "en", null, record);
        var down = PalQuery.Run(Data, new PalFilter(), new PalSort { Key = PalSortKey.CaptureBonus, Descending = true }, "en", null, record);

        Assert.Equal(0, record.BonusOf(up[0].Id));
        Assert.Equal(5, record.BonusOf(up[^1].Id));
        Assert.Equal(5, record.BonusOf(down[0].Id));
        Assert.True(up.Zip(up.Skip(1)).All(p => record.BonusOf(p.First.Id) < record.BonusOf(p.Second.Id) || (record.BonusOf(p.First.Id) == record.BonusOf(p.Second.Id) && p.First.DexNo <= p.Second.DexNo)));

        var fallback = PalQuery.Run(Data, new PalFilter(), new PalSort { Key = PalSortKey.CaptureBonus }, "en", null, null);
        Assert.Equal(Data.Pals.OrderBy(p => p.DexNo).ThenBy(p => p.IsVariant).ThenBy(p => p.Id, StringComparer.Ordinal).Select(p => p.Id), fallback.Select(p => p.Id));
    }

    [Fact]
    public void UncapturableSpecies_StayLast_AndNeverMatchAFilter()
    {
        var odd = Data.Pals[0] with { IsUncapturable = true };
        var pals = new[] { odd }.Concat(Data.Pals.Skip(1).Take(5)).ToList();
        var record = Record((Data.Pals[1].Id, 2), (odd.Id, 0));

        foreach (var descending in new[] { false, true })
        {
            var sorted = PalQuery.Sort(pals, new PalSort { Key = PalSortKey.CaptureBonus, Descending = descending }, "en", null, record);
            Assert.Equal(odd.Id, sorted[^1].Id);
        }

        Assert.False(PalQuery.MatchesCapture(odd, PalCaptureFilter.None, record));
        Assert.False(PalQuery.MatchesCapture(odd, PalCaptureFilter.Open, record));
        Assert.True(PalQuery.MatchesCapture(odd, PalCaptureFilter.Any, record));
    }
}
