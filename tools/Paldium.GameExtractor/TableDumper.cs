using CUE4Parse.FileProvider;
using Newtonsoft.Json;

namespace Paldium.GameExtractor;

/// <summary>Writes the exports of game assets to JSON files.</summary>
public static class TableDumper
{
    /// <summary>Writes the properties of a game type (and of its parents) from the mappings file to the console.</summary>
    public static void PrintType(DefaultFileProvider provider, string typeName)
    {
        var types = provider.MappingsForGame?.Types;
        if (typeName.StartsWith('~'))
        {
            // "~Part": which types have a property whose name contains Part.
            foreach (var (name, found) in types?.OrderBy(t => t.Key, StringComparer.Ordinal).AsEnumerable() ?? [])
            {
                var matches = found.Properties.Values.Where(p => p.Name.Contains(typeName[1..], StringComparison.OrdinalIgnoreCase)).Select(p => p.Name).Distinct().ToList();
                if (matches.Count > 0) Console.WriteLine($"{name}: {string.Join(", ", matches)}");
            }
            return;
        }

        if (types == null || !types.TryGetValue(typeName, out var type))
        {
            Console.WriteLine($"{typeName}: not in the mappings.");
            return;
        }

        Console.WriteLine(typeName + (type.SuperType != null ? " : " + type.SuperType : ""));
        foreach (var property in type.Properties.Values.OrderBy(p => p.Index))
        {
            var kind = property.MappingType;
            Console.WriteLine($"  {property.Name}: {kind.Type} {kind.StructType ?? kind.EnumName}" +
                              (kind.InnerType != null ? $" [{kind.InnerType.Type} {kind.InnerType.StructType ?? kind.InnerType.EnumName}]" : "") +
                              (kind.ValueType != null ? $" -> {kind.ValueType.Type} {kind.ValueType.StructType ?? kind.ValueType.EnumName}" : ""));
        }
    }

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
