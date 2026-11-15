using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

namespace Yaparr.Cloud;

/// <summary>
/// <see cref="IBackupStore"/> in an S3-compatible bucket (Cloudflare R2, DigitalOcean Spaces, MinIO): one object per version, under
/// <c>&lt;prefix&gt;&lt;account&gt;/v00000003.bin</c>, where the account is the hash of the username (see <see cref="BackupStore.FolderOf"/>), so nothing from a request ever
/// becomes part of a key. What is stored is only ever what the app has locked (checked before it is sent on), so the bucket holds nothing readable.
/// A save is written to a temporary file first, which checks it (size, locked header) before anything leaves the machine, then put with one request
/// that only succeeds if the version's key is new (<c>If-None-Match: *</c>), so two saves of the same version cannot both win, even from two copies of the
/// service. A store that does not take that header (<see cref="ServiceOptions.S3ConditionalWrites"/>) falls back to the check made before the upload.
/// </summary>
public sealed class S3BackupStore : IBackupStore
{
    private readonly IAmazonS3 _s3;
    private readonly string _bucket;
    private readonly string _prefix;
    private readonly ServiceOptions _options;
    private readonly Func<DateTimeOffset> _now;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    public S3BackupStore(IAmazonS3 s3, ServiceOptions options) : this(s3, options, () => DateTimeOffset.UtcNow) { }

    internal S3BackupStore(IAmazonS3 s3, ServiceOptions options, Func<DateTimeOffset> now)
    {
        _s3 = s3;
        _options = options;
        _now = now;
        _bucket = options.S3Bucket;
        var prefix = options.S3Prefix.Trim().TrimStart('/');
        _prefix = prefix.Length == 0 || prefix.EndsWith('/') ? prefix : prefix + "/";
    }

    /// <summary>
    /// A client for the store in the options. Checksums are only sent where a request needs one (the SDK's newer default adds a trailing checksum to every
    /// upload, which not every S3-compatible store accepts), and addressing is by path, which they all take.
    /// </summary>
    public static IAmazonS3 CreateClient(ServiceOptions options)
    {
        var config = new AmazonS3Config
        {
            ServiceURL = options.S3Endpoint,
            ForcePathStyle = true,
            AuthenticationRegion = options.S3Region.Length > 0 ? options.S3Region : "auto",
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
        };
        return new AmazonS3Client(new BasicAWSCredentials(options.S3AccessKey, options.S3SecretKey), config);
    }

    private string AccountPrefix(string account) => _prefix + BackupStore.CheckAccount(account) + "/";

    private static string KeyName(int version) => "v" + version.ToString("D8", CultureInfo.InvariantCulture) + ".bin";

    private static DateTimeOffset Utc(DateTime? time) => time is { } t ? new DateTimeOffset(DateTime.SpecifyKind(t, DateTimeKind.Utc)) : DateTimeOffset.UnixEpoch;

    // Every object under the account's prefix, a page at a time.
    private async Task<List<S3Object>> ObjectsAsync(string prefix, CancellationToken cancel)
    {
        var all = new List<S3Object>();
        string? token = null;
        do
        {
            var page = await _s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = _bucket, Prefix = prefix, ContinuationToken = token }, cancel).ConfigureAwait(false);
            all.AddRange(page.S3Objects ?? new List<S3Object>());
            token = page.IsTruncated == true ? page.NextContinuationToken : null;
        } while (token != null);
        return all;
    }

    private async Task<List<BackupVersion>> ListAsync(string account, CancellationToken cancel)
    {
        var prefix = AccountPrefix(account);
        var found = new List<BackupVersion>();
        foreach (var o in await ObjectsAsync(prefix, cancel).ConfigureAwait(false))
        {
            var name = o.Key[prefix.Length..];
            if (name.Length != 13 || name[0] != 'v' || !name.EndsWith(".bin", StringComparison.Ordinal)) continue;
            if (!int.TryParse(name.AsSpan(1, 8), NumberStyles.None, CultureInfo.InvariantCulture, out var version)) continue;
            found.Add(new BackupVersion(version, o.Size ?? 0, Utc(o.LastModified)));
        }
        return found.OrderByDescending(v => v.Version).ToList();
    }

    public async Task<IReadOnlyList<BackupVersion>> VersionsAsync(string account, CancellationToken cancel = default) =>
        await ListAsync(account, cancel).ConfigureAwait(false);

    public async Task<(Stream Content, BackupVersion Info)?> OpenAsync(string account, int? version, CancellationToken cancel = default)
    {
        var all = await ListAsync(account, cancel).ConfigureAwait(false);
        var chosen = version == null ? all.FirstOrDefault() : all.FirstOrDefault(v => v.Version == version);
        if (chosen == null) return null;
        try
        {
            var response = await _s3.GetObjectAsync(_bucket, AccountPrefix(account) + KeyName(chosen.Version), cancel).ConfigureAwait(false);
            return (new OwnedStream(response.ResponseStream, response), chosen);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound) { return null; }   // taken away between the list and the read
    }

    public async Task<SaveResult> SaveAsync(string account, int basedOn, Stream body, CancellationToken cancel)
    {
        var prefix = AccountPrefix(account);
        var gate = _locks.GetOrAdd(account, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            var existing = await ListAsync(account, cancel).ConfigureAwait(false);
            if ((existing.Count == 0 ? 0 : existing[0].Version) != basedOn) return new SaveResult(SaveOutcome.Conflict, existing.Count == 0 ? 0 : existing[0].Version);

            var temp = Path.Combine(Path.GetTempPath(), "yaparr-incoming-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                long size = 0;
                var head = new byte[BackupStore.Magic.Length];
                int headLength = 0;
                await using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                {
                    var buffer = new byte[81920];
                    int n;
                    while ((n = await body.ReadAsync(buffer, cancel).ConfigureAwait(false)) > 0)
                    {
                        size += n;
                        if (size > _options.MaxBlobBytes) return new SaveResult(SaveOutcome.TooLarge);
                        if (headLength < head.Length)
                        {
                            var take = Math.Min(n, head.Length - headLength);
                            Array.Copy(buffer, 0, head, headLength, take);
                            headLength += take;
                        }
                        await file.WriteAsync(buffer.AsMemory(0, n), cancel).ConfigureAwait(false);
                    }
                }
                if (headLength < head.Length || !head.AsSpan().SequenceEqual(BackupStore.Magic)) return new SaveResult(SaveOutcome.NotALockedBackup);

                var version = basedOn + 1;
                try
                {
                    await using var upload = new FileStream(temp, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
                    var request = new PutObjectRequest
                    {
                        BucketName = _bucket, Key = prefix + KeyName(version), InputStream = upload, ContentType = "application/octet-stream",
                        AutoCloseStream = false,
                        // Not the SDK's streaming signature (aws-chunked): R2 does not implement it. The body is signed whole instead, which reads the file once more.
                        UseChunkEncoding = false,
                    };
                    if (_options.S3ConditionalWrites) request.IfNoneMatch = "*";
                    await _s3.PutObjectAsync(request, cancel).ConfigureAwait(false);
                }
                catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.PreconditionFailed)
                {
                    // Someone else saved this version first (another copy of the service): this save is a conflict, as if it had come second.
                    var now = await ListAsync(account, cancel).ConfigureAwait(false);
                    return new SaveResult(SaveOutcome.Conflict, now.Count == 0 ? 0 : now[0].Version);
                }

                // The new one counts as of now; the older ones by when their objects were written. What is kept is Retention's rule.
                var all = existing.Append(new BackupVersion(version, size, _now())).ToList();
                var old = Retention.ToDelete(all, _now(), _options.KeepVersions, _options.KeepDays);
                if (old.Count > 0)
                {
                    try
                    {
                        await _s3.DeleteObjectsAsync(new DeleteObjectsRequest
                        {
                            BucketName = _bucket, Objects = old.Select(v => new KeyVersion { Key = prefix + KeyName(v) }).ToList(), Quiet = true,
                        }, cancel).ConfigureAwait(false);
                    }
                    catch (AmazonS3Exception) { }   // an old version that is not removed now is removed by the next save
                }
                return new SaveResult(SaveOutcome.Saved, version, size);
            }
            finally { try { File.Delete(temp); } catch (IOException) { } }
        }
        finally { gate.Release(); }
    }

    public async Task<bool> DeleteAsync(string account, CancellationToken cancel)
    {
        var prefix = AccountPrefix(account);
        var gate = _locks.GetOrAdd(account, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            var all = await ObjectsAsync(prefix, cancel).ConfigureAwait(false);
            for (int i = 0; i < all.Count; i += 1000)
                await _s3.DeleteObjectsAsync(new DeleteObjectsRequest
                {
                    BucketName = _bucket, Objects = all.Skip(i).Take(1000).Select(o => new KeyVersion { Key = o.Key }).ToList(), Quiet = true,
                }, cancel).ConfigureAwait(false);
            return all.Count > 0;
        }
        finally { gate.Release(); }
    }

    // The body of a download: reads from the response and lets go of the response (and so of the connection) when it is disposed.
    private sealed class OwnedStream : Stream
    {
        private readonly Stream _inner;
        private readonly IDisposable _owner;

        public OwnedStream(Stream inner, IDisposable owner) { _inner = inner; _owner = owner; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => _inner.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
                _owner.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
