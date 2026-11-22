using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace Paldium.Updater;

/// <summary>
/// Validates app and staged folders before automatic update operations so user directories or overlapping paths are never touched.
/// </summary>
internal static class AppFolderSafety
{
    public const string AppExe = "Paldium.exe";
    public const string ManifestFile = "app-files.txt";
    private static readonly Guid DownloadsFolderId = new("374DE290-123F-4565-9164-39C4925E467B");

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath(ref Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr pszPath);

    /// <summary>Returns null when <paramref name="appFolder"/> is safe to update; otherwise returns a brief reason.</summary>
    public static string? ValidateAppFolder(string appFolder, string updatesFolder, IEnumerable<string>? extraForbidden = null)
    {
        if (string.IsNullOrWhiteSpace(appFolder) || !Directory.Exists(appFolder))
            return "The app folder does not exist.";

        var appFull = Path.GetFullPath(appFolder);
        if (!File.Exists(Path.Combine(appFull, AppExe)))
            return AppExe + " was not found in the app folder.";

        var appTrimmed = Path.TrimEndingDirectorySeparator(appFull);
        var root = Path.GetPathRoot(appTrimmed);
        if (string.IsNullOrEmpty(root) || string.Equals(appTrimmed, Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase))
            return "Refused to update in a drive root.";

        foreach (var forbidden in EnumerateForbiddenFolders(extraForbidden))
        {
            if (string.IsNullOrWhiteSpace(forbidden)) continue;
            var forbiddenTrimmed = Path.TrimEndingDirectorySeparator(Path.GetFullPath(forbidden));
            if (string.Equals(appTrimmed, forbiddenTrimmed, StringComparison.OrdinalIgnoreCase))
                return "Refused to update directly inside a protected user folder.";
        }

        if (!string.IsNullOrWhiteSpace(updatesFolder))
        {
            var updatesFull = Path.GetFullPath(updatesFolder);
            if (IsSameOrChild(appFull, updatesFull) || IsSameOrChild(updatesFull, appFull))
                return "App folder overlaps the updates folder.";
        }

        return null;
    }

    public static string? ValidateStagedOverlap(string appFolder, string stagedFolder)
    {
        if (string.IsNullOrWhiteSpace(stagedFolder) || !Directory.Exists(stagedFolder))
            return "Staged folder does not exist.";
        var appFull = Path.GetFullPath(appFolder);
        var stagedFull = Path.GetFullPath(stagedFolder);
        if (IsSameOrChild(appFull, stagedFull) || IsSameOrChild(stagedFull, appFull))
            return "App folder overlaps the staged folder.";
        return null;
    }

    public static bool IsValidVersionFolderName(string? version)
    {
        if (string.IsNullOrWhiteSpace(version) || version is "." or "..") return false;
        if (version.IndexOfAny(['/', '\\', ':', '\0']) >= 0) return false;
        if (version.Contains("..", StringComparison.Ordinal)) return false;
        return version.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
    }

    /// <summary>
    /// Resolves either a flat staged folder (<c>Paldium.exe</c> at root) or a nested single top-level directory (<c>Paldium/Paldium.exe</c>).
    /// </summary>
    public static string? ResolveStagedRoot(string stagedFolder, bool requireGameData = false)
    {
        if (string.IsNullOrWhiteSpace(stagedFolder) || !Directory.Exists(stagedFolder)) return null;
        if (HasRequiredFiles(stagedFolder, requireGameData)) return stagedFolder;
        if (Directory.EnumerateFiles(stagedFolder).Any()) return null;
        var dirs = Directory.GetDirectories(stagedFolder);
        if (dirs.Length == 1 && HasRequiredFiles(dirs[0], requireGameData)) return dirs[0];
        return null;
    }

    public static bool TryResolveWithinRoot(string rootFull, string relativePath, out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath)) return false;
        var combined = Path.GetFullPath(Path.Combine(rootFull, relativePath));
        var rootWithSep = EnsureTrailingSeparator(rootFull);
        if (!combined.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase)) return false;
        fullPath = combined;
        return true;
    }

    public static bool IsSameOrChild(string candidatePath, string parentPath)
    {
        var candidateTrimmed = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidatePath));
        var parentTrimmed = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parentPath));
        if (string.Equals(candidateTrimmed, parentTrimmed, StringComparison.OrdinalIgnoreCase)) return true;
        return EnsureTrailingSeparator(candidatePath).StartsWith(EnsureTrailingSeparator(parentPath), StringComparison.OrdinalIgnoreCase);
    }

    public static string EnsureTrailingSeparator(string path)
    {
        var full = Path.GetFullPath(path);
        return Path.EndsInDirectorySeparator(full) ? full : full + Path.DirectorySeparatorChar;
    }

    private static bool HasRequiredFiles(string folder, bool requireGameData) =>
        File.Exists(Path.Combine(folder, AppExe)) &&
        (!requireGameData || File.Exists(Path.Combine(folder, "data", "gamedata.json")));

    private static IEnumerable<string?> EnumerateForbiddenFolders(IEnumerable<string>? extraForbidden)
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(userProfile))
            userProfile = Environment.GetEnvironmentVariable("USERPROFILE") ?? string.Empty;

        yield return userProfile;
        yield return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.Personal);
        if (!string.IsNullOrWhiteSpace(userProfile))
            yield return Path.Combine(userProfile, "Downloads");

        if (OperatingSystem.IsWindows())
        {
            var downloads = TryGetKnownDownloadsFolder();
            if (!string.IsNullOrWhiteSpace(downloads))
                yield return downloads;
        }

        if (extraForbidden != null)
        {
            foreach (var f in extraForbidden)
                yield return f;
        }
    }

    private static string? TryGetKnownDownloadsFolder()
    {
        var ptr = IntPtr.Zero;
        try
        {
            var id = DownloadsFolderId;
            if (SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out ptr) == 0 && ptr != IntPtr.Zero)
                return Marshal.PtrToStringUni(ptr);
        }
        catch
        {
            // Fall back to %USERPROFILE%\Downloads when known-folder query is unavailable.
        }
        finally
        {
            if (ptr != IntPtr.Zero) Marshal.FreeCoTaskMem(ptr);
        }

        return null;
    }
}
