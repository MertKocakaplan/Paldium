using System.Globalization;
using Paldium.Core.Domain;
using Paldium.Import.Saves;

namespace Paldium.Import.Tests;

public class SaveWorldReaderTests : IDisposable
{
    private readonly string _dir = TestSupport.NewTempFolder();
    private readonly List<string> _log = [];

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private SaveWorldReader NewReader(IReadOnlyList<string>? accounts = null, TimeSpan? retryDelay = null) =>
        new(TestSupport.GameData, Path.Combine(_dir, "work"), accounts, (m, _) => _log.Add(m), retryDelay ?? TimeSpan.FromMilliseconds(10));

    // ---- always run -------------------------------------------------------------------------------------------

    [Fact]
    public void Libooz_LoadsInThisBuildConfiguration()
    {
        // PlM1 (Oodle) saves need libooz.dll: loose next to the exe in Debug, embedded by Costura in Release.
        Assert.True(SaveWorldReader.IsDecompressionAvailable,
            "libooz did not load; the Visual C++ x64 redistributable may be missing.");
    }

    [Fact]
    public void WorldWithoutLevelSave_IsReportedAsHostedElsewhere()
    {
        var account = Path.Combine(_dir, "account");
        var joined = Path.Combine(account, "0123456789ABCDEF0123456789ABCDEF");
        Directory.CreateDirectory(joined);
        File.WriteAllBytes(Path.Combine(joined, "LocalData.sav"), [1, 2, 3]);

        var reader = NewReader([account]);

        var world = Assert.Single(reader.DiscoverLocalWorlds());
        Assert.False(world.HasLevelSave);
        Assert.Null(world.WorldName);
        Assert.Equal("0123456789ABCDEF0123456789ABCDEF", world.WorldId);
        Assert.Equal(account, world.AccountFolder);

        var ex = Assert.Throws<WorldUnavailableException>(() => reader.ReadWorld(joined));
        Assert.Equal(WorldUnavailableException.HostedElsewhereKey, ex.Key);
        Assert.Contains("hosted by another player", ex.Message);
    }

    [Fact]
    public void Discovery_IgnoresFoldersThatAreNotWorldIds_AndMissingAccounts()
    {
        var account = Path.Combine(_dir, "account");
        Directory.CreateDirectory(Path.Combine(account, "backup"));
        Directory.CreateDirectory(Path.Combine(account, "not-a-world"));
        Directory.CreateDirectory(Path.Combine(account, "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"));

        var worlds = NewReader([account, Path.Combine(_dir, "missing")]).DiscoverLocalWorlds();

        Assert.Equal(["AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"], worlds.Select(w => w.WorldId));
    }

    [Fact]
    public void ReadingAMissingFolder_Throws()
    {
        Assert.Throws<DirectoryNotFoundException>(() => NewReader().ReadWorld(Path.Combine(_dir, "nope")));
    }

    [Fact]
    public void GlobalStorage_WithoutFile_IsEmpty()
    {
        Assert.Empty(NewReader().ReadGlobalStorage(_dir));
    }

    [Fact]
    public void LockedFile_IsRetriedThreeTimes_ThenReported()
    {
        var account = Path.Combine(_dir, "account");
        Directory.CreateDirectory(account);
        var gps = Path.Combine(account, "GlobalPalStorage.sav");
        File.WriteAllBytes(gps, [1, 2, 3]);

        // Exclusive lock: even the most permissive read sharing cannot open the file.
        using var locked = new FileStream(gps, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        Assert.Throws<IOException>(() => NewReader().ReadGlobalStorage(account));
        Assert.Equal(2, _log.Count(m => m.Contains("failed (attempt", StringComparison.Ordinal)));
    }

    [Fact]
    public void FileThatIsBeingWritten_CanStillBeCopied()
    {
        // The game keeps the save open for writing; a reader with FileShare.ReadWrite must not be blocked.
        var account = Path.Combine(_dir, "account");
        Directory.CreateDirectory(account);
        var gps = Path.Combine(account, "GlobalPalStorage.sav");
        File.WriteAllBytes(gps, [1, 2, 3]);
        using var writer = new FileStream(gps, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);

        // Copying succeeds; the bytes are not a real save, so parsing fails with something other than a sharing error.
        var ex = Record.Exception(() => NewReader().ReadGlobalStorage(account));
        Assert.DoesNotContain(_log, m => m.Contains("failed (attempt", StringComparison.Ordinal));
        Assert.False(ex is IOException { HResult: -2147024864 }, "the file was reported as locked");
    }

    // ---- real saves (skipped unless the environment variables are set) -----------------------------------------

    [RealSaveFact(TestSupport.WorldVariable)]
    public void RealWorld_IsRead_WithPlayersAndKnownPals()
    {
        var snapshot = NewReader().ReadWorld(TestSupport.WorldFolder!);

        Assert.True(snapshot.Players.Count >= 1, "no players");
        Assert.True(snapshot.Pals.Count >= 1, "no Pals");
        Assert.Empty(snapshot.SkippedUnknownSpecies);
        Assert.All(snapshot.Pals, p =>
        {
            Assert.True(TestSupport.GameData.TryNormalizePalId(p.SpeciesId, out _), "unknown species " + p.SpeciesId);
            Assert.StartsWith("save:" + snapshot.WorldId + ":", p.Uid, StringComparison.Ordinal);
            Assert.InRange(p.Passives.Count, 0, 4);
            Assert.All(p.Passives, id => Assert.NotNull(TestSupport.GameData.Passive(id)));
            Assert.NotNull(p.Ivs);
        });
        Assert.Equal(snapshot.Pals.Count, snapshot.Pals.Select(p => p.Uid).Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(snapshot.Pals, p => p.Owner != null);
        Assert.All(snapshot.ExpeditionPalUids, uid => Assert.Contains(snapshot.Pals, p => p.Uid == uid));
        Assert.True(snapshot.ReadDuration > TimeSpan.Zero);

        // Temporary copies are removed.
        var work = Path.Combine(_dir, "work");
        Assert.True(!Directory.Exists(work) || Directory.GetFileSystemEntries(work).Length == 0);
    }

    [RealSaveFact(TestSupport.WorldVariable)]
    public void RealWorld_ReadingDoesNotChangeTheSourceFiles()
    {
        var before = TestSupport.Fingerprint(TestSupport.WorldFolder!);

        NewReader().ReadWorld(TestSupport.WorldFolder!);

        Assert.Equal(before, TestSupport.Fingerprint(TestSupport.WorldFolder!));
    }

    [RealSaveFact(TestSupport.WorldVariable)]
    public void RealWorld_GivesTheSameResult_UnderTurkishCulture()
    {
        var invariant = NewReader().ReadWorld(TestSupport.WorldFolder!);

        var previous = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        WorldSnapshot turkish;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("tr-TR");
            turkish = NewReader().ReadWorld(TestSupport.WorldFolder!);
        }
        finally
        {
            (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture) = previous;
        }

        Assert.Equal(invariant.Pals.Count, turkish.Pals.Count);
        Assert.Equal(invariant.Players.Select(p => (p.PlayerId, p.Name, p.Level)), turkish.Players.Select(p => (p.PlayerId, p.Name, p.Level)));
        Assert.Equal(
            invariant.Pals.OrderBy(p => p.Uid, StringComparer.Ordinal).Select(Describe),
            turkish.Pals.OrderBy(p => p.Uid, StringComparer.Ordinal).Select(Describe));
        Assert.Equal(invariant.SkippedUnknownSpecies, turkish.SkippedUnknownSpecies);
    }

    private static string Describe(OwnedPal p) =>
        $"{p.Uid}|{p.SpeciesId}|{p.Gender}|{p.Level}|{p.Rank}|{string.Join(',', p.Passives)}|{p.Ivs}|{p.Location}|{p.Owner?.PlayerId}";

    [RealSaveFact(TestSupport.AccountVariable)]
    public void RealAccount_GlobalPalbox_IsRead()
    {
        var pals = NewReader().ReadGlobalStorage(TestSupport.AccountFolder!);

        // Cross-check with PalCalc's raw view of the same file: every occupied slot is a Pal we mapped
        // (an empty Global Palbox has 960 empty slots named "None" and no Pals).
        var copy = Path.Combine(_dir, "gps-copy.sav");
        File.Copy(Path.Combine(TestSupport.AccountFolder!, SaveWorldReader.GlobalStorageFileName), copy);
        var raw = new PalCalc.SaveReader.SaveFile.GlobalPalStorageSaveFile(new PalCalc.SaveReader.SingleFileSource(copy)).ReadRawCharacters();
        Assert.NotNull(raw);
        Assert.Equal(raw!.Count(c => c.CharacterId != "None"), pals.Count);

        Assert.All(pals, p =>
        {
            Assert.StartsWith("gps:", p.Uid, StringComparison.Ordinal);
            Assert.Equal(LocationKind.GlobalStorage, p.Location.Kind);
            Assert.Equal(PalSourceKind.GlobalStorage, p.Source);
            Assert.Null(p.Owner);
        });
        Assert.Equal(pals.Count, pals.Select(p => p.Uid).Distinct(StringComparer.Ordinal).Count());
    }

    [RealSaveFact(TestSupport.AccountVariable)]
    public void RealAccount_ReadingGlobalPalboxDoesNotChangeTheFiles()
    {
        var before = TestSupport.Fingerprint(TestSupport.AccountFolder!);
        NewReader().ReadGlobalStorage(TestSupport.AccountFolder!);
        Assert.Equal(before, TestSupport.Fingerprint(TestSupport.AccountFolder!));
    }

    [RealSaveFact(TestSupport.AccountVariable)]
    public void RealAccount_Discovery_FindsTheWorldsWithNames()
    {
        var worlds = NewReader([TestSupport.AccountFolder!]).DiscoverLocalWorlds();

        Assert.NotEmpty(worlds);
        Assert.All(worlds, w => Assert.Equal(TestSupport.AccountFolder, w.AccountFolder));
        Assert.Contains(worlds, w => w.HasLevelSave);
        if (TestSupport.WorldFolder is { } folder)
        {
            var read = Assert.Single(worlds, w => string.Equals(w.FolderPath, folder, StringComparison.OrdinalIgnoreCase));
            Assert.True(read.HasLevelSave);
        }
    }
}
