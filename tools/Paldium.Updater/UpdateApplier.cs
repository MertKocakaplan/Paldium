namespace Paldium.Updater;

/// <param name="ProcessId">The running app that must exit first.</param>
/// <param name="AppFolder">The installed app (where Paldium.exe is).</param>
/// <param name="StagedFolder">The unpacked new version.</param>
/// <param name="UpdatesFolder"><c>%LOCALAPPDATA%\Paldium\updates</c>: the backup and last-error.txt go here.</param>
/// <param name="OldVersion">The version being replaced (the backup folder's name).</param>
public sealed record UpdateRequest(int ProcessId, string AppFolder, string StagedFolder, string UpdatesFolder, string OldVersion);

public sealed record UpdateOutcome(bool Succeeded, string? Error);

/// <summary>
/// T131: replaces the installed app by the staged new version. Order: wait for the app to exit, copy the app folder to a backup, empty it, copy the staged files in,
/// start the new Paldium.exe. Any failure puts the backup back, writes <c>last-error.txt</c> and starts the old version. The user's data is not in the app folder
/// and is never touched.
/// </summary>
public static class UpdateApplier
{
    public const string AppExe = "Paldium.exe";

    /// <param name="waitForExit">Waits for the process (id, timeout); false when it is still running.</param>
    /// <param name="start">Starts an executable (path); false when it could not be started.</param>
    /// <param name="step">Called before each step ("backup", "clear", "copy", "start"): tests make a step fail by throwing here.</param>
    public static UpdateOutcome Apply(UpdateRequest request, Func<int, TimeSpan, bool> waitForExit, Func<string, bool> start, Action<string>? step = null)
    {
        var backup = Path.Combine(request.UpdatesFolder, "backup", request.OldVersion);
        var changed = false;
        try
        {
            if (!waitForExit(request.ProcessId, TimeSpan.FromSeconds(30))) throw new IOException("The app did not exit in 30 seconds.");
            if (!File.Exists(Path.Combine(request.StagedFolder, AppExe))) throw new FileNotFoundException("The staged version has no " + AppExe + ".");

            step?.Invoke("backup");
            if (Directory.Exists(backup)) Directory.Delete(backup, recursive: true);
            CopyTree(request.AppFolder, backup);

            changed = true;
            step?.Invoke("clear");
            Clear(request.AppFolder);
            step?.Invoke("copy");
            CopyTree(request.StagedFolder, request.AppFolder);

            step?.Invoke("start");
            if (!start(Path.Combine(request.AppFolder, AppExe))) throw new IOException("The new version could not be started.");
            return new UpdateOutcome(true, null);
        }
        catch (Exception ex)
        {
            Restore(request, backup, changed);
            WriteError(request.UpdatesFolder, ex);
            try { start(Path.Combine(request.AppFolder, AppExe)); }
            catch (Exception) { /* nothing more can be done; last-error.txt tells the story */ }
            return new UpdateOutcome(false, ex.Message);
        }
    }

    private static void Restore(UpdateRequest request, string backup, bool changed)
    {
        if (!changed || !Directory.Exists(backup)) return;
        try
        {
            Clear(request.AppFolder);
            CopyTree(backup, request.AppFolder);
        }
        catch (Exception)
        {
            // The backup folder is still there for a manual restore.
        }
    }

    private static void WriteError(string updatesFolder, Exception ex)
    {
        try
        {
            Directory.CreateDirectory(updatesFolder);
            File.WriteAllText(Path.Combine(updatesFolder, "last-error.txt"), $"{DateTimeOffset.Now:O}{Environment.NewLine}{ex.GetType().Name}: {ex.Message}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // Nothing else to do.
        }
    }

    private static void Clear(string folder)
    {
        foreach (var file in Directory.EnumerateFiles(folder)) File.Delete(file);
        foreach (var directory in Directory.EnumerateDirectories(folder)) Directory.Delete(directory, recursive: true);
    }

    private static void CopyTree(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source)) File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        foreach (var directory in Directory.EnumerateDirectories(source)) CopyTree(directory, Path.Combine(target, Path.GetFileName(directory)));
    }
}
