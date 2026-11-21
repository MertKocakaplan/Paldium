using Paldium.Import.Bridge;
using Paldium.Import.Migration;

namespace Paldium.Import.Tests;

public class LegacyDataMigrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pb-migrate-" + Guid.NewGuid().ToString("N"));
    private string Roaming => Path.Combine(_root, "Roaming");
    private string Local => Path.Combine(_root, "Local");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private void Seed()
    {
        Directory.CreateDirectory(Path.Combine(Roaming, "Palbreeding"));
        Directory.CreateDirectory(Path.Combine(Local, "Palbreeding", "bridge"));
        File.WriteAllText(Path.Combine(Local, "Palbreeding", "bridge", "palbox.json"), "{}");
        File.WriteAllText(Path.Combine(Roaming, "Palbreeding", "overrides.json"), "{}");
        var oldBridge = Path.Combine(Local, "Palbreeding", "bridge", "palbox.json").Replace("\\", "\\\\");
        File.WriteAllText(Path.Combine(Roaming, "Palbreeding", "settings.json"),
            "{\"Sources\":[{\"Id\":\"a\",\"Type\":\"ModBridgeFile\",\"Path\":\"" + oldBridge + "\"},{\"Id\":\"b\",\"Type\":\"World\",\"Path\":\"D:\\\\Saves\\\\W\"},"
            + "{\"Id\":\"c\",\"Type\":\"World\",\"Path\":\"" + Path.Combine(Local, "PalbreedingX", "w").Replace("\\", "\\\\") + "\"}],\"Language\":\"tr\"}");
    }

    [Fact]
    public void FirstStart_CopiesBothFolders_ChangesThePathsInSettings_AndKeepsTheOldFolders()
    {
        Seed();
        var result = LegacyDataMigration.Run(Roaming, Local);

        Assert.True(result.Roaming.Copied && result.Local.Copied);
        Assert.True(File.Exists(Path.Combine(Roaming, "Paldium", "overrides.json")));
        Assert.True(File.Exists(Path.Combine(Local, "Paldium", "bridge", "palbox.json")));
        Assert.True(File.Exists(Path.Combine(Roaming, "Palbreeding", "settings.json")));   // the old folder stays
        Assert.True(File.Exists(Path.Combine(Roaming, "Paldium", LegacyDataMigration.MarkerFile)));

        var settings = File.ReadAllText(Path.Combine(Roaming, "Paldium", "settings.json"));
        Assert.Equal(1, result.RewrittenPaths);
        Assert.Contains(Path.Combine(Local, "Paldium", "bridge", "palbox.json").Replace("\\", "\\\\"), settings);
        Assert.Contains("D:\\\\Saves\\\\W", settings);
        Assert.Contains("PalbreedingX", settings);   // another folder name that only starts the same way is not touched
        Assert.Contains("\"Language\": \"tr\"", settings);
        // The old settings file is not changed.
        Assert.Contains("Palbreeding", File.ReadAllText(Path.Combine(Roaming, "Palbreeding", "settings.json")));
    }

    [Fact]
    public void NewFoldersThatSomethingCreatedEarly_AreStillMovedInto_WithoutLosingWhatIsThere()
    {
        // The state on the developer's machine: the new folders exist (a default settings.json in Roaming, a few files in Local) but have no marker.
        Seed();
        Directory.CreateDirectory(Path.Combine(Roaming, "Paldium"));
        File.WriteAllText(Path.Combine(Roaming, "Paldium", "settings.json"), "{\"Language\":\"en\"}");
        Directory.CreateDirectory(Path.Combine(Local, "Paldium", "bridge"));
        File.WriteAllText(Path.Combine(Local, "Paldium", "bridge", "palbox.json"), "{\"newer\":true}");
        File.WriteAllText(Path.Combine(Local, "Paldium", "only-new.txt"), "keep");

        var result = LegacyDataMigration.Run(Roaming, Local);

        Assert.True(result.Roaming.Copied && result.Local.Copied);
        var settings = File.ReadAllText(Path.Combine(Roaming, "Paldium", "settings.json"));
        Assert.Contains("\"Language\": \"tr\"", settings);                                               // the old settings are back
        Assert.Equal("{\"Language\":\"en\"}", File.ReadAllText(Path.Combine(Roaming, "Paldium", "settings.json.bak")));   // the default one is kept
        Assert.True(File.Exists(Path.Combine(Roaming, "Paldium", "overrides.json")));
        Assert.Contains(Path.Combine(Local, "Paldium", "bridge", "palbox.json").Replace("\\", "\\\\"), settings);
        // Local: what was there stays; what was missing is added.
        Assert.Equal("{\"newer\":true}", File.ReadAllText(Path.Combine(Local, "Paldium", "bridge", "palbox.json")));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(Local, "Paldium", "only-new.txt")));
        Assert.True(File.Exists(Path.Combine(Local, "Paldium", LegacyDataMigration.MarkerFile)));

        // Done once: a second start changes nothing, even when the user changes the new settings.
        File.WriteAllText(Path.Combine(Roaming, "Paldium", "settings.json"), "{\"Language\":\"en\"}");
        var again = LegacyDataMigration.Run(Roaming, Local);
        Assert.False(again.Roaming.Copied || again.Local.Copied);
        Assert.Equal("{\"Language\":\"en\"}", File.ReadAllText(Path.Combine(Roaming, "Paldium", "settings.json")));
    }

    [Fact]
    public void SecondStart_DoesNotCopyAgain()
    {
        Seed();
        LegacyDataMigration.Run(Roaming, Local);
        File.WriteAllText(Path.Combine(Roaming, "Paldium", "overrides.json"), "{\"changed\":true}");
        File.WriteAllText(Path.Combine(Roaming, "Palbreeding", "overrides.json"), "{\"later\":true}");

        var again = LegacyDataMigration.Run(Roaming, Local);

        Assert.False(again.Roaming.Copied || again.Local.Copied);
        Assert.Equal("{\"changed\":true}", File.ReadAllText(Path.Combine(Roaming, "Paldium", "overrides.json")));
    }

    [Fact]
    public void WithoutOldFolders_NothingIsCreated()
    {
        Directory.CreateDirectory(_root);
        var result = LegacyDataMigration.Run(Roaming, Local);
        Assert.False(result.Roaming.Copied || result.Local.Copied || result.Roaming.Failed);
        Assert.False(Directory.Exists(Path.Combine(Roaming, "Paldium")));
    }

    [Fact]
    public void AFailedCopy_IsReported_LeavesNoNewFolder_AndTheOldOneIsKept()
    {
        Seed();
        // A file that is open without sharing cannot be copied.
        using var locked = new FileStream(Path.Combine(Roaming, "Palbreeding", "overrides.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var result = LegacyDataMigration.Run(Roaming, Local, applyFallback: false);

        Assert.True(result.Roaming.Failed);
        Assert.False(File.Exists(Path.Combine(Roaming, "Paldium", LegacyDataMigration.MarkerFile)));   // not done: the next start tries again
        Assert.True(File.Exists(Path.Combine(Roaming, "Palbreeding", "settings.json")));
        Assert.True(result.Local.Copied);   // the other folder is independent
    }

    [Fact]
    public void TheBridgeFileOfTheOldMod_IsReadUntilTheNewOneIsNewer()
    {
        var current = Path.Combine(_root, "new", "palbox.json");
        var legacy = Path.Combine(_root, "old", "palbox.json");
        Directory.CreateDirectory(Path.GetDirectoryName(current)!);
        Directory.CreateDirectory(Path.GetDirectoryName(legacy)!);

        Assert.Equal(current, ModBridgeReader.EffectiveFile(current, current, legacy));   // none exists
        File.WriteAllText(legacy, "{}");
        Assert.Equal(legacy, ModBridgeReader.EffectiveFile(current, current, legacy));    // only the old one
        File.WriteAllText(current, "{}");
        File.SetLastWriteTimeUtc(legacy, DateTime.UtcNow.AddMinutes(-5));
        Assert.Equal(current, ModBridgeReader.EffectiveFile(current, current, legacy));   // the new one is newer
        File.SetLastWriteTimeUtc(legacy, DateTime.UtcNow.AddMinutes(5));
        Assert.Equal(legacy, ModBridgeReader.EffectiveFile(current, current, legacy));    // the old mod wrote last
        Assert.Equal("elsewhere.json", ModBridgeReader.EffectiveFile("elsewhere.json", current, legacy));
    }
}
