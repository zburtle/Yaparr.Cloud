using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using Amazon.S3;
using Amazon.S3.Model;

namespace Yaparr.Cloud;

/// <summary>
/// <see cref="IBlobStore"/> in an S3-compatible bucket (Cloudflare R2, DigitalOcean Spaces, MinIO). Two kinds of object, both under <see cref="ServiceOptions.S3BlobPrefix"/>:
/// <list type="bullet">
/// <item><c>data/&lt;id&gt;</c>: the locked bytes, with the time they were stored as the object metadata <c>at</c> (Unix seconds, from the <c>now</c> the caller gives, so the
/// keeping time does not depend on the bucket's clock).</item>
/// <item><c>index/&lt;account&gt;/&lt;at&gt;.&lt;size&gt;.&lt;id&gt;</c>: an empty object for each blob an account uploaded, where the account is the hash of the username
/// (<see cref="BackupStore.FolderOf"/>, so no address is ever in the bucket) and what the account holds is read from the keys of one listing, never from the blobs themselves.
/// That is what counts a quota, finds what has expired, and removes an account's blobs.</item>
/// </list>
/// The index is written before the bytes, so a blob is never held that nothing counts; an index entry left by a failed upload is removed with the expired ones.
/// The interface is not asynchronous (the file store is not), so each call waits for the bucket.
/// </summary>
public class S3BlobStore : IBlobStore
{
    private const string AtKey = "x-amz-meta-at";

    private readonly IAmazonS3 _s3;
    private readonly string _bucket;
    private readonly string _prefix;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    private readonly record struct Entry(string Key, string Account, long At, long Size, string Id);

    public S3BlobStore(IAmazonS3 s3, ServiceOptions options) : this(s3, options, options.S3BlobPrefix, "Service__S3BlobPrefix", "pictures") { }

    protected S3BlobStore(IAmazonS3 s3, ServiceOptions options, string folder, string setting, string what)
    {
        _s3 = s3;
        _bucket = options.S3Bucket;
        _prefix = Normalize(folder);
        if (_prefix.Length == 0) throw new InvalidOperationException($"{setting} cannot be empty: {what} need a folder of their own in the bucket.");
        // Pruning one kind of object must never reach another, so the folders may not contain each other (the other two kinds of blobs are among them).
        foreach (var (name, raw) in new[] { ("S3Prefix", options.S3Prefix), ("S3DumpPrefix", options.S3DumpPrefix), ("S3BlobPrefix", options.S3BlobPrefix), ("S3KeepPrefix", options.S3KeepPrefix) })
        {
            if (setting.EndsWith("__" + name, StringComparison.Ordinal)) continue;   // this folder's own setting; the same folder in another setting is an overlap
            var other = Normalize(raw);
            if (other.Length > 0 && (_prefix.StartsWith(other, StringComparison.Ordinal) || other.StartsWith(_prefix, StringComparison.Ordinal)))
                throw new InvalidOperationException($"{setting} ({_prefix}) overlaps another folder in the bucket ({other}). Use a folder of its own.");
        }
    }

    private static string Normalize(string prefix)
    {
        var p = prefix.Trim().TrimStart('/');
        return p.Length == 0 || p.EndsWith('/') ? p : p + "/";
    }

    private string DataKey(string id) => _prefix + "data/" + id;

    private string AccountPrefix(string account) => _prefix + "index/" + account + "/";

    private static long Seconds(DateTimeOffset time) => time.ToUnixTimeSeconds();

    private string IndexKey(string account, long at, long size, string id) =>
        AccountPrefix(account) + at.ToString("D12", CultureInfo.InvariantCulture) + "." + size.ToString(CultureInfo.InvariantCulture) + "." + id;

    // Every index entry under the prefix (one account's, or everyone's), a page at a time. A key that is not one of ours is ignored.
    private List<Entry> List(string prefix)
    {
        var found = new List<Entry>();
        string? token = null;
        var indexRoot = _prefix + "index/";
        do
        {
            var page = _s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = _bucket, Prefix = prefix, ContinuationToken = token }).GetAwaiter().GetResult();
            foreach (var o in page.S3Objects ?? new List<S3Object>())
            {
                var rest = o.Key[indexRoot.Length..];
                var slash = rest.IndexOf('/');
                if (slash != 64) continue;
                var account = rest[..64];
                var parts = rest[65..].Split('.');
                if (parts.Length != 3 || !BlobRules.GoodId(parts[2])) continue;
                if (!long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var at)) continue;
                if (!long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var size)) continue;
                found.Add(new Entry(o.Key, account, at, size, parts[2]));
            }
            token = page.IsTruncated == true ? page.NextContinuationToken : null;
        } while (token != null);
        return found;
    }

    private static long? AtOf(MetadataCollection metadata)
    {
        foreach (var key in metadata.Keys)
            if (string.Equals(key, AtKey, StringComparison.OrdinalIgnoreCase) && long.TryParse(metadata[key], NumberStyles.None, CultureInfo.InvariantCulture, out var at)) return at;
        return null;
    }

    // When the bytes under an id were stored, or null when there are none (or they carry no time, so they are not ours).
    private long? StoredAt(string id)
    {
        try { return AtOf(_s3.GetObjectMetadataAsync(_bucket, DataKey(id)).GetAwaiter().GetResult().Metadata); }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound) { return null; }
    }

    // Removes an index entry and, if the bytes under its id are still the ones it stands for, the bytes. Someone else may have stored the same id since it expired,
    // and what they stored is theirs to keep.
    private void Remove(Entry entry)
    {
        if (StoredAt(entry.Id) == entry.At) _s3.DeleteObjectAsync(_bucket, DataKey(entry.Id)).GetAwaiter().GetResult();
        _s3.DeleteObjectAsync(_bucket, entry.Key).GetAwaiter().GetResult();
    }

    private void Put(string key, byte[] body, long? at)
    {
        var request = new PutObjectRequest
        {
            BucketName = _bucket, Key = key, InputStream = new MemoryStream(body), ContentType = "application/octet-stream", AutoCloseStream = true,
            // Not the SDK's streaming signature (aws-chunked): R2 does not implement it.
            UseChunkEncoding = false,
        };
        if (at is { } stamp) request.Metadata["at"] = stamp.ToString(CultureInfo.InvariantCulture);
        _s3.PutObjectAsync(request).GetAwaiter().GetResult();
    }

    public BlobPutOutcome Put(string id, string account, byte[] data, long quotaBytes, DateTimeOffset now, TimeSpan keep)
    {
        if (!BlobRules.GoodId(id)) throw new ArgumentException("Not a blob id.", nameof(id));
        var owner = BackupStore.FolderOf(account);
        var gate = _locks.GetOrAdd(owner, _ => new SemaphoreSlim(1, 1));
        gate.Wait();
        try
        {
            var cutoff = Seconds(now - keep);
            var mine = List(AccountPrefix(owner));
            foreach (var old in mine.Where(e => e.At < cutoff)) Remove(old);
            mine.RemoveAll(e => e.At < cutoff);

            if (StoredAt(id) is { } there && there >= cutoff) return BlobPutOutcome.AlreadyThere;
            if (mine.Sum(e => e.Size) + data.Length > quotaBytes) return BlobPutOutcome.OverQuota;

            var at = Seconds(now);
            var index = IndexKey(owner, at, data.Length, id);
            Put(index, Array.Empty<byte>(), null);
            try { Put(DataKey(id), data, at); }
            catch
            {
                try { _s3.DeleteObjectAsync(_bucket, index).GetAwaiter().GetResult(); } catch (AmazonS3Exception) { }
                throw;
            }
            return BlobPutOutcome.Stored;
        }
        finally { gate.Release(); }
    }

    public byte[]? Get(string id, DateTimeOffset now, TimeSpan keep)
    {
        if (!BlobRules.GoodId(id)) return null;
        try
        {
            using var response = _s3.GetObjectAsync(_bucket, DataKey(id)).GetAwaiter().GetResult();
            if (AtOf(response.Metadata) is not { } at || at < Seconds(now - keep)) return null;
            using var copy = new MemoryStream();
            response.ResponseStream.CopyTo(copy);
            return copy.ToArray();
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound) { return null; }
    }

    public int Expire(DateTimeOffset now, TimeSpan keep)
    {
        var cutoff = Seconds(now - keep);
        var gone = List(_prefix + "index/").Where(e => e.At < cutoff).ToList();
        foreach (var entry in gone) Remove(entry);
        return gone.Count;
    }

    public long UsedBytes(string account, DateTimeOffset now, TimeSpan keep)
    {
        var owner = BackupStore.FolderOf(account);
        var cutoff = Seconds(now - keep);
        return List(AccountPrefix(owner)).Where(e => e.At >= cutoff).Sum(e => e.Size);
    }

    public bool Remove(string id, string account)
    {
        if (!BlobRules.GoodId(id)) return false;
        var owner = BackupStore.FolderOf(account);
        var gate = _locks.GetOrAdd(owner, _ => new SemaphoreSlim(1, 1));
        gate.Wait();
        try
        {
            var mine = List(AccountPrefix(owner)).Where(e => e.Id == id).ToList();
            foreach (var entry in mine) Remove(entry);
            return mine.Count > 0;
        }
        finally { gate.Release(); }
    }

    public void RemoveAccount(string account)
    {
        var owner = BackupStore.FolderOf(account);
        var gate = _locks.GetOrAdd(owner, _ => new SemaphoreSlim(1, 1));
        gate.Wait();
        try { foreach (var entry in List(AccountPrefix(owner))) Remove(entry); }
        finally { gate.Release(); }
    }
}

/// <summary><see cref="IKeepStore"/> in the bucket, in the folder <see cref="ServiceOptions.S3KeepPrefix"/> (<c>keep/</c>), laid out like <see cref="S3BlobStore"/>.</summary>
public sealed class S3KeepStore(IAmazonS3 s3, ServiceOptions options) : S3BlobStore(s3, options, options.S3KeepPrefix, "Service__S3KeepPrefix", "kept pieces"), IKeepStore { }
