using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Paldium.Import.Migration;

/// <summary>
/// The app's data folders: <c>%APPDATA%\Paldium</c> (settings, My Pals data) and <c>%LOCALAPPDATA%\Paldium</c> (logs, caches, the mod's bridge output, imports).
/// When the one-time move from the folders of the former name failed, the old folder stays in use (<see cref="LegacyDataMigration"/> says so here), so the app
/// opens with the user's data instead of an empty profile.
/// </summary>
public static class AppFolders
{
    public const string Name = "Paldium";

    /// <summary>The app's name before T129; used only to find and move the data of that time.</summary>
    public const string LegacyName = "Palbreeding";

    private static string _roamingName = Name;
    private static string _localName = Name;

    public static string Roaming => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), _roamingName);

    public static string Local => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), _localName);

    /// <summary>Switches a folder back to the old name (the move failed and the old folder exists).</summary>
    internal static void UseLegacy(bool roaming, bool local)
    {
        if (roaming) _roamingName = LegacyName;
        if (local) _localName = LegacyName;
    }

    /// <summary>Back to the normal names (tests).</summary>
    public static void Reset()
    {
        _roamingName = Name;
        _localName = Name;
    }
}

/// <param name="Old">The folder of the former name.</param>
/// <param name="New">The folder of the current name.</param>
/// <param name="Copied">The old folder was copied to the new one by this run.</param>
/// <param name="Error">Why the copy failed; null when there was nothing to do or it worked.</param>
public sealed record MigrationFolderResult(string Old, string New, bool Copied, string? Error)
{
    public bool Failed => Error != null;
}

/// <param name="RewrittenPaths">Paths in the copied <c>settings.json</c> that pointed into an old folder and now point into the new one.</param>
public sealed record MigrationResult(MigrationFolderResult Roaming, MigrationFolderResult Local, int RewrittenPaths);

/// <summary>
/// T129: the one-time move of the user's data from the folders of the former name (Palbreeding) to the new ones (Paldium), done at the first start:
/// the old folder is copied (never deleted, so going back is possible) unless the new one already carries the marker file, and the paths in <c>settings.json</c> that point
/// into an old folder are changed to the new one. A failed copy is logged and the app goes on with the old folder.
/// </summary>
public static class LegacyDataMigration
{
    /// <summary>Runs the move for the two folder pairs under the given roots. Never throws; <see cref="MigrationResult"/> tells what happened.</summary>
    public static MigrationResult Run(string roamingRoot, string localRoot, Action<string, Exception?>? log = null, bool applyFallback = true)
    {
        var roamingOld = Path.Combine(roamingRoot, AppFolders.LegacyName);
        var roamingNew = Path.Combine(roamingRoot, AppFolders.Name);
        var localOld = Path.Combine(localRoot, AppFolders.LegacyName);
        var localNew = Path.Combine(localRoot, AppFolders.Name);

        var roaming = CopyFolder(roamingOld, roamingNew, roaming: true, log, writeMarker: false);
        var local = CopyFolder(localOld, localNew, roaming: false, log, writeMarker: true);

        var rewritten = 0;
        if (roaming.Copied)
        {
            var pairs = new List<(string Old, string New)> { (roaming.Old, roaming.New) };
            if (!local.Failed) pairs.Add((local.Old, local.New));
            try
            {
                rewritten = RewriteSettings(Path.Combine(roaming.New, "settings.json"), pairs);
                WriteMarker(roaming.Old, roaming.New);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                log?.Invoke("Paldium: the paths in the copied settings.json could not be changed.", ex);
            }
        }
        else if (local.Copied && !roaming.Failed && Directory.Exists(roaming.New))
        {
            try
            {
                rewritten = RewriteSettings(Path.Combine(roaming.New, "settings.json"), [(local.Old, local.New)]);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                log?.Invoke("Paldium: the local paths in settings.json could not be changed.", ex);
            }
        }

        // A folder whose move failed keeps being used under its old name.
        if (applyFallback) AppFolders.UseLegacy(roaming.Failed && Directory.Exists(roaming.Old), local.Failed && Directory.Exists(local.Old));
        return new MigrationResult(roaming, local, rewritten);
    }

    /// <summary>The move with the real folders.</summary>
    public static MigrationResult Run(Action<string, Exception?>? log = null) => Run(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        log);

    /// <summary>Written into the new folder when the move is done; a folder that exists without it (something created it early) is still moved into.</summary>
    public const string MarkerFile = ".migrated-from-palbreeding";

    private static void WriteMarker(string old, string current) =>
        File.WriteAllText(Path.Combine(current, MarkerFile), "moved from " + old + " at " + DateTimeOffset.Now.ToString("O") + Environment.NewLine);

    private static MigrationFolderResult CopyFolder(string old, string current, bool roaming, Action<string, Exception?>? log, bool writeMarker)
    {
        var marker = Path.Combine(current, MarkerFile);
        if (!Directory.Exists(old) || File.Exists(marker)) return new MigrationFolderResult(old, current, false, null);
        try
        {
            Directory.CreateDirectory(current);
            // Roaming: the old files replace the new ones of the same name (a default file made by an early start stays as .bak).
            // Local: only what the new folder does not have is copied.
            CopyRecursive(old, current, replace: roaming);
            if (writeMarker) WriteMarker(old, current);
            log?.Invoke($"Paldium: moved the data of the former name from {old} to {current} (the old folder was left in place).", null);
            return new MigrationFolderResult(old, current, true, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // No marker: the next start tries again. This run keeps using the old folder.
            log?.Invoke($"Paldium: could not move {old} to {current}; the old folder stays in use.", ex);
            return new MigrationFolderResult(old, current, false, ex.Message);
        }
    }

    private static void CopyRecursive(string source, string target, bool replace)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            var destination = Path.Combine(target, Path.GetFileName(file));
            if (File.Exists(destination))
            {
                if (!replace) continue;
            }

            var temp = destination + ".tmp";
            File.Copy(file, temp, overwrite: true);
            if (File.Exists(destination))
            {
                var bak = destination + ".bak";
                if (!File.Exists(bak)) File.Move(destination, bak);
            }
            File.Move(temp, destination, overwrite: true);
        }

        foreach (var directory in Directory.EnumerateDirectories(source))
            CopyRecursive(directory, Path.Combine(target, Path.GetFileName(directory)), replace);
    }

    /// <summary>Changes every string value that starts with an old folder to start with the new one; returns how many. The file is only written when something changed.</summary>
    public static int RewriteSettings(string settingsFile, IReadOnlyList<(string Old, string New)> folders)
    {
        if (!File.Exists(settingsFile)) return 0;
        var root = JsonNode.Parse(File.ReadAllText(settingsFile));
        if (root == null) return 0;
        var count = Walk(root, folders);
        if (count > 0)
            File.WriteAllText(settingsFile, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        return count;
    }

    private static int Walk(JsonNode node, IReadOnlyList<(string Old, string New)> folders)
    {
        var count = 0;
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(p => p.Key).ToList())
                {
                    if (obj[key] is JsonValue value && value.TryGetValue<string>(out var text))
                    {
                        var changed = Replace(text, folders);
                        if (changed != null) { obj[key] = changed; count++; }
                    }
                    else if (obj[key] is { } child) count += Walk(child, folders);
                }

                break;
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    if (array[i] is JsonValue value && value.TryGetValue<string>(out var text))
                    {
                        var changed = Replace(text, folders);
                        if (changed != null) { array[i] = changed; count++; }
                    }
                    else if (array[i] is { } child) count += Walk(child, folders);
                }

                break;
        }

        return count;
    }

    private static string? Replace(string text, IReadOnlyList<(string Old, string New)> folders)
    {
        foreach (var (old, current) in folders)
        {
            if (!text.StartsWith(old, StringComparison.OrdinalIgnoreCase)) continue;
            // Only a whole folder name counts: "...\Palbreeding" or "...\Palbreeding\bridge\palbox.json", not "...\PalbreedingX".
            if (text.Length > old.Length && text[old.Length] is not ('\\' or '/')) continue;
            return current + text[old.Length..];
        }

        return null;
    }
}
