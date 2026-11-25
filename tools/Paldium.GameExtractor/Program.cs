using Paldium.GameExtractor;
using Paldium.GameExtractor.Exporters;

// Usage (see README.md):
//   Paldium.GameExtractor list [--prefix <path>] [--contains <text>]... [--paks <dir>] [--usmap <file>]
//   Paldium.GameExtractor dump <assetPath>... [--out <dir>] [--script] [--paks <dir>] [--usmap <file>]
//   Paldium.GameExtractor export --out <dir> [--paks <dir>] [--usmap <file>]
//   Paldium.GameExtractor types <typeName | ~propertyPart>... [--paks <dir>] [--usmap <file>]
//   Paldium.GameExtractor scan-world --out <dir> [--grid MainGrid,Foliage] [--paks <dir>] [--usmap <file>]
// --paks falls back to PALDIUM_PALWORLD_PAKS, --usmap to PALDIUM_USMAP. Game files are only read.
var command = args.Length > 0 ? args[0] : "";
string? paks = Environment.GetEnvironmentVariable("PALDIUM_PALWORLD_PAKS");
string? usmap = Environment.GetEnvironmentVariable("PALDIUM_USMAP");
string? prefix = null;
string? worldDir = null;
string gridArg = "MainGrid,oilrig,CloseRange";
var outFolder = Path.Combine(AppContext.BaseDirectory, "out");
var contains = new List<string>();
var script = false;
var positional = new List<string>();

for (var i = 1; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--paks" when i + 1 < args.Length: paks = args[++i]; break;
        case "--usmap" when i + 1 < args.Length: usmap = args[++i]; break;
        case "--prefix" when i + 1 < args.Length: prefix = args[++i]; break;
        case "--contains" when i + 1 < args.Length: contains.Add(args[++i]); break;
        case "--out" when i + 1 < args.Length: outFolder = args[++i]; break;
        case "--grid" when i + 1 < args.Length: gridArg = args[++i]; break;
        case "--script": script = true; break;
        case "--world" when i + 1 < args.Length: worldDir = args[++i]; break;
        case var a when a.StartsWith("--", StringComparison.Ordinal):
            Console.Error.WriteLine($"Unknown or incomplete option: {a}");
            return 2;
        default: positional.Add(args[i]); break;
    }
}

if (command is not ("list" or "dump" or "export" or "scan-world" or "types") || string.IsNullOrEmpty(paks) || string.IsNullOrEmpty(usmap) ||
    (command is "dump" or "types" && positional.Count == 0))
{
    Console.Error.WriteLine(
        "Usage:\n" +
        "  list [--prefix <path>] [--contains <text>]...  --paks <dir> --usmap <file>\n" +
        "  dump <assetPath>... [--out <dir>]               --paks <dir> --usmap <file>\n" +
        "  export --out <dir> [--world <dir>]              --paks <dir> --usmap <file>\n" +
        "  scan-world --out <dir> [--grid MainGrid,Foliage] --paks <dir> --usmap <file>\n" +
        "  types <typeName>...                              --paks <dir> --usmap <file>\n" +
        "(--paks / --usmap fall back to PALDIUM_PALWORLD_PAKS / PALDIUM_USMAP)");
    return 2;
}

try
{
    Console.WriteLine($"Opening {GameProvider.MainPakName} in {paks} (mod pak files are not loaded)...");
    var provider = GameProvider.Open(paks, usmap);
    Console.WriteLine("Mounted pak files: " + string.Join(", ", provider.MountedVfs.Select(v => v.Name)));
    Console.WriteLine($"Files indexed: {provider.Files.Count}");

    if (command == "list")
    {
        var files = provider.Files.Keys
            .Where(k => prefix == null || k.StartsWith(prefix.Replace('\\', '/').TrimStart('/'), StringComparison.OrdinalIgnoreCase))
            .Where(k => contains.All(c => k.Contains(c, StringComparison.OrdinalIgnoreCase)))
            .Order(StringComparer.Ordinal)
            .ToList();
        foreach (var file in files) Console.WriteLine(file);
        Console.WriteLine($"{files.Count} files");
        return 0;
    }

    if (command == "types")
    {
        // The properties of the game's own types, as the mappings file describes them (e.g. the player record a save and the mod read).
        foreach (var name in positional) TableDumper.PrintType(provider, name);
        return 0;
    }

    if (command == "export")
    {
        ExportCommand.Run(provider, paks, usmap, outFolder, worldDir);
        return 0;
    }

    if (command == "scan-world")
    {
        var grids = gridArg.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        WorldScanExporter.Run(provider, outFolder, grids);
        return 0;
    }

    provider.ReadScriptData = script; // Blueprint bytecode, e.g. the text keys a function reads
    foreach (var asset in positional)
    {
        var written = TableDumper.Dump(provider, asset, outFolder);
        Console.WriteLine($"{asset} -> {written} ({new FileInfo(written).Length / 1024} KB)");
    }

    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine("Error: " + ex);
    return 1;
}
