using Npgsql;

namespace Yaparr.Cloud;

/// <summary><see cref="ISubscriptions"/> in the database.</summary>
public sealed class PostgresSubscriptions(NpgsqlDataSource db) : ISubscriptions
{
    public void Upsert(Subscription s)
    {
        using var command = db.CreateCommand("""
            insert into subscriptions (provider, provider_subscription_id, account, provider_customer_id, plan, status, period_end, updated)
            values (@p, @i, @a, @c, @l, @s, @e, @u)
            on conflict (provider, provider_subscription_id) do update set
                account = excluded.account, provider_customer_id = excluded.provider_customer_id, plan = excluded.plan,
                status = excluded.status, period_end = excluded.period_end, updated = excluded.updated
            """);
        command.Parameters.AddWithValue("p", s.Provider);
        command.Parameters.AddWithValue("i", s.ProviderSubscriptionId);
        command.Parameters.AddWithValue("a", s.Account);
        command.Parameters.AddWithValue("c", s.ProviderCustomerId);
        command.Parameters.AddWithValue("l", s.Plan);
        command.Parameters.AddWithValue("s", (int)s.Status);
        command.Parameters.AddWithValue("e", s.PeriodEnd);
        command.Parameters.AddWithValue("u", s.UpdatedAt);
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<Subscription> Of(string account)
    {
        using var command = db.CreateCommand("select provider, provider_subscription_id, provider_customer_id, plan, status, period_end, updated from subscriptions where account = @a order by period_end desc");
        command.Parameters.AddWithValue("a", account);
        using var reader = command.ExecuteReader();
        var list = new List<Subscription>();
        while (reader.Read())
            list.Add(new Subscription(account, reader.GetString(0), reader.GetString(2), reader.GetString(1), reader.GetString(3), (SubscriptionStatus)reader.GetInt32(4),
                reader.GetFieldValue<DateTimeOffset>(5), reader.GetFieldValue<DateTimeOffset>(6)));
        return list;
    }

    public IReadOnlyList<Subscription> Stale(DateTimeOffset updatedBefore, DateTimeOffset periodEndedBefore)
    {
        using var command = db.CreateCommand("select account, provider, provider_subscription_id, provider_customer_id, plan, status, period_end, updated from subscriptions where status <> @ended and (updated < @u or period_end < @p)");
        command.Parameters.AddWithValue("ended", (int)SubscriptionStatus.Ended);
        command.Parameters.AddWithValue("u", updatedBefore);
        command.Parameters.AddWithValue("p", periodEndedBefore);
        using var reader = command.ExecuteReader();
        var list = new List<Subscription>();
        while (reader.Read())
            list.Add(new Subscription(reader.GetString(0), reader.GetString(1), reader.GetString(3), reader.GetString(2), reader.GetString(4), (SubscriptionStatus)reader.GetInt32(5),
                reader.GetFieldValue<DateTimeOffset>(6), reader.GetFieldValue<DateTimeOffset>(7)));
        return list;
    }

    public string BillingIdOf(string account)
    {
        // Two callers asking at once both end up with the one id that got in first.
        using var command = db.CreateCommand("""
            insert into billing_accounts (account, billing_id) values (@a, @b)
            on conflict (account) do update set account = excluded.account
            returning billing_id
            """);
        command.Parameters.AddWithValue("a", account);
        command.Parameters.AddWithValue("b", BillingIds.Make());
        return (string)command.ExecuteScalar()!;
    }

    public string? AccountOf(string? billingId)
    {
        if (string.IsNullOrEmpty(billingId)) return null;
        using var command = db.CreateCommand("select account from billing_accounts where billing_id = @b");
        command.Parameters.AddWithValue("b", billingId);
        return command.ExecuteScalar() as string;
    }

    public bool TryRecordEvent(string provider, string eventId, DateTimeOffset at)
    {
        using var command = db.CreateCommand("insert into billing_events (provider, event_id, received) values (@p, @e, @a) on conflict do nothing");
        command.Parameters.AddWithValue("p", provider);
        command.Parameters.AddWithValue("e", eventId);
        command.Parameters.AddWithValue("a", at);
        return command.ExecuteNonQuery() == 1;
    }

    public void ForgetEvent(string provider, string eventId)
    {
        using var command = db.CreateCommand("delete from billing_events where provider = @p and event_id = @e");
        command.Parameters.AddWithValue("p", provider);
        command.Parameters.AddWithValue("e", eventId);
        command.ExecuteNonQuery();
    }

    public int PurgeEvents(DateTimeOffset before)
    {
        using var command = db.CreateCommand("delete from billing_events where received < @b");
        command.Parameters.AddWithValue("b", before);
        return command.ExecuteNonQuery();
    }

    public void RemoveAccount(string account)
    {
        using var c = db.OpenConnection();
        using var t = c.BeginTransaction();
        foreach (var sql in new[] { "delete from subscriptions where account = @a", "delete from billing_accounts where account = @a" })
        {
            using var command = new NpgsqlCommand(sql, c, t);
            command.Parameters.AddWithValue("a", account);
            command.ExecuteNonQuery();
        }
        t.Commit();
    }
}
