namespace Paldium.Updater;

/// <param name="ProcessId">The running app that must exit first.</param>
/// <param name="AppFolder">The installed app (where Paldium.exe is).</param>
/// <param name="StagedFolder">The unpacked new version.</param>
/// <param name="UpdatesFolder"><c>%LOCALAPPDATA%\Paldium\updates</c>: the backup and last-error.txt go here.</param>
/// <param name="OldVersion">The version being replaced (the backup folder's name).</param>
public sealed record UpdateRequest(int ProcessId, string AppFolder, string StagedFolder, string UpdatesFolder, string OldVersion);

public sealed record UpdateOutcome(bool Succeeded, string? Error);

/// <summary>
/// Replaces the installed app with the staged new version using a manifest when available.
/// Order: validate folders/version, wait for exit, stage backup atomically, remove obsolete manifest files,
/// copy staged files in, and start the new Paldium.exe.
/// </summary>
public static class UpdateApplier
{
    public const string AppExe = AppFolderSafety.AppExe;
    public const string ManifestFile = AppFolderSafety.ManifestFile;
    public const int DefaultRetryAttempts = 5;
    public static readonly TimeSpan DefaultRetryBaseDelay = TimeSpan.FromMilliseconds(100);

    /// <param name="waitForExit">Waits for the process (id, timeout); false when it is still running.</param>
    /// <param name="start">Starts an executable (path); false when it could not be started.</param>
    /// <param name="step">Called before each step ("backup", "clear", "copy", "start"): tests make a step fail by throwing here.</param>
    public static UpdateOutcome Apply(UpdateRequest request, Func<int, TimeSpan, bool> waitForExit, Func<string, bool> start, Action<string>? step = null) =>
        Apply(request, waitForExit, start, step, DefaultRetryBaseDelay, extraForbiddenFolders: null);

    internal static UpdateOutcome Apply(
        UpdateRequest request,
        Func<int, TimeSpan, bool> waitForExit,
        Func<string, bool> start,
        Action<string>? step,
        TimeSpan retryBaseDelay,
        IEnumerable<string>? extraForbiddenFolders = null)
    {
        var appExited = false;
        var safeToRestart = false;
        var changed = false;
        string? backup = null;
        string? backupTemp = null;
        HashSet<string>? backedUpSet = null;
        IReadOnlyList<string> newRelFiles = [];

        try
        {
            var safetyError = AppFolderSafety.ValidateAppFolder(request.AppFolder, request.UpdatesFolder, extraForbiddenFolders);
            if (safetyError != null) throw new InvalidOperationException(safetyError);
            if (!AppFolderSafety.IsValidVersionFolderName(request.OldVersion))
                throw new ArgumentException("Invalid OldVersion for backup folder.", nameof(request));
            var overlapError = AppFolderSafety.ValidateStagedOverlap(request.AppFolder, request.StagedFolder);
            if (overlapError != null) throw new InvalidOperationException(overlapError);

            safeToRestart = true;

            if (!waitForExit(request.ProcessId, TimeSpan.FromSeconds(30)))
                throw new IOException("The app did not exit in 30 seconds.");
            appExited = true;

            var stagedRoot = AppFolderSafety.ResolveStagedRoot(request.StagedFolder)
                ?? throw new FileNotFoundException("The staged version has no " + AppExe + ".");

            var appFull = Path.GetFullPath(request.AppFolder);
            var stagedFull = Path.GetFullPath(stagedRoot);
            var updatesFull = Path.GetFullPath(request.UpdatesFolder);
            var backupRoot = Path.GetFullPath(Path.Combine(updatesFull, "backup"));
            backup = Path.GetFullPath(Path.Combine(backupRoot, request.OldVersion));
            if (!backup.StartsWith(AppFolderSafety.EnsureTrailingSeparator(backupRoot), StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Backup path escapes updates folder.", nameof(request));

            var oldManifest = ReadManifest(appFull);
            var stagedEntries = EnumerateRelativeFiles(stagedFull);
            newRelFiles = stagedEntries.Select(e => e.Rel).ToList();
            var newFileSet = new HashSet<string>(newRelFiles, StringComparer.OrdinalIgnoreCase);

            IEnumerable<string> managedCandidates = oldManifest != null
                ? oldManifest.Union(newFileSet, StringComparer.OrdinalIgnoreCase)
                : newFileSet;

            var filesToBackup = new List<(string Rel, string SrcFull)>();
            foreach (var rel in managedCandidates)
            {
                if (AppFolderSafety.TryResolveWithinRoot(appFull, rel, out var src) && File.Exists(src))
                    filesToBackup.Add((rel, src));
            }
            backedUpSet = new HashSet<string>(filesToBackup.Select(f => f.Rel), StringComparer.OrdinalIgnoreCase);

            step?.Invoke("backup");
            Directory.CreateDirectory(backupRoot);
            backupTemp = Path.Combine(backupRoot, request.OldVersion + ".tmp-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(backupTemp);
            foreach (var (rel, src) in filesToBackup)
            {
                var dst = Path.Combine(backupTemp, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                RetryIo(() => File.Copy(src, dst, overwrite: true), retryBaseDelay);
            }

            string? backupOld = null;
            if (Directory.Exists(backup))
            {
                backupOld = Path.Combine(backupRoot, request.OldVersion + ".old-" + Guid.NewGuid().ToString("N"));
                RetryIo(() => Directory.Move(backup, backupOld), retryBaseDelay);
            }
            try
            {
                RetryIo(() => Directory.Move(backupTemp, backup), retryBaseDelay);
                backupTemp = null;
            }
            catch
            {
                if (backupOld != null && Directory.Exists(backupOld) && !Directory.Exists(backup))
                {
                    try { Directory.Move(backupOld, backup); } catch { /* best effort */ }
                }
                throw;
            }
            if (backupOld != null) TryDeleteDirectory(backupOld);

            changed = true;
            step?.Invoke("clear");
            if (oldManifest != null)
            {
                var dirsToPrune = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var rel in oldManifest)
                {
                    if (!AppFolderSafety.TryResolveWithinRoot(appFull, rel, out var oldFile) || !File.Exists(oldFile)) continue;
                    RetryIo(() => File.Delete(oldFile), retryBaseDelay);
                    CollectParentDirs(appFull, oldFile, dirsToPrune);
                }
                PruneEmptyDirectories(dirsToPrune);
            }

            step?.Invoke("copy");
            foreach (var (rel, srcFull) in stagedEntries)
            {
                if (!AppFolderSafety.TryResolveWithinRoot(appFull, rel, out var dstFull))
                    throw new InvalidDataException($"Staged file escapes app folder: {rel}");
                Directory.CreateDirectory(Path.GetDirectoryName(dstFull)!);
                RetryIo(() => File.Copy(srcFull, dstFull, overwrite: true), retryBaseDelay);
            }

            step?.Invoke("start");
            if (!start(Path.Combine(appFull, AppExe)))
                throw new IOException("The new version could not be started.");

            TryDeleteFile(Path.Combine(updatesFull, "last-error.txt"));
            return new UpdateOutcome(true, null);
        }
        catch (Exception ex)
        {
            if (backupTemp != null) TryDeleteDirectory(backupTemp);
            if (changed && backup != null)
                Restore(request.AppFolder, backup, newRelFiles, backedUpSet ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase), retryBaseDelay);
            WriteError(request.UpdatesFolder, ex);
            if (appExited && safeToRestart)
            {
                try { start(Path.Combine(request.AppFolder, AppExe)); }
                catch (Exception) { /* nothing more can be done; last-error.txt tells the story */ }
            }
            return new UpdateOutcome(false, ex.Message);
        }
    }

    private static void Restore(
        string appFolder,
        string backup,
        IReadOnlyList<string> newRelFiles,
        HashSet<string> backedUpSet,
        TimeSpan retryBaseDelay)
    {
        if (!Directory.Exists(backup)) return;
        var appFull = Path.GetFullPath(appFolder);

        // Remove newly added files that did not exist before the update; ignore locked-file failures so remaining files still restore.
        var dirsToPrune = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rel in newRelFiles)
        {
            if (backedUpSet.Contains(rel)) continue;
            if (!AppFolderSafety.TryResolveWithinRoot(appFull, rel, out var dst) || !File.Exists(dst)) continue;
            try
            {
                RetryIo(() => File.Delete(dst), retryBaseDelay);
                CollectParentDirs(appFull, dst, dirsToPrune);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Keep going so every other file still gets restored.
            }
        }
        PruneEmptyDirectories(dirsToPrune);

        // Overwrite backed-up files in place even if delete during Clear failed on a locked file.
        foreach (var backupFile in Directory.EnumerateFiles(backup, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(backup, backupFile);
            if (!AppFolderSafety.TryResolveWithinRoot(appFull, rel, out var dst)) continue;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                RetryIo(() => File.Copy(backupFile, dst, overwrite: true), retryBaseDelay);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Keep restoring the remaining files; backup remains intact for manual recovery if needed.
            }
        }
    }

    private static HashSet<string>? ReadManifest(string appFull)
    {
        var manifestPath = Path.Combine(appFull, ManifestFile);
        if (!File.Exists(manifestPath)) return null;

        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ManifestFile };
        foreach (var rawLine in File.ReadAllLines(manifestPath))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var normalized = line.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
            if (!AppFolderSafety.TryResolveWithinRoot(appFull, normalized, out var resolved))
                throw new InvalidDataException($"Manifest entry escapes app folder: {line}");
            set.Add(Path.GetRelativePath(appFull, resolved));
        }
        return set;
    }

    private static List<(string Rel, string Full)> EnumerateRelativeFiles(string rootFull)
    {
        var list = new List<(string Rel, string Full)>();
        foreach (var file in Directory.EnumerateFiles(rootFull, "*", SearchOption.AllDirectories))
            list.Add((Path.GetRelativePath(rootFull, file), file));
        return list;
    }

    private static void RetryIo(Action action, TimeSpan baseDelay)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < DefaultRetryAttempts)
            {
                if (baseDelay > TimeSpan.Zero)
                    Thread.Sleep(TimeSpan.FromMilliseconds(baseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1)));
            }
        }
    }

    private static void CollectParentDirs(string appFull, string filePath, HashSet<string> dirs)
    {
        var appWithSep = AppFolderSafety.EnsureTrailingSeparator(appFull);
        var dir = Path.GetDirectoryName(filePath);
        while (!string.IsNullOrEmpty(dir))
        {
            var full = Path.GetFullPath(dir);
            if (!full.StartsWith(appWithSep, StringComparison.OrdinalIgnoreCase)) break;
            dirs.Add(full);
            dir = Path.GetDirectoryName(full);
        }
    }

    private static void PruneEmptyDirectories(HashSet<string> dirs)
    {
        foreach (var dir in dirs.OrderByDescending(d => d.Length))
        {
            try
            {
                if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                    Directory.Delete(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Ignore if a directory cannot be removed.
            }
        }
    }

    private static void WriteError(string updatesFolder, Exception ex)
    {
        try
        {
            Directory.CreateDirectory(updatesFolder);
            File.WriteAllText(Path.Combine(updatesFolder, "last-error.txt"),
                $"{DateTimeOffset.Now:O}{Environment.NewLine}{ex.GetType().Name}: {ex.Message}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // Nothing else to do.
        }
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
