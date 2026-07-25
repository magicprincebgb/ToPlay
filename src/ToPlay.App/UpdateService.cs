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

    private const string SetupAssetName = "ToPlaySetup.exe";

    /// <summary>The version of the ToPlay.exe that is running right now.</summary>
    public static Version Current { get; } =
        Normalize(Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0));

    /// <summary>
    /// Returns the newest release if it is newer than what's installed, or
    /// <c>null</c> when we're already up to date (or nothing is published yet).
    /// </summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        using var http = NewClient(TimeSpan.FromSeconds(20));
        using var req = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApi);
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        req.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct)
                                   .ConfigureAwait(false);

        // No releases published (yet) — nothing to update to.
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;

        // GitHub throttles anonymous callers per IP; say so in plain language.
        if (resp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            throw new InvalidOperationException(
                "GitHub is temporarily limiting update checks from this network. " +
                "Please try again in a few minutes.");

        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (JsonNode.Parse(body) is not JsonObject rel)
            throw new InvalidOperationException("GitHub sent an update list we couldn't read.");

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

    private static HttpClient NewClient(TimeSpan timeout)
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };

        var http = new HttpClient(handler) { Timeout = timeout };
        // GitHub's API rejects requests without a User-Agent.
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"ToPlay/{Current} (+https://github.com/magicprincebgb/ToPlay)");
        return http;
    }

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
