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
    public const int SchemaVersion = 1;

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
        transaction.Commit();
    }
}
