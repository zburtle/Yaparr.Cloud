using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;

namespace Yaparr.Cloud;

public sealed record BackupVersion(int Version, long Size, DateTimeOffset At);

public enum SaveOutcome { Saved, Conflict, TooLarge, NotALockedBackup }

public sealed record SaveResult(SaveOutcome Outcome, int Version = 0, long Size = 0);

/// <summary>
/// Keeps one backup per account, as numbered versions. The server never holds a readable backup: what it accepts is checked to be a locked one
/// (<see cref="Magic"/> is the first thing in it), so a plain zip, which would hold someone's identity key, is refused instead of stored.
/// </summary>
public sealed class BackupStore
{
    /// <summary>The start of a locked Yaparr backup (<c>ProfileCipher</c> in the app).</summary>
    public static readonly byte[] Magic = "YAPBAK1\n"u8.ToArray();

    private readonly string _root;
    private readonly ServiceOptions _options;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();
    private readonly Func<DateTimeOffset> _now;

    public BackupStore(ServiceOptions options) : this(options, () => DateTimeOffset.UtcNow) { }

    internal BackupStore(ServiceOptions options, Func<DateTimeOffset> now)
    {
        _now = now;
        _options = options;
        _root = Path.GetFullPath(options.DataDirectory);
        Directory.CreateDirectory(_root);
    }

    /// <summary>The name of an account's folder: the hex SHA-256 of its username, so nothing from a request ever becomes part of a path.</summary>
    public static string FolderOf(string username) =>
        Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("yaparr-account:" + username)));

    private static string FileName(int version) => "v" + version.ToString("D8", CultureInfo.InvariantCulture) + ".bin";

    // An account is 64 hex characters, so it can never name anything outside the data folder.
    private string Folder(string account)
    {
        if (account.Length != 64 || !account.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'))) throw new ArgumentException("Not an account.", nameof(account));
        return Path.Combine(_root, account);
    }

    private List<BackupVersion> List(string folder)
    {
        var found = new List<BackupVersion>();
        if (!Directory.Exists(folder)) return found;
        foreach (var path in Directory.EnumerateFiles(folder, "v????????.bin"))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (!int.TryParse(name.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var version)) continue;
            var info = new FileInfo(path);
            found.Add(new BackupVersion(version, info.Length, info.LastWriteTimeUtc));
        }
        return found.OrderByDescending(v => v.Version).ToList();
    }

    public IReadOnlyList<BackupVersion> Versions(string account) => List(Folder(account));

    /// <summary>Opens a version (the newest if <paramref name="version"/> is null) for reading, or null if there is none.</summary>
    public (Stream Content, BackupVersion Info)? Open(string account, int? version)
    {
        var folder = Folder(account);
        var all = List(folder);
        var chosen = version == null ? all.FirstOrDefault() : all.FirstOrDefault(v => v.Version == version);
        if (chosen == null) return null;
        try { return (new FileStream(Path.Combine(folder, FileName(chosen.Version)), FileMode.Open, FileAccess.Read, FileShare.Read), chosen); }
        catch (FileNotFoundException) { return null; }
    }

    /// <summary>
    /// Saves a new version if <paramref name="basedOn"/> is the version that is newest now (0 for an account with none), so two devices cannot overwrite
    /// each other's backup without knowing, and a request that is sent twice is saved once. The body is written to a temporary file and only then put in place.
    /// </summary>
    public async Task<SaveResult> SaveAsync(string account, int basedOn, Stream body, CancellationToken cancel)
    {
        var folder = Folder(account);
        var gate = _locks.GetOrAdd(account, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancel);
        try
        {
            var existing = List(folder);
            if ((existing.Count == 0 ? 0 : existing[0].Version) != basedOn) return new SaveResult(SaveOutcome.Conflict, existing.Count == 0 ? 0 : existing[0].Version);

            Directory.CreateDirectory(folder);
            var temp = Path.Combine(folder, "incoming-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                long size = 0;
                var head = new byte[Magic.Length];
                int headLength = 0;
                await using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                {
                    var buffer = new byte[81920];
                    int n;
                    while ((n = await body.ReadAsync(buffer, cancel)) > 0)
                    {
                        size += n;
                        if (size > _options.MaxBlobBytes) return new SaveResult(SaveOutcome.TooLarge);
                        if (headLength < head.Length)
                        {
                            var take = Math.Min(n, head.Length - headLength);
                            Array.Copy(buffer, 0, head, headLength, take);
                            headLength += take;
                        }
                        await file.WriteAsync(buffer.AsMemory(0, n), cancel);
                    }
                }
                if (headLength < head.Length || !head.AsSpan().SequenceEqual(Magic)) return new SaveResult(SaveOutcome.NotALockedBackup);

                var version = basedOn + 1;
                File.Move(temp, Path.Combine(folder, FileName(version)));
                // The new one counts as of now; the older ones by when their files were written. What is kept is Retention's rule.
                var all = existing.Append(new BackupVersion(version, size, _now())).ToList();
                foreach (var old in Retention.ToDelete(all, _now(), _options.KeepVersions, _options.KeepDays))
                    try { File.Delete(Path.Combine(folder, FileName(old))); } catch (IOException) { }
                return new SaveResult(SaveOutcome.Saved, version, size);
            }
            finally { try { File.Delete(temp); } catch (IOException) { } }
        }
        finally { gate.Release(); }
    }

    /// <summary>Removes the account's backups. False if there were none.</summary>
    public async Task<bool> DeleteAsync(string account, CancellationToken cancel)
    {
        var folder = Folder(account);
        var gate = _locks.GetOrAdd(account, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancel);
        try
        {
            if (!Directory.Exists(folder)) return false;
            Directory.Delete(folder, recursive: true);
            return true;
        }
        finally { gate.Release(); }
    }
}
