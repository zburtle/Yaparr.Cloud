using Npgsql;

namespace Yaparr.Cloud;

/// <summary>An account as it is kept, with everything: what moves it from the file to a database.</summary>
internal sealed record ExportedAccount(string Name, string? PasswordHash, DateTimeOffset Created, string Pass, string? SignupCode, string? SetupHash, DateTimeOffset? SetupExpires,
    bool Admin, string? TotpSecret, IReadOnlyList<ExportedDevice> Devices);

internal sealed record ExportedDevice(string Hash, string Name, DateTimeOffset Created);

/// <summary>A signup code as it is kept, with its hash and who used it.</summary>
internal sealed record ExportedCode(string Id, string Hash, string Note, DateTimeOffset Created, int? MaxUses, DateTimeOffset? Expires, bool Revoked, IReadOnlyList<CodeUse> Uses);

/// <summary>
/// Chooses where accounts and signup codes are kept: in a database when <see cref="ServiceOptions.Database"/> is set, in files otherwise. The first time a
/// database is used and is empty, the accounts and codes in the files from before are copied into it (see <see cref="PostgresImport"/>).
/// </summary>
public static class Stores
{
    private static readonly object Gate = new();
    private static readonly HashSet<string> Imported = new();

    private static NpgsqlDataSource Source(ServiceOptions options)
    {
        var source = PostgresDatabase.Open(options.Database);
        lock (Gate)
        {
            if (Imported.Add(options.Database)) PostgresImport.FromFiles(source, options);
        }
        return source;
    }

    public static IAccounts Accounts(ServiceOptions options) => options.Database.Length > 0 ? new PostgresAccounts(Source(options)) : new AccountDirectory(options);

    public static IPushRegistry Push(ServiceOptions options) => options.Database.Length > 0 ? new PostgresPushRegistry(Source(options)) : new PushRegistry(options);

    public static ISignupCodes Codes(ServiceOptions options) => Codes(options, () => DateTimeOffset.UtcNow);

    internal static ISignupCodes Codes(ServiceOptions options, Func<DateTimeOffset> now) =>
        options.Database.Length > 0 ? new PostgresSignupCodes(Source(options), now) : new SignupCodes(options, now);

    /// <summary>Where backups are kept: in the bucket when <see cref="ServiceOptions.S3Bucket"/> is set, as files in the data folder otherwise.</summary>
    public static IBackupStore Backups(ServiceOptions options) =>
        options.S3Bucket.Length > 0 ? new S3BackupStore(S3BackupStore.CreateClient(options), options) : new BackupStore(options);

    /// <summary>Forgets that a database was imported into and closes it (for tests).</summary>
    internal static void Forget(string connectionString)
    {
        lock (Gate) Imported.Remove(connectionString);
        PostgresDatabase.Close(connectionString);
    }
}
