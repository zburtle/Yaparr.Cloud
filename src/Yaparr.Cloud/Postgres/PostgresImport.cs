using Npgsql;

namespace Yaparr.Cloud;

/// <summary>
/// Moves the accounts and signup codes of a service that kept them in files (<c>accounts.json</c>, <c>codes.json</c>) into an empty database, once, in one
/// transaction, and then renames the files <c>.imported</c> so that nothing reads them as live again. A database that already has accounts or codes is left
/// as it is, so this can run at every start.
/// </summary>
internal static class PostgresImport
{
    /// <summary>How many accounts and codes were copied, or null if there was nothing to do (no files, or the database is not empty).</summary>
    public static (int Accounts, int Codes)? FromFiles(NpgsqlDataSource db, ServiceOptions options)
    {
        var root = Path.GetFullPath(options.DataDirectory);
        var accountsFile = Path.Combine(root, "accounts.json");
        var codesFile = Path.Combine(root, "codes.json");
        if (!File.Exists(accountsFile) && !File.Exists(codesFile)) return null;

        var accounts = File.Exists(accountsFile) ? new AccountDirectory(options).Export() : Array.Empty<ExportedAccount>();
        var codes = File.Exists(codesFile) ? new SignupCodes(options).Export() : Array.Empty<ExportedCode>();

        using (var connection = db.OpenConnection())
        using (var transaction = connection.BeginTransaction())
        {
            using (var lockCommand = new NpgsqlCommand("select pg_advisory_xact_lock(7340212)", connection, transaction)) lockCommand.ExecuteNonQuery();
            using (var count = new NpgsqlCommand("select (select count(*) from accounts) + (select count(*) from signup_codes)", connection, transaction))
                if (Convert.ToInt64(count.ExecuteScalar()) > 0) return null;

            foreach (var a in accounts)
            {
                using (var insert = new NpgsqlCommand("""
                    insert into accounts (name, password_hash, created, pass, signup_code, setup_hash, setup_expires, is_admin, totp_secret)
                    values (@n, @p, @c, @pass, @s, @h, @e, @a, @t)
                    """, connection, transaction))
                {
                    insert.Parameters.AddWithValue("n", a.Name);
                    insert.Parameters.AddWithValue("p", (object?)a.PasswordHash ?? DBNull.Value);
                    insert.Parameters.AddWithValue("c", a.Created);
                    insert.Parameters.AddWithValue("pass", a.Pass);
                    insert.Parameters.AddWithValue("s", (object?)a.SignupCode ?? DBNull.Value);
                    insert.Parameters.AddWithValue("h", (object?)a.SetupHash ?? DBNull.Value);
                    insert.Parameters.AddWithValue("e", (object?)a.SetupExpires ?? DBNull.Value);
                    insert.Parameters.AddWithValue("a", a.Admin);
                    insert.Parameters.AddWithValue("t", (object?)a.TotpSecret ?? DBNull.Value);
                    insert.ExecuteNonQuery();
                }
                foreach (var d in a.Devices)
                {
                    using var device = new NpgsqlCommand("insert into devices (token_hash, account, name, created) values (@h, @a, @n, @c) on conflict (token_hash) do nothing", connection, transaction);
                    device.Parameters.AddWithValue("h", d.Hash);
                    device.Parameters.AddWithValue("a", a.Name);
                    device.Parameters.AddWithValue("n", d.Name);
                    device.Parameters.AddWithValue("c", d.Created);
                    device.ExecuteNonQuery();
                }
            }

            foreach (var code in codes)
            {
                using (var insert = new NpgsqlCommand("insert into signup_codes (id, hash, note, created, max_uses, expires, revoked) values (@i, @h, @n, @c, @m, @e, @r)", connection, transaction))
                {
                    insert.Parameters.AddWithValue("i", code.Id);
                    insert.Parameters.AddWithValue("h", code.Hash);
                    insert.Parameters.AddWithValue("n", code.Note);
                    insert.Parameters.AddWithValue("c", code.Created);
                    insert.Parameters.AddWithValue("m", (object?)code.MaxUses ?? DBNull.Value);
                    insert.Parameters.AddWithValue("e", (object?)code.Expires ?? DBNull.Value);
                    insert.Parameters.AddWithValue("r", code.Revoked);
                    insert.ExecuteNonQuery();
                }
                foreach (var use in code.Uses)
                {
                    using var usage = new NpgsqlCommand("insert into signup_code_uses (code_id, email, at) values (@i, @e, @a) on conflict do nothing", connection, transaction);
                    usage.Parameters.AddWithValue("i", code.Id);
                    usage.Parameters.AddWithValue("e", use.Email);
                    usage.Parameters.AddWithValue("a", use.At);
                    usage.ExecuteNonQuery();
                }
            }
            transaction.Commit();
        }

        // Only after the copy is safe: the files are no longer the record.
        foreach (var file in new[] { accountsFile, codesFile })
            if (File.Exists(file)) File.Move(file, file + ".imported", overwrite: true);
        return (accounts.Count, codes.Count);
    }
}
