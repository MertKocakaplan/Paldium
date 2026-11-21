using System.IO.Compression;

using Paldium.Import.Migration;

namespace Paldium.Import.Sync;

/// <summary>Where a zip was extracted and which folder inside it is the world.</summary>
public sealed record ZipImportResult(string ExtractedRoot, string WorldFolder);

/// <summary>
/// Extracts a zip that contains a Palworld world (for example one a friend sent) under the imports folder and finds
/// the folder with <c>Level.sav</c> in it (ARCHITECTURE 6.2, T12).
/// </summary>
public sealed class ZipImporter
{
    private readonly string _importsRoot;

    /// <param name="importsRoot">Default: <c>%LOCALAPPDATA%\Paldium\imports</c>.</param>
    public ZipImporter(string? importsRoot = null)
    {
        _importsRoot = importsRoot ?? DefaultRoot;
    }

    public static string DefaultRoot { get; } = Path.Combine(
        AppFolders.Local, "imports");

    public string ImportsRoot => _importsRoot;

    /// <exception cref="InvalidDataException">The zip is unreadable or holds no Level.sav (the extracted files are removed).</exception>
    public ZipImportResult Import(string zipPath)
    {
        var root = UniqueFolder(Path.GetFileNameWithoutExtension(zipPath));
        Directory.CreateDirectory(root);
        try
        {
            Extract(zipPath, root);
            var world = FindWorldFolder(root, maxDepth: 6)
                        ?? throw new InvalidDataException("The zip does not contain a world (no Level.sav found).");
            return new ZipImportResult(root, world);
        }
        catch
        {
            TryDelete(root);
            throw;
        }
    }

    /// <summary>
    /// The shallowest folder at or below <paramref name="folder"/> that contains <c>Level.sav</c>, or null.
    /// Used for zips and for folders the user picks (the world folder itself or a parent of it).
    /// </summary>
    public static string? FindWorldFolder(string folder, int maxDepth = 3)
    {
        var queue = new Queue<(string Path, int Depth)>();
        queue.Enqueue((folder, 0));
        while (queue.Count > 0)
        {
            var (path, depth) = queue.Dequeue();
            if (File.Exists(Path.Combine(path, "Level.sav"))) return path;
            if (depth >= maxDepth) continue;

            foreach (var child in Directory.EnumerateDirectories(path).Order(StringComparer.Ordinal))
            {
                // Backups hold older copies of the same world; never pick them.
                if (string.Equals(Path.GetFileName(child), "backup", StringComparison.OrdinalIgnoreCase)) continue;
                queue.Enqueue((child, depth + 1));
            }
        }

        return null;
    }

    private static void Extract(string zipPath, string root)
    {
        var rootFull = Path.GetFullPath(root);
        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            // Zip-slip guard: an entry must stay inside the target folder.
            var target = Path.GetFullPath(Path.Combine(rootFull, entry.FullName));
            if (!target.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                continue;

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(target);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
        }
    }

    private string UniqueFolder(string name)
    {
        var safe = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
        if (safe.Length == 0) safe = "import";

        var candidate = Path.Combine(_importsRoot, safe);
        for (var i = 2; Directory.Exists(candidate); i++)
            candidate = Path.Combine(_importsRoot, $"{safe}-{i}");
        return candidate;
    }

    /// <summary>Deletes a folder created by <see cref="Import"/> (only below the imports root).</summary>
    public bool TryRemove(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetFullPath(_importsRoot) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return false;

        // The world folder may be nested; remove the top-level import folder.
        var relative = full[root.Length..];
        var top = relative.Split(Path.DirectorySeparatorChar, 2)[0];
        return TryDelete(Path.Combine(_importsRoot, top));
    }

    private static bool TryDelete(string folder)
    {
        try
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
