using Paldium.Core.Data;

namespace Paldium.Import.Tests;

public static class TestSupport
{
    public const string WorldVariable = "PALDIUM_TEST_WORLD";
    public const string AccountVariable = "PALDIUM_TEST_ACCOUNT";

    private static readonly Lazy<string> Root = new(() =>
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Paldium.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    });

    private static readonly Lazy<GameData> Data = new(() => GameDataLoader.Load(Path.Combine(Root.Value, "data")));

    public static string RepoRoot => Root.Value;

    public static GameData GameData => Data.Value;

    public static string? WorldFolder => Environment.GetEnvironmentVariable(WorldVariable);

    public static string? AccountFolder => Environment.GetEnvironmentVariable(AccountVariable);

    public static string NewTempFolder()
    {
        var path = Path.Combine(Path.GetTempPath(), "pb-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Size and last write time of every file under a folder, to prove nothing was modified.</summary>
    public static Dictionary<string, (long Length, DateTime LastWriteUtc)> Fingerprint(string folder) =>
        Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .ToDictionary(f => f, f =>
            {
                var info = new FileInfo(f);
                return (info.Length, info.LastWriteTimeUtc);
            }, StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// A fact that is skipped unless the environment variable names an existing folder. Real saves are never part of the
/// repository; point PALDIUM_TEST_WORLD / PALDIUM_TEST_ACCOUNT at local copies to run these tests.
/// </summary>
public sealed class RealSaveFactAttribute : FactAttribute
{
    public RealSaveFactAttribute(string variable)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(value) || !Directory.Exists(value))
            Skip = $"Set {variable} to a Palworld save folder to run this test.";
    }
}
