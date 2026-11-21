using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Paldium.App.Services;

namespace Paldium.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
#if DEBUG
        var demoPalsPath = App.DemoPalsPath;
        var services = AppServices.Build(demoPalsPath);
        if (App.StartupWindowSize is { } size)
        {
            Width = size.Width;
            Height = size.Height;
        }
#else
        var services = AppServices.Build();
#endif
        // Resolve the language first so the culture is applied before any component renders.
        services.GetRequiredService<LanguageService>();

        var state = services.GetRequiredService<AppState>();
        var startedSources = false;
        void StartSourcesOnce()
        {
            if (startedSources || !state.IsReady) return;
            startedSources = true;
#if DEBUG
            if (!string.IsNullOrWhiteSpace(demoPalsPath))
            {
                ((DemoSyncService)services.GetRequiredService<Paldium.Import.Sync.ISyncService>()).LoadInitial();
                return;
            }
#endif
            services.GetRequiredService<SourcesService>().Start();
        }

        // Start reading sources now if ready, or once Retry succeeds on ErrorPage.
        StartSourcesOnce();
        state.Changed += (_, _) => StartSourcesOnce();
        Resources.Add("services", services);

        InitializeComponent();
    }
}
