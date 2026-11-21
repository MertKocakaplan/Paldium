using System.Globalization;
using Paldium.Core.Data;
using Paldium.Core.Domain;
using Paldium.Core.Owned;

namespace Paldium.Core.Tests;

public class OwnedPalRepositoryTests : IDisposable
{
    private static readonly Lazy<GameData> Real = new(() => GameDataLoader.Load(TestPaths.DataFolder));
    private static readonly DateTimeOffset T0 = new(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pb-owned-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _warnings = [];

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static IGameData Data => Real.Value;

    private OwnedPalRepository NewRepo(bool persist = true) =>
        new(Data, persist ? new UserDataStore(_dir, (m, _) => _warnings.Add(m)) : null, () => T0);

    private static OwnedPal MakePal(
        string uid, string? instanceId = null, string species = "CowPal", DateTimeOffset? seen = null,
        string[]? passives = null, string? owner = "p1", LocationKind location = LocationKind.Palbox,
        PalSourceKind source = PalSourceKind.Save, string sourceId = "world1") =>
        new(uid, instanceId, species, Gender.Female, 20, 1, passives ?? [], null, [], [], null,
            owner == null ? null : new PalOwner(owner, "Player " + owner, null, null),
            new PalLocation(location, "c1", 0, null), source, sourceId, seen ?? T0, false, false);

    private static SourceStatus Ok(string id, int count) => new(id, SourceState.Ok, T0, count, null);

    private static OwnedPalDraft Draft(string species = "CowPal", params string[] passives) =>
        new() { SpeciesId = species, Gender = Gender.Male, Level = 12, Rank = 2, Passives = passives };

    // ---- manual pals and persistence -------------------------------------------------------------------------

    [Fact]
    public void Manual_AddUpdateRemove_ArePersisted()
    {
        var repo = NewRepo();
        var added = repo.AddManual(Draft("CowPal", "CraftSpeed_up3", "Rare"));
        Assert.StartsWith("manual:", added.Uid);
        Assert.Equal(PalSourceKind.Manual, added.Source);
        Assert.Null(added.InstanceId);

        var reloaded = NewRepo();
        var loaded = Assert.Single(reloaded.Current.Pals);
        Assert.Equal(added.Uid, loaded.Uid);
        Assert.Equal(added.SpeciesId, loaded.SpeciesId);
        Assert.Equal(added.Gender, loaded.Gender);
        Assert.Equal(added.Level, loaded.Level);
        Assert.Equal(added.Rank, loaded.Rank);
        Assert.Equal(added.Passives, loaded.Passives);
        Assert.Equal(added.SeenAtUtc, loaded.SeenAtUtc);
        Assert.Equal(PalSourceKind.Manual, loaded.Source);

        reloaded.UpdateManual(added.Uid, Draft("Sekhmet", "Rare") with { Level = 30, Nickname = " Hammer " });
        var third = NewRepo();
        var updated = Assert.Single(third.Current.Pals);
        Assert.Equal("Sekhmet", updated.SpeciesId);
        Assert.Equal(30, updated.Level);
        Assert.Equal("Hammer", updated.Nickname);
        Assert.Equal(added.Uid, updated.Uid);

        third.RemoveManual(added.Uid);
        Assert.Empty(NewRepo().Current.Pals);
    }

    [Fact]
    public void Persistence_UsesAtomicWritesWithoutTempFiles()
    {
        var repo = NewRepo();
        repo.AddManual(Draft());
        repo.SetOverride("save:w:x", new PalOverride { Level = 5 });

        Assert.True(File.Exists(Path.Combine(_dir, "manual-pals.json")));
        Assert.True(File.Exists(Path.Combine(_dir, "overrides.json")));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));

        // Computed properties are not part of the file format.
        var overrides = File.ReadAllText(Path.Combine(_dir, "overrides.json"));
        Assert.DoesNotContain("hasCorrections", overrides);
        Assert.DoesNotContain("isEmpty", overrides);
    }

    [Fact]
    public void CorruptUserFiles_AreBackedUp_AndStartEmpty()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "manual-pals.json"), "{ nope");
        File.WriteAllText(Path.Combine(_dir, "overrides.json"), "{ \"schemaVersion\": 99, \"overrides\": {} }");

        var repo = NewRepo();

        Assert.Empty(repo.Current.Pals);
        Assert.Empty(repo.Current.Overrides);
        Assert.True(File.Exists(Path.Combine(_dir, "manual-pals.json.bak")));
        Assert.True(File.Exists(Path.Combine(_dir, "overrides.json.bak")));
        Assert.Equal(2, _warnings.Count);
    }

    // ---- validation ------------------------------------------------------------------------------------------

    [Fact]
    public void Validation_RejectsBadDrafts_WithLocalizableKeys()
    {
        var repo = NewRepo(persist: false);

        string KeyOf(OwnedPalDraft draft) => Assert.Throws<OwnedPalValidationException>(() => repo.AddManual(draft)).Key;

        Assert.Equal(OwnedPalValidationException.UnknownSpecies, KeyOf(Draft("NoSuchPal")));
        Assert.Equal(OwnedPalValidationException.UnknownPassive, KeyOf(Draft("CowPal", "NoSuchPassive")));
        Assert.Equal(OwnedPalValidationException.TooManyPassives,
            KeyOf(Draft("CowPal", "CraftSpeed_up1", "CraftSpeed_up2", "CraftSpeed_up3", "Rare", "Legend")));
        Assert.Equal(OwnedPalValidationException.DuplicatePassive, KeyOf(Draft("CowPal", "Rare", "rare")));
        Assert.Equal(OwnedPalValidationException.IvRange, KeyOf(Draft() with { Ivs = new Ivs(101, 50, 50) }));
        Assert.Equal(OwnedPalValidationException.IvRange, KeyOf(Draft() with { Ivs = new Ivs(50, -1, 50) }));
        Assert.Equal(OwnedPalValidationException.LevelRange, KeyOf(Draft() with { Level = 0 }));
        Assert.Equal(OwnedPalValidationException.RankRange, KeyOf(Draft() with { Rank = 6 }));
        Assert.Empty(repo.Current.Pals);
    }

    [Fact]
    public void Validation_AcceptsFourPassives_AndNormalizesIds()
    {
        var repo = NewRepo(persist: false);
        var pal = repo.AddManual(Draft("cowpal", "craftspeed_up3", "RARE", "Legend", "Alien"));
        Assert.Equal("CowPal", pal.SpeciesId);
        Assert.Equal(["CraftSpeed_up3", "Rare", "Legend", "Alien"], pal.Passives);
    }

    [Fact]
    public void Update_OfNonManualPal_IsRejected()
    {
        var repo = NewRepo(persist: false);
        Assert.Equal(OwnedPalValidationException.NotManual,
            Assert.Throws<OwnedPalValidationException>(() => repo.UpdateManual("save:w:x", Draft())).Key);
        Assert.Throws<OwnedPalValidationException>(() => repo.RemoveManual("save:w:x"));
    }

    [Fact]
    public void Override_WithInvalidPassives_IsRejected()
    {
        var repo = NewRepo(persist: false);
        Assert.Throws<OwnedPalValidationException>(() =>
            repo.SetOverride("save:w:x", new PalOverride { Passives = ["NoSuchPassive"] }));
        Assert.Throws<OwnedPalValidationException>(() =>
            repo.SetOverride("save:w:x", new PalOverride { Ivs = new Ivs(0, 0, 200) }));
    }

    // ---- merging and de-duplication --------------------------------------------------------------------------

    [Fact]
    public void SameInstanceFromTwoSources_BecomesOnePal_NewestWins()
    {
        var repo = NewRepo(persist: false);
        var id = Guid.NewGuid().ToString();
        repo.PublishSourceSnapshot("world1", [MakePal("save:w1:" + id, id, seen: T0)], Ok("world1", 1));
        repo.PublishSourceSnapshot("bridge",
            [MakePal("mod:" + id, id, seen: T0.AddMinutes(5), source: PalSourceKind.ModBridge, sourceId: "bridge", passives: ["Rare"])],
            Ok("bridge", 1));

        var pal = Assert.Single(repo.Current.Pals);
        Assert.Equal("mod:" + id, pal.Uid);
        Assert.Equal(["Rare"], pal.Passives);
        Assert.Equal(1, repo.Current.DuplicateCount);
    }

    [Fact]
    public void SameInstance_WithEqualTimestamps_IsResolvedDeterministically()
    {
        var id = "AAAA";
        var first = NewRepo(persist: false);
        first.PublishSourceSnapshot("a", [MakePal("save:a:1", id)], Ok("a", 1));
        first.PublishSourceSnapshot("b", [MakePal("save:b:1", id)], Ok("b", 1));

        var second = NewRepo(persist: false);
        second.PublishSourceSnapshot("b", [MakePal("save:b:1", id)], Ok("b", 1));
        second.PublishSourceSnapshot("a", [MakePal("save:a:1", id)], Ok("a", 1));

        Assert.Equal("save:a:1", Assert.Single(first.Current.Pals).Uid);
        Assert.Equal("save:a:1", Assert.Single(second.Current.Pals).Uid);
    }

    [Fact]
    public void PalsWithoutInstanceId_AreNeverMerged()
    {
        var repo = NewRepo(persist: false);
        repo.PublishSourceSnapshot("a", [MakePal("save:a:1"), MakePal("save:a:2")], Ok("a", 2));
        Assert.Equal(2, repo.Current.Pals.Count);
        Assert.Equal(0, repo.Current.DuplicateCount);
    }

    [Fact]
    public void RepublishingASource_ReplacesItsPals_AndRemoveSourceDropsThem()
    {
        var repo = NewRepo(persist: false);
        repo.PublishSourceSnapshot("a", [MakePal("save:a:1"), MakePal("save:a:2")], Ok("a", 2));
        repo.PublishSourceSnapshot("a", [MakePal("save:a:3")], Ok("a", 1));
        Assert.Equal(["save:a:3"], repo.Current.Pals.Select(p => p.Uid));
        Assert.Equal(1, Assert.Single(repo.Current.Sources).PalCount);

        repo.RemoveSource("a");
        Assert.Empty(repo.Current.Pals);
        Assert.Empty(repo.Current.Sources);
    }

    [Fact]
    public void ManualAndSourcePals_AreCombined()
    {
        var repo = NewRepo(persist: false);
        repo.PublishSourceSnapshot("a", [MakePal("save:a:1")], Ok("a", 1));
        repo.AddManual(Draft());
        Assert.Equal(2, repo.Current.Pals.Count);
        Assert.Contains(repo.Current.Pals, p => p.Source == PalSourceKind.Manual);
    }

    // ---- corrections -----------------------------------------------------------------------------------------

    [Fact]
    public void Override_IsAppliedToSourcePals_AndReappliedToEveryNewSnapshot()
    {
        var repo = NewRepo();
        repo.PublishSourceSnapshot("a", [MakePal("save:a:1", passives: ["Rare"])], Ok("a", 1));
        repo.SetOverride("save:a:1", new PalOverride { Passives = ["Legend", "Alien"], Level = 44, Gender = Gender.Male });

        var edited = Assert.Single(repo.Current.Pals);
        Assert.True(edited.IsEdited);
        Assert.Equal(["Legend", "Alien"], edited.Passives);
        Assert.Equal(44, edited.Level);
        Assert.Equal(Gender.Male, edited.Gender);

        // A fresh read of the source still shows the original data; the correction is applied again.
        repo.PublishSourceSnapshot("a", [MakePal("save:a:1", passives: ["Rare"])], Ok("a", 1));
        Assert.Equal(["Legend", "Alien"], Assert.Single(repo.Current.Pals).Passives);

        repo.SetOverride("save:a:1", null);
        var reverted = Assert.Single(repo.Current.Pals);
        Assert.False(reverted.IsEdited);
        Assert.Equal(["Rare"], reverted.Passives);
        Assert.Empty(repo.Current.Overrides);
    }

    [Fact]
    public void ExclusionSwitch_IsStoredAsOverride_ButDoesNotCountAsEdit()
    {
        var repo = NewRepo();
        repo.PublishSourceSnapshot("a", [MakePal("save:a:1")], Ok("a", 1));
        repo.SetOverride("save:a:1", new PalOverride { IsExcluded = true });

        var pal = Assert.Single(repo.Current.Pals);
        Assert.True(pal.IsExcluded);
        Assert.False(pal.IsEdited);
        Assert.True(NewRepo().Current.Overrides.ContainsKey("save:a:1"));
    }

    [Fact]
    public void EmptyOverride_RemovesTheCorrection()
    {
        var repo = NewRepo();
        repo.SetOverride("save:a:1", new PalOverride { Level = 3 });
        repo.SetOverride("save:a:1", new PalOverride());
        Assert.Empty(repo.Current.Overrides);
        Assert.Empty(NewRepo().Current.Overrides);
    }

    [Fact]
    public void Override_OfPalThatLeftTheSource_IsKeptAsOrphan_AndComesBack()
    {
        var repo = NewRepo();
        repo.PublishSourceSnapshot("a", [MakePal("save:a:1")], Ok("a", 1));
        repo.SetOverride("save:a:1", new PalOverride { Level = 50 });

        repo.PublishSourceSnapshot("a", [], Ok("a", 0));
        Assert.Empty(repo.Current.Pals);
        Assert.Equal(["save:a:1"], repo.Current.OrphanOverrideUids);
        Assert.Equal(["save:a:1"], NewRepo().Current.OrphanOverrideUids);

        repo.PublishSourceSnapshot("a", [MakePal("save:a:1")], Ok("a", 1));
        Assert.Empty(repo.Current.OrphanOverrideUids);
        Assert.Equal(50, Assert.Single(repo.Current.Pals).Level);
    }

    [Fact]
    public void Override_OfHiddenDuplicate_AppliesToTheVisibleCopy()
    {
        var repo = NewRepo(persist: false);
        var id = "INST-1";
        repo.PublishSourceSnapshot("world", [MakePal("save:w:" + id, id, seen: T0)], Ok("world", 1));
        repo.SetOverride("save:w:" + id, new PalOverride { Level = 61 });

        // The mod bridge now has the newer copy of the same Pal; the user's correction must survive.
        repo.PublishSourceSnapshot("bridge",
            [MakePal("mod:" + id, id, seen: T0.AddMinutes(1), source: PalSourceKind.ModBridge, sourceId: "bridge")],
            Ok("bridge", 1));

        var pal = Assert.Single(repo.Current.Pals);
        Assert.Equal("mod:" + id, pal.Uid);
        Assert.Equal(61, pal.Level);
        Assert.Empty(repo.Current.OrphanOverrideUids);
    }

    [Fact]
    public void ACorrection_IsWrittenAndRemovedOnEveryCopyOfThePal()
    {
        var repo = NewRepo();
        var id = "INST-2";
        repo.PublishSourceSnapshot("world", [MakePal("save:w:" + id, id, seen: T0)], Ok("world", 1));
        repo.SetOverride("save:w:" + id, new PalOverride { IsExcluded = true });

        // The mod has the newer copy: it is the visible one and shows the switch that was set on the save copy.
        repo.PublishSourceSnapshot("bridge",
            [MakePal("mod:" + id, id, seen: T0.AddMinutes(1), source: PalSourceKind.ModBridge, sourceId: "bridge")], Ok("bridge", 1));
        var visible = Assert.Single(repo.Current.Pals);
        Assert.True(visible.IsExcluded);
        Assert.True(repo.Current.Overrides.ContainsKey(visible.Uid));

        // "Use in planning" is switched back on from the visible copy: it must stay on (the hidden copy's correction is gone too).
        repo.SetOverride(visible.Uid, repo.Current.Overrides[visible.Uid] with { IsExcluded = null });
        Assert.False(Assert.Single(repo.Current.Pals).IsExcluded);
        Assert.Empty(NewRepo().Current.Overrides);

        // A correction made on the visible copy follows the Pal when the other source comes back as the newest one.
        repo.SetOverride(visible.Uid, new PalOverride { Level = 70 });
        repo.PublishSourceSnapshot("world", [MakePal("save:w:" + id, id, seen: T0.AddMinutes(5))], Ok("world", 1));
        var pal = Assert.Single(repo.Current.Pals);
        Assert.Equal("save:w:" + id, pal.Uid);
        Assert.Equal(70, pal.Level);
    }

    [Fact]
    public void AnUnknownGenderAndUnknownIvs_AreCorrectionsOfTheirOwn_AndSurviveAReload()
    {
        var repo = NewRepo();
        var withIvs = MakePal("save:a:1") with { Ivs = new Ivs(10, 20, 30) };
        repo.PublishSourceSnapshot("a", [withIvs], Ok("a", 1));

        repo.SetOverride("save:a:1", new PalOverride { ClearGender = true, ClearIvs = true });
        var corrected = Assert.Single(repo.Current.Pals);
        Assert.Null(corrected.Gender);
        Assert.Null(corrected.Ivs);
        Assert.True(corrected.IsEdited);

        var reloaded = NewRepo();
        reloaded.PublishSourceSnapshot("a", [withIvs], Ok("a", 1));
        var again = Assert.Single(reloaded.Current.Pals);
        Assert.Null(again.Gender);
        Assert.Null(again.Ivs);

        // Removing the correction follows the source again.
        reloaded.SetOverride("save:a:1", null);
        Assert.Equal(Gender.Female, Assert.Single(reloaded.Current.Pals).Gender);
        Assert.Equal(new Ivs(10, 20, 30), Assert.Single(reloaded.Current.Pals).Ivs);
    }

    [Fact]
    public void ASelectionKey_IsTheInstanceId_AndAnOldUidStillFindsTheVisibleCopy()
    {
        var repo = NewRepo(persist: false);
        var id = "INST-3";
        repo.PublishSourceSnapshot("world", [MakePal("save:w:" + id, id, seen: T0), MakePal("save:w:other")], Ok("world", 2));
        var saved = repo.Current.Pals.Single(p => p.InstanceId == id);
        Assert.Equal(id, saved.SelectionKey);
        Assert.Equal("save:w:other", repo.Current.Pals.Single(p => p.InstanceId == null).SelectionKey);

        repo.PublishSourceSnapshot("bridge",
            [MakePal("mod:" + id, id, seen: T0.AddMinutes(1), source: PalSourceKind.ModBridge, sourceId: "bridge")], Ok("bridge", 1));

        // The saved key and the old uid selection both resolve to the copy that is visible now.
        Assert.Equal("mod:" + id, repo.Current.Find(id)!.Uid);
        Assert.Equal("mod:" + id, repo.Current.Find("save:w:" + id)!.Uid);
        Assert.Equal("save:w:other", repo.Current.Find("save:w:other")!.Uid);
        Assert.Null(repo.Current.Find("save:w:gone"));
        Assert.Null(repo.Current.Find(null));
    }

    [Fact]
    public void ANewerSaveCopy_BeatsAnOlderModPageCopy()
    {
        var repo = NewRepo(persist: false);
        var id = "INST-4";
        // The mod page was read two days ago; the save was written today.
        repo.PublishSourceSnapshot("bridge",
            [MakePal("mod:" + id, id, seen: T0.AddDays(-2), source: PalSourceKind.ModBridge, sourceId: "bridge")], Ok("bridge", 1));
        repo.PublishSourceSnapshot("world", [MakePal("save:w:" + id, id, seen: T0)], Ok("world", 1));

        Assert.Equal("save:w:" + id, Assert.Single(repo.Current.Pals).Uid);
    }

    [Fact]
    public void RemovingAManualPal_AlsoDropsItsCorrection()
    {
        var repo = NewRepo();
        var pal = repo.AddManual(Draft());
        repo.SetOverride(pal.Uid, new PalOverride { Level = 9 });
        repo.RemoveManual(pal.Uid);
        Assert.Empty(repo.Current.Overrides);
        Assert.Empty(repo.Current.OrphanOverrideUids);
    }

    // ---- events and threads ----------------------------------------------------------------------------------

    [Fact]
    public void Changed_IsRaisedOncePerMutation_WithTheNewSnapshot()
    {
        var repo = NewRepo(persist: false);
        var seen = new List<OwnedPalSnapshot>();
        repo.Changed += (_, s) => seen.Add(s);

        repo.PublishSourceSnapshot("a", [MakePal("save:a:1")], Ok("a", 1));
        var manual = repo.AddManual(Draft());
        repo.SetOverride("save:a:1", new PalOverride { Level = 7 });
        repo.RemoveManual(manual.Uid);

        Assert.Equal(4, seen.Count);
        Assert.Equal([1L, 2L, 3L, 4L], seen.Select(s => s.Version));
        Assert.Same(repo.Current, seen[^1]);
    }

    [Fact]
    public void FailedMutation_RaisesNoEvent_AndKeepsState()
    {
        var repo = NewRepo(persist: false);
        var raised = 0;
        repo.Changed += (_, _) => raised++;
        Assert.Throws<OwnedPalValidationException>(() => repo.AddManual(Draft("NoSuchPal")));
        Assert.Equal(0, raised);
        Assert.Equal(0, repo.Current.Version);
    }

    [Fact]
    public void ConcurrentPublishing_FromManyThreads_StaysConsistent()
    {
        var repo = NewRepo(persist: false);
        var events = 0;
        repo.Changed += (_, _) => Interlocked.Increment(ref events);

        Parallel.For(0, 16, source =>
        {
            for (var round = 0; round < 25; round++)
            {
                var pals = Enumerable.Range(0, 10).Select(i => MakePal($"save:s{source}:{i}")).ToList();
                repo.PublishSourceSnapshot("s" + source, pals, Ok("s" + source, pals.Count));
                _ = repo.Current.Pals.Count; // readers never see a torn snapshot
            }
        });

        Assert.Equal(16 * 25, events);
        Assert.Equal(160, repo.Current.Pals.Count);
        Assert.Equal(16 * 25, repo.Current.Version);
        Assert.Equal(16, repo.Current.Sources.Count);
    }

    // ---- scope -----------------------------------------------------------------------------------------------

    [Fact]
    public void Filter_AppliesOwnerLocationAndExclusion()
    {
        var repo = NewRepo(persist: false);
        repo.PublishSourceSnapshot("a",
        [
            MakePal("save:a:1", owner: "p1", location: LocationKind.Party),
            MakePal("save:a:2", owner: "p1", location: LocationKind.Base),
            MakePal("save:a:3", owner: "p2", location: LocationKind.Palbox),
            MakePal("save:a:4", owner: "P1", location: LocationKind.GlobalStorage),
        ], Ok("a", 4));
        repo.AddManual(Draft());
        repo.SetOverride("save:a:2", new PalOverride { IsExcluded = true });
        var snapshot = repo.Current;

        IEnumerable<string> Uids(OwnerScope scope) => OwnedPalRepository.Filter(snapshot, scope).Select(p => p.Uid).Order();

        Assert.Equal(5 - 1, OwnedPalRepository.Filter(snapshot, OwnerScope.Everything).Count);
        Assert.Equal(5, OwnedPalRepository.Filter(snapshot, OwnerScope.Everything with { IncludeExcluded = true }).Count);

        // Owner "p1" (case-insensitive); the manual Pal has no owner and always counts.
        var mine = Uids(new OwnerScope(["p1"], OwnerScope.AllLocations)).ToList();
        Assert.Equal(["save:a:1", "save:a:4"], mine.Where(u => u.StartsWith("save:", StringComparison.Ordinal)));
        Assert.Contains(mine, u => u.StartsWith("manual:", StringComparison.Ordinal));

        var party = Uids(new OwnerScope(null, [LocationKind.Party])).ToList();
        Assert.Equal(["save:a:1"], party);
    }

    [Fact]
    public void OwnerFreeLocations_CountWhoeverOwnsThePal_AndThePlainListFilterIsTheSameAsTheSnapshotOne()
    {
        var pals = new[]
        {
            MakePal("save:a:1", owner: "host", location: LocationKind.DimensionalStorage),
            MakePal("save:a:2", owner: "host", location: LocationKind.Palbox),
        };
        var scope = new OwnerScope(["me"], OwnerScope.AllLocations, OwnerFreeLocations: [LocationKind.DimensionalStorage]);

        Assert.Equal(["save:a:1"], OwnedPalRepository.Filter(pals, scope).Select(p => p.Uid));
        Assert.Empty(OwnedPalRepository.Filter(pals, scope with { OwnerFreeLocations = null }));
    }

    // ---- culture independence --------------------------------------------------------------------------------

    [Fact]
    public void Ids_AreMatchedWithoutCultureRules_UnderTurkishCulture()
    {
        var previous = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("tr-TR");
            var repo = NewRepo(persist: false);

            // "I" and "i" must still match each other (Turkish dotted/dotless I would break a ToLower() comparison).
            var pal = repo.AddManual(Draft("ICEHORSE", "CRAFTSPEED_UP3"));
            Assert.Equal("IceHorse", pal.SpeciesId);
            Assert.Equal(["CraftSpeed_up3"], pal.Passives);
            Assert.StartsWith("manual:", pal.Uid);
        }
        finally
        {
            (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture) = previous;
        }
    }

    // ---- corrections against the raw source Pal (T62) -----------------------------------------------------------

    private static OwnedPal Raw(Ivs? ivs = null, Gender? gender = Gender.Female) =>
        MakePal("save:w:i1", "i1") with { Level = 20, Gender = gender, Ivs = ivs, Passives = ["Rare"] };

    [Fact]
    public void Diff_PuttingAValueBack_RemovesTheCorrection()
    {
        var raw = Raw();
        var changed = PalOverride.Diff(raw, raw.Gender, 25, raw.Passives, raw.Ivs, null);
        Assert.Equal(25, changed.Level);
        Assert.True(changed.HasCorrections);

        var back = PalOverride.Diff(raw, raw.Gender, 20, raw.Passives, raw.Ivs, changed);
        Assert.False(back.HasCorrections);
        Assert.True(back.IsEmpty);
    }

    [Fact]
    public void Diff_PartialRevert_KeepsTheOtherCorrections()
    {
        var raw = Raw();
        var both = PalOverride.Diff(raw, Gender.Male, 25, raw.Passives, null, null);
        var onlyGender = PalOverride.Diff(raw, Gender.Male, 20, raw.Passives, null, both);
        Assert.Equal(Gender.Male, onlyGender.Gender);
        Assert.Null(onlyGender.Level);
        Assert.False(onlyGender.IsEmpty);
    }

    [Fact]
    public void Diff_ClearingIvsTheSourceKnew_IsAnExplicitCorrection_AndEqualIvsAreNone()
    {
        var raw = Raw(new Ivs(80, 70, 60));
        var cleared = PalOverride.Diff(raw, raw.Gender, raw.Level, raw.Passives, null, null);
        Assert.True(cleared.ClearIvs);
        Assert.Null(cleared.Ivs);

        var same = PalOverride.Diff(raw, raw.Gender, raw.Level, raw.Passives, new Ivs(80, 70, 60), cleared);
        Assert.False(same.ClearIvs);
        Assert.True(same.IsEmpty);

        // A source that never knew the IVs: no IVs entered, no correction.
        Assert.True(PalOverride.Diff(Raw(null), Gender.Female, 20, ["Rare"], null, null).IsEmpty);
    }

    [Fact]
    public void Diff_KeepsTheExclusionSwitch()
    {
        var raw = Raw();
        var previous = new PalOverride { Level = 25, IsExcluded = true };
        var back = PalOverride.Diff(raw, raw.Gender, 20, raw.Passives, raw.Ivs, previous);
        Assert.False(back.HasCorrections);
        Assert.True(back.IsExcluded);
        Assert.False(back.IsEmpty);
    }

    [Fact]
    public void RawPal_IsTheSourceValue_WhileTheSnapshotHoldsTheCorrectedPal()
    {
        var repo = NewRepo(persist: false);
        var pal = MakePal("save:w:i1", "i1") with { Level = 20, Ivs = new Ivs(80, 70, 60) };
        repo.PublishSourceSnapshot("world1", [pal], Ok("world1", 1));
        repo.SetOverride(pal.Uid, new PalOverride { Level = 25, ClearIvs = true });

        var visible = Assert.Single(repo.Current.Pals);
        Assert.Equal(25, visible.Level);
        Assert.Null(visible.Ivs);
        var raw = repo.RawPal(pal.Uid)!;
        Assert.Equal(20, raw.Level);
        Assert.NotNull(raw.Ivs);

        // Another copy of the same in-game Pal (the mod's) is found by the instance id.
        Assert.Equal(20, repo.RawPal("mod:i1")!.Level);
    }
}
