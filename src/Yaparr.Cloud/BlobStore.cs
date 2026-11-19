using System.Text.Json;

namespace Yaparr.Cloud;

public enum BlobPutOutcome { Stored, AlreadyThere, OverQuota }

/// <summary>
/// Locked files that go with messages (pictures): the devices lock a file with a key this service never has and give it the id <c>SHA-256 of the locked bytes</c>, so the id
/// is as unguessable as the file is secret. A blob is kept for <c>keep</c> and then goes. The account that uploaded it is kept beside it, to count its quota and so that
/// deleting the account deletes its blobs; it is never given to a reader.
/// </summary>
public interface IBlobStore
{
    /// <summary>Stores the bytes under the id unless it is already there; refuses when the account would hold more than <paramref name="quotaBytes"/> of unexpired blobs.</summary>
    BlobPutOutcome Put(string id, string account, byte[] data, long quotaBytes, DateTimeOffset now, TimeSpan keep);

    /// <summary>The bytes, or null when there is no such blob or it has expired.</summary>
    byte[]? Get(string id, DateTimeOffset now, TimeSpan keep);

    /// <summary>Removes every blob that has expired. Returns how many.</summary>
    int Expire(DateTimeOffset now, TimeSpan keep);

    /// <summary>Removes the blobs an account uploaded.</summary>
    void RemoveAccount(string account);

    /// <summary>Removes one blob, if this account uploaded it. False when there is none, or it is another account's.</summary>
    bool Remove(string id, string account);
}

/// <summary>
/// The durable kind of <see cref="IBlobStore"/>: locked pieces of what an account keeps in sync (message history, the shelves of emoji, sounds and GIFs) that never expire and
/// are counted against a quota of their own (<see cref="ServiceOptions.KeepQuotaBytes"/>). It is a store of its own (a folder, or a folder of the bucket), so the sweep of
/// expired pictures can never reach it. Make it with <see cref="Stores.Keep"/>.
/// </summary>
public interface IKeepStore : IBlobStore { }

/// <summary><see cref="IKeepStore"/> as files, in a folder <c>keep</c> of the data folder.</summary>
public sealed class KeepStore(ServiceOptions options) : BlobStore(options, "keep"), IKeepStore { }

internal static class BlobRules
{
    /// <summary>A blob's id: 64 hexadecimal digits, lower case.</summary>
    public static bool GoodId(string? id) => id is { Length: 64 } && id.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    public static string IdOf(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(data));
}

/// <summary><see cref="IBlobStore"/> as files (a folder <c>blobs</c> in the data folder: the bytes in a file named by the id, and a small file beside it that says who uploaded it and when).</summary>
public class BlobStore : IBlobStore
{
    private sealed class Meta
    {
        public string Account { get; set; } = "";
        public long Size { get; set; }
        public DateTimeOffset At { get; set; }
    }

    private readonly string _root;
    private readonly object _gate = new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public BlobStore(ServiceOptions options) : this(options, "blobs") { }

    protected BlobStore(ServiceOptions options, string folder)
    {
        _root = Path.Combine(Path.GetFullPath(options.DataDirectory), folder);
        Directory.CreateDirectory(_root);
    }

    private string DataPath(string id) => Path.Combine(_root, id);

    private string MetaPath(string id) => Path.Combine(_root, id + ".meta");

    private Meta? ReadMeta(string id)
    {
        try { return JsonSerializer.Deserialize<Meta>(File.ReadAllText(MetaPath(id)), Json); }
        catch (Exception ex) when (ex is IOException or JsonException) { return null; }
    }

    private IEnumerable<(string Id, Meta Meta)> All()
    {
        foreach (var path in Directory.EnumerateFiles(_root, "*.meta"))
        {
            var id = Path.GetFileNameWithoutExtension(path);
            if (BlobRules.GoodId(id) && ReadMeta(id) is { } meta) yield return (id, meta);
        }
    }

    private void Delete(string id)
    {
        try { File.Delete(DataPath(id)); } catch (IOException) { }
        try { File.Delete(MetaPath(id)); } catch (IOException) { }
    }

    public BlobPutOutcome Put(string id, string account, byte[] data, long quotaBytes, DateTimeOffset now, TimeSpan keep)
    {
        if (!BlobRules.GoodId(id)) throw new ArgumentException("Not a blob id.", nameof(id));
        lock (_gate)
        {
            var cutoff = now - keep;
            foreach (var (old, meta) in All().Where(b => b.Meta.At < cutoff).ToList()) Delete(old);
            if (File.Exists(DataPath(id)) && ReadMeta(id) != null) return BlobPutOutcome.AlreadyThere;
            long held = All().Where(b => b.Meta.Account == account).Sum(b => b.Meta.Size);
            if (held + data.Length > quotaBytes) return BlobPutOutcome.OverQuota;
            var temp = DataPath(id) + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(temp, data);
            File.Move(temp, DataPath(id), overwrite: true);
            File.WriteAllText(MetaPath(id), JsonSerializer.Serialize(new Meta { Account = account, Size = data.Length, At = now }, Json));
            return BlobPutOutcome.Stored;
        }
    }

    public byte[]? Get(string id, DateTimeOffset now, TimeSpan keep)
    {
        if (!BlobRules.GoodId(id)) return null;
        lock (_gate)
        {
            if (ReadMeta(id) is not { } meta || meta.At < now - keep) return null;
            try { return File.ReadAllBytes(DataPath(id)); }
            catch (IOException) { return null; }
        }
    }

    public int Expire(DateTimeOffset now, TimeSpan keep)
    {
        lock (_gate)
        {
            var cutoff = now - keep;
            var gone = All().Where(b => b.Meta.At < cutoff).Select(b => b.Id).ToList();
            foreach (var id in gone) Delete(id);
            return gone.Count;
        }
    }

    public void RemoveAccount(string account)
    {
        lock (_gate)
            foreach (var (id, _) in All().Where(b => b.Meta.Account == account).ToList()) Delete(id);
    }

    public bool Remove(string id, string account)
    {
        if (!BlobRules.GoodId(id)) return false;
        lock (_gate)
        {
            if (ReadMeta(id) is not { } meta || meta.Account != account) return false;
            Delete(id);
            return true;
        }
    }
}
