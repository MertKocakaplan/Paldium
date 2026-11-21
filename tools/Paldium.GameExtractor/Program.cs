using Paldium.GameExtractor;

// Usage (see README.md):
//   Paldium.GameExtractor list [--prefix <path>] [--contains <text>]... [--paks <dir>] [--usmap <file>]
//   Paldium.GameExtractor dump <assetPath>... [--out <dir>] [--paks <dir>] [--usmap <file>]
//   Paldium.GameExtractor export --out <dir> [--paks <dir>] [--usmap <file>]
// --paks falls back to PALDIUM_PALWORLD_PAKS, --usmap to PALDIUM_USMAP. Game files are only read.
var command = args.Length > 0 ? args[0] : "";
string? paks = Environment.GetEnvironmentVariable("PALDIUM_PALWORLD_PAKS");
string? usmap = Environment.GetEnvironmentVariable("PALDIUM_USMAP");
string? prefix = null;
var outFolder = Path.Combine(AppContext.BaseDirectory, "out");
var contains = new List<string>();
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
        case var a when a.StartsWith("--", StringComparison.Ordinal):
            Console.Error.WriteLine($"Unknown or incomplete option: {a}");
            return 2;
        default: positional.Add(args[i]); break;
    }
}

if (command is not ("list" or "dump" or "export") || string.IsNullOrEmpty(paks) || string.IsNullOrEmpty(usmap) ||
    (command == "dump" && positional.Count == 0))
{
    Console.Error.WriteLine(
        "Usage:\n" +
        "  list [--prefix <path>] [--contains <text>]...  --paks <dir> --usmap <file>\n" +
        "  dump <assetPath>... [--out <dir>]               --paks <dir> --usmap <file>\n" +
        "  export --out <dir>                              --paks <dir> --usmap <file>\n" +
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

    if (command == "export")
    {
        ExportCommand.Run(provider, paks, usmap, outFolder);
        return 0;
    }

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
