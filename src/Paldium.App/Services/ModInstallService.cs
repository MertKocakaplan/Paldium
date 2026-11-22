using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace Paldium.App.Services;

/// <summary>Why the mod could not be installed; <see cref="Key"/> is a localization key.</summary>
public sealed class ModInstallException(string key, string detail) : Exception(detail)
{
    public const string NoGame = "Mod.NoGame";
    public const string NoUe4ss = "Mod.NoUe4ss";
    public const string NoSource = "Mod.NoSource";
    public const string CopyFailed = "Mod.CopyFailed";
    public const string VerifyFailed = "Mod.VerifyFailed";
    public const string OldRemoveFailed = "Mod.OldRemoveFailed";

    public string Key { get; } = key;
}

/// <summary>Whether the mod in the game folder is the one that ships with the app (T57).</summary>
public enum ModInstallState
{
    NotInstalled,
    Current,
    Outdated,

    /// <summary>Only the mod of the former name is installed (T129): "Install mod" replaces it.</summary>
    LegacyInstalled,
}

/// <summary>
/// Copies the PaldiumBridge mod folder (including enabled.txt) to <c>Palworld\Mods\NativeMods\UE4SS\Mods\PaldiumBridge</c>
/// (T27: the place where UE4SS loads a hand-installed Lua mod; not ManagedMods). Only that one folder is written.
/// </summary>
public sealed class ModInstallService
{
    public const string ModName = "PaldiumBridge";

    /// <summary>The mod name before T129; only used to find and remove it.</summary>
    public const string LegacyModName = "PalbreedingBridge";
    private const string GameProcess = "Palworld-Win64-Shipping";

    private readonly string _sourceFolder;
    private readonly Func<IReadOnlyList<string>> _steamLibraries;
    private readonly Func<bool> _gameRunning;

    public ModInstallService(string? sourceFolder = null, Func<IReadOnlyList<string>>? steamLibraries = null, Func<bool>? gameRunning = null)
    {
        _sourceFolder = sourceFolder ?? Path.Combine(AppContext.BaseDirectory, "mod", ModName);
        _steamLibraries = steamLibraries ?? FindSteamLibraries;
        _gameRunning = gameRunning ?? (() => Process.GetProcessesByName(GameProcess).Length > 0);
    }

    public bool IsGameRunning() => _gameRunning();

    public const string SteamAppId = "1623730";

    /// <summary>
    /// The Palworld folder: the one the user picked by hand, else the Steam library that really has the game installed, i.e. the one with
    /// <c>steamapps\appmanifest_1623730.acf</c> (an old leftover copy on another drive has none). If no manifest is found, a library whose
    /// folder holds <c>Palworld-Win64-Shipping.exe</c> is accepted.
    /// </summary>
    public string? FindGameFolder(string? picked)
    {
        if (IsGameFolder(picked)) return picked;

        var withExe = new List<string>();
        foreach (var library in _steamLibraries())
        {
            var common = Path.Combine(library, "steamapps", "common");
            var manifest = Path.Combine(library, "steamapps", $"appmanifest_{SteamAppId}.acf");
            if (File.Exists(manifest))
            {
                var candidate = Path.Combine(common, InstallDirOf(manifest) ?? "Palworld");
                if (IsGameFolder(candidate)) return candidate;
            }

            var fallback = Path.Combine(common, "Palworld");
            if (File.Exists(Path.Combine(fallback, "Pal", "Binaries", "Win64", "Palworld-Win64-Shipping.exe"))) withExe.Add(fallback);
        }

        return withExe.FirstOrDefault();
    }

    private static string? InstallDirOf(string manifest)
    {
        try
        {
            var match = Regex.Match(File.ReadAllText(manifest), "\"installdir\"\\s+\"([^\"]+)\"");
            return match.Success ? match.Groups[1].Value : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static bool IsGameFolder(string? folder) =>
        !string.IsNullOrEmpty(folder) && Directory.Exists(Path.Combine(folder, "Pal"));

    /// <summary>Where the mod goes for a game folder.</summary>
    public static string TargetFolder(string gameFolder) =>
        Path.Combine(gameFolder, "Mods", "NativeMods", "UE4SS", "Mods", ModName);

    public bool IsInstalled(string gameFolder) => File.Exists(Path.Combine(TargetFolder(gameFolder), "Scripts", "main.lua"));

    /// <summary>
    /// Compares the installed mod with the one that comes with the app: every file of the shipped folder must exist in the installed folder with the
    /// same content (SHA-256). Files that only the installed folder has (the bridge output) are ignored.
    /// </summary>
    public ModInstallState GetState(string gameFolder)
    {
        var target = TargetFolder(gameFolder);
        var legacy = HasLegacyMod(gameFolder);
        if (!File.Exists(Path.Combine(target, "Scripts", "main.lua"))) return legacy ? ModInstallState.LegacyInstalled : ModInstallState.NotInstalled;
        // Both installed: two bridges would run at once, so the new one counts as outdated until the old one is removed.
        return SameContent(_sourceFolder, target) && !legacy ? ModInstallState.Current : ModInstallState.Outdated;
    }

    private static string LegacyFolder(string gameFolder) => Path.Combine(Path.GetDirectoryName(TargetFolder(gameFolder))!, LegacyModName);

    /// <summary>The mod of the former name is in the game folder (its folder, or an entry in the UE4SS mods.txt / mods.json).</summary>
    public static bool HasLegacyMod(string gameFolder)
    {
        var mods = Path.GetDirectoryName(TargetFolder(gameFolder))!;
        if (Directory.Exists(LegacyFolder(gameFolder))) return true;
        try
        {
            var txt = Path.Combine(mods, "mods.txt");
            if (File.Exists(txt) && File.ReadLines(txt).Any(IsLegacyTxtLine)) return true;
            var json = Path.Combine(mods, "mods.json");
            return File.Exists(json) && File.ReadAllText(json).Contains("\"" + LegacyModName + "\"", StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsLegacyTxtLine(string line) =>
        string.Equals(line.Split(':')[0].Trim(), LegacyModName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Removes the mod of the former name: its folder and its entry in mods.txt / mods.json (only that entry; the rest of those files stays as it is).
    /// Two bridges must not run together. Throws <see cref="ModInstallException"/> when something could not be removed.
    /// </summary>
    public void RemoveLegacyMod(string gameFolder)
    {
        var mods = Path.GetDirectoryName(TargetFolder(gameFolder))!;
        try
        {
            if (Directory.Exists(LegacyFolder(gameFolder))) Directory.Delete(LegacyFolder(gameFolder), recursive: true);

            var txt = Path.Combine(mods, "mods.txt");
            if (File.Exists(txt))
            {
                var lines = File.ReadAllLines(txt);
                if (lines.Any(IsLegacyTxtLine)) File.WriteAllLines(txt, lines.Where(l => !IsLegacyTxtLine(l)));
            }

            var json = Path.Combine(mods, "mods.json");
            if (File.Exists(json) && File.ReadAllText(json).Contains("\"" + LegacyModName + "\"", StringComparison.Ordinal))
            {
                var docOptions = new System.Text.Json.JsonDocumentOptions { CommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true };
                var root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(json), documentOptions: docOptions);
                if (root is System.Text.Json.Nodes.JsonArray array)
                {
                    foreach (var entry in array.Where(e => e is System.Text.Json.Nodes.JsonObject obj
                        && obj["mod_name"] is System.Text.Json.Nodes.JsonValue val
                        && val.TryGetValue<string>(out var name)
                        && name == LegacyModName).ToList())
                    {
                        array.Remove(entry);
                    }
                    File.WriteAllText(json, array.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException)
        {
            throw new ModInstallException(ModInstallException.OldRemoveFailed, ex.Message);
        }
    }

    private static bool SameContent(string source, string target)
    {
        if (!Directory.Exists(source)) return false;
        try
        {
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                var installed = Path.Combine(target, Path.GetRelativePath(source, file));
                if (!File.Exists(installed)) return false;
                if (new FileInfo(file).Length != new FileInfo(installed).Length) return false;
                if (!HashOf(file).SequenceEqual(HashOf(installed))) return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static byte[] HashOf(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return System.Security.Cryptography.SHA256.HashData(stream);
    }

    /// <summary>Copies the mod; returns the target folder. The copied files are read again and compared with the shipped ones.</summary>
    public string Install(string gameFolder)
    {
        if (!IsGameFolder(gameFolder)) throw new ModInstallException(ModInstallException.NoGame, gameFolder);
        if (!File.Exists(Path.Combine(_sourceFolder, "Scripts", "main.lua")))
            throw new ModInstallException(ModInstallException.NoSource, _sourceFolder);

        var target = TargetFolder(gameFolder);
        // UE4SS is installed by the Palworld mod manager; without its Mods folder a copy would do nothing.
        var ue4ssMods = Path.GetDirectoryName(target)!;
        if (!Directory.Exists(ue4ssMods)) throw new ModInstallException(ModInstallException.NoUe4ss, ue4ssMods);

        var enabledSource = Path.Combine(_sourceFolder, "enabled.txt");
        var enabledTarget = Path.Combine(target, "enabled.txt");
        try
        {
            CopyFolder(_sourceFolder, target);
            // Keep the new mod disabled until the legacy mod is removed so two bridges cannot load together.
            if (File.Exists(enabledTarget) && HasLegacyMod(gameFolder)) File.Delete(enabledTarget);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ModInstallException(ModInstallException.CopyFailed, ex.Message);
        }

        // The old mod goes before enabling the new one.
        RemoveLegacyMod(gameFolder);
        if (File.Exists(enabledSource) && !File.Exists(enabledTarget))
        {
            try { File.Copy(enabledSource, enabledTarget, overwrite: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new ModInstallException(ModInstallException.CopyFailed, ex.Message); }
        }
        if (GetState(gameFolder) != ModInstallState.Current) throw new ModInstallException(ModInstallException.VerifyFailed, target);
        return target;
    }

    private static void CopyFolder(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        foreach (var directory in Directory.EnumerateDirectories(source))
            CopyFolder(directory, Path.Combine(target, Path.GetFileName(directory)));
    }

    private static string? SteamPathFromRegistry()
    {
        try
        {
            if (!OperatingSystem.IsWindows()) return null;
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            return key?.GetValue("SteamPath") is string path && path.Length > 0 ? path.Replace('/', '\\') : null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    /// <summary>Steam library folders from <c>libraryfolders.vdf</c> (plus the default Steam folder).</summary>
    private static IReadOnlyList<string> FindSteamLibraries()
    {
        var result = new List<string>();
        // The registry knows where Steam is installed (any drive); the default folder is only the last resort.
        var steam = SteamPathFromRegistry();
        if (steam == null || !Directory.Exists(steam)) steam = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");
        if (Directory.Exists(steam)) result.Add(steam);

        try
        {
            var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
            if (File.Exists(vdf))
                foreach (Match match in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                {
                    var path = match.Groups[1].Value.Replace("\\\\", "\\");
                    if (Directory.Exists(path) && !result.Contains(path, StringComparer.OrdinalIgnoreCase)) result.Add(path);
                }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The default folder alone is still tried.
        }

        return result;
    }
}
