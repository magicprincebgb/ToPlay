using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using SIPSorcery.Net;
using ToPlay.Host.Config;
using ToPlay.Host.Data;
using ToPlay.Host.Security;
using ToPlay.Host.Services;
using ToPlay.Host.WebRtc;

// Become DPI-aware before anything queries monitors: on a scaled display
// (e.g. 1920x1080 @ 125%) this makes GetMonitorInfo report real pixels so
// ffmpeg captures the whole screen and touch coordinates map 1:1.
ToPlay.Host.Display.Dpi.EnablePerMonitorV2();

var dataDir = Path.Combine(AppContext.BaseDirectory, "data");
Directory.CreateDirectory(dataDir);

var configPath = Path.Combine(dataDir, "config.json");
var config = HostConfig.Load(configPath);

var userStore = new UserStore(Path.Combine(dataDir, "toplay.db"));
var auth = new AuthService(userStore);
var host = new StreamHost(config, configPath);

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o => o.SingleLine = true);
builder.Services.AddSingleton(config);
builder.Services.AddSingleton(auth);
builder.Services.AddSingleton(host);

X509CertificateHolder? certHolder = null;
if (config.UseHttps)
{
    var cert = DevCertificate.LoadOrCreate(Path.Combine(dataDir, "toplay-cert.pfx"), "toplay");
    certHolder = new X509CertificateHolder(cert);
}

builder.WebHost.ConfigureKestrel(k =>
{
    k.ListenAnyIP(config.HttpPort);
    if (config.UseHttps && certHolder != null)
        k.ListenAnyIP(config.HttpsPort, lo => lo.UseHttps(certHolder.Certificate));
});

var app = builder.Build();
app.UseWebSockets();

// Hosts this machine legitimately answers to. Used to block DNS-rebinding
// (a remote page resolving its own name to 127.0.0.1 to reach this local server
// and abuse the loopback-privileged endpoints).
var allowedHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    "localhost", "127.0.0.1", "::1", "[::1]"
};
try { allowedHosts.Add(Dns.GetHostName()); } catch { /* best effort */ }
foreach (var lanIp in DevCertificate.LocalIPv4Addresses())
    allowedHosts.Add(lanIp.ToString());

app.Use(async (ctx, next) =>
{
    // 0) LAN-only: refuse any client that isn't the local machine or a private
    //    (RFC1918 / link-local) address. Defense in depth so the host stays
    //    unreachable from the public internet even if a router forwards a port.
    if (!IsPrivateOrLocal(ctx.Connection.RemoteIpAddress))
    {
        ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }

    // 1) Anti-DNS-rebinding: only serve requests addressed to this machine.
    var reqHost = ctx.Request.Host.Host;
    if (!allowedHosts.Contains(reqHost))
    {
        ctx.Response.StatusCode = StatusCodes.Status421MisdirectedRequest;
        return;
    }

    // 2) Keep tokens / video / touch input off cleartext: push off-box clients
    //    to HTTPS (loopback on the PC may stay on http for convenience).
    if (config.UseHttps && !ctx.Request.IsHttps && !IsLocal(ctx))
    {
        var target = $"https://{reqHost}:{config.HttpsPort}{ctx.Request.Path}{ctx.Request.QueryString}";
        ctx.Response.Redirect(target, permanent: false);
        return;
    }

    // 3) Hardening headers + strict CSP (matches our no-inline-script pages).
    var h = ctx.Response.Headers;
    h["X-Content-Type-Options"] = "nosniff";
    h["X-Frame-Options"] = "DENY";
    h["Referrer-Policy"] = "no-referrer";
    h["Content-Security-Policy"] =
        "default-src 'self'; img-src 'self' data:; media-src 'self' blob: mediastream:; " +
        "connect-src 'self' ws: wss:; style-src 'self' 'unsafe-inline'; script-src 'self'; " +
        "base-uri 'none'; frame-ancestors 'none'; object-src 'none'";

    await next();
});

app.UseDefaultFiles();
app.UseStaticFiles();

// ---- trusted-certificate download -----------------------------------------
// Phones/tablets install this one file to make ToPlay's HTTPS fully trusted
// (no more "Not secure" / "Your connection is not private" warnings). Served
// unauthenticated so it can be fetched before the first login.
var caCerPath = Path.Combine(dataDir, "toplay-ca.crt");
app.MapGet("/toplay-ca.crt", () =>
    File.Exists(caCerPath)
        ? Results.File(caCerPath, "application/x-x509-ca-cert", "ToPlay-CA.crt")
        : Results.NotFound("Certificate not generated (HTTPS may be disabled)."));

// ---- helpers ---------------------------------------------------------------


static bool IsLocal(HttpContext ctx)
{
    var ip = ctx.Connection.RemoteIpAddress;
    return ip != null && IPAddress.IsLoopback(ip);
}

// True for loopback and private LAN ranges only (RFC1918 IPv4, link-local,
// IPv6 unique-local/link-local). Everything else — i.e. the public internet —
// is rejected before any route runs.
static bool IsPrivateOrLocal(IPAddress? ip)
{
    if (ip is null) return false;
    if (IPAddress.IsLoopback(ip)) return true;
    if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();

    if (ip.AddressFamily == AddressFamily.InterNetwork)
    {
        var b = ip.GetAddressBytes();
        if (b[0] == 10) return true;                        // 10.0.0.0/8
        if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true; // 172.16.0.0/12
        if (b[0] == 192 && b[1] == 168) return true;        // 192.168.0.0/16
        if (b[0] == 169 && b[1] == 254) return true;        // 169.254.0.0/16 link-local
        return false;
    }

    if (ip.AddressFamily == AddressFamily.InterNetworkV6)
    {
        if (ip.IsIPv6LinkLocal) return true;                // fe80::/10
        var b = ip.GetAddressBytes();
        if ((b[0] & 0xFE) == 0xFC) return true;             // fc00::/7 unique-local
        return false;
    }

    return false;
}

static string? BearerToken(HttpContext ctx)
{
    var h = ctx.Request.Headers.Authorization.ToString();
    if (h.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        return h["Bearer ".Length..].Trim();
    return ctx.Request.Query["token"];
}

Session? Current(HttpContext ctx) => auth.Validate(BearerToken(ctx));

// ---- auth API --------------------------------------------------------------

app.MapPost("/api/login", (HttpContext ctx, LoginDto dto) =>
{
    var clientKey = ctx.Connection.RemoteIpAddress?.ToString() ?? "?";
    var result = auth.Login(dto.Username, dto.Password, clientKey);
    if (!result.Ok || result.Session is null)
        return Results.Json(new { ok = false, error = result.Error }, statusCode: 401);

    var s = result.Session;
    return Results.Json(new { ok = true, token = s.Token, username = s.Username, isAdmin = s.IsAdmin });
});

app.MapGet("/api/me", (HttpContext ctx) =>
{
    var s = Current(ctx);
    return s is null
        ? Results.Json(new { ok = false }, statusCode: 401)
        : Results.Json(new { ok = true, username = s.Username, isAdmin = s.IsAdmin });
});

app.MapPost("/api/logout", (HttpContext ctx) =>
{
    auth.Logout(BearerToken(ctx));
    return Results.Json(new { ok = true });
});

// Account creation: allowed from the PC (loopback) or by an admin. The very
// first account created becomes an admin automatically.
app.MapPost("/api/register", (HttpContext ctx, RegisterDto dto) =>
{
    var caller = Current(ctx);
    bool allowed = IsLocal(ctx) || (caller?.IsAdmin ?? false);
    if (!allowed)
        return Results.Json(new { ok = false, error = "Accounts can only be created from the host PC." }, statusCode: 403);

    bool makeAdmin = dto.IsAdmin || !auth.HasAnyUsers;
    var result = auth.Register(dto.Username, dto.Password, makeAdmin);
    return result.Ok
        ? Results.Json(new { ok = true })
        : Results.Json(new { ok = false, error = result.Error }, statusCode: 400);
});

app.MapGet("/api/users", (HttpContext ctx) =>
{
    var caller = Current(ctx);
    if (!IsLocal(ctx) && !(caller?.IsAdmin ?? false))
        return Results.Json(new { ok = false }, statusCode: 403);

    var users = auth.ListUsers().Select(u => new { id = u.Id, username = u.Username, isAdmin = u.IsAdmin, created = u.CreatedUtc });
    return Results.Json(new { ok = true, users });
});

app.MapDelete("/api/users/{id:long}", (HttpContext ctx, long id) =>
{
    var caller = Current(ctx);
    if (!IsLocal(ctx) && !(caller?.IsAdmin ?? false))
        return Results.Json(new { ok = false }, statusCode: 403);
    return Results.Json(new { ok = auth.DeleteUser(id) });
});

// ---- stream status + settings ---------------------------------------------

app.MapGet("/api/status", (HttpContext ctx) =>
{
    if (Current(ctx) is null) return Results.Json(new { ok = false }, statusCode: 401);
    return Results.Json(host.Status());
});

app.MapPost("/api/settings", (HttpContext ctx, SettingsDto dto) =>
{
    if (Current(ctx) is null) return Results.Json(new { ok = false }, statusCode: 401);

    EncoderBackend? enc = null;
    if (!string.IsNullOrEmpty(dto.Encoder) && Enum.TryParse<EncoderBackend>(dto.Encoder, true, out var e))
        enc = e;

    host.ApplySettings(dto.MonitorIndex, dto.PresetId, enc);
    return Results.Json(host.Status());
});

// ---- WebRTC signaling ------------------------------------------------------

// The browser can't attach an Authorization header to a WebSocket, so the auth
// token travels in the Sec-WebSocket-Protocol header ("toplay.auth", "<token>")
// instead of the query string — that keeps session tokens out of request logs.
const string WsAuthProtocol = "toplay.auth";

app.Map("/ws/signal", async (HttpContext ctx) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }

    var protocols = ctx.WebSockets.WebSocketRequestedProtocols;
    string? token = protocols.Count >= 2 && protocols[0] == WsAuthProtocol ? protocols[1] : null;
    if (auth.Validate(token) is null) { ctx.Response.StatusCode = 401; return; }

    using var ws = protocols.Contains(WsAuthProtocol)
        ? await ctx.WebSockets.AcceptWebSocketAsync(WsAuthProtocol)
        : await ctx.WebSockets.AcceptWebSocketAsync();
    var sendLock = new SemaphoreSlim(1, 1);
    var id = Guid.NewGuid().ToString("n")[..8];

    async Task Send(object payload)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        await sendLock.WaitAsync();
        try
        {
            if (ws.State == WebSocketState.Open)
                await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
        }
        finally { sendLock.Release(); }
    }

    var stream = host.CreateSession(id);
    if (stream is null)
    {
        await Send(new { type = "error", message = host.StatusMessage });
        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "not-ready", CancellationToken.None);
        return;
    }

    stream.LocalIceCandidate += c =>
    {
        try { _ = Send(new { type = "candidate", candidate = c.ToString(), sdpMid = c.sdpMid, sdpMLineIndex = c.sdpMLineIndex }); }
        catch { }
    };
    stream.StateChanged += s =>
    {
        if (s is RTCPeerConnectionState.failed or RTCPeerConnectionState.closed)
            try { ws.Abort(); } catch { }
    };

    Console.WriteLine($"[signal:{id}] viewer connected from {ctx.Connection.RemoteIpAddress}");

    var buffer = new byte[32 * 1024];
    try
    {
        while (ws.State == WebSocketState.Open)
        {
            var text = await ReceiveText(ws, buffer);
            if (text is null) break;

            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            var type = root.GetProperty("type").GetString();

            if (type == "offer")
            {
                var sdp = root.GetProperty("sdp").GetString() ?? "";
                var answer = await stream.AcceptOfferAsync(sdp);
                if (answer != null) await Send(new { type = "answer", sdp = answer });
                else await Send(new { type = "error", message = "Failed to accept offer." });
            }
            else if (type == "candidate")
            {
                var cand = root.TryGetProperty("candidate", out var cEl) ? cEl.GetString() : null;
                if (!string.IsNullOrEmpty(cand))
                {
                    var mid = root.TryGetProperty("sdpMid", out var mEl) ? mEl.GetString() : null;
                    ushort mline = root.TryGetProperty("sdpMLineIndex", out var iEl) && iEl.ValueKind == JsonValueKind.Number
                        ? (ushort)iEl.GetInt32() : (ushort)0;
                    stream.AddRemoteIceCandidate(cand!, mid, mline);
                }
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[signal:{id}] error: {ex.Message}");
    }
    finally
    {
        Console.WriteLine($"[signal:{id}] viewer disconnected");
        stream.Dispose();
    }
});

// ---- global shutdown hotkey ------------------------------------------------

var hotkey = new HotkeyService(() =>
{
    Console.WriteLine("[host] Shutdown hotkey pressed. Stopping…");
    app.Lifetime.StopApplication();
});
hotkey.Start();

app.Lifetime.ApplicationStopping.Register(() =>
{
    try { hotkey.Dispose(); } catch { }
    try { host.Dispose(); } catch { }
});

// ---- startup banner --------------------------------------------------------

PrintBanner(config, hotkey.Description);

app.Run();


// ---- local functions & DTOs ------------------------------------------------

static async Task<string?> ReceiveText(WebSocket ws, byte[] buffer)
{
    const int maxBytes = 64 * 1024; // signaling messages are tiny; reject floods.
    using var ms = new MemoryStream();
    WebSocketReceiveResult result;
    do
    {
        result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
        if (result.MessageType == WebSocketMessageType.Close) return null;
        ms.Write(buffer, 0, result.Count);
        if (ms.Length > maxBytes)
        {
            try { await ws.CloseAsync(WebSocketCloseStatus.MessageTooBig, "too-big", CancellationToken.None); } catch { }
            return null;
        }
    }
    while (!result.EndOfMessage);
    return Encoding.UTF8.GetString(ms.ToArray());
}

void PrintBanner(HostConfig cfg, string hotkeyDesc)
{
    var ip = DevCertificate.PrimaryLanIp() ?? "<your-pc-ip>";
    var scheme = cfg.UseHttps ? "https" : "http";
    var port = cfg.UseHttps ? cfg.HttpsPort : cfg.HttpPort;

    Console.WriteLine();
    Console.WriteLine("==================================================================");
    Console.WriteLine("  ToPlay — play your PC on your phone");
    Console.WriteLine("==================================================================");
    Console.WriteLine($"  On this PC (create accounts here):  {scheme}://localhost:{port}/");
    Console.WriteLine($"  On your phone (same Wi-Fi):         {scheme}://{ip}:{port}/");
    if (cfg.UseHttps)
    {
        Console.WriteLine("  First time on a phone? To remove the browser \"Not secure\" warning,");
        Console.WriteLine($"  open this once and install the certificate: {scheme}://{ip}:{port}/toplay-ca.crt");
    }
    Console.WriteLine($"  Host status: {host.StatusMessage}");

    Console.WriteLine("  Only one phone connects at a time (a newer connection takes over).");
    Console.WriteLine($"  Shut down anytime:  {hotkeyDesc}   (or press Ctrl+C here)");
    Console.WriteLine("==================================================================");
    Console.WriteLine();
}

sealed record LoginDto(string Username, string Password);
sealed record RegisterDto(string Username, string Password, bool IsAdmin = false);
sealed record SettingsDto(int? MonitorIndex, string? PresetId, string? Encoder);

/// <summary>Tiny wrapper so we can capture the cert instance in a closure.</summary>
sealed class X509CertificateHolder(System.Security.Cryptography.X509Certificates.X509Certificate2 cert)
{
    public System.Security.Cryptography.X509Certificates.X509Certificate2 Certificate { get; } = cert;
}
