namespace Paldium.Core.Tests;

/// <summary>Locates repository folders relative to the test binaries.</summary>
public static class TestPaths
{
    private static readonly Lazy<string> Root = new(FindRoot);

    public static string RepoRoot => Root.Value;

    public static string DataFolder => Path.Combine(RepoRoot, "data");

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Paldium.sln"))) return dir.FullName;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Repository root (Paldium.sln) not found above " + AppContext.BaseDirectory);
    }
}
