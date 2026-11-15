using System.Buffers.Text;
using System.Security.Cryptography;
using Npgsql;

namespace Yaparr.Cloud;

/// <summary>
/// <see cref="IAccounts"/> in a database. Each call is one statement or one short transaction, so nothing is held in memory between calls and more than one
/// copy of the service can share a database. A password hash is made (slowly, on purpose) before a statement is run, never inside one.
/// </summary>
public sealed class PostgresAccounts : IAccounts
{
    private readonly NpgsqlDataSource _db;

    public PostgresAccounts(NpgsqlDataSource db) => _db = db;

    private static NpgsqlCommand Command(NpgsqlConnection c, string sql, params (string Name, object? Value)[] values)
    {
        var command = new NpgsqlCommand(sql, c);
        foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    private T? Scalar<T>(string sql, params (string, object?)[] values)
    {
        using var c = _db.OpenConnection();
        using var command = Command(c, sql, values);
        var result = command.ExecuteScalar();
        return result is null or DBNull ? default : (T)result;
    }

    private int Run(string sql, params (string, object?)[] values)
    {
        using var c = _db.OpenConnection();
        using var command = Command(c, sql, values);
        return command.ExecuteNonQuery();
    }

    public bool Exists(string name) => Scalar<int?>("select 1 from accounts where name = @n", ("n", name)) != null;

    public bool HasPassword(string name) => Scalar<bool?>("select password_hash is not null from accounts where name = @n", ("n", name)) == true;

    public IReadOnlyList<AccountEntry> List()
    {
        using var c = _db.OpenConnection();
        using var command = Command(c, """
            select a.name, a.created, (select count(*) from devices d where d.account = a.name), a.password_hash is not null, a.pass, a.signup_code, a.is_admin
            from accounts a order by a.name collate "C"
            """);
        using var reader = command.ExecuteReader();
        var all = new List<AccountEntry>();
        while (reader.Read())
            all.Add(new AccountEntry(reader.GetString(0), reader.GetFieldValue<DateTimeOffset>(1), (int)reader.GetInt64(2), reader.GetBoolean(3), reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5), reader.GetBoolean(6)));
        return all;
    }

    public bool Add(string name, string password)
    {
        var hash = Passwords.Hash(password);
        return Run("insert into accounts (name, password_hash, created) values (@n, @h, @c) on conflict (name) do nothing", ("n", name), ("h", hash), ("c", DateTimeOffset.UtcNow)) == 1;
    }

    public string? StartSignup(string name, string? signupCodeId)
    {
        var token = "set_" + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        // A new account, or a new token for one that has not chosen a password yet; an account with a password is left alone and nothing is returned.
        var made = Scalar<string>("""
            insert into accounts (name, created, pass, signup_code, setup_hash, setup_expires) values (@n, @c, @p, @s, @h, @e)
            on conflict (name) do update set setup_hash = excluded.setup_hash, setup_expires = excluded.setup_expires where accounts.password_hash is null
            returning name
            """, ("n", name), ("c", DateTimeOffset.UtcNow), ("p", AccountDirectory.LifetimePass), ("s", signupCodeId), ("h", AccountDirectory.HashOf(token)),
            ("e", DateTimeOffset.UtcNow + AccountDirectory.SetupLifetime));
        return made == null ? null : token;
    }

    public string? SetupFor(string? token)
    {
        if (token == null || !token.StartsWith("set_", StringComparison.Ordinal) || token.Length > 100) return null;
        return Scalar<string>("select name from accounts where setup_hash = @h and setup_expires > @now", ("h", AccountDirectory.HashOf(token)), ("now", DateTimeOffset.UtcNow));
    }

    public string? FinishSetup(string? token, string password)
    {
        if (SetupFor(token) is null) return null;
        var hash = Passwords.Hash(password);   // slow on purpose, so before the statement
        using var c = _db.OpenConnection();
        using var transaction = c.BeginTransaction();
        string? name;
        using (var update = new NpgsqlCommand("update accounts set password_hash = @p, setup_hash = null, setup_expires = null where setup_hash = @h and setup_expires > @now returning name", c, transaction))
        {
            update.Parameters.AddWithValue("p", hash);
            update.Parameters.AddWithValue("h", AccountDirectory.HashOf(token!));
            update.Parameters.AddWithValue("now", DateTimeOffset.UtcNow);
            name = update.ExecuteScalar() as string;
        }
        if (name == null) return null;
        using (var devices = new NpgsqlCommand("delete from devices where account = @n", c, transaction))
        {
            devices.Parameters.AddWithValue("n", name);
            devices.ExecuteNonQuery();
        }
        transaction.Commit();
        return name;
    }

    public bool SetPassword(string name, string password)
    {
        var hash = Passwords.Hash(password);
        using var c = _db.OpenConnection();
        using var transaction = c.BeginTransaction();
        using (var update = new NpgsqlCommand("update accounts set password_hash = @p where name = @n", c, transaction))
        {
            update.Parameters.AddWithValue("p", hash);
            update.Parameters.AddWithValue("n", name);
            if (update.ExecuteNonQuery() != 1) return false;
        }
        using (var devices = new NpgsqlCommand("delete from devices where account = @n", c, transaction))
        {
            devices.Parameters.AddWithValue("n", name);
            devices.ExecuteNonQuery();
        }
        transaction.Commit();
        return true;
    }

    public bool CheckPassword(string name, string password) =>
        Passwords.Verify(password, Scalar<string>("select password_hash from accounts where name = @n", ("n", name)));   // slow on purpose, and as slow for a name that is not there

    public bool Remove(string name) => Run("delete from accounts where name = @n", ("n", name)) == 1;

    public string? IssueToken(string name, string deviceName)
    {
        var token = AccountDirectory.TokenPrefix + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        using var c = _db.OpenConnection();
        using var transaction = c.BeginTransaction();
        using (var insert = new NpgsqlCommand("insert into devices (token_hash, account, name, created) select @h, name, @d, @c from accounts where name = @n", c, transaction))
        {
            insert.Parameters.AddWithValue("h", AccountDirectory.HashOf(token));
            insert.Parameters.AddWithValue("d", deviceName.Length > 60 ? deviceName[..60] : deviceName);
            insert.Parameters.AddWithValue("c", DateTimeOffset.UtcNow);
            insert.Parameters.AddWithValue("n", name);
            if (insert.ExecuteNonQuery() != 1) return null;
        }
        // Only the newest devices stay signed in.
        using (var trim = new NpgsqlCommand("delete from devices where token_hash in (select token_hash from devices where account = @n order by created desc, token_hash offset @keep)", c, transaction))
        {
            trim.Parameters.AddWithValue("n", name);
            trim.Parameters.AddWithValue("keep", AccountDirectory.MaxDevicesPerAccount);
            trim.ExecuteNonQuery();
        }
        transaction.Commit();
        return token;
    }

    public string? UserOfToken(string? token)
    {
        if (token == null || !token.StartsWith(AccountDirectory.TokenPrefix, StringComparison.Ordinal) || token.Length > 100) return null;
        return Scalar<string>("select account from devices where token_hash = @h", ("h", AccountDirectory.HashOf(token)));
    }

    public bool RevokeToken(string token) => Run("delete from devices where token_hash = @h", ("h", AccountDirectory.HashOf(token))) == 1;

    public string? MakeAdmin(string name) =>
        Scalar<string>("update accounts set is_admin = true, totp_secret = @s where name = @n and password_hash is not null returning totp_secret", ("s", Totp.NewSecret()), ("n", name));

    public bool RemoveAdmin(string name) => Run("update accounts set is_admin = false, totp_secret = null where name = @n and is_admin", ("n", name)) == 1;

    public bool IsAdmin(string name) => Scalar<bool?>("select is_admin from accounts where name = @n", ("n", name)) == true;

    public bool AnyAdmin() => Scalar<int?>("select 1 from accounts where is_admin limit 1") != null;

    public string? TotpSecretOf(string name) => Scalar<string>("select totp_secret from accounts where name = @n and is_admin", ("n", name));
}
