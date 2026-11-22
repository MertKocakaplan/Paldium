using System.ComponentModel;
using System.Diagnostics;
using Paldium.Updater;

// Paldium.Updater <pid> <appFolder> <stagedFolder> <updatesFolder> <oldVersion>
// Started by Paldium from %LOCALAPPDATA%\Paldium\updates\updater (a copy, so the app folder can be replaced). No window.
if (args.Length < 5 || !int.TryParse(args[0], out var pid)) return 2;

var request = new UpdateRequest(pid, args[1], args[2], args[3], args[4]);
var outcome = UpdateApplier.Apply(
    request,
    waitForExit: (id, timeout) =>
    {
        try
        {
            using var process = Process.GetProcessById(id);
            return process.WaitForExit(timeout);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return true;   // already gone or exited
        }
    },
    start: exe =>
    {
        var started = Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = Path.GetDirectoryName(exe)!, UseShellExecute = true });
        return started != null;
    });
return outcome.Succeeded ? 0 : 1;
