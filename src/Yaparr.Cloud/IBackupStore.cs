namespace Yaparr.Cloud;

public sealed record BackupVersion(int Version, long Size, DateTimeOffset At);

public enum SaveOutcome { Saved, Conflict, TooLarge, NotALockedBackup }

public sealed record SaveResult(SaveOutcome Outcome, int Version = 0, long Size = 0);

/// <summary>What an account has stored: how many versions, how many bytes, and when the newest was saved.</summary>
public sealed record AccountBackups(int Versions, long Bytes, DateTimeOffset? Last);

/// <summary>
/// Where something is kept and whether it can be reached now, for the admin portal: <paramref name="Kind"/> (PostgreSQL, Cloudflare R2, Files), <paramref name="Where"/>
/// (a database or bucket name, never an address with a key in it) and a line of <paramref name="Detail"/>. When it cannot be reached <paramref name="Ok"/> is false and
/// the detail says why.
/// </summary>
public sealed record StoreStatus(string Kind, string Where, bool Ok, string Detail);

/// <summary>
/// Keeps the backups, one account's as numbered versions. The server never holds a readable backup: what it accepts is checked to be a locked one
/// (<see cref="BackupStore.Magic"/> is the first thing in it), so a plain zip, which would hold someone's identity key, is refused instead of stored.
/// Two implementations: <see cref="BackupStore"/> keeps them as files in the data folder, <see cref="S3BackupStore"/> in an S3-compatible bucket
/// (Cloudflare R2, DigitalOcean Spaces, MinIO), held to the same behaviour by the same tests.
/// </summary>
public interface IBackupStore
{
    /// <summary>The saved versions of an account, newest first.</summary>
    Task<IReadOnlyList<BackupVersion>> VersionsAsync(string account, CancellationToken cancel = default);

    /// <summary>Opens a version (the newest if <paramref name="version"/> is null) for reading, or null if there is none. The caller disposes the stream.</summary>
    Task<(Stream Content, BackupVersion Info)?> OpenAsync(string account, int? version, CancellationToken cancel = default);

    /// <summary>
    /// Saves a new version if <paramref name="basedOn"/> is the version that is newest now (0 for an account with none), so two devices cannot overwrite each
    /// other's backup without knowing, and a request that is sent twice is saved once. What is saved is checked first (not too big, a locked backup), and only then put in place.
    /// </summary>
    Task<SaveResult> SaveAsync(string account, int basedOn, Stream body, CancellationToken cancel);

    /// <summary>Removes the account's backups. False if there were none.</summary>
    Task<bool> DeleteAsync(string account, CancellationToken cancel);

    /// <summary>
    /// What every account has stored (the hashes of the accounts that have something, see <see cref="BackupStore.FolderOf"/>), in one pass: a store that is across a network
    /// must not be asked once for each account.
    /// </summary>
    Task<IReadOnlyDictionary<string, AccountBackups>> SummariesAsync(CancellationToken cancel = default);

    /// <summary>Whether the store can be reached, and where it is. Never throws: a store that is down says so.</summary>
    Task<StoreStatus> StatusAsync(CancellationToken cancel = default);
}
