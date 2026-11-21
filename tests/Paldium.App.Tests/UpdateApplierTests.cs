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
        Directory.CreateDirectory(Path.Combine(Staged, "data"));
        File.WriteAllText(Path.Combine(Staged, "Paldium.exe"), "new exe");
        File.WriteAllText(Path.Combine(Staged, "data", "gamedata.json"), "new data");
        File.WriteAllText(Path.Combine(Staged, "new-only.dll"), "new");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private UpdateRequest Request() => new(1234, App, Staged, Updates, "0.1.0");

    [Fact]
    public void Success_PutsTheNewFilesInPlace_KeepsABackup_AndStartsTheNewExe()
    {
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
    public void AppThatDoesNotExit_LeavesTheFilesAlone()
    {
        var outcome = UpdateApplier.Apply(Request(), (_, _) => false, _ => true);
        Assert.False(outcome.Succeeded);
        Assert.Equal("old exe", File.ReadAllText(Path.Combine(App, "Paldium.exe")));
        Assert.True(File.Exists(Path.Combine(Updates, "last-error.txt")));
    }

    [Fact]
    public void AStagedFolderWithoutTheExe_IsRefused()
    {
        File.Delete(Path.Combine(Staged, "Paldium.exe"));
        var outcome = UpdateApplier.Apply(Request(), (_, _) => true, _ => true);
        Assert.False(outcome.Succeeded);
        Assert.Equal("old exe", File.ReadAllText(Path.Combine(App, "Paldium.exe")));
    }
}
