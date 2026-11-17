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

    public static ISyncStore Sync(ServiceOptions options) => options.Database.Length > 0 ? new PostgresSyncStore(Source(options)) : new SyncStore(options);

    public static IMailboxStore Mailbox(ServiceOptions options) => options.Database.Length > 0 ? new PostgresMailboxStore(Source(options)) : new MailboxStore(options);

    /// <summary>
    /// Where locked pictures are kept: in the bucket when <see cref="ServiceOptions.S3Bucket"/> is set, as files in the data folder otherwise (the database holds records,
    /// not files).
    /// </summary>
    public static IBlobStore Blobs(ServiceOptions options) =>
        options.S3Bucket.Length > 0 ? new S3BlobStore(S3BackupStore.CreateClient(options), options) : new BlobStore(options);

    public static ISignupCodes Codes(ServiceOptions options) => Codes(options, () => DateTimeOffset.UtcNow);

    internal static ISignupCodes Codes(ServiceOptions options, Func<DateTimeOffset> now) =>
        options.Database.Length > 0 ? new PostgresSignupCodes(Source(options), now) : new SignupCodes(options, now);

    /// <summary>Where backups are kept: in the bucket when <see cref="ServiceOptions.S3Bucket"/> is set, as files in the data folder otherwise.</summary>
    public static IBackupStore Backups(ServiceOptions options) =>
        options.S3Bucket.Length > 0 ? new S3BackupStore(S3BackupStore.CreateClient(options), options) : new BackupStore(options);

    /// <summary>
    /// With <see cref="ServiceOptions.RequireRemoteStorage"/> on, throws unless backups and pictures are set up for a bucket (every S3 setting but the optional ones) and a
    /// database is set, naming each setting that is missing. Does nothing when it is off.
    /// </summary>
    public static void CheckRemoteStorage(ServiceOptions options)
    {
        if (!options.RequireRemoteStorage) return;
        var missing = new List<string>();
        if (options.Database.Length == 0) missing.Add("Service__Database");
        if (options.S3Bucket.Length == 0) missing.Add("Service__S3Bucket");
        if (options.S3Endpoint.Length == 0) missing.Add("Service__S3Endpoint");
        if (options.S3AccessKey.Length == 0) missing.Add("Service__S3AccessKey");
        if (options.S3SecretKey.Length == 0) missing.Add("Service__S3SecretKey");
        if (missing.Count > 0)
            throw new InvalidOperationException("Service__RequireRemoteStorage is on, so nothing may be kept as files on this machine, but these are not set: " + string.Join(", ", missing)
                + ". Set them, or turn Service__RequireRemoteStorage off.");
    }

    /// <summary>Forgets that a database was imported into and closes it (for tests).</summary>
    internal static void Forget(string connectionString)
    {
        lock (Gate) Imported.Remove(connectionString);
        PostgresDatabase.Close(connectionString);
    }
}
