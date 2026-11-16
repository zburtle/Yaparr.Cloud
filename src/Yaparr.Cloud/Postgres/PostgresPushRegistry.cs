using Npgsql;

namespace Yaparr.Cloud;

/// <summary><see cref="IPushRegistry"/> in the database. Only the hash of a wake token is kept.</summary>
public sealed class PostgresPushRegistry : IPushRegistry
{
    private readonly NpgsqlDataSource _db;
    private readonly Func<DateTimeOffset> _now;

    public PostgresPushRegistry(NpgsqlDataSource db) : this(db, () => DateTimeOffset.UtcNow) { }

    internal PostgresPushRegistry(NpgsqlDataSource db, Func<DateTimeOffset> now)
    {
        _db = db;
        _now = now;
    }

    public PushRegisterResult Register(string account, string deviceId, PushKind kind, string endpoint, int maxPerAccount)
    {
        using var c = _db.OpenConnection();
        using var t = c.BeginTransaction();
        // One account's registrations take turns, so two phones registering at once cannot both take the last place.
        using (var gate = new NpgsqlCommand("select pg_advisory_xact_lock(hashtext(@a))", c, t))
        {
            gate.Parameters.AddWithValue("a", "push:" + account);
            gate.ExecuteNonQuery();
        }
        using (var update = new NpgsqlCommand("update push_registrations set kind = @k, endpoint = @e where account = @a and device_id = @d", c, t))
        {
            update.Parameters.AddWithValue("k", (int)kind);
            update.Parameters.AddWithValue("e", endpoint);
            update.Parameters.AddWithValue("a", account);
            update.Parameters.AddWithValue("d", deviceId);
            if (update.ExecuteNonQuery() > 0)
            {
                t.Commit();
                return new PushRegisterResult(PushRegisterOutcome.Updated, null);
            }
        }
        using (var count = new NpgsqlCommand("select count(*) from push_registrations where account = @a", c, t))
        {
            count.Parameters.AddWithValue("a", account);
            if (Convert.ToInt32(count.ExecuteScalar()) >= Math.Max(1, maxPerAccount)) return new PushRegisterResult(PushRegisterOutcome.TooMany, null);
        }
        var token = WakeTokens.Make();
        using (var insert = new NpgsqlCommand("insert into push_registrations (account, device_id, kind, endpoint, token_hash, created) values (@a, @d, @k, @e, @h, @c)", c, t))
        {
            insert.Parameters.AddWithValue("a", account);
            insert.Parameters.AddWithValue("d", deviceId);
            insert.Parameters.AddWithValue("k", (int)kind);
            insert.Parameters.AddWithValue("e", endpoint);
            insert.Parameters.AddWithValue("h", WakeTokens.HashOf(token));
            insert.Parameters.AddWithValue("c", _now());
            insert.ExecuteNonQuery();
        }
        t.Commit();
        return new PushRegisterResult(PushRegisterOutcome.Created, token);
    }

    public string? Rotate(string account, string deviceId)
    {
        var token = WakeTokens.Make();
        using var c = _db.OpenConnection();
        using var command = new NpgsqlCommand("update push_registrations set token_hash = @h where account = @a and device_id = @d", c);
        command.Parameters.AddWithValue("h", WakeTokens.HashOf(token));
        command.Parameters.AddWithValue("a", account);
        command.Parameters.AddWithValue("d", deviceId);
        return command.ExecuteNonQuery() > 0 ? token : null;
    }

    public bool Remove(string account, string deviceId)
    {
        using var c = _db.OpenConnection();
        using var command = new NpgsqlCommand("delete from push_registrations where account = @a and device_id = @d", c);
        command.Parameters.AddWithValue("a", account);
        command.Parameters.AddWithValue("d", deviceId);
        return command.ExecuteNonQuery() > 0;
    }

    public IReadOnlyList<PushRegistration> List(string account)
    {
        using var c = _db.OpenConnection();
        using var command = new NpgsqlCommand("select device_id, kind, created from push_registrations where account = @a order by created, device_id", c);
        command.Parameters.AddWithValue("a", account);
        using var reader = command.ExecuteReader();
        var list = new List<PushRegistration>();
        while (reader.Read()) list.Add(new PushRegistration(reader.GetString(0), (PushKind)reader.GetInt32(1), reader.GetFieldValue<DateTimeOffset>(2)));
        return list;
    }

    public PushTarget? Find(string? token)
    {
        if (!WakeTokens.Looks(token)) return null;
        using var c = _db.OpenConnection();
        using var command = new NpgsqlCommand("select account, device_id, kind, endpoint from push_registrations where token_hash = @h", c);
        command.Parameters.AddWithValue("h", WakeTokens.HashOf(token!));
        using var reader = command.ExecuteReader();
        return reader.Read() ? new PushTarget(reader.GetString(0), reader.GetString(1), (PushKind)reader.GetInt32(2), reader.GetString(3)) : null;
    }

    public bool RemoveByToken(string token)
    {
        if (!WakeTokens.Looks(token)) return false;
        using var c = _db.OpenConnection();
        using var command = new NpgsqlCommand("delete from push_registrations where token_hash = @h", c);
        command.Parameters.AddWithValue("h", WakeTokens.HashOf(token));
        return command.ExecuteNonQuery() > 0;
    }

    public void RemoveAccount(string account)
    {
        using var c = _db.OpenConnection();
        using var command = new NpgsqlCommand("delete from push_registrations where account = @a", c);
        command.Parameters.AddWithValue("a", account);
        command.ExecuteNonQuery();
    }
}
