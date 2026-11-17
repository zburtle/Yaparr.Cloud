using Npgsql;

namespace Yaparr.Cloud;

/// <summary><see cref="IMailboxStore"/> in the database.</summary>
public sealed class PostgresMailboxStore : IMailboxStore
{
    private readonly NpgsqlDataSource _db;

    public PostgresMailboxStore(NpgsqlDataSource db) => _db = db;

    private static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc), TimeSpan.Zero);

    public MailboxPage Pull(string mailbox, long since, int limit, DateTimeOffset now, TimeSpan keep)
    {
        limit = Math.Max(1, limit);
        using var c = _db.OpenConnection();
        using var command = new NpgsqlCommand("select id, body, at, cursor from mailbox_items where mailbox = @m and cursor > @s and at >= @cut order by cursor limit @l", c);
        command.Parameters.AddWithValue("m", mailbox);
        command.Parameters.AddWithValue("s", since);
        command.Parameters.AddWithValue("cut", (now - keep).UtcDateTime);
        command.Parameters.AddWithValue("l", limit + 1);
        using var reader = command.ExecuteReader();
        var list = new List<MailboxItem>();
        while (reader.Read()) list.Add(new MailboxItem(reader.GetString(0), reader.GetString(1), Utc(reader.GetDateTime(2)), reader.GetInt64(3)));
        bool more = list.Count > limit;
        if (more) list.RemoveAt(list.Count - 1);
        return new MailboxPage(list, list.Count > 0 ? list[^1].Cursor : since, more);
    }

    public MailboxPostResult Post(string mailbox, string account, IReadOnlyList<MailboxItem> items, int maxItems, DateTimeOffset now, TimeSpan keep)
    {
        using var c = _db.OpenConnection();
        using var t = c.BeginTransaction();
        // One mailbox's posts take turns, so two devices posting at once get cursors in an order and neither takes the other's place.
        using (var gate = new NpgsqlCommand("select pg_advisory_xact_lock(hashtext(@m))", c, t))
        {
            gate.Parameters.AddWithValue("m", "mailbox:" + mailbox);
            gate.ExecuteNonQuery();
        }
        using (var expire = new NpgsqlCommand("delete from mailbox_items where mailbox = @m and at < @cut", c, t))
        {
            expire.Parameters.AddWithValue("m", mailbox);
            expire.Parameters.AddWithValue("cut", (now - keep).UtcDateTime);
            expire.ExecuteNonQuery();
        }
        long counter = 0;
        using (var read = new NpgsqlCommand("select counter from mailbox_cursors where mailbox = @m", c, t))
        {
            read.Parameters.AddWithValue("m", mailbox);
            if (read.ExecuteScalar() is long known) counter = known;
        }
        int total;
        using (var count = new NpgsqlCommand("select count(*) from mailbox_items where mailbox = @m", c, t))
        {
            count.Parameters.AddWithValue("m", mailbox);
            total = Convert.ToInt32(count.ExecuteScalar());
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var take = new List<MailboxItem>();
        foreach (var item in items)
        {
            if (!seen.Add(item.Id)) continue;
            using var find = new NpgsqlCommand("select 1 from mailbox_items where mailbox = @m and id = @i", c, t);
            find.Parameters.AddWithValue("m", mailbox);
            find.Parameters.AddWithValue("i", item.Id);
            if (find.ExecuteScalar() != null) continue;
            take.Add(item);
        }
        if (total + take.Count > Math.Max(1, maxItems))
        {
            t.Commit();   // the expiry above stands
            return new MailboxPostResult(MailboxPostOutcome.Full, counter, 0);
        }
        foreach (var item in take)
        {
            using var insert = new NpgsqlCommand("insert into mailbox_items (mailbox, cursor, id, account, body, at) values (@m, @n, @i, @a, @b, @t)", c, t);
            insert.Parameters.AddWithValue("m", mailbox);
            insert.Parameters.AddWithValue("n", ++counter);
            insert.Parameters.AddWithValue("i", item.Id);
            insert.Parameters.AddWithValue("a", account);
            insert.Parameters.AddWithValue("b", item.Body);
            insert.Parameters.AddWithValue("t", now.UtcDateTime);
            insert.ExecuteNonQuery();
        }
        if (take.Count > 0)
        {
            using var save = new NpgsqlCommand("insert into mailbox_cursors (mailbox, counter) values (@m, @n) on conflict (mailbox) do update set counter = excluded.counter", c, t);
            save.Parameters.AddWithValue("m", mailbox);
            save.Parameters.AddWithValue("n", counter);
            save.ExecuteNonQuery();
        }
        t.Commit();
        return new MailboxPostResult(MailboxPostOutcome.Stored, counter, take.Count);
    }

    public int Expire(DateTimeOffset now, TimeSpan keep)
    {
        using var c = _db.OpenConnection();
        using var command = new NpgsqlCommand("delete from mailbox_items where at < @cut", c);
        command.Parameters.AddWithValue("cut", (now - keep).UtcDateTime);
        return command.ExecuteNonQuery();
    }

    public void RemoveAccount(string account)
    {
        using var c = _db.OpenConnection();
        using var command = new NpgsqlCommand("delete from mailbox_items where account = @a", c);
        command.Parameters.AddWithValue("a", account);
        command.ExecuteNonQuery();
    }
}
