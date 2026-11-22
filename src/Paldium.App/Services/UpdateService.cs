using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Paldium.App.Services;

/// <summary>A release on GitHub: what the badge, the dialog and the download need (screens/update.md).</summary>
public sealed record ReleaseInfo(
    string Version, string Tag, string Notes, string PageUrl, DateTimeOffset? PublishedAt,
    string? AssetName, string? AssetUrl, long AssetSize, string? Digest, string? ChecksumUrl);

public enum UpdateCheckStatus
{
    /// <summary>Nothing newer (also: no release yet, a draft, a pre-release).</summary>
    UpToDate,

    /// <summary>A newer release exists (also when the user skipped it: <see cref="UpdateCheckResult.Release"/> is set, <see cref="UpdateService.Available"/> is not).</summary>
    Available,

    /// <summary>No network or GitHub did not answer in time.</summary>
    Offline,

    /// <summary>HTTP 403 or 429.</summary>
    RateLimited,
}

public sealed record UpdateCheckResult(UpdateCheckStatus Status, ReleaseInfo? Release = null);

/// <summary>Why preparing an update stopped; <see cref="Key"/> is a localization key.</summary>
public sealed class UpdateException(string key, string detail) : Exception(detail)
{
    public const string NotWritable = "Update.NotWritable";
    public const string UnsafeFolder = "Update.UnsafeFolder";
    public const string VerifyFailed = "Update.VerifyFailed";
    public const string DownloadFailed = "Update.DownloadFailed";
    public const string NoAsset = "Update.NoAsset";
    public const string BadPackage = "Update.BadPackage";

    public string Key { get; } = key;
}

public enum UpdatePhase
{
    Idle,
    Downloading,
    Verifying,
    Restarting,
}

/// <summary>The progress of an update in the dialog and on the badge.</summary>
public sealed record UpdateProgress(UpdatePhase Phase, long BytesRead = 0, long TotalBytes = 0)
{
    public int Percent => TotalBytes > 0 ? (int)Math.Clamp(BytesRead * 100 / TotalBytes, 0, 100) : 0;
}

/// <summary>
/// A version number "1.2.3", "1.2.3.4", or "1.2.3-beta.1" (a leading "v" is dropped);
/// compared using SemVer 2.0 ordering for numeric and pre-release identifiers.
/// </summary>
public readonly record struct UpdateVersion(int Major, int Minor, int Patch, string? Pre, int Revision = 0) : IComparable<UpdateVersion>
{
    private static readonly Regex VersionRegex = new(
        @"^(\d+)\.(\d+)(?:\.(\d+)(?:\.(\d+))?)?(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+.*)?$",
        RegexOptions.CultureInvariant);

    public static bool TryParse(string? text, out UpdateVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var match = VersionRegex.Match(text.Trim().TrimStart('v', 'V'));
        if (!match.Success) return false;

        if (!int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var major) ||
            !int.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var minor))
            return false;

        var patch = 0;
        if (match.Groups[3].Success && !int.TryParse(match.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture, out patch))
            return false;

        var revision = 0;
        if (match.Groups[4].Success && !int.TryParse(match.Groups[4].Value, NumberStyles.None, CultureInfo.InvariantCulture, out revision))
            return false;

        version = new UpdateVersion(major, minor, patch, match.Groups[5].Success ? match.Groups[5].Value : null, revision);
        return true;
    }

    public int CompareTo(UpdateVersion other)
    {
        var c = Major.CompareTo(other.Major);
        if (c != 0) return c;
        c = Minor.CompareTo(other.Minor);
        if (c != 0) return c;
        c = Patch.CompareTo(other.Patch);
        if (c != 0) return c;
        c = Revision.CompareTo(other.Revision);
        if (c != 0) return c;
        if (Pre == other.Pre) return 0;
        if (Pre == null) return 1;
        if (other.Pre == null) return -1;
        return ComparePreRelease(Pre, other.Pre);
    }

    private static int ComparePreRelease(string left, string right)
    {
        var a = left.Split('.');
        var b = right.Split('.');
        var common = Math.Min(a.Length, b.Length);
        for (var i = 0; i < common; i++)
        {
            var ai = a[i];
            var bi = b[i];
            var aDigits = IsAllAsciiDigits(ai);
            var bDigits = IsAllAsciiDigits(bi);
            if (aDigits && bDigits)
            {
                var cmp = CompareUnsignedDecimalStrings(ai, bi);
                if (cmp != 0) return cmp;
            }
            else if (aDigits != bDigits)
            {
                return aDigits ? -1 : 1; // SemVer 2.0: numeric identifiers always have lower precedence than non-numeric
            }
            else
            {
                var cmp = string.CompareOrdinal(ai, bi);
                if (cmp != 0) return cmp;
            }
        }
        return a.Length.CompareTo(b.Length);
    }

    private static bool IsAllAsciiDigits(string s)
    {
        if (s.Length == 0) return false;
        foreach (var ch in s)
            if (ch < '0' || ch > '9') return false;
        return true;
    }

    private static int CompareUnsignedDecimalStrings(string a, string b)
    {
        var ta = a.TrimStart('0');
        var tb = b.TrimStart('0');
        var lenCmp = ta.Length.CompareTo(tb.Length);
        return lenCmp != 0 ? lenCmp : string.CompareOrdinal(ta, tb);
    }

    public override string ToString()
    {
        var core = Revision == 0 ? $"{Major}.{Minor}.{Patch}" : $"{Major}.{Minor}.{Patch}.{Revision}";
        return Pre == null ? core : $"{core}-{Pre}";
    }
}

/// <summary>
/// T131 (screens/update.md): asks GitHub Releases for a newer version, downloads and checks the zip, unpacks it and starts the updater. Nothing is sent but the
/// request itself (no account, no token, no personal data).
/// </summary>
public sealed class UpdateService
{
    public const string Repository = "MertKocakaplan/Paldium";
    public static readonly string DefaultPageUrl = $"https://github.com/{Repository}/releases";
    public static readonly Uri LatestUrl = new($"https://api.github.com/repos/{Repository}/releases/latest");
    public static readonly TimeSpan MinInterval = TimeSpan.FromHours(6);
    public static readonly TimeSpan Period = TimeSpan.FromHours(24);
    public static readonly TimeSpan StartDelay = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan TotalDownloadTimeout = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan ChecksumTimeout = TimeSpan.FromSeconds(30);
    private const int MaxRedirectHops = 5;
    private const long ProgressNotifyIntervalMs = 100;

    private static readonly string[] DownloadHosts = ["github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com"];
    private static readonly Regex Hex64Regex = new("^[0-9A-Fa-f]{64}$", RegexOptions.CultureInvariant);
    private static readonly Regex ChecksumLineWithFileRegex = new(@"^([0-9A-Fa-f]{64})\s+\*?(.+)$", RegexOptions.CultureInvariant);

    private readonly HttpMessageHandler _handler;
    private readonly SettingsStore _settings;
    private readonly string _currentVersion;
    private readonly Func<DateTimeOffset> _now;
    private readonly ILogger<UpdateService>? _log;
    private readonly string _updatesFolder;
    private readonly string _appFolder;
    private readonly bool _autoAllowed;
    private readonly IEnumerable<string>? _extraForbidden;
    private readonly object _gate = new();
    private CancellationTokenSource? _download;
    private Task<UpdateCheckResult>? _inflightCheck;
    private bool _preparing;

    /// <param name="handler">The HTTP handler (a fake one in tests).</param>
    /// <param name="currentVersion">The running version, e.g. "0.1.0".</param>
    /// <param name="autoAllowed">False in Debug builds and the demo and self-check modes: no automatic requests.</param>
    public UpdateService(HttpMessageHandler handler, SettingsStore settings, string currentVersion, string updatesFolder, string appFolder, bool autoAllowed,
        Func<DateTimeOffset>? now = null, ILogger<UpdateService>? log = null, IEnumerable<string>? extraForbidden = null)
    {
        _handler = handler;
        _settings = settings;
        _currentVersion = currentVersion;
        _updatesFolder = updatesFolder;
        _appFolder = appFolder;
        _autoAllowed = autoAllowed;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _log = log;
        _extraForbidden = extraForbidden;
    }

    public event EventHandler? Changed;

    public string CurrentVersion => _currentVersion;

    /// <summary>The newer release the user has not skipped (the badge shows while this is set).</summary>
    public ReleaseInfo? Available { get; private set; }

    /// <summary>The newest release found at the last check even when it is skipped or not newer (null: none found).</summary>
    public ReleaseInfo? Latest { get; private set; }

    public UpdateProgress Progress { get; private set; } = new(UpdatePhase.Idle);

    public bool Checking { get; private set; }

    /// <summary>The update dialog is open (the badge, the settings page and the dialog share this).</summary>
    public bool DialogOpen { get; private set; }

    public void OpenDialog()
    {
        DialogOpen = true;
        Raise();
    }

    public void CloseDialog()
    {
        DialogOpen = false;
        Raise();
    }

    public UpdateSettings Settings => _settings.Current.Update;

    /// <summary>The rule for an automatic check: switched on, allowed in this mode, 6 hours since the last check and not held back after a rate limit.</summary>
    public bool ShouldAutoCheck()
    {
        if (!_autoAllowed || !Settings.AutoCheck) return false;
        var now = _now();
        if (Settings.LastCheckUtc is { } last && now - last < MinInterval) return false;
        return Settings.NextAutoCheckUtc is not { } next || now >= next;
    }

    /// <summary>Waits 10 seconds, checks when the rule allows, then once a day while the app stays open. Returns when cancelled.</summary>
    public async Task RunBackgroundAsync(CancellationToken ct)
    {
        if (!_autoAllowed) return;
        try
        {
            await Task.Delay(StartDelay, ct);
            if (ShouldAutoCheck()) await CheckAsync(manual: false, ct);
            using var timer = new PeriodicTimer(Period);
            while (await timer.WaitForNextTickAsync(ct))
                if (ShouldAutoCheck()) await CheckAsync(manual: false, ct);
        }
        catch (OperationCanceledException)
        {
        }
    }

    public Task<UpdateCheckResult> CheckAsync(bool manual, CancellationToken ct = default)
    {
        TaskCompletionSource<UpdateCheckResult>? owned = null;
        Task<UpdateCheckResult> task;
        lock (_gate)
        {
            if (_inflightCheck != null)
            {
                task = _inflightCheck;
            }
            else
            {
                Checking = true;
                owned = new TaskCompletionSource<UpdateCheckResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                task = _inflightCheck = owned.Task;
            }
        }

        if (owned != null)
        {
            Raise();
            _ = CompleteCheckAsync(owned);
        }
        return ct.CanBeCanceled ? task.WaitAsync(ct) : task;
    }

    private async Task CompleteCheckAsync(TaskCompletionSource<UpdateCheckResult> owned)
    {
        UpdateCheckResult result;
        try
        {
            result = await CheckCoreAsync();
        }
        finally
        {
            lock (_gate)
            {
                Checking = false;
                if (ReferenceEquals(_inflightCheck, owned.Task))
                    _inflightCheck = null;
            }
            Raise();
        }
        owned.TrySetResult(result);
    }

    private async Task<UpdateCheckResult> CheckCoreAsync()
    {
        try
        {
            using var client = new HttpClient(_handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(10) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Paldium/" + _currentVersion);
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            using var response = await client.GetAsync(LatestUrl);

            if (response.StatusCode == HttpStatusCode.NotFound) return Finish(UpdateCheckStatus.UpToDate, null);
            if (response.StatusCode is HttpStatusCode.Forbidden or (HttpStatusCode)429)
            {
                _settings.Update(s => s with { Update = s.Update with { NextAutoCheckUtc = _now() + Period } });
                _log?.LogWarning("GitHub answered {Status}; the next automatic check is in 24 hours", (int)response.StatusCode);
                return new UpdateCheckResult(UpdateCheckStatus.RateLimited);
            }

            response.EnsureSuccessStatusCode();
            var release = ParseRelease(await response.Content.ReadAsStringAsync());
            return Finish(release != null && IsNewerVersion(release.Version) ? UpdateCheckStatus.Available : UpdateCheckStatus.UpToDate, release);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException)
        {
            _log?.LogInformation(ex, "Update check failed");
            return new UpdateCheckResult(UpdateCheckStatus.Offline);
        }
    }

    private UpdateCheckResult Finish(UpdateCheckStatus status, ReleaseInfo? release)
    {
        var newer = status == UpdateCheckStatus.Available ? release : null;
        Latest = newer;
        Available = newer != null && !IsSkipped(newer.Version) ? newer : null;
        _settings.Update(s => s with { Update = s.Update with { LastCheckUtc = _now(), LastSeenVersion = newer?.Version } });
        return new UpdateCheckResult(status, newer);
    }

    public bool IsNewerVersion(string? version) =>
        UpdateVersion.TryParse(version, out var candidate) && UpdateVersion.TryParse(_currentVersion, out var current) && candidate.CompareTo(current) > 0;

    public bool IsSkipped(string? version) =>
        !string.IsNullOrEmpty(version) && string.Equals(Settings.SkippedVersion, version, StringComparison.OrdinalIgnoreCase);

    public static string ValidatePageUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return DefaultPageUrl;
        if (uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort) return DefaultPageUrl;
        if (!uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)) return DefaultPageUrl;
        var path = uri.AbsolutePath;
        if (!path.StartsWith($"/{Repository}/", StringComparison.OrdinalIgnoreCase) &&
            !path.Equals($"/{Repository}", StringComparison.OrdinalIgnoreCase))
            return DefaultPageUrl;
        return uri.AbsoluteUri;
    }

    /// <summary>The release in GitHub's "latest" answer; null for a draft, a pre-release or an answer without a usable tag.</summary>
    public static ReleaseInfo? ParseRelease(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (root.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True) return null;
        if (root.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True) return null;

        var tag = Str(root, "tag_name");
        if (tag == null || !UpdateVersion.TryParse(tag, out var parsed)) return null;
        var version = parsed.ToString();
        var assetName = $"Paldium-{version}-win-x64.zip";

        string? assetUrl = null, digest = null, checksumUrl = null;
        long size = 0;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            foreach (var asset in assets.EnumerateArray())
            {
                var name = Str(asset, "name");
                if (name == assetName)
                {
                    assetUrl = Str(asset, "browser_download_url");
                    digest = Str(asset, "digest");
                    if (asset.TryGetProperty("size", out var s) && s.TryGetInt64(out var bytes)) size = bytes;
                }
                else if (name == assetName + ".sha256") checksumUrl = Str(asset, "browser_download_url");
            }

        DateTimeOffset? published = DateTimeOffset.TryParse(Str(root, "published_at"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var p) ? p : null;
        return new ReleaseInfo(version, tag, Str(root, "body") ?? "", ValidatePageUrl(Str(root, "html_url")), published,
            assetUrl == null ? null : assetName, assetUrl, size, digest, checksumUrl);
    }

    private static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public void Skip(string version)
    {
        _settings.Update(s => s with { Update = s.Update with { SkippedVersion = version } });
        if (Available != null && string.Equals(Available.Version, version, StringComparison.OrdinalIgnoreCase)) Available = null;
        Raise();
    }

    public void Unskip()
    {
        _settings.Update(s => s with { Update = s.Update with { SkippedVersion = null } });
        Available = Latest;
        Raise();
    }

    /// <summary>A download from the project's releases, or from the hosts GitHub sends those downloads to.</summary>
    public static bool IsAllowedUrl(Uri uri, bool final)
    {
        if (uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort) return false;
        if (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            return uri.AbsolutePath.StartsWith($"/{Repository}/releases/download/", StringComparison.Ordinal);
        return final && DownloadHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The app folder can be written to without extra rights (no UAC is ever asked).</summary>
    public bool IsAppFolderWritable()
    {
        try
        {
            var probe = Path.Combine(_appFolder, ".write-test-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Downloads the zip, checks its SHA-256, unpacks it and checks the content; returns the staged folder. A zip that does not match is deleted.</summary>
    public async Task<string> PrepareAsync(ReleaseInfo release, CancellationToken ct = default)
    {
        if (release.AssetUrl == null || release.AssetName == null) throw new UpdateException(UpdateException.NoAsset, release.Tag);
        if (!Uri.TryCreate(release.AssetUrl, UriKind.Absolute, out var assetUri) || !IsAllowedUrl(assetUri, final: false))
            throw new UpdateException(UpdateException.DownloadFailed, "The download address is not one of the project's releases.");
        if (!IsAppFolderWritable()) throw new UpdateException(UpdateException.NotWritable, _appFolder);
        var safetyError = Paldium.Updater.AppFolderSafety.ValidateAppFolder(_appFolder, _updatesFolder, _extraForbidden);
        if (safetyError != null) throw new UpdateException(UpdateException.UnsafeFolder, safetyError);

        var folder = Path.Combine(_updatesFolder, release.Version);
        var zip = Path.Combine(folder, release.AssetName);
        var staged = Path.Combine(folder, "staged");
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lock (_gate)
        {
            if (_preparing) throw new UpdateException(UpdateException.DownloadFailed, "An update download is already in progress.");
            _preparing = true;
            _download = cancel;
        }
        try
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
            Directory.CreateDirectory(folder);
            TryDeleteFile(Path.Combine(_updatesFolder, "last-error.txt"));

            SetProgress(new UpdateProgress(UpdatePhase.Downloading, 0, release.AssetSize));
            using var totalTimeout = new CancellationTokenSource(TotalDownloadTimeout);
            using var downloadCts = CancellationTokenSource.CreateLinkedTokenSource(cancel.Token, totalTimeout.Token);
            using var client = new HttpClient(_handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Paldium/" + _currentVersion);
            await DownloadAsync(client, assetUri, zip, release.AssetSize, cancel.Token, downloadCts.Token);

            SetProgress(new UpdateProgress(UpdatePhase.Verifying));
            var expected = await ExpectedHashAsync(client, release, cancel.Token);
            var actual = await HashOfAsync(zip, cancel.Token);
            if (expected == null || !string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(zip);
                throw new UpdateException(UpdateException.VerifyFailed, expected == null ? "no checksum" : "checksum differs");
            }

            try
            {
                ZipFile.ExtractToDirectory(zip, staged);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                throw new UpdateException(UpdateException.BadPackage, ex.Message);
            }

            var stagedRoot = Paldium.Updater.AppFolderSafety.ResolveStagedRoot(staged, requireGameData: true)
                ?? throw new UpdateException(UpdateException.BadPackage, "The package has no Paldium.exe or data/gamedata.json.");
            return stagedRoot;
        }
        catch
        {
            SetProgress(new UpdateProgress(UpdatePhase.Idle));
            TryDelete(folder);
            throw;
        }
        finally
        {
            lock (_gate)
            {
                _download = null;
                _preparing = false;
            }
        }
    }

    /// <summary>Stops a running download (the partial file is deleted).</summary>
    public void CancelDownload()
    {
        lock (_gate) { _download?.Cancel(); }
    }

    private async Task<HttpResponseMessage> GetWithValidatedRedirectsAsync(HttpClient client, Uri initialUrl, HttpCompletionOption completion, CancellationToken ct)
    {
        var current = initialUrl;
        for (var hop = 0; hop <= MaxRedirectHops; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            var response = await client.SendAsync(request, completion, ct);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
            {
                response.Dispose();
                var next = location.IsAbsoluteUri ? location : new Uri(current, location);
                if (!IsAllowedUrl(next, final: true))
                    throw new UpdateException(UpdateException.DownloadFailed, "The download was sent to " + next.Host + ".");
                current = next;
                continue;
            }
            return response;
        }
        throw new UpdateException(UpdateException.DownloadFailed, "Too many redirects.");
    }

    private async Task DownloadAsync(HttpClient client, Uri url, string target, long expectedSize, CancellationToken userCt, CancellationToken totalCt)
    {
        try
        {
            using var headerCts = CancellationTokenSource.CreateLinkedTokenSource(totalCt);
            headerCts.CancelAfter(ReadTimeout);
            using var response = await GetWithValidatedRedirectsAsync(client, url, HttpCompletionOption.ResponseHeadersRead, headerCts.Token);
            response.EnsureSuccessStatusCode();
            var final = response.RequestMessage?.RequestUri ?? url;
            if (!IsAllowedUrl(final, final: true)) throw new UpdateException(UpdateException.DownloadFailed, "The download was sent to " + final.Host + ".");
            var total = response.Content.Headers.ContentLength ?? expectedSize;
            await using var source = await response.Content.ReadAsStreamAsync(headerCts.Token);
            await using var file = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
            var buffer = new byte[81920];
            long read = 0;
            long lastNotifyRead = 0;
            var lastNotifyTick = Environment.TickCount64;
            while (true)
            {
                using var readCts = CancellationTokenSource.CreateLinkedTokenSource(totalCt);
                readCts.CancelAfter(ReadTimeout);
                var n = await source.ReadAsync(buffer, readCts.Token);
                if (n <= 0) break;
                await file.WriteAsync(buffer.AsMemory(0, n), totalCt);
                read += n;
                var nowTick = Environment.TickCount64;
                if (nowTick - lastNotifyTick >= ProgressNotifyIntervalMs)
                {
                    lastNotifyTick = nowTick;
                    lastNotifyRead = read;
                    SetProgress(new UpdateProgress(UpdatePhase.Downloading, read, total));
                }
            }
            if (read != lastNotifyRead)
                SetProgress(new UpdateProgress(UpdatePhase.Downloading, read, total));
        }
        catch (OperationCanceledException) when (!userCt.IsCancellationRequested)
        {
            throw new UpdateException(UpdateException.DownloadFailed, "Download timed out.");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            throw new UpdateException(UpdateException.DownloadFailed, ex.Message);
        }
    }

    private async Task<string?> ExpectedHashAsync(HttpClient client, ReleaseInfo release, CancellationToken userCt)
    {
        if (release.Digest is { } digest && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
        {
            var hex = digest["sha256:".Length..].Trim();
            if (Hex64Regex.IsMatch(hex)) return hex;
        }

        if (release.ChecksumUrl == null || !Uri.TryCreate(release.ChecksumUrl, UriKind.Absolute, out var url) || !IsAllowedUrl(url, final: false)) return null;
        try
        {
            using var checksumCts = CancellationTokenSource.CreateLinkedTokenSource(userCt);
            checksumCts.CancelAfter(ChecksumTimeout);
            using var response = await GetWithValidatedRedirectsAsync(client, url, HttpCompletionOption.ResponseContentRead, checksumCts.Token);
            response.EnsureSuccessStatusCode();
            if (!IsAllowedUrl(response.RequestMessage?.RequestUri ?? url, final: true)) return null;
            var text = await response.Content.ReadAsStringAsync(checksumCts.Token);
            return ParseChecksumText(text, release.AssetName);
        }
        catch (OperationCanceledException) when (!userCt.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            return null;
        }
    }

    public static string? ParseChecksumText(string content, string? assetName)
    {
        var bareLines = new List<string>();
        var sawNamedLine = false;
        foreach (var rawLine in content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;
            var namedMatch = ChecksumLineWithFileRegex.Match(line);
            if (namedMatch.Success)
            {
                sawNamedLine = true;
                var file = Path.GetFileName(namedMatch.Groups[2].Value.Trim());
                if (assetName != null && string.Equals(file, assetName, StringComparison.OrdinalIgnoreCase))
                    return namedMatch.Groups[1].Value;
            }
            else if (Hex64Regex.IsMatch(line))
            {
                bareLines.Add(line);
            }
        }
        return !sawNamedLine && bareLines.Count == 1 ? bareLines[0] : null;
    }

    private static async Task<string> HashOfAsync(string file, CancellationToken ct)
    {
        await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
    }

    /// <summary>
    /// Copies the updater out of the app folder (it replaces that folder) and starts it with the staged version. The caller closes the app right after.
    /// </summary>
    public void LaunchUpdater(string stagedFolder)
    {
        var source = Path.Combine(_appFolder, "updater");
        var exe = Path.Combine(source, "Paldium.Updater.exe");
        if (!File.Exists(exe))
        {
            SetProgress(new UpdateProgress(UpdatePhase.Idle));
            throw new UpdateException(UpdateException.BadPackage, "The updater is missing from the app folder.");
        }

        try
        {
            var target = Path.Combine(_updatesFolder, "updater");
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
            CopyDirectoryRecursive(source, target);
            TryDeleteFile(Path.Combine(_updatesFolder, "last-error.txt"));

            SetProgress(new UpdateProgress(UpdatePhase.Restarting));
            var info = new ProcessStartInfo(Path.Combine(target, "Paldium.Updater.exe"))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = target,
            };
            var appArg = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_appFolder));
            foreach (var argument in new[] { Environment.ProcessId.ToString(CultureInfo.InvariantCulture), appArg, stagedFolder, _updatesFolder, _currentVersion })
                info.ArgumentList.Add(argument);

            using var process = Process.Start(info);
            if (process == null)
                throw new UpdateException(UpdateException.BadPackage, "The updater process could not be started.");
        }
        catch (UpdateException)
        {
            SetProgress(new UpdateProgress(UpdatePhase.Idle));
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException)
        {
            SetProgress(new UpdateProgress(UpdatePhase.Idle));
            throw new UpdateException(UpdateException.BadPackage, ex.Message);
        }
    }

    private static void CopyDirectoryRecursive(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);
        foreach (var file in Directory.EnumerateFiles(sourceDir))
            File.Copy(file, Path.Combine(targetDir, Path.GetFileName(file)), overwrite: true);
        foreach (var subDir in Directory.EnumerateDirectories(sourceDir))
            CopyDirectoryRecursive(subDir, Path.Combine(targetDir, Path.GetFileName(subDir)));
    }

    /// <summary>Keeps the backup of the version just replaced (when known) and removes older backups and unpacked packages.</summary>
    public void CleanUp(string? keepBackupVersion)
    {
        try
        {
            var backups = Path.Combine(_updatesFolder, "backup");
            if (keepBackupVersion != null && Directory.Exists(backups))
                foreach (var directory in Directory.EnumerateDirectories(backups).Where(d => Path.GetFileName(d) != keepBackupVersion)) Directory.Delete(directory, recursive: true);
            if (Directory.Exists(_updatesFolder))
                foreach (var directory in Directory.EnumerateDirectories(_updatesFolder).Where(d => Path.GetFileName(d) is not ("backup" or "updater"))) Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.LogInformation(ex, "Old update files could not be removed");
        }
    }

    /// <summary>The text of <c>last-error.txt</c> the updater wrote when it could not finish; logs it and removes the file once read.</summary>
    public string? TakeLastError()
    {
        var file = Path.Combine(_updatesFolder, "last-error.txt");
        try
        {
            if (!File.Exists(file)) return null;
            var text = File.ReadAllText(file);
            _log?.LogError("Previous update failed: {Error}", text.Trim());
            File.Delete(file);
            return text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void SetProgress(UpdateProgress progress)
    {
        Progress = progress;
        Raise();
    }

    private void Raise() => Changed?.Invoke(this, EventArgs.Empty);

    private static void TryDeleteFile(string file)
    {
        try { if (File.Exists(file)) File.Delete(file); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static void TryDelete(string folder)
    {
        try { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
