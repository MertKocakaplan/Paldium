using System.IO.Compression;
using Paldium.Import.Sync;

namespace Paldium.Import.Tests;

public class ZipImporterTests : IDisposable
{
    private readonly string _dir = TestSupport.NewTempFolder();

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private string MakeZip(string name, params (string Path, string Content)[] entries)
    {
        var zip = Path.Combine(_dir, name + ".zip");
        using var archive = ZipFile.Open(zip, ZipArchiveMode.Create);
        foreach (var (path, content) in entries)
        {
            var entry = archive.CreateEntry(path);
            using var writer = new StreamWriter(entry.Open());
            writer.Write(content);
        }

        return zip;
    }

    private ZipImporter NewImporter() => new(Path.Combine(_dir, "imports"));

    [Fact]
    public void FindsTheWorldFolderInsideNestedFolders()
    {
        var zip = MakeZip("friend-world",
            ("Backup stuff/readme.txt", "x"),
            ("SaveGames/0123456789ABCDEF0123456789ABCDEF/Level.sav", "level"),
            ("SaveGames/0123456789ABCDEF0123456789ABCDEF/Players/a.sav", "player"));

        var result = NewImporter().Import(zip);

        Assert.EndsWith("0123456789ABCDEF0123456789ABCDEF", result.WorldFolder, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(result.WorldFolder, "Level.sav")));
        Assert.True(File.Exists(Path.Combine(result.WorldFolder, "Players", "a.sav")));
        Assert.StartsWith(Path.Combine(_dir, "imports", "friend-world"), result.ExtractedRoot, StringComparison.Ordinal);
    }

    [Fact]
    public void WorldAtTheTopLevel_IsFound()
    {
        var result = NewImporter().Import(MakeZip("flat", ("Level.sav", "x")));
        Assert.Equal(result.ExtractedRoot, result.WorldFolder);
    }

    [Fact]
    public void ImportingTheSameNameTwice_GetsASeparateFolder()
    {
        var importer = NewImporter();
        var zip = MakeZip("again", ("Level.sav", "x"));
        var first = importer.Import(zip);
        var second = importer.Import(zip);
        Assert.NotEqual(first.ExtractedRoot, second.ExtractedRoot);
        Assert.EndsWith("again-2", second.ExtractedRoot, StringComparison.Ordinal);
    }

    [Fact]
    public void ZipWithoutLevelSave_IsRejected_AndNothingIsLeftBehind()
    {
        var importer = NewImporter();
        var zip = MakeZip("nope", ("LocalData.sav", "x"), ("Players/a.sav", "y"));

        Assert.Throws<InvalidDataException>(() => importer.Import(zip));

        Assert.True(!Directory.Exists(importer.ImportsRoot) || Directory.GetDirectories(importer.ImportsRoot).Length == 0);
    }

    [Fact]
    public void NotAZipFile_IsRejected()
    {
        var bad = Path.Combine(_dir, "bad.zip");
        File.WriteAllText(bad, "this is not a zip");
        Assert.Throws<InvalidDataException>(() => NewImporter().Import(bad));
    }

    [Fact]
    public void EntriesThatEscapeTheTargetFolder_AreSkipped()
    {
        var zip = MakeZip("evil", ("../outside.txt", "bad"), ("Level.sav", "ok"));

        var result = NewImporter().Import(zip);

        Assert.True(File.Exists(Path.Combine(result.WorldFolder, "Level.sav")));
        Assert.False(File.Exists(Path.Combine(_dir, "imports", "outside.txt")));
        Assert.False(File.Exists(Path.Combine(_dir, "outside.txt")));
    }

    [Fact]
    public void FindWorldFolder_PrefersTheShallowestAndSkipsBackups()
    {
        var root = Path.Combine(_dir, "pick");
        Directory.CreateDirectory(Path.Combine(root, "backup", "world", "x"));
        File.WriteAllBytes(Path.Combine(root, "backup", "world", "x", "Level.sav"), [1]);
        Directory.CreateDirectory(Path.Combine(root, "deep", "er", "world"));
        File.WriteAllBytes(Path.Combine(root, "deep", "er", "world", "Level.sav"), [1]);
        Directory.CreateDirectory(Path.Combine(root, "near"));
        File.WriteAllBytes(Path.Combine(root, "near", "Level.sav"), [1]);

        Assert.Equal(Path.Combine(root, "near"), ZipImporter.FindWorldFolder(root));
        Assert.Null(ZipImporter.FindWorldFolder(_dir, maxDepth: 0));
    }

    [Fact]
    public void TryRemove_OnlyDeletesInsideTheImportsFolder()
    {
        var importer = NewImporter();
        var result = importer.Import(MakeZip("removable", ("sub/Level.sav", "x")));
        var outside = Path.Combine(_dir, "keep");
        Directory.CreateDirectory(outside);

        Assert.False(importer.TryRemove(outside));
        Assert.True(Directory.Exists(outside));

        Assert.True(importer.TryRemove(result.WorldFolder));
        Assert.False(Directory.Exists(result.ExtractedRoot));
    }
}
