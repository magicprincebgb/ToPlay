using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace ToPlay.App;

/// <summary>One published release of ToPlay, as announced on GitHub.</summary>
internal sealed record UpdateInfo(
    Version Version,
    string Tag,
    string Notes,
    string PageUrl,
    string DownloadUrl,
    long Size,
    string? ChecksumUrl)
{
    /// <summary>Download size in MB, for a human-readable "≈288 MB" hint.</summary>
    public double SizeMb => Size / 1024d / 1024d;
}

/// <summary>
/// Powers the Control Panel's "Check for updates" button: asks GitHub for the
/// latest published release, downloads its ToPlaySetup.exe and hands it to the
/// installer, which upgrades in place (accounts and settings are kept).
///
/// Security: an updater is a perfect place to hide malware, so this one is
/// deliberately strict.
///   • Everything is fetched over HTTPS from GitHub only — the release JSON and
///     every redirect of the download must land on github.com or a
///     *.githubusercontent.com host, otherwise the download is abandoned.
///   • The finished file must match the size GitHub announced, must really be a
///     Windows program (MZ header), and its embedded version must be exactly
///     the version we were told we are installing.
///   • If the release also publishes ToPlaySetup.exe.sha256, that checksum is
///     verified and a mismatch aborts the update.
///   • Nothing is ever installed silently: the user reads the release notes,
///     presses the button, and Windows still shows its usual admin prompt.
/// </summary>
internal static class UpdateService
{
    private const string LatestReleaseApi =
        "https://api.github.com/repos/magicprincebgb/ToPlay/releases/latest";

    /// <summary>The plain (non-API) page; it redirects to /releases/tag/vX.Y.Z.</summary>
    private const string LatestReleasePage =
        "https://github.com/magicprincebgb/ToPlay/releases/latest";

    private const string ReleasesPage = "https://github.com/magicprincebgb/ToPlay/releases";

    private const string SetupAssetName = "ToPlaySetup.exe";

    /// <summary>The version of the ToPlay.exe that is running right now.</summary>
    public static Version Current { get; } =
        Normalize(Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0));

    /// <summary>
    /// Returns the newest release if it is newer than what's installed, or
    /// <c>null</c> when we're already up to date (or nothing is published yet).
    ///
    /// GitHub allows only 60 anonymous API calls per hour per IP address — a
    /// budget every device on the same Wi-Fi shares — so this check is
    /// deliberately frugal. It remembers the last answer together with its ETag
    /// (GitHub then replies "304 Not Modified", which costs nothing from the
    /// budget), and if the API is throttled or unreachable anyway it reads the
    /// ordinary releases page instead, which isn't rationed at all.
    /// </summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        JsonObject release;
        try
        {
            release = await FetchLatestFromApiAsync(ct).ConfigureAwait(false);
        }
        catch (NoReleaseException) { return null; }
        catch (OperationCanceledException) { throw; }
        catch (Exception apiError)
        {
            try
            {
                release = await FetchLatestFromWebsiteAsync(ct).ConfigureAwait(false);
            }
            catch (NoReleaseException) { return null; }
            catch (OperationCanceledException) { throw; }
            catch { throw apiError; }   // the API explained the problem best
        }

        return BuildInfo(release);
    }

    /// <summary>Asks the GitHub API, re-using the cached reply when nothing changed.</summary>
    private static async Task<JsonObject> FetchLatestFromApiAsync(CancellationToken ct)
    {
        var cached = LoadCache();

        using var http = NewClient(TimeSpan.FromSeconds(20));
        using var req = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApi);
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        req.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        if (cached is { Etag.Length: > 0, Body.Length: > 0 })
            req.Headers.TryAddWithoutValidation("If-None-Match", cached.Etag);

        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct)
                                   .ConfigureAwait(false);

        // Nothing new since last time — the cached release is still the latest.
        if (resp.StatusCode == HttpStatusCode.NotModified && cached is not null)
            return ParseRelease(cached.Body);

        // No releases published (yet) — nothing to update to.
        if (resp.StatusCode == HttpStatusCode.NotFound) throw new NoReleaseException();

        // GitHub throttles anonymous callers per IP; say so in plain language.
        if (resp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            throw new InvalidOperationException(RateLimitMessage(resp));

        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var release = ParseRelease(body);

        var etag = resp.Headers.ETag?.ToString();
        if (!string.IsNullOrWhiteSpace(etag)) SaveCache(etag!, body);

        return release;
    }

    /// <summary>
    /// Plan B when the API is rationed or blocked: <c>/releases/latest</c> on the
    /// normal website answers with a redirect to <c>/releases/tag/vX.Y.Z</c>, so
    /// the tag alone is enough to work out the release and its download links.
    /// </summary>
    private static async Task<JsonObject> FetchLatestFromWebsiteAsync(CancellationToken ct)
    {
        string? location;
        using (var http = NewClient(TimeSpan.FromSeconds(20), followRedirects: false))
        using (var req = new HttpRequestMessage(HttpMethod.Get, LatestReleasePage))
        {
            req.Headers.Accept.ParseAdd("text/html");
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
                                       .ConfigureAwait(false);

            if (resp.StatusCode == HttpStatusCode.NotFound) throw new NoReleaseException();
            location = resp.Headers.Location?.ToString();
        }

        if (string.IsNullOrEmpty(location))
            throw new InvalidOperationException("GitHub didn't say which release is the latest one.");

        var target = Uri.TryCreate(location, UriKind.Absolute, out var abs)
            ? abs
            : new Uri(new Uri(LatestReleasePage), location);
        RequireTrusted(target.ToString());

        const string marker = "/tag/";
        var path = target.AbsolutePath.TrimEnd('/');
        var at = path.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (at < 0) throw new NoReleaseException();          // repo has no releases yet

        var tag = Uri.UnescapeDataString(path[(at + marker.Length)..]);
        if (tag.Length == 0) throw new NoReleaseException();

        var release = new JsonObject
        {
            ["tag_name"] = tag,
            ["html_url"] = $"{ReleasesPage}/tag/{Uri.EscapeDataString(tag)}"
        };

        // Only bother looking up the installer when this really is an upgrade.
        var version = ParseVersion(tag);
        if (version is null || version <= Current) return release;

        var setupUrl = $"{ReleasesPage}/download/{Uri.EscapeDataString(tag)}/{SetupAssetName}";
        var assets = new JsonArray
        {
            new JsonObject
            {
                ["name"] = SetupAssetName,
                ["browser_download_url"] = setupUrl,
                ["size"] = await ContentLengthAsync(setupUrl, ct).ConfigureAwait(false)
            }
        };

        var checksumUrl = setupUrl + ".sha256";
        if (await ContentLengthAsync(checksumUrl, ct).ConfigureAwait(false) > 0)
        {
            assets.Add(new JsonObject
            {
                ["name"] = SetupAssetName + ".sha256",
                ["browser_download_url"] = checksumUrl
            });
        }

        release["assets"] = assets;
        release["body"] =
            $"What's new in {tag} is listed on the release page:\n{ReleasesPage}/tag/{tag}";
        return release;
    }

    /// <summary>Turns GitHub's release object into an <see cref="UpdateInfo"/>.</summary>
    private static UpdateInfo? BuildInfo(JsonObject rel)
    {
        var tag = rel["tag_name"]?.GetValue<string>() ?? "";

        var latest = ParseVersion(tag);
        if (latest is null)
            throw new InvalidOperationException($"The latest release ({tag}) has an unexpected version number.");

        if (latest <= Current) return null;      // already current — or even newer (a dev build)

        // Find the installer asset (and its optional checksum companion).
        var assets = rel["assets"] as JsonArray ?? new JsonArray();
        JsonObject? setup = null, checksum = null;
        foreach (var node in assets)
        {
            if (node is not JsonObject a) continue;
            var name = a["name"]?.GetValue<string>() ?? "";
            if (name.Equals(SetupAssetName, StringComparison.OrdinalIgnoreCase)) setup = a;
            else if (name.Equals(SetupAssetName + ".sha256", StringComparison.OrdinalIgnoreCase)) checksum = a;
        }
        setup ??= assets.OfType<JsonObject>().FirstOrDefault(a =>
            (a["name"]?.GetValue<string>() ?? "").EndsWith(".exe", StringComparison.OrdinalIgnoreCase));

        if (setup is null)
            throw new InvalidOperationException(
                $"Release {tag} doesn't include {SetupAssetName} yet. Please download it from the website instead.");

        var url = setup["browser_download_url"]?.GetValue<string>() ?? "";
        RequireTrusted(url);

        var checksumUrl = checksum?["browser_download_url"]?.GetValue<string>();
        if (checksumUrl is not null) RequireTrusted(checksumUrl);

        return new UpdateInfo(
            Version: latest,
            Tag: string.IsNullOrEmpty(tag) ? "v" + latest : tag,
            Notes: CleanNotes(rel["body"]?.GetValue<string>()),
            PageUrl: rel["html_url"]?.GetValue<string>() ?? "https://github.com/magicprincebgb/ToPlay/releases",
            DownloadUrl: url,
            Size: setup["size"]?.GetValue<long>() ?? 0,
            ChecksumUrl: checksumUrl);
    }

    /// <summary>
    /// Downloads the release's installer to a temp folder, reporting 0–100 %,
    /// verifies it, and returns the path. Throws if anything looks wrong.
    /// </summary>
    public static async Task<string> DownloadAsync(
        UpdateInfo info, IProgress<int>? progress, CancellationToken ct = default)
    {
        var dir = Path.Combine(Path.GetTempPath(), "ToPlay-update");
        Directory.CreateDirectory(dir);
        foreach (var stale in Directory.EnumerateFiles(dir))
            try { File.Delete(stale); } catch { /* in use / gone — ignore */ }

        var dest = Path.Combine(dir, $"ToPlaySetup-{info.Version}.exe");

        // Generous timeout: this is a ~290 MB download on home Wi-Fi.
        using var http = NewClient(TimeSpan.FromMinutes(30));
        using var resp = await http.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct)
                                   .ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        // Re-check the host we actually ended up on: GitHub redirects asset
        // downloads to its CDN, and a redirect is exactly how an attacker on a
        // hostile network would try to slip in a different file.
        RequireTrusted(resp.RequestMessage?.RequestUri?.ToString() ?? info.DownloadUrl);

        var total = resp.Content.Headers.ContentLength ?? info.Size;

        await using (var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var dst = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None,
                                              128 * 1024, useAsync: true))
        {
            var buffer = new byte[128 * 1024];
            long done = 0;
            int last = -1, read;
            while ((read = await src.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                done += read;
                if (total <= 0) continue;
                var pct = (int)Math.Min(100, done * 100 / total);
                if (pct != last) { last = pct; progress?.Report(pct); }
            }
        }

        try
        {
            await VerifyAsync(dest, info, ct).ConfigureAwait(false);
        }
        catch
        {
            try { File.Delete(dest); } catch { }
            throw;
        }

        progress?.Report(100);
        return dest;
    }

    /// <summary>
    /// Hands the downloaded setup the folder to upgrade. It stops ToPlay,
    /// replaces the files, keeps <c>data\</c> (accounts, settings, certificate)
    /// and starts ToPlay again.
    /// </summary>
    public static void LaunchInstaller(string setupPath, string installDir)
    {
        // Trailing backslashes must go: "C:\dir\" would escape the closing quote
        // on the command line and the installer would see a mangled path.
        var dir = installDir.TrimEnd('\\', '/');

        var psi = new ProcessStartInfo(setupPath)
        {
            UseShellExecute = true,          // lets Windows show the admin prompt
            Verb = "runas",
            Arguments = $"--update \"{dir}\""
        };
        Process.Start(psi);
    }

    // ======================= verification =======================

    private static async Task VerifyAsync(string file, UpdateInfo info, CancellationToken ct)
    {
        var len = new FileInfo(file).Length;

        if (info.Size > 0 && len != info.Size)
            throw new InvalidOperationException(
                "The download didn't finish completely. Please try again.");

        if (len < 1024 * 1024)
            throw new InvalidOperationException(
                "The downloaded file is far too small to be ToPlay. Update cancelled.");

        // Real Windows programs start with "MZ".
        await using (var fs = File.OpenRead(file))
        {
            var head = new byte[2];
            if (await fs.ReadAsync(head.AsMemory(), ct).ConfigureAwait(false) != 2 || head[0] != 0x4D || head[1] != 0x5A)
                throw new InvalidOperationException(
                    "The downloaded file isn't a Windows program. Update cancelled.");
        }

        // Optional but strongest check: the checksum published with the release.
        if (info.ChecksumUrl is not null)
        {
            var expected = await FetchChecksumAsync(info.ChecksumUrl, ct).ConfigureAwait(false);
            if (expected is not null)
            {
                var actual = await Sha256Async(file, ct).ConfigureAwait(false);
                if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        "The downloaded file doesn't match the checksum published with the release, " +
                        "so it was discarded. Please try again later.");
            }
        }

        // The version baked into the file must be the version we were promised.
        var stamped = ParseVersion(FileVersionInfo.GetVersionInfo(file).FileVersion);
        if (stamped is null || stamped != info.Version)
            throw new InvalidOperationException(
                $"The downloaded installer says it is {stamped?.ToString() ?? "an unknown version"}, " +
                $"but the release announced {info.Version}. Update cancelled.");
    }

    private static async Task<string?> FetchChecksumAsync(string url, CancellationToken ct)
    {
        try
        {
            using var http = NewClient(TimeSpan.FromSeconds(20));
            using var resp = await http.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            RequireTrusted(resp.RequestMessage?.RequestUri?.ToString() ?? url);

            var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            // Accept both "<hash>" and the "sha256sum" style "<hash> *file" lines.
            return text.Split(new[] { ' ', '\t', '\r', '\n', '*' }, StringSplitOptions.RemoveEmptyEntries)
                       .FirstOrDefault(t => t.Length == 64 && t.All(Uri.IsHexDigit));
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }   // no checksum published / unreachable — other checks still apply
    }

    private static async Task<string> Sha256Async(string file, CancellationToken ct)
    {
        await using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read,
                                            1024 * 1024, useAsync: true);
        var hash = await SHA256.HashDataAsync(fs, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    // ======================= helpers =======================

    private static HttpClient NewClient(TimeSpan timeout, bool followRedirects = true)
    {
        var handler = new HttpClientHandler
        {
            // Redirects are followed everywhere except when we *want* to read the
            // "Location" of /releases/latest to learn the newest tag.
            AllowAutoRedirect = followRedirects,
            MaxAutomaticRedirections = 5,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };

        var http = new HttpClient(handler) { Timeout = timeout };
        // GitHub's API rejects requests without a User-Agent.
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"ToPlay/{Current} (+https://github.com/magicprincebgb/ToPlay)");
        return http;
    }

    private static JsonObject ParseRelease(string body) =>
        JsonNode.Parse(body) as JsonObject
        ?? throw new InvalidOperationException("GitHub sent an update list we couldn't read.");

    /// <summary>
    /// "Try again in about 12 minutes (after 13:45)" reads far better than
    /// "try again later", and GitHub tells us exactly when the hour resets.
    /// </summary>
    private static string RateLimitMessage(HttpResponseMessage resp)
    {
        const string basic = "GitHub is temporarily limiting update checks from this network.";
        try
        {
            if (resp.Headers.TryGetValues("x-ratelimit-reset", out var values)
                && long.TryParse(values.FirstOrDefault(), out var unix))
            {
                var resets = DateTimeOffset.FromUnixTimeSeconds(unix).ToLocalTime();
                var minutes = (int)Math.Ceiling((resets - DateTimeOffset.Now).TotalMinutes);
                if (minutes is > 0 and <= 60)
                    return $"{basic} It frees up in about {minutes} minute{(minutes == 1 ? "" : "s")} " +
                           $"(at {resets:HH:mm}) — ToPlay will try again then.";
            }
        }
        catch { /* fall through to the generic wording */ }

        return basic + " Please try again in a few minutes.";
    }

    /// <summary>HEAD request: the file's size, or 0 when it isn't there.</summary>
    private static async Task<long> ContentLengthAsync(string url, CancellationToken ct)
    {
        try
        {
            RequireTrusted(url);
            using var http = NewClient(TimeSpan.FromSeconds(20));
            using var req = new HttpRequestMessage(HttpMethod.Head, url);
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
                                       .ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return 0;
            RequireTrusted(resp.RequestMessage?.RequestUri?.ToString() ?? url);
            return Math.Max(0, resp.Content.Headers.ContentLength ?? 0);
        }
        catch (OperationCanceledException) { throw; }
        catch { return 0; }
    }

    // ---- the last successful answer, so repeat checks cost no API quota ----

    private sealed record ReleaseCache(string Etag, string Body);

    private static string CachePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ToPlay", "update-cache.json");

    private static ReleaseCache? LoadCache()
    {
        try
        {
            if (!File.Exists(CachePath)) return null;
            if (JsonNode.Parse(File.ReadAllText(CachePath)) is not JsonObject o) return null;

            var etag = o["etag"]?.GetValue<string>();
            var body = o["body"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(etag) || string.IsNullOrWhiteSpace(body)) return null;
            return new ReleaseCache(etag!, body!);
        }
        catch { return null; }   // corrupt or unreadable — just ask GitHub again
    }

    private static void SaveCache(string etag, string body)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            var o = new JsonObject { ["etag"] = etag, ["body"] = body };
            File.WriteAllText(CachePath, o.ToJsonString());
        }
        catch { /* a cache we can't write is only a missed optimisation */ }
    }

    /// <summary>Thrown when the repository simply has no published release.</summary>
    private sealed class NoReleaseException : Exception;

    /// <summary>Throws unless the URL is HTTPS on a GitHub-owned host.</summary>
    private static void RequireTrusted(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !IsGitHubHost(uri.Host))
        {
            throw new InvalidOperationException(
                "The update link doesn't point at ToPlay's official GitHub download, so it was ignored.");
        }
    }

    private static bool IsGitHubHost(string host) =>
        host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
        || host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>Parses "v2.3.0", "2.3.0.0" or "2.3.0-beta" into 2.3.0.</summary>
    private static Version? ParseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var s = text.Trim();
        if (s.StartsWith("v", StringComparison.OrdinalIgnoreCase)) s = s[1..];
        var cut = s.IndexOfAny(new[] { '-', '+', ' ' });
        if (cut > 0) s = s[..cut];
        return Version.TryParse(s, out var v) ? Normalize(v) : null;
    }

    /// <summary>Compares as MAJOR.MINOR.PATCH — the build/revision field is noise.</summary>
    private static Version Normalize(Version v) =>
        new(v.Major, v.Minor, Math.Max(v.Build, 0));

    /// <summary>Release notes come as Markdown with Unix line endings.</summary>
    private static string CleanNotes(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "No release notes were published for this version.";
        return body.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", Environment.NewLine).Trim();
    }
}
