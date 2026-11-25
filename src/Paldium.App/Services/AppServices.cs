using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MudBlazor.Services;
using Paldium.Core.Breeding;
using Paldium.Core.Data;
using Paldium.Core.Owned;
using Paldium.Import.Saves;
using Paldium.Import.Sync;
using Serilog;

namespace Paldium.App.Services;

/// <summary>Composition root: logging, settings, data bundle and Blazor services.</summary>
public static class AppServices
{
    public static string LocalDataFolder => Paldium.Import.Migration.AppFolders.Local;

    public static string LogFolder => Path.Combine(LocalDataFolder, "logs");

    public static void ConfigureLogging()
    {
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                Path.Combine(LogFolder, "app-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                shared: true)
            .CreateLogger();
    }

    public static IServiceProvider Build(string? demoPalsPath = null)
    {
        var services = new ServiceCollection();
        services.AddWpfBlazorWebView();
        services.AddMudServices(config =>
        {
            // Notifications: bottom right, six seconds, with a close icon (screens/layout.md).
            config.SnackbarConfiguration.PositionClass = MudBlazor.Defaults.Classes.Position.BottomRight;
            config.SnackbarConfiguration.VisibleStateDuration = 6000;
            config.SnackbarConfiguration.ShowCloseIcon = true;
        });
        services.AddSingleton<Microsoft.Extensions.Localization.IStringLocalizer<Paldium.App.Resources.Strings>, LanguageStringLocalizer>();
        services.AddLogging(b => b.AddSerilog(Log.Logger, dispose: false));

#if DEBUG
        var isDemo = !string.IsNullOrWhiteSpace(demoPalsPath);
        services.AddSingleton<SettingsStore>(sp =>
        {
            var path = isDemo ? Path.Combine(Path.GetTempPath(), "paldium-demo-settings.json") : null;
            return new SettingsStore(path, log: sp.GetRequiredService<ILogger<SettingsStore>>());
        });
#else
        services.AddSingleton<SettingsStore>(sp =>
            new SettingsStore(log: sp.GetRequiredService<ILogger<SettingsStore>>()));
#endif
        services.AddSingleton<LanguageService>();
        services.AddSingleton<GameTerms>(sp => new GameTerms(
            () => sp.GetRequiredService<AppState>().Data,
            sp.GetRequiredService<LanguageService>(),
            sp.GetRequiredService<Microsoft.Extensions.Localization.IStringLocalizer<Paldium.App.Resources.Strings>>()));
        services.AddSingleton<AppState>(sp =>
            AppState.Load(Path.Combine(AppContext.BaseDirectory, "data"), sp.GetRequiredService<ILogger<AppState>>()));

        // Pages inject IGameData / IBreedingCalculator; they are only rendered when the bundle loaded.
        services.AddSingleton<IGameData>(sp =>
            sp.GetRequiredService<AppState>().Data ?? throw new InvalidOperationException("Game data is not available."));
        services.AddSingleton<Paldium.Core.Probability.IPassiveOddsCalculator>(sp =>
            new Paldium.Core.Probability.PassiveOddsCalculator(sp.GetRequiredService<IGameData>()));
        services.AddSingleton<Paldium.Core.Planning.PassiveSolver>(sp => new Paldium.Core.Planning.PassiveSolver(
            sp.GetRequiredService<IGameData>(), sp.GetRequiredService<IBreedingCalculator>(),
            (Paldium.Core.Probability.PassiveOddsCalculator)sp.GetRequiredService<Paldium.Core.Probability.IPassiveOddsCalculator>()));
        services.AddSingleton<PlannerStateService>();
        services.AddSingleton<MyPalsStateService>();
        services.AddSingleton<CalculatorStateService>();
        services.AddSingleton<SkillsStateService>();
        services.AddSingleton<GuideStateService>();
        services.AddSingleton<TechnologyStateService>();
        services.AddSingleton<MapStateService>();
        services.AddSingleton(sp => new MapDataService(Path.Combine(AppContext.BaseDirectory, "data"),
            Path.Combine(AppContext.BaseDirectory, "wwwroot", "gamedata", "map"), sp.GetRequiredService<ILogger<MapDataService>>()));
        services.AddSingleton(sp =>
        {
            var version = (System.Reflection.Assembly.GetEntryAssembly() ?? typeof(AppServices).Assembly)
                .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion?.Split('+')[0] ?? "0.0.0";
#if DEBUG
            const bool autoAllowed = false;   // no automatic requests in Debug builds and the demo mode
#else
            const bool autoAllowed = true;
#endif
#if DEBUG
            // Debug only: PALDIUM_FAKE_UPDATE=<version> answers the check with a made-up release (screenshots; no request leaves the PC).
            System.Net.Http.HttpMessageHandler handler = Environment.GetEnvironmentVariable("PALDIUM_FAKE_UPDATE") is { Length: > 0 } fake
                ? new FakeReleaseHandler(fake)
                : new System.Net.Http.SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(15) };
#else
            var handler = new System.Net.Http.SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(15) };
#endif
            return new UpdateService(handler, sp.GetRequiredService<SettingsStore>(), version,
                Path.Combine(Paldium.Import.Migration.AppFolders.Local, "updates"), AppContext.BaseDirectory, autoAllowed,
                log: sp.GetRequiredService<ILogger<UpdateService>>());
        });
        services.AddSingleton<Paldium.Core.Planning.ISpeciesPathFinder>(sp => new Paldium.Core.Planning.SpeciesPathFinder(
            sp.GetRequiredService<IGameData>(), sp.GetRequiredService<IBreedingCalculator>()));
        services.AddSingleton(_ => Paldium.Core.Breeding.BreedingRules.Load(Path.Combine(AppContext.BaseDirectory, "data")));
        services.AddSingleton<Paldium.Core.Breeding.IMutationCalculator>(sp => new Paldium.Core.Breeding.MutationCalculator(
            sp.GetRequiredService<IGameData>(), sp.GetRequiredService<Paldium.Core.Breeding.BreedingRules>()));
        services.AddSingleton(sp => new GuideService(sp.GetRequiredService<IGameData>(), Path.Combine(AppContext.BaseDirectory, "content", "guides"), sp.GetRequiredService<ILogger<GuideService>>()));
        services.AddSingleton<IBreedingCalculator>(sp =>
            sp.GetRequiredService<AppState>().Breeding ?? throw new InvalidOperationException("Game data is not available."));

        services.AddSingleton<UserDataStore>(sp =>
        {
            var log = sp.GetRequiredService<ILogger<UserDataStore>>();
            var folder = Paldium.Import.Migration.AppFolders.Roaming;
            return new UserDataStore(folder, (message, ex) => log.LogWarning(ex, "{Message}", message));
        });
        services.AddSingleton<IOwnedPalRepository>(sp =>
        {
            var log = sp.GetRequiredService<ILogger<OwnedPalRepository>>();
#if DEBUG
            UserDataStore? store = isDemo ? null : sp.GetRequiredService<UserDataStore>();
#else
            var store = sp.GetRequiredService<UserDataStore>();
#endif
            return new OwnedPalRepository(
                sp.GetRequiredService<IGameData>(),
                store,
                warn: (message, ex) => log.LogWarning(ex, "{Message}", message));
        });
        services.AddSingleton<OwnedScopeService>();
        services.AddSingleton<PreferredPals>();
        services.AddSingleton<PanelWidths>();
        services.AddScoped<PreferredPalActions>();

        services.AddSingleton<ISaveWorldReader>(sp =>
        {
            var log = sp.GetRequiredService<ILogger<SaveWorldReader>>();
            return new SaveWorldReader(
                sp.GetRequiredService<IGameData>(),
                log: (message, ex) => log.LogInformation(ex, "{Message}", message));
        });
        services.AddSingleton<CaptureProgressStore>();
        services.AddSingleton<ISyncService>(sp =>
        {
#if DEBUG
            if (isDemo)
            {
                return new DemoSyncService(
                    demoPalsPath!,
                    sp.GetRequiredService<AppState>(),
                    sp.GetRequiredService<IOwnedPalRepository>(),
                    sp.GetRequiredService<CaptureProgressStore>());
            }
#endif
            var log = sp.GetRequiredService<ILogger<SyncService>>();
            return new SyncService(
                sp.GetRequiredService<ISaveWorldReader>(),
                sp.GetRequiredService<IOwnedPalRepository>(),
                log: (message, ex) => log.LogWarning(ex, "{Message}", message),
                bridge: sp.GetRequiredService<Paldium.Import.Bridge.IModBridgeReader>(),
                captures: sp.GetRequiredService<CaptureProgressStore>());
        });
        services.AddSingleton<Paldium.Import.Bridge.IModBridgeReader>(sp =>
        {
            var log = sp.GetRequiredService<ILogger<Paldium.Import.Bridge.ModBridgeReader>>();
            return new Paldium.Import.Bridge.ModBridgeReader(sp.GetRequiredService<IGameData>(), (message, ex) => log.LogInformation(ex, "{Message}", message),
                sp.GetRequiredService<Paldium.Import.Bridge.BridgePageStore>());
        });
        services.AddSingleton<ModInstallService>();
        services.AddSingleton<Paldium.Import.Bridge.BridgePageStore>(sp =>
        {
            var log = sp.GetRequiredService<ILogger<Paldium.Import.Bridge.BridgePageStore>>();
            return new Paldium.Import.Bridge.BridgePageStore(log: (message, ex) => log.LogWarning(ex, "{Message}", message));
        });
        services.AddSingleton<Paldium.Import.Bridge.BridgeScanService>(_ => new Paldium.Import.Bridge.BridgeScanService());
        services.AddSingleton<Paldium.Import.Bridge.BridgeSettingsStore>(_ => new Paldium.Import.Bridge.BridgeSettingsStore());
        services.AddSingleton<ZipImporter>();
        services.AddSingleton<IFilePickerService, WpfFilePickerService>();
        services.AddSingleton<SourcesService>(sp =>
        {
            var log = sp.GetRequiredService<ILogger<SourcesService>>();
            return new SourcesService(
                sp.GetRequiredService<SettingsStore>(),
                sp.GetRequiredService<ISyncService>(),
                sp.GetRequiredService<ZipImporter>(),
                log: (message, ex) => log.LogWarning(ex, "{Message}", message));
        });

        return services.BuildServiceProvider();
    }
}

#if DEBUG
internal sealed class FakeReleaseHandler(string version) : System.Net.Http.HttpMessageHandler
{
    protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var name = $"Paldium-{version}-win-x64.zip";
        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            tag_name = "v" + version, draft = false, prerelease = false,
            body = string.Join(Environment.NewLine, "## What is new", "- **Technology tab** with every level", "- Update check from GitHub", "- A long path to see the wrap: C:/Users/someone/AppData/Local/Paldium/updates/0.2.0/staged/data/gamedata.json", "", "See [the page](https://example.com)."),
            html_url = "https://github.com/MertKocakaplan/Paldium/releases/tag/v" + version,
            published_at = DateTimeOffset.UtcNow.AddDays(-3).ToString("O"),
            assets = new[] { new { name, size = 98_000_000L, browser_download_url = $"https://github.com/MertKocakaplan/Paldium/releases/download/v{version}/{name}", digest = "sha256:" + new string('a', 64) } },
        });
        return Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new System.Net.Http.StringContent(json), RequestMessage = request });
    }
}
#endif
