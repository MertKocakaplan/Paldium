using CUE4Parse.Compression;
using CUE4Parse.FileProvider;
using CUE4Parse.MappingsProvider;
using CUE4Parse.MappingsProvider.Usmap;
using CUE4Parse.UE4.Versions;

namespace Paldium.GameExtractor;

/// <summary>Opens the game's main pak (and only that one) read-only through CUE4Parse.</summary>
public static class GameProvider
{
    /// <summary>The one pak the extractor may load. Mod pak files can override assets and corrupt the data.</summary>
    public const string MainPakName = "Pal-Windows.pak";

    public static DefaultFileProvider Open(string paksFolder, string usmapPath)
    {
        if (!Directory.Exists(paksFolder))
            throw new DirectoryNotFoundException($"Paks folder not found: {paksFolder}");
        if (!File.Exists(Path.Combine(paksFolder, MainPakName)))
            throw new FileNotFoundException($"{MainPakName} not found in {paksFolder}");
        if (!File.Exists(usmapPath))
            throw new FileNotFoundException($"Mappings (.usmap) file not found: {usmapPath}");

        // Oodle (Kraken) decompression library: CUE4Parse downloads it into the current directory, so switch to the
        // tool's own folder first (never leave a binary in the repository or the game folder).
        var previousDirectory = Environment.CurrentDirectory;
        Environment.CurrentDirectory = AppContext.BaseDirectory;
        try
        {
            OodleHelper.DownloadOodleDll();
            OodleHelper.Initialize();
        }
        finally
        {
            Environment.CurrentDirectory = previousDirectory;
        }

        // TopDirectoryOnly: the mod folders (~mods, LogicMods) are subfolders of the paks folder and must not be scanned.
        var provider = new DefaultFileProvider(paksFolder, SearchOption.TopDirectoryOnly, new VersionContainer(EGame.GAME_UE5_1), StringComparer.OrdinalIgnoreCase)
        {
            MappingsContainer = new FileUsmapTypeMappingsProvider(usmapPath),
        };
        provider.Initialize();
        provider.Mount();
        provider.LoadVirtualPaths();

        var mounted = provider.MountedVfs.Select(v => v.Name).ToList();
        var unexpected = mounted.Where(n => !string.Equals(n, MainPakName, StringComparison.OrdinalIgnoreCase)).ToList();
        if (unexpected.Count > 0)
            throw new InvalidOperationException("Unexpected pak files were mounted: " + string.Join(", ", unexpected));
        return provider;
    }

    /// <summary>Resolves a path or bare asset name (with or without extension) to the provider's game file path.</summary>
    public static string ResolveAsset(DefaultFileProvider provider, string assetPath)
    {
        var normalized = assetPath.Replace('\\', '/').Trim('/');
        foreach (var candidate in new[] { normalized, normalized + ".uasset", normalized + ".umap" })
            if (provider.Files.ContainsKey(candidate)) return candidate;

        // Bare name such as "DT_PalMonsterParameter": find the unique file with that name.
        var name = Path.GetFileNameWithoutExtension(normalized);
        var matches = provider.Files.Keys
            .Where(k => (k.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) || k.EndsWith(".umap", StringComparison.OrdinalIgnoreCase)) &&
                        string.Equals(Path.GetFileNameWithoutExtension(k), name, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new FileNotFoundException($"Asset not found: {assetPath}"),
            _ => throw new InvalidOperationException($"Asset name '{assetPath}' is ambiguous: {string.Join(", ", matches)}"),
        };
    }
}
