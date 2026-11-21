using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Paldium.App.Services;

namespace Paldium.App.Tests;

public class UpdateServiceTests : IDisposable
{
    private const string Download = "https://github.com/MertKocakaplan/Paldium/releases/download/";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "pb-updsvc-" + Guid.NewGuid().ToString("N"));
    private readonly FakeHandler _http = new();
    private DateTimeOffset _now = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

    public UpdateServiceTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "app"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private SettingsStore Settings() => new(Path.Combine(_root, "settings.json"));

    private UpdateService Service(SettingsStore? settings = null, string current = "0.1.0", bool auto = true) =>
        new(_http, settings ?? Settings(), current, Path.Combine(_root, "updates"), Path.Combine(_root, "app"), auto, () => _now);

    private static string ReleaseJson(string tag, bool draft = false, bool prerelease = false, string? digest = null, bool checksumAsset = false, string host = Download)
    {
        var version = tag.TrimStart('v');
        var name = $"Paldium-{version}-win-x64.zip";
        var assets = new List<object> { new { name, size = 100, browser_download_url = host + tag + "/" + name, digest } };
        if (checksumAsset) assets.Add(new { name = name + ".sha256", size = 70, browser_download_url = host + tag + "/" + name + ".sha256" });
        return JsonSerializer.Serialize(new { tag_name = tag, draft, prerelease, body = "## Notes\n- one", html_url = "https://github.com/MertKocakaplan/Paldium/releases/tag/" + tag, published_at = "2026-11-17T10:00:00Z", assets });
    }

    private static byte[] Zip(bool complete = true, char separator = '/')
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string name, string text) { using var w = new StreamWriter(zip.CreateEntry(name).Open()); w.Write(text); }
            Add("Paldium.exe", "exe");
            if (complete) Add("data" + separator + "gamedata.json", "{}");
        }

        return stream.ToArray();
    }

    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    [Theory]
    [InlineData("v0.2.0", "0.1.0", UpdateCheckStatus.Available)]
    [InlineData("v0.1.0", "0.1.0", UpdateCheckStatus.UpToDate)]
    [InlineData("v0.0.9", "0.1.0", UpdateCheckStatus.UpToDate)]
    [InlineData("v0.10.0", "0.9.5", UpdateCheckStatus.Available)]
    [InlineData("v1.0.0", "1.0.0-beta", UpdateCheckStatus.Available)]
    public async Task OnlyAHigherVersion_IsNew(string tag, string current, UpdateCheckStatus expected)
    {
        _http.Json(UpdateService.LatestUrl, ReleaseJson(tag));
        var service = Service(current: current);
        var result = await service.CheckAsync(manual: true);
        Assert.Equal(expected, result.Status);
        Assert.Equal(expected == UpdateCheckStatus.Available, service.Available != null);
    }

    [Fact]
    public async Task DraftsAndPreReleases_NeverShow()
    {
        var service = Service();
        _http.Json(UpdateService.LatestUrl, ReleaseJson("v0.2.0", draft: true));
        Assert.Equal(UpdateCheckStatus.UpToDate, (await service.CheckAsync(true)).Status);
        _http.Json(UpdateService.LatestUrl, ReleaseJson("v0.2.0", prerelease: true));
        Assert.Equal(UpdateCheckStatus.UpToDate, (await service.CheckAsync(true)).Status);
        Assert.Null(service.Available);
    }

    [Fact]
    public async Task TheRequestCarriesOnlyTheAppNameAndVersion()
    {
        _http.Json(UpdateService.LatestUrl, ReleaseJson("v0.1.0"));
        await Service().CheckAsync(true);
        var request = Assert.Single(_http.Requests);
        Assert.Equal("Paldium/0.1.0", request.Headers.UserAgent.ToString());
        Assert.Null(request.Headers.Authorization);
        Assert.DoesNotContain(request.Headers, h => h.Key is "Cookie" or "Authorization");
    }

    [Fact]
    public async Task SkippingAVersion_HidesTheBadge_UntilANewerOneComes()
    {
        var settings = Settings();
        var service = Service(settings);
        _http.Json(UpdateService.LatestUrl, ReleaseJson("v0.2.0"));
        await service.CheckAsync(true);
        Assert.NotNull(service.Available);

        service.Skip("0.2.0");
        Assert.Null(service.Available);
        await Service(settings).CheckAsync(true);   // a fresh run of the app: still skipped
        Assert.Null(new UpdateService(_http, settings, "0.1.0", "u", "a", true).Available);
        var again = Service(settings);
        await again.CheckAsync(true);
        Assert.Null(again.Available);
        Assert.NotNull(again.Latest);   // the settings page still knows about it

        _http.Json(UpdateService.LatestUrl, ReleaseJson("v0.3.0"));
        await again.CheckAsync(true);
        Assert.Equal("0.3.0", again.Available?.Version);

        again.Skip("0.3.0");
        again.Unskip();
        Assert.Equal("0.3.0", again.Available?.Version);
    }

    [Fact]
    public async Task AutomaticChecks_FollowTheSixHourRule_TheSwitch_AndTheMode()
    {
        var settings = Settings();
        Assert.True(Service(settings).ShouldAutoCheck());   // never checked
        _http.Json(UpdateService.LatestUrl, ReleaseJson("v0.1.0"));
        var service = Service(settings);
        await service.CheckAsync(manual: false);
        Assert.False(service.ShouldAutoCheck());            // just checked
        _now += TimeSpan.FromHours(5.9);
        Assert.False(service.ShouldAutoCheck());
        _now += TimeSpan.FromMinutes(10);
        Assert.True(service.ShouldAutoCheck());

        settings.Update(s => s with { Update = s.Update with { AutoCheck = false } });
        Assert.False(service.ShouldAutoCheck());            // switched off: only "Check now"
        settings.Update(s => s with { Update = s.Update with { AutoCheck = true } });
        Assert.False(Service(settings, auto: false).ShouldAutoCheck());   // Debug / demo: no automatic request
    }

    [Fact]
    public async Task TheBackgroundLoop_SendsNoRequestWhenAutomaticChecksAreNotAllowed()
    {
        var service = Service(auto: false);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await service.RunBackgroundAsync(cts.Token);
        Assert.Empty(_http.Requests);
    }

    [Fact]
    public async Task NoNetwork_IsOfflineForACheckNow_AndSilentInTheBackground()
    {
        _http.Fail = new HttpRequestException("no network");
        var service = Service();
        var result = await service.CheckAsync(manual: true);
        Assert.Equal(UpdateCheckStatus.Offline, result.Status);
        Assert.Null(service.Available);
        // Nothing was recorded as a check, so the next start tries again.
        Assert.Null(service.Settings.LastCheckUtc);
    }

    [Fact]
    public async Task NotFound_CountsAsUpToDate_AndARateLimitPostponesTheNextAutomaticCheckByADay()
    {
        var settings = Settings();
        var service = Service(settings);
        _http.Status(UpdateService.LatestUrl, HttpStatusCode.NotFound);
        Assert.Equal(UpdateCheckStatus.UpToDate, (await service.CheckAsync(true)).Status);

        _now += TimeSpan.FromHours(7);
        _http.Status(UpdateService.LatestUrl, (HttpStatusCode)429);
        Assert.Equal(UpdateCheckStatus.RateLimited, (await service.CheckAsync(true)).Status);
        _now += TimeSpan.FromHours(23);
        Assert.False(service.ShouldAutoCheck());
        _now += TimeSpan.FromHours(2);
        Assert.True(service.ShouldAutoCheck());

        _http.Status(UpdateService.LatestUrl, HttpStatusCode.Forbidden);
        Assert.Equal(UpdateCheckStatus.RateLimited, (await service.CheckAsync(true)).Status);
    }

    [Fact]
    public async Task ADownloadWhoseHashMatchesTheApiDigest_IsUnpackedAndChecked()
    {
        var zip = Zip();
        _http.Json(UpdateService.LatestUrl, ReleaseJson("v0.2.0", digest: "sha256:" + Sha(zip)));
        _http.Bytes(new Uri(Download + "v0.2.0/Paldium-0.2.0-win-x64.zip"), zip);
        var service = Service();
        await service.CheckAsync(true);

        var staged = await service.PrepareAsync(service.Available!);

        Assert.True(File.Exists(Path.Combine(staged, "Paldium.exe")));
        Assert.True(File.Exists(Path.Combine(staged, "data", "gamedata.json")));
    }

    [Fact]
    public async Task AZipWithBackslashEntryNames_IsUnpackedIntoRealFolders()
    {
        // Windows PowerShell 5.1's Compress-Archive wrote packages like this; they must still update.
        var zip = Zip(separator: '\\');
        _http.Json(UpdateService.LatestUrl, ReleaseJson("v0.2.0", digest: "sha256:" + Sha(zip)));
        _http.Bytes(new Uri(Download + "v0.2.0/Paldium-0.2.0-win-x64.zip"), zip);
        var service = Service();
        await service.CheckAsync(true);

        var staged = await service.PrepareAsync(service.Available!);

        Assert.True(File.Exists(Path.Combine(staged, "data", "gamedata.json")));
    }

    [Fact]
    public async Task ADownloadChecked_WithTheSha256Asset_Works()
    {
        var zip = Zip();
        _http.Json(UpdateService.LatestUrl, ReleaseJson("v0.2.0", checksumAsset: true));
        _http.Bytes(new Uri(Download + "v0.2.0/Paldium-0.2.0-win-x64.zip"), zip);
        _http.Bytes(new Uri(Download + "v0.2.0/Paldium-0.2.0-win-x64.zip.sha256"), Encoding.UTF8.GetBytes(Sha(zip) + "  Paldium-0.2.0-win-x64.zip\n"));
        var service = Service();
        await service.CheckAsync(true);
        Assert.True(Directory.Exists(await service.PrepareAsync(service.Available!)));
    }

    [Theory]
    [InlineData("wrong digest")]
    [InlineData("no checksum at all")]
    public async Task AZipThatCannotBeVerified_IsNotInstalled_AndIsDeleted(string case_)
    {
        var zip = Zip();
        var digest = case_ == "wrong digest" ? "sha256:" + new string('0', 64) : null;
        _http.Json(UpdateService.LatestUrl, ReleaseJson("v0.2.0", digest: digest));
        _http.Bytes(new Uri(Download + "v0.2.0/Paldium-0.2.0-win-x64.zip"), zip);
        var service = Service();
        await service.CheckAsync(true);

        var ex = await Assert.ThrowsAsync<UpdateException>(() => service.PrepareAsync(service.Available!));

        Assert.Equal(UpdateException.VerifyFailed, ex.Key);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_root, "updates"), "*", SearchOption.AllDirectories));
        Assert.Equal(UpdatePhase.Idle, service.Progress.Phase);
    }

    [Fact]
    public async Task APackageWithoutTheDataFile_IsRefused()
    {
        var zip = Zip(complete: false);
        _http.Json(UpdateService.LatestUrl, ReleaseJson("v0.2.0", digest: "sha256:" + Sha(zip)));
        _http.Bytes(new Uri(Download + "v0.2.0/Paldium-0.2.0-win-x64.zip"), zip);
        var service = Service();
        await service.CheckAsync(true);
        var ex = await Assert.ThrowsAsync<UpdateException>(() => service.PrepareAsync(service.Available!));
        Assert.Equal(UpdateException.BadPackage, ex.Key);
    }

    [Fact]
    public async Task OnlyTheProjectsReleaseDownloads_AreFetched()
    {
        _http.Json(UpdateService.LatestUrl, ReleaseJson("v0.2.0", host: "https://evil.example.com/"));
        var service = Service();
        await service.CheckAsync(true);
        var ex = await Assert.ThrowsAsync<UpdateException>(() => service.PrepareAsync(service.Available!));
        Assert.Equal(UpdateException.DownloadFailed, ex.Key);
        Assert.DoesNotContain(_http.Requests, r => r.RequestUri!.Host == "evil.example.com");

        Assert.True(UpdateService.IsAllowedUrl(new Uri(Download + "v1/x.zip"), final: false));
        Assert.False(UpdateService.IsAllowedUrl(new Uri("https://github.com/Other/Paldium/releases/download/v1/x.zip"), final: false));
        Assert.False(UpdateService.IsAllowedUrl(new Uri("http://github.com/MertKocakaplan/Paldium/releases/download/v1/x.zip"), final: false));
        Assert.True(UpdateService.IsAllowedUrl(new Uri("https://objects.githubusercontent.com/abc"), final: true));
        Assert.False(UpdateService.IsAllowedUrl(new Uri("https://example.com/abc"), final: true));
    }

    [Fact]
    public async Task AReleaseWithoutTheZip_HasNoAsset_AndTheDialogOffersTheReleasePage()
    {
        _http.Json(UpdateService.LatestUrl, JsonSerializer.Serialize(new { tag_name = "v0.2.0", html_url = "https://github.com/x", assets = Array.Empty<object>() }));
        var service = Service();
        await service.CheckAsync(true);
        Assert.Null(service.Available!.AssetUrl);
        var ex = await Assert.ThrowsAsync<UpdateException>(() => service.PrepareAsync(service.Available));
        Assert.Equal(UpdateException.NoAsset, ex.Key);
    }

    [Fact]
    public async Task AFolderThatCannotBeWritten_StopsBeforeTheDownload()
    {
        var zip = Zip();
        _http.Json(UpdateService.LatestUrl, ReleaseJson("v0.2.0", digest: "sha256:" + Sha(zip)));
        var service = new UpdateService(_http, Settings(), "0.1.0", Path.Combine(_root, "updates"), Path.Combine(_root, "missing-folder"), true, () => _now);
        await service.CheckAsync(true);
        var ex = await Assert.ThrowsAsync<UpdateException>(() => service.PrepareAsync(service.Available!));
        Assert.Equal(UpdateException.NotWritable, ex.Key);
        Assert.Single(_http.Requests);   // only the check
    }

    [Fact]
    public void ReleaseNotes_ShowHeadingsListsAndBold_ButNoHtmlOrLinks()
    {
        var html = UpdateNotes.ToHtml("## New\n- **fast** thing\n\n<script>alert(1)</script>\n\n[site](https://example.com) and ![pic](https://example.com/a.png)");
        Assert.Contains("<h2>", html);
        Assert.Contains("<li>", html);
        Assert.Contains("<strong>fast</strong>", html);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<a ", html);
        Assert.DoesNotContain("<img", html);
        Assert.Contains("site", html);
    }

    [Fact]
    public void CleanUp_KeepsOnlyTheLastBackup_AndRemovesStagedPackages()
    {
        var updates = Path.Combine(_root, "updates");
        Directory.CreateDirectory(Path.Combine(updates, "backup", "0.0.9"));
        Directory.CreateDirectory(Path.Combine(updates, "backup", "0.1.0"));
        Directory.CreateDirectory(Path.Combine(updates, "0.2.0", "staged"));
        Directory.CreateDirectory(Path.Combine(updates, "updater"));
        Service().CleanUp("0.1.0");
        Assert.False(Directory.Exists(Path.Combine(updates, "backup", "0.0.9")));
        Assert.True(Directory.Exists(Path.Combine(updates, "backup", "0.1.0")));
        Assert.False(Directory.Exists(Path.Combine(updates, "0.2.0")));
        Assert.True(Directory.Exists(Path.Combine(updates, "updater")));
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, Func<HttpResponseMessage>> _routes = new();
        public List<HttpRequestMessage> Requests { get; } = [];
        public Exception? Fail { get; set; }

        public void Json(Uri uri, string json) => _routes[uri.ToString()] = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        public void Bytes(Uri uri, byte[] bytes) => _routes[uri.ToString()] = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        public void Status(Uri uri, HttpStatusCode code) => _routes[uri.ToString()] = () => new HttpResponseMessage(code);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (Fail != null) throw Fail;
            var response = _routes.TryGetValue(request.RequestUri!.ToString(), out var make) ? make() : new HttpResponseMessage(HttpStatusCode.NotFound);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }
}
