using Npgsql;

namespace Yaparr.Cloud;

/// <summary><see cref="ISyncStore"/> in the database. Clocks are compared here, as plain text (the database's own ordering of text depends on its language settings).</summary>
public sealed class PostgresSyncStore : ISyncStore
{
    private readonly NpgsqlDataSource _db;

    public PostgresSyncStore(NpgsqlDataSource db) => _db = db;

    public SyncPage Pull(string account, long since, int limit)
    {
        limit = Math.Max(1, limit);
        using var c = _db.OpenConnection();
        using var command = new NpgsqlCommand("select kind, id, clock, device, deleted, body, cursor from sync_records where account = @a and cursor > @s order by cursor limit @l", c);
        command.Parameters.AddWithValue("a", account);
        command.Parameters.AddWithValue("s", since);
        command.Parameters.AddWithValue("l", limit + 1);
        using var reader = command.ExecuteReader();
        var list = new List<SyncRecord>();
        while (reader.Read()) list.Add(new SyncRecord(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetBoolean(4), reader.GetString(5), reader.GetInt64(6)));
        bool more = list.Count > limit;
        if (more) list.RemoveAt(list.Count - 1);
        return new SyncPage(list, list.Count > 0 ? list[^1].Cursor : since, more);
    }

    public SyncPutResult Put(string account, IReadOnlyList<SyncRecord> records, int maxRecords)
    {
        using var c = _db.OpenConnection();
        using var t = c.BeginTransaction();
        // One account's writes take turns, so two devices sending at once get cursors in an order and neither takes the other's place.
        using (var gate = new NpgsqlCommand("select pg_advisory_xact_lock(hashtext(@a))", c, t))
        {
            gate.Parameters.AddWithValue("a", "sync:" + account);
            gate.ExecuteNonQuery();
        }
        long counter = 0;
        using (var read = new NpgsqlCommand("select counter from sync_cursors where account = @a", c, t))
        {
            read.Parameters.AddWithValue("a", account);
            if (read.ExecuteScalar() is long known) counter = known;
        }
        int total;
        using (var count = new NpgsqlCommand("select count(*) from sync_records where account = @a", c, t))
        {
            count.Parameters.AddWithValue("a", account);
            total = Convert.ToInt32(count.ExecuteScalar());
        }
        var clocks = new Dictionary<string, string>();   // the clock held (or taken earlier in this call) for each record this call touches
        var stale = new List<string>();
        var take = new List<SyncRecord>();
        int added = 0;
        foreach (var record in records)
        {
            var key = SyncRules.KeyOf(record.Kind, record.Id);
            if (!clocks.TryGetValue(key, out var clock))
            {
                using var find = new NpgsqlCommand("select clock from sync_records where account = @a and kind = @k and id = @i", c, t);
                find.Parameters.AddWithValue("a", account);
                find.Parameters.AddWithValue("k", record.Kind);
                find.Parameters.AddWithValue("i", record.Id);
                clock = find.ExecuteScalar() as string;
            }
            int order = clock == null ? 1 : string.CompareOrdinal(record.Clock, clock);
            if (order < 0) { stale.Add(key); continue; }
            if (order == 0) continue;
            if (clock == null) added++;
            clocks[key] = record.Clock;
            take.Add(record);
        }
        if (total + added > Math.Max(1, maxRecords)) return new SyncPutResult(SyncPutOutcome.TooMany, counter, stale);
        foreach (var record in take)
        {
            using var upsert = new NpgsqlCommand("""
                insert into sync_records (account, kind, id, clock, device, deleted, body, cursor) values (@a, @k, @i, @c, @d, @x, @b, @n)
                on conflict (account, kind, id) do update set clock = excluded.clock, device = excluded.device, deleted = excluded.deleted, body = excluded.body, cursor = excluded.cursor
                """, c, t);
            upsert.Parameters.AddWithValue("a", account);
            upsert.Parameters.AddWithValue("k", record.Kind);
            upsert.Parameters.AddWithValue("i", record.Id);
            upsert.Parameters.AddWithValue("c", record.Clock);
            upsert.Parameters.AddWithValue("d", record.Device);
            upsert.Parameters.AddWithValue("x", record.Deleted);
            upsert.Parameters.AddWithValue("b", record.Body);
            upsert.Parameters.AddWithValue("n", ++counter);
            upsert.ExecuteNonQuery();
        }
        if (take.Count > 0)
        {
            using var save = new NpgsqlCommand("insert into sync_cursors (account, counter) values (@a, @n) on conflict (account) do update set counter = excluded.counter", c, t);
            save.Parameters.AddWithValue("a", account);
            save.Parameters.AddWithValue("n", counter);
            save.ExecuteNonQuery();
        }
        t.Commit();
        return new SyncPutResult(SyncPutOutcome.Stored, counter, stale);
    }

    public void RemoveAccount(string account)
    {
        using var c = _db.OpenConnection();
        using var t = c.BeginTransaction();
        foreach (var table in new[] { "sync_records", "sync_cursors" })
        {
            using var command = new NpgsqlCommand($"delete from {table} where account = @a", c, t);
            command.Parameters.AddWithValue("a", account);
            command.ExecuteNonQuery();
        }
        t.Commit();
    }
}
