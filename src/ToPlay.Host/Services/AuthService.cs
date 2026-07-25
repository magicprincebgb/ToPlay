using System.Collections.Concurrent;
using System.Security.Cryptography;
using ToPlay.Host.Data;

namespace ToPlay.Host.Services;

public sealed record Session(string Token, long UserId, string Username, bool IsAdmin, DateTime ExpiresUtc);

public sealed record AuthResult(bool Ok, string? Error = null, Session? Session = null, string? Remember = null);


/// <summary>
/// Handles registration, login and session tokens. Sessions live in memory
/// (fine for a single-PC LAN host); users persist in SQLite.
/// </summary>
public sealed class AuthService
{
    private readonly UserStore _users;
    private readonly ConcurrentDictionary<string, Session> _sessions = new();
    private readonly TimeSpan _sessionLifetime = TimeSpan.FromDays(7);

    // Simple per-client login throttle to blunt brute-force attempts on the LAN.
    private readonly ConcurrentDictionary<string, Attempt> _throttle = new();
    private const int MaxFailures = 8;
    private static readonly TimeSpan LockoutWindow = TimeSpan.FromMinutes(1);

    // "Remember me": how long a device may sign itself back in for, and how many
    // devices one account can remember (older ones drop off the end).
    private static readonly TimeSpan RememberLifetime = TimeSpan.FromDays(30);
    private const int MaxRememberedDevicesPerUser = 8;


    // A pre-computed hash we verify against when a username doesn't exist, so a
    // login attempt takes the same time whether or not the account is real
    // (defeats username-enumeration by response timing).
    private static readonly string DummyHash = BCrypt.Net.BCrypt.HashPassword("toplay-dummy-password");

    private sealed class Attempt
    {
        public int Failures;
        public DateTime LockUntil;
    }

    public AuthService(UserStore users) => _users = users;

    public bool HasAnyUsers => _users.Count() > 0;

    public IReadOnlyList<AppUser> ListUsers() => _users.All();

    public AuthResult Register(string username, string password, bool isAdmin)
    {
        username = (username ?? string.Empty).Trim();
        password ??= string.Empty;

        if (username.Length is < 3 or > 32)
            return new AuthResult(false, "Username must be 3-32 characters.");
        if (!username.All(c => char.IsLetterOrDigit(c) || c is '_' or '-' or '.'))
            return new AuthResult(false, "Username may only contain letters, digits, _ - .");
        if (password.Length < 8)
            return new AuthResult(false, "Password must be at least 8 characters.");
        if (_users.FindByUsername(username) != null)
            return new AuthResult(false, "That username is already taken.");

        var hash = BCrypt.Net.BCrypt.HashPassword(password);
        _users.Create(username, hash, isAdmin);
        return new AuthResult(true);
    }

    /// <param name="clientKey">
    /// Stable per-client identifier (the remote IP) used for rate-limiting.
    /// </param>
    /// <param name="remember">
    /// When true, also issue a long-lived "remember me" token for this device so
    /// the phone can sign itself back in without the password.
    /// </param>
    /// <param name="device">Short device description, stored for the user's reference only.</param>
    public AuthResult Login(string username, string password, string clientKey, bool remember = false, string? device = null)
    {

        clientKey = string.IsNullOrEmpty(clientKey) ? "?" : clientKey;

        if (_throttle.TryGetValue(clientKey, out var existing) && existing.LockUntil > DateTime.UtcNow)
            return new AuthResult(false, "Too many attempts. Please wait a moment and try again.");

        username = (username ?? string.Empty).Trim();
        var user = _users.FindByUsername(username);

        bool valid;
        try
        {
            // Always run one BCrypt verify so timing doesn't reveal valid usernames.
            valid = user != null
                ? BCrypt.Net.BCrypt.Verify(password ?? string.Empty, user.PasswordHash)
                : RunDummyVerify(password);
        }
        catch { valid = false; }

        if (!valid || user == null)
        {
            RegisterFailure(clientKey);
            return new AuthResult(false, "Invalid username or password.");
        }

        _throttle.TryRemove(clientKey, out _);
        SweepExpiredSessions();

        var session = StartSession(user);
        var rememberToken = remember ? IssueRemember(user.Id, device) : null;
        return new AuthResult(true, Session: session, Remember: rememberToken);
    }

    /// <summary>
    /// Signs a device back in from its "remember me" token, WITHOUT the password.
    /// The token is single-use: a fresh one is returned and the old one is
    /// deleted, so a copy that was captured earlier stops working as soon as the
    /// real device reconnects.
    /// </summary>
    public AuthResult Resume(string? rememberToken, string clientKey, string? device = null)
    {
        clientKey = string.IsNullOrEmpty(clientKey) ? "?" : clientKey;

        if (_throttle.TryGetValue(clientKey, out var existing) && existing.LockUntil > DateTime.UtcNow)
            return new AuthResult(false, "Too many attempts. Please wait a moment and try again.");

        if (!TrySplitRemember(rememberToken, out var selector, out var verifier))
        {
            RegisterFailure(clientKey);
            return new AuthResult(false, "Please sign in again.");
        }

        var stored = _users.FindRemember(selector);
        if (stored == null)
        {
            RegisterFailure(clientKey);
            return new AuthResult(false, "Please sign in again.");
        }

        // Whatever happens next, this token is spent.
        _users.DeleteRemember(selector);

        if (stored.ExpiresUtc < DateTime.UtcNow)
            return new AuthResult(false, "Please sign in again.");

        // Compare hashes in constant time so a wrong secret leaks nothing.
        var presented = Convert.FromHexString(Hash(verifier));
        var expected = Convert.FromHexString(stored.VerifierHash);
        if (!CryptographicOperations.FixedTimeEquals(presented, expected))
        {
            RegisterFailure(clientKey);
            return new AuthResult(false, "Please sign in again.");
        }

        var user = _users.FindById(stored.UserId);
        if (user == null)
            return new AuthResult(false, "Please sign in again.");

        _throttle.TryRemove(clientKey, out _);
        SweepExpiredSessions();

        var session = StartSession(user);
        return new AuthResult(true, Session: session, Remember: IssueRemember(user.Id, device));
    }

    /// <summary>Forgets one device (called on sign out).</summary>
    public void ForgetDevice(string? rememberToken)
    {
        if (TrySplitRemember(rememberToken, out var selector, out _))
            _users.DeleteRemember(selector);
    }

    private Session StartSession(AppUser user)
    {
        var session = new Session(
            NewToken(),
            user.Id,
            user.Username,
            user.IsAdmin,
            DateTime.UtcNow.Add(_sessionLifetime));

        _sessions[session.Token] = session;
        return session;
    }

    /// <summary>
    /// Creates a "selector.verifier" token: the selector is the database lookup
    /// key, the verifier is a 256-bit secret we only ever store hashed. Stealing
    /// the database therefore does not let anyone sign in.
    /// </summary>
    private string IssueRemember(long userId, string? device)
    {
        _users.DeleteExpiredRemember();

        var selector = RandomHex(16);
        var verifier = RandomHex(32);
        _users.AddRemember(
            userId,
            selector,
            Hash(verifier),
            Sanitize(device),
            DateTime.UtcNow.Add(RememberLifetime),
            MaxRememberedDevicesPerUser);

        return selector + "." + verifier;
    }

    private static bool TrySplitRemember(string? token, out string selector, out string verifier)
    {
        selector = string.Empty;
        verifier = string.Empty;
        if (string.IsNullOrEmpty(token) || token.Length is < 33 or > 200) return false;

        var dot = token.IndexOf('.');
        if (dot != 32 || dot == token.Length - 1) return false;

        selector = token[..dot];
        verifier = token[(dot + 1)..];
        return IsHex(selector) && IsHex(verifier);
    }

    private static bool IsHex(string s) => s.All(Uri.IsHexDigit);

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    /// <summary>Device labels are shown back to the user, so keep them short and boring.</summary>
    private static string Sanitize(string? device)
    {
        if (string.IsNullOrWhiteSpace(device)) return "";
        var clean = new string(device.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return clean.Length > 60 ? clean[..60] : clean;
    }


    private static bool RunDummyVerify(string? password)
    {
        try { BCrypt.Net.BCrypt.Verify(password ?? string.Empty, DummyHash); } catch { }
        return false;
    }

    private void RegisterFailure(string clientKey)
    {
        var a = _throttle.GetOrAdd(clientKey, _ => new Attempt());
        lock (a)
        {
            a.Failures++;
            if (a.Failures >= MaxFailures)
            {
                a.LockUntil = DateTime.UtcNow.Add(LockoutWindow);
                a.Failures = 0;
            }
        }
    }

    public Session? Validate(string? token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        if (!_sessions.TryGetValue(token, out var session)) return null;
        if (session.ExpiresUtc < DateTime.UtcNow)
        {
            _sessions.TryRemove(token, out _);
            return null;
        }
        return session;
    }

    public void Logout(string? token)
    {
        if (!string.IsNullOrEmpty(token)) _sessions.TryRemove(token, out _);
    }

    /// <summary>
    /// Drops sessions past their expiry so stale tokens don't linger in memory
    /// forever (they were only evicted lazily when someone presented them).
    /// Called on each successful login — cheap, and logins are rare.
    /// </summary>
    private void SweepExpiredSessions()
    {
        var now = DateTime.UtcNow;
        foreach (var (token, session) in _sessions)
            if (session.ExpiresUtc < now)
                _sessions.TryRemove(token, out _);
    }

    public bool DeleteUser(long id)
    {
        var users = _users.All();
        var target = users.FirstOrDefault(u => u.Id == id);
        if (target == null) return false;

        // Never remove the last administrator — that would lock everyone out of
        // account management (which is admin/loopback-only).
        if (target.IsAdmin && users.Count(u => u.IsAdmin) <= 1)
            return false;

        // Deleting the account must also end its access right now: drop every
        // live session (the store drops its remembered devices), otherwise a
        // phone that is already signed in would keep streaming.
        foreach (var (token, session) in _sessions)
            if (session.UserId == id)
                _sessions.TryRemove(token, out _);

        return _users.Delete(id);
    }

    private static string NewToken() => RandomHex(32);

    private static string RandomHex(int byteCount)
    {
        Span<byte> bytes = stackalloc byte[byteCount];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

}
