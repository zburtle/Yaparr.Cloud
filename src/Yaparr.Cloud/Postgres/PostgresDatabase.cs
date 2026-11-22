using Npgsql;

namespace Yaparr.Cloud;

/// <summary>
/// The connection to the database (Postgres) that holds accounts and signup codes when <see cref="ServiceOptions.Database"/> is set, and its schema. The
/// schema is made and brought up to date when the connection is first opened (<see cref="Open"/>), by <see cref="Migrate"/>, so a new database needs nothing done to it.
/// The backups themselves are not in the database: they are files (see <see cref="BackupStore"/>).
/// </summary>
public static class PostgresDatabase
{
    /// <summary>The version of the schema this build makes and expects. A database from a newer build is refused rather than damaged.</summary>
    public const int SchemaVersion = 6;

    private static readonly object Gate = new();
    private static readonly Dictionary<string, NpgsqlDataSource> Sources = new();

    /// <summary>The (shared) data source for a connection string, with the schema made. Throws <see cref="InvalidOperationException"/> with something fit to show if it cannot be reached or is too new.</summary>
    public static NpgsqlDataSource Open(string connectionString)
    {
        lock (Gate)
        {
            if (Sources.TryGetValue(connectionString, out var known)) return known;
            // Kerberos (GSS) is not used, and the image has no library for it, which Npgsql would otherwise complain about on every connection.
            var builder = new NpgsqlConnectionStringBuilder(connectionString) { GssEncryptionMode = GssEncryptionMode.Disable };
            var source = NpgsqlDataSource.Create(builder.ConnectionString);
            try { Migrate(source); }
            catch (NpgsqlException ex)
            {
                source.Dispose();
                throw new InvalidOperationException("Could not use the database: " + ex.Message, ex);
            }
            Sources[connectionString] = source;
            return source;
        }
    }

    /// <summary>Closes the data source for a connection string and forgets it (for tests that make and drop databases).</summary>
    internal static void Close(string connectionString)
    {
        lock (Gate)
        {
            if (Sources.Remove(connectionString, out var source)) source.Dispose();
        }
    }

    // One script, run in one transaction with a lock that makes two starting copies of the service take turns, so neither sees a half-made schema.
    internal static void Migrate(NpgsqlDataSource source)
    {
        using var connection = source.OpenConnection();
        using var transaction = connection.BeginTransaction();
        using (var lockCommand = new NpgsqlCommand("select pg_advisory_xact_lock(7340211)", connection, transaction)) lockCommand.ExecuteNonQuery();

        using (var create = new NpgsqlCommand("create table if not exists schema_version (version integer not null)", connection, transaction)) create.ExecuteNonQuery();
        int current;
        using (var read = new NpgsqlCommand("select coalesce(max(version), 0) from schema_version", connection, transaction)) current = Convert.ToInt32(read.ExecuteScalar());
        if (current > SchemaVersion) throw new InvalidOperationException($"The database is from a newer version of this service (schema {current}, this one knows {SchemaVersion}).");
        if (current < 1)
        {
            using var v1 = new NpgsqlCommand("""
                create table accounts (
                    name text primary key,
                    password_hash text,
                    created timestamptz not null,
                    pass text not null default 'lifetime',
                    signup_code text,
                    setup_hash text,
                    setup_expires timestamptz,
                    is_admin boolean not null default false,
                    totp_secret text
                );
                create index accounts_setup_hash on accounts (setup_hash) where setup_hash is not null;

                create table devices (
                    token_hash text primary key,
                    account text not null references accounts (name) on delete cascade,
                    name text not null,
                    created timestamptz not null
                );
                create index devices_account on devices (account, created);

                create table signup_codes (
                    id text primary key,
                    hash text not null unique,
                    note text not null,
                    created timestamptz not null,
                    max_uses integer,
                    expires timestamptz,
                    revoked boolean not null default false
                );

                create table signup_code_uses (
                    code_id text not null references signup_codes (id) on delete cascade,
                    email text not null,
                    at timestamptz not null,
                    primary key (code_id, email)
                );

                insert into schema_version (version) values (1);
                """, connection, transaction);
            v1.ExecuteNonQuery();
        }
        if (current < 2)
        {
            using var v2 = new NpgsqlCommand("""
                create table push_registrations (
                    account text not null,
                    device_id text not null,
                    kind integer not null,
                    endpoint text not null,
                    token_hash text not null unique,
                    created timestamptz not null,
                    primary key (account, device_id)
                );

                insert into schema_version (version) values (2);
                """, connection, transaction);
            v2.ExecuteNonQuery();
        }
        if (current < 3)
        {
            using var v3 = new NpgsqlCommand("""
                create table sync_records (
                    account text not null,
                    kind text not null,
                    id text not null,
                    clock text not null,
                    device text not null,
                    deleted boolean not null,
                    body text not null,
                    cursor bigint not null,
                    primary key (account, kind, id)
                );
                create index sync_records_cursor on sync_records (account, cursor);

                create table sync_cursors (
                    account text primary key,
                    counter bigint not null
                );

                insert into schema_version (version) values (3);
                """, connection, transaction);
            v3.ExecuteNonQuery();
        }
        if (current < 4)
        {
            using var v4 = new NpgsqlCommand("""
                create table mailbox_items (
                    mailbox text not null,
                    cursor bigint not null,
                    id text not null,
                    account text not null,
                    body text not null,
                    at timestamptz not null,
                    primary key (mailbox, cursor),
                    unique (mailbox, id)
                );
                create index mailbox_items_at on mailbox_items (at);
                create index mailbox_items_account on mailbox_items (account);

                create table mailbox_cursors (
                    mailbox text primary key,
                    counter bigint not null
                );

                insert into schema_version (version) values (4);
                """, connection, transaction);
            v4.ExecuteNonQuery();
        }
        if (current < 5)
        {
            // A device's own id and an id derived from the machine (both hashes), so the same device signing in again takes the place of its earlier sign-in. Additive: older rows have none.
            using var v5 = new NpgsqlCommand("""
                alter table devices add column device_key text;
                alter table devices add column machine_key text;
                create index devices_account_key on devices (account, device_key) where device_key is not null;
                create index devices_account_machine on devices (account, machine_key) where machine_key is not null;

                insert into schema_version (version) values (5);
                """, connection, transaction);
            v5.ExecuteNonQuery();
        }
        if (current < 6)
        {
            // Paid plans (docs/billing.md): what a provider says each subscription is, the random id each account is known by to a provider, and the webhook events handled. Additive.
            using var v6 = new NpgsqlCommand("""
                create table subscriptions (
                    provider text not null,
                    provider_subscription_id text not null,
                    account text not null,
                    provider_customer_id text not null,
                    plan text not null,
                    status integer not null,
                    period_end timestamptz not null,
                    updated timestamptz not null,
                    primary key (provider, provider_subscription_id)
                );
                create index subscriptions_account on subscriptions (account, period_end);

                create table billing_accounts (
                    account text primary key,
                    billing_id text not null unique
                );

                create table billing_events (
                    provider text not null,
                    event_id text not null,
                    received timestamptz not null,
                    primary key (provider, event_id)
                );
                create index billing_events_received on billing_events (received);

                insert into schema_version (version) values (6);
                """, connection, transaction);
            v6.ExecuteNonQuery();
        }
        transaction.Commit();
    }
}
