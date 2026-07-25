using Microsoft.Data.Sqlite;

namespace ToPlay.Host.Data;

public sealed record AppUser(long Id, string Username, string PasswordHash, bool IsAdmin, DateTime CreatedUtc);

/// <summary>
/// A "remember me" token for one device. Only the <see cref="VerifierHash"/> is
/// stored (SHA-256 of a 256-bit random secret), so a copy of the database can
/// never be replayed as a login.
/// </summary>
public sealed record RememberToken(long Id, long UserId, string Selector, string VerifierHash, DateTime ExpiresUtc);


/// <summary>
/// Tiny SQLite-backed user store. Accounts are created on the PC (admin) and
/// used to log in from phones on the LAN.
/// </summary>
public sealed class UserStore
{
    private readonly string _connectionString;

    public UserStore(string dbPath)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();

        Init();
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    private void Init()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS users (
                id            INTEGER PRIMARY KEY AUTOINCREMENT,
                username      TEXT NOT NULL UNIQUE COLLATE NOCASE,
                password_hash TEXT NOT NULL,
                is_admin      INTEGER NOT NULL DEFAULT 0,
                created_utc   TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS remember_tokens (
                id            INTEGER PRIMARY KEY AUTOINCREMENT,
                user_id       INTEGER NOT NULL,
                selector      TEXT NOT NULL UNIQUE,
                verifier_hash TEXT NOT NULL,
                device        TEXT NOT NULL DEFAULT '',
                created_utc   TEXT NOT NULL,
                expires_utc   TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }


    public int Count()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM users;";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public AppUser? FindByUsername(string username)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, username, password_hash, is_admin, created_utc FROM users WHERE username = $u LIMIT 1;";
        cmd.Parameters.AddWithValue("$u", username);
        using var r = cmd.ExecuteReader();
        return r.Read() ? Read(r) : null;
    }

    public IReadOnlyList<AppUser> All()
    {
        var list = new List<AppUser>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, username, password_hash, is_admin, created_utc FROM users ORDER BY id;";
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(Read(r));
        return list;
    }

    public AppUser Create(string username, string passwordHash, bool isAdmin)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO users (username, password_hash, is_admin, created_utc)
            VALUES ($u, $p, $a, $c);
            SELECT last_insert_rowid();
            """;
        var created = DateTime.UtcNow;
        cmd.Parameters.AddWithValue("$u", username);
        cmd.Parameters.AddWithValue("$p", passwordHash);
        cmd.Parameters.AddWithValue("$a", isAdmin ? 1 : 0);
        cmd.Parameters.AddWithValue("$c", created.ToString("o"));
        var id = Convert.ToInt64(cmd.ExecuteScalar());
        return new AppUser(id, username, passwordHash, isAdmin, created);
    }

    public bool Delete(long id)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        // Remove the account's "remember me" devices in the same breath, so a
        // deleted user can never be resumed from a phone that still has a token.
        cmd.CommandText = "DELETE FROM remember_tokens WHERE user_id = $id; DELETE FROM users WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteNonQuery() > 0;
    }

    // ------------------------------------------------------------- remember me

    /// <summary>Stores one device's token and prunes that user's oldest ones.</summary>
    public void AddRemember(long userId, string selector, string verifierHash, string device, DateTime expiresUtc, int keepPerUser)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO remember_tokens (user_id, selector, verifier_hash, device, created_utc, expires_utc)
            VALUES ($u, $s, $h, $d, $c, $e);

            DELETE FROM remember_tokens
            WHERE user_id = $u AND id NOT IN (
                SELECT id FROM remember_tokens WHERE user_id = $u ORDER BY id DESC LIMIT $k
            );
            """;
        cmd.Parameters.AddWithValue("$u", userId);
        cmd.Parameters.AddWithValue("$s", selector);
        cmd.Parameters.AddWithValue("$h", verifierHash);
        cmd.Parameters.AddWithValue("$d", device ?? string.Empty);
        cmd.Parameters.AddWithValue("$c", DateTime.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("$e", expiresUtc.ToString("o"));
        cmd.Parameters.AddWithValue("$k", keepPerUser);
        cmd.ExecuteNonQuery();
    }

    public RememberToken? FindRemember(string selector)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, user_id, selector, verifier_hash, expires_utc FROM remember_tokens WHERE selector = $s LIMIT 1;";
        cmd.Parameters.AddWithValue("$s", selector);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new RememberToken(
            r.GetInt64(0),
            r.GetInt64(1),
            r.GetString(2),
            r.GetString(3),
            DateTime.Parse(r.GetString(4), null, System.Globalization.DateTimeStyles.RoundtripKind));
    }

    public AppUser? FindById(long id)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, username, password_hash, is_admin, created_utc FROM users WHERE id = $id LIMIT 1;";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? Read(r) : null;
    }

    public void DeleteRemember(string selector)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM remember_tokens WHERE selector = $s;";
        cmd.Parameters.AddWithValue("$s", selector);
        cmd.ExecuteNonQuery();
    }

    public void DeleteExpiredRemember()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM remember_tokens WHERE expires_utc < $now;";
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
    }


    private static AppUser Read(SqliteDataReader r) => new(
        r.GetInt64(0),
        r.GetString(1),
        r.GetString(2),
        r.GetInt32(3) != 0,
        DateTime.Parse(r.GetString(4), null, System.Globalization.DateTimeStyles.RoundtripKind));
}
