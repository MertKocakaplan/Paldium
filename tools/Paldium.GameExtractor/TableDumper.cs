using CUE4Parse.FileProvider;
using Newtonsoft.Json;

namespace Paldium.GameExtractor;

/// <summary>Writes the exports of game assets to JSON files.</summary>
public static class TableDumper
{
    /// <summary>Dumps one asset; returns the file written. File name: the asset path with '/' replaced by '_'.</summary>
    public static string Dump(DefaultFileProvider provider, string assetPath, string outFolder)
    {
        var resolved = GameProvider.ResolveAsset(provider, assetPath);
        var package = provider.LoadPackage(resolved);
        var exports = package.GetExports().ToList();

        Directory.CreateDirectory(outFolder);
        var fileName = Path.ChangeExtension(resolved, null).Replace('/', '_') + ".json";
        var target = Path.Combine(outFolder, fileName);
        File.WriteAllText(target, JsonConvert.SerializeObject(exports, Formatting.Indented));
        return target;
    }
}
