using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using Serilog;

namespace Paldium.App;

public partial class App : Application
{
#if DEBUG
    internal static string? DemoPalsPath { get; private set; }
    internal static (int Width, int Height)? StartupWindowSize { get; private set; }
#endif

    protected override void OnStartup(StartupEventArgs e)
    {
        var selfCheck = Array.IndexOf(e.Args, "--selfcheck");
        if (selfCheck >= 0 && selfCheck + 1 < e.Args.Length)
        {
            var world = selfCheck + 2 < e.Args.Length ? e.Args[selfCheck + 2] : null;
            Shutdown(Services.SelfCheck.Run(e.Args[selfCheck + 1], world));
            return;
        }

#if DEBUG
        ParseDebugArgs(e.Args);
#endif

        // T129: the data of the former name is moved first, before anything creates the new folders (the log goes into one of them).
        var migration = Paldium.Import.Migration.LegacyDataMigration.Run();
        Services.AppServices.ConfigureLogging();
        Log.Information("Paldium starting");
        if (migration.Roaming.Copied || migration.Local.Copied || migration.Roaming.Failed || migration.Local.Failed)
            Log.Information("Data move: settings copied {Roaming}, local data copied {Local}, paths changed {Paths}, errors: {Errors}",
                migration.Roaming.Copied, migration.Local.Copied, migration.RewrittenPaths, new[] { migration.Roaming.Error, migration.Local.Error }.Where(e => e != null));
        if (Services.WebView2Check.InstalledVersion() == null)
        {
            Log.Error("WebView2 runtime not found");
            var open = MessageBox.Show(
                "The Microsoft Edge WebView2 runtime is required but was not found.\n\nOpen the download page now?",
                "Paldium", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (open == MessageBoxResult.Yes)
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Services.WebView2Check.DownloadUrl) { UseShellExecute = true });
            Shutdown(1);
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Fatal(args.ExceptionObject as Exception, "Unhandled exception (terminating: {Terminating})", args.IsTerminating);
        base.OnStartup(e);
    }

#if DEBUG
    private static void ParseDebugArgs(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--demo-pals")
            {
                DemoPalsPath = args[++i];
            }
            else if (args[i] == "--window")
            {
                var spec = args[++i];
                var sep = spec.IndexOfAny(['x', 'X']);
                if (sep > 0
                    && int.TryParse(spec[..sep], NumberStyles.None, CultureInfo.InvariantCulture, out var w)
                    && int.TryParse(spec[(sep + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var h)
                    && w >= 400 && h >= 400)
                {
                    StartupWindowSize = (w, h);
                }
            }
        }
    }
#endif

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Information("Paldium exiting");
        Log.CloseAndFlush();
        base.OnExit(e);
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // The app must not crash on a component error; log it and keep running.
        Log.Error(e.Exception, "Unhandled UI exception");
        e.Handled = true;
    }
}
