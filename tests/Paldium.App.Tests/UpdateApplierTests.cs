using Paldium.Updater;

namespace Paldium.App.Tests;

public class UpdateApplierTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pb-upd-" + Guid.NewGuid().ToString("N"));
    private string App => Path.Combine(_root, "app");
    private string Staged => Path.Combine(_root, "updates", "0.2.0", "staged");
    private string Updates => Path.Combine(_root, "updates");

    public UpdateApplierTests()
    {
        Directory.CreateDirectory(Path.Combine(App, "data"));
        File.WriteAllText(Path.Combine(App, "Paldium.exe"), "old exe");
        File.WriteAllText(Path.Combine(App, "data", "gamedata.json"), "old data");
        File.WriteAllText(Path.Combine(App, "old-only.dll"), "gone after the update");
        File.WriteAllLines(Path.Combine(App, UpdateApplier.ManifestFile),
            ["Paldium.exe", "data/gamedata.json", "old-only.dll", UpdateApplier.ManifestFile]);

        Directory.CreateDirectory(Path.Combine(Staged, "data"));
        File.WriteAllText(Path.Combine(Staged, "Paldium.exe"), "new exe");
        File.WriteAllText(Path.Combine(Staged, "data", "gamedata.json"), "new data");
        File.WriteAllText(Path.Combine(Staged, "new-only.dll"), "new");
        File.WriteAllLines(Path.Combine(Staged, UpdateApplier.ManifestFile),
            ["Paldium.exe", "data/gamedata.json", "new-only.dll", UpdateApplier.ManifestFile]);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private UpdateRequest Request(string? app = null, string? staged = null, string? oldVersion = "0.1.0") =>
        new(1234, app ?? App, staged ?? Staged, Updates, oldVersion!);

    [Fact]
    public void Success_PutsTheNewFilesInPlace_KeepsABackup_AndStartsTheNewExe()
    {
        File.WriteAllText(Path.Combine(Updates, "last-error.txt"), "stale error");
        var started = new List<string>();
        var outcome = UpdateApplier.Apply(Request(), (_, _) => true, exe => { started.Add(exe); return true; });

        Assert.True(outcome.Succeeded);
        Assert.Equal("new exe", File.ReadAllText(Path.Combine(App, "Paldium.exe")));
        Assert.Equal("new data", File.ReadAllText(Path.Combine(App, "data", "gamedata.json")));
        Assert.True(File.Exists(Path.Combine(App, "new-only.dll")));
        Assert.False(File.Exists(Path.Combine(App, "old-only.dll")));
        Assert.Equal("old exe", File.ReadAllText(Path.Combine(Updates, "backup", "0.1.0", "Paldium.exe")));
        Assert.Equal("old data", File.ReadAllText(Path.Combine(Updates, "backup", "0.1.0", "data", "gamedata.json")));
        Assert.Equal([Path.Combine(App, "Paldium.exe")], started);
        Assert.False(File.Exists(Path.Combine(Updates, "last-error.txt")));
    }

    [Fact]
    public void UnlistedUserFilesAndFolders_AreNeverTouchedOrBackedUp()
    {
        var userFile = Path.Combine(App, "my-notes.txt");
        var userSubDir = Path.Combine(App, "MySaves");
        var userNestedFile = Path.Combine(userSubDir, "world.sav");
        Directory.CreateDirectory(userSubDir);
        File.WriteAllText(userFile, "keep me");
        File.WriteAllText(userNestedFile, "keep save");

        var outcome = UpdateApplier.Apply(Request(), (_, _) => true, _ => true);

        Assert.True(outcome.Succeeded);
        Assert.Equal("keep me", File.ReadAllText(userFile));
        Assert.Equal("keep save", File.ReadAllText(userNestedFile));
        Assert.False(File.Exists(Path.Combine(Updates, "backup", "0.1.0", "my-notes.txt")));
        Assert.False(Directory.Exists(Path.Combine(Updates, "backup", "0.1.0", "MySaves")));
    }

    [Fact]
    public void LegacyInstallWithoutManifest_ReplacesOnlyMatchingPackageFiles_AndDeletesNothingElse()
    {
        File.Delete(Path.Combine(App, UpdateApplier.ManifestFile));
        var extraFile = Path.Combine(App, "user-extra.txt");
        File.WriteAllText(extraFile, "keep");

        var outcome = UpdateApplier.Apply(Request(), (_, _) => true, _ => true);

        Assert.True(outcome.Succeeded);
        Assert.Equal("new exe", File.ReadAllText(Path.Combine(App, "Paldium.exe")));
        Assert.Equal("new data", File.ReadAllText(Path.Combine(App, "data", "gamedata.json")));
        Assert.True(File.Exists(Path.Combine(App, "new-only.dll")));
        Assert.True(File.Exists(Path.Combine(App, "old-only.dll")));
        Assert.Equal("keep", File.ReadAllText(extraFile));
    }

    [Fact]
    public void NestedTopLevelFolderInStaged_IsUnwrappedAndApplied()
    {
        var nestedOuter = Path.Combine(_root, "updates", "0.2.0", "staged-nested");
        var nestedInner = Path.Combine(nestedOuter, "Paldium");
        Directory.CreateDirectory(Path.Combine(nestedInner, "data"));
        File.WriteAllText(Path.Combine(nestedInner, "Paldium.exe"), "nested exe");
        File.WriteAllText(Path.Combine(nestedInner, "data", "gamedata.json"), "nested data");

        var outcome = UpdateApplier.Apply(Request(staged: nestedOuter), (_, _) => true, _ => true);

        Assert.True(outcome.Succeeded);
        Assert.Equal("nested exe", File.ReadAllText(Path.Combine(App, "Paldium.exe")));
        Assert.Equal("nested data", File.ReadAllText(Path.Combine(App, "data", "gamedata.json")));
    }

    [Fact]
    public void SafetyChecks_RefuseMissingExe_UnsafeFolder_Overlap_AndInvalidOldVersion()
    {
        var started = new List<string>();
        bool Start(string p) { started.Add(p); return true; }

        // Protected user folder (simulated via extraForbiddenFolders)
        var outcomeForbidden = UpdateApplier.Apply(Request(), (_, _) => true, Start, null, TimeSpan.Zero, [App]);
        Assert.False(outcomeForbidden.Succeeded);
        Assert.Empty(started);
        Assert.Equal("old exe", File.ReadAllText(Path.Combine(App, "Paldium.exe")));

        // AppFolder as ancestor of UpdatesFolder
        var nestedApp = Path.Combine(_root, "ancestor-app");
        Directory.CreateDirectory(nestedApp);
        File.WriteAllText(Path.Combine(nestedApp, "Paldium.exe"), "old");
        var childUpdates = Path.Combine(nestedApp, "updates");
        Directory.CreateDirectory(childUpdates);
        var outcomeAncestor = UpdateApplier.Apply(new UpdateRequest(1, nestedApp, Staged, childUpdates, "0.1.0"), (_, _) => true, Start, null, TimeSpan.Zero);
        Assert.False(outcomeAncestor.Succeeded);
        Assert.Empty(started);

        // Drive root validation
        var driveRoot = Path.GetPathRoot(App)!;
        Assert.NotNull(AppFolderSafety.ValidateAppFolder(driveRoot, Updates));

        // Path traversal OldVersion
        var outcomeBadVer = UpdateApplier.Apply(Request(oldVersion: "..\\evil"), (_, _) => true, Start, null, TimeSpan.Zero);
        Assert.False(outcomeBadVer.Succeeded);
        Assert.Empty(started);

        // Missing Paldium.exe in AppFolder
        File.Delete(Path.Combine(App, "Paldium.exe"));
        var outcomeNoExe = UpdateApplier.Apply(Request(), (_, _) => true, Start, null, TimeSpan.Zero);
        Assert.False(outcomeNoExe.Succeeded);
        Assert.Empty(started);
    }

    [Theory]
    [InlineData("copy")]
    [InlineData("clear")]
    [InlineData("start")]
    public void AFailureInTheMiddle_PutsTheOldFilesBack_WritesLastError_AndStartsTheOldVersion(string failingStep)
    {
        var started = new List<string>();
        var outcome = UpdateApplier.Apply(Request(), (_, _) => true, exe => { started.Add(exe); return true; },
            step => { if (step == failingStep) throw new IOException("disk full"); });

        Assert.False(outcome.Succeeded);
        Assert.Equal("old exe", File.ReadAllText(Path.Combine(App, "Paldium.exe")));
        Assert.Equal("old data", File.ReadAllText(Path.Combine(App, "data", "gamedata.json")));
        Assert.True(File.Exists(Path.Combine(App, "old-only.dll")));
        Assert.False(File.Exists(Path.Combine(App, "new-only.dll")));
        Assert.Contains("disk full", File.ReadAllText(Path.Combine(Updates, "last-error.txt")));
        Assert.Single(started);   // the old version was started again
    }

    [Fact]
    public void BackupFailure_DoesNotDeletePreviousExistingBackup()
    {
        var prevBackup = Path.Combine(Updates, "backup", "0.1.0");
        Directory.CreateDirectory(prevBackup);
        File.WriteAllText(Path.Combine(prevBackup, "Paldium.exe"), "previous backup exe");

        var outcome = UpdateApplier.Apply(Request(), (_, _) => true, _ => true,
            step => { if (step == "backup") throw new IOException("no space"); });

        Assert.False(outcome.Succeeded);
        Assert.Equal("previous backup exe", File.ReadAllText(Path.Combine(prevBackup, "Paldium.exe")));
    }

    [Fact]
    public void PermanentlyLockedFileDuringClear_RestoresRemainingFilesEvenWhenLockedFileCannotBeOverwritten()
    {
        var started = new List<string>();
        UpdateOutcome outcome;
        using (new FileStream(Path.Combine(App, "data", "gamedata.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            outcome = UpdateApplier.Apply(Request(), (_, _) => true, exe => { started.Add(exe); return true; },
                step: null, TimeSpan.FromMilliseconds(2));
        }

        Assert.False(outcome.Succeeded);
        Assert.Equal("old exe", File.ReadAllText(Path.Combine(App, "Paldium.exe")));
        Assert.Equal("old data", File.ReadAllText(Path.Combine(App, "data", "gamedata.json")));
        Assert.True(File.Exists(Path.Combine(App, "old-only.dll")));
        Assert.Single(started);
    }

    [Fact]
    public void BrieflyLockedFileDuringClear_RecoversViaRetries()
    {
        FileStream? lockStream = null;
        var outcome = UpdateApplier.Apply(Request(), (_, _) => true, _ => true, step =>
        {
            if (step == "clear")
            {
                lockStream = new FileStream(Path.Combine(App, "old-only.dll"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                var t = new Thread(() =>
                {
                    Thread.Sleep(80);
                    lockStream.Dispose();
                })
                {
                    IsBackground = true,
                };
                t.Start();
            }
        });
        lockStream?.Dispose();

        Assert.True(outcome.Succeeded, outcome.Error);
        Assert.False(File.Exists(Path.Combine(App, "old-only.dll")));
        Assert.Equal("new exe", File.ReadAllText(Path.Combine(App, "Paldium.exe")));
    }

    [Fact]
    public void AppThatDoesNotExit_LeavesTheFilesAlone_AndDoesNotStartASecondInstance()
    {
        var started = new List<string>();
        var outcome = UpdateApplier.Apply(Request(), (_, _) => false, exe => { started.Add(exe); return true; });
        Assert.False(outcome.Succeeded);
        Assert.Empty(started);
        Assert.Equal("old exe", File.ReadAllText(Path.Combine(App, "Paldium.exe")));
        Assert.True(File.Exists(Path.Combine(Updates, "last-error.txt")));
    }

    [Fact]
    public void AStagedFolderWithoutTheExe_IsRefused()
    {
        File.Delete(Path.Combine(Staged, "Paldium.exe"));
        var started = new List<string>();
        var outcome = UpdateApplier.Apply(Request(), (_, _) => true, exe => { started.Add(exe); return true; });
        Assert.False(outcome.Succeeded);
        Assert.Equal("old exe", File.ReadAllText(Path.Combine(App, "Paldium.exe")));
        Assert.Single(started);
    }
}
