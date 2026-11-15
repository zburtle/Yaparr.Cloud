namespace Yaparr.Cloud;

public sealed record BackupVersion(int Version, long Size, DateTimeOffset At);

public enum SaveOutcome { Saved, Conflict, TooLarge, NotALockedBackup }

public sealed record SaveResult(SaveOutcome Outcome, int Version = 0, long Size = 0);

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
}
