using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Amazon.S3;
using Amazon.S3.Model;

namespace Yaparr.Cloud;

/// <summary>
/// The daily dumps of the database, kept in the bucket in a folder of their own (<see cref="ServiceOptions.S3DumpPrefix"/>, <c>db-dumps/</c>), apart from the backups
/// (<see cref="ServiceOptions.S3Prefix"/>), so they survive losing the machine. A dump is a gzip file of SQL: it holds password hashes and the like, so what it is allowed to
/// be named is narrow, and it is checked to be gzip before it is put there. The dump is made on the machine by <c>scripts/pg-backup.sh</c>, which sends each one
/// here with <c>dumps upload</c> and removes the old ones from the bucket with <c>dumps prune</c>.
/// </summary>
public sealed partial class DumpStore
{
    public sealed record Dump(string Name, long Size, DateTimeOffset At);

    private readonly IAmazonS3 _s3;
    private readonly string _bucket;
    private readonly string _prefix;

    public DumpStore(IAmazonS3 s3, ServiceOptions options)
    {
        _s3 = s3;
        _bucket = options.S3Bucket;
        var prefix = options.S3DumpPrefix.Trim().TrimStart('/');
        _prefix = prefix.Length == 0 || prefix.EndsWith('/') ? prefix : prefix + "/";
        // Dumps and backups must not share a folder: pruning one must never reach the other.
        var backups = options.S3Prefix.Trim().TrimStart('/');
        if (backups.Length > 0 && !backups.EndsWith('/')) backups += "/";
        if (_prefix.Length == 0 || _prefix.StartsWith(backups, StringComparison.Ordinal) && backups.Length > 0 || backups.StartsWith(_prefix, StringComparison.Ordinal))
            throw new ArgumentException("The folder for dumps has to be a different one from the folder for backups, and neither inside the other.");
    }

    // yaparr-2026-11-15.sql.gz: the only name a dump has.
    [GeneratedRegex(@"^yaparr-\d{4}-\d{2}-\d{2}\.sql\.gz$")]
    private static partial Regex NamePattern();

    public static bool IsDumpName(string? name) => name != null && NamePattern().IsMatch(name);

    private static DateTimeOffset Utc(DateTime? time) => time is { } t ? new DateTimeOffset(DateTime.SpecifyKind(t, DateTimeKind.Utc)) : DateTimeOffset.UnixEpoch;

    /// <summary>
    /// Puts a dump in the bucket (a dump of the same name is replaced: running the script twice in a day leaves the later one). Refused, with
    /// <see cref="ArgumentException"/>, if the name is not a dump's or the file is not gzip.
    /// </summary>
    public async Task<Dump> UploadAsync(string name, Stream content, CancellationToken cancel)
    {
        if (!IsDumpName(name)) throw new ArgumentException("A dump is called yaparr-YYYY-MM-DD.sql.gz.", nameof(name));
        // Written to a file first: the store needs the length, and the file is checked before anything is sent.
        var temp = Path.Combine(Path.GetTempPath(), "yaparr-dump-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            long size;
            await using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                await content.CopyToAsync(file, cancel).ConfigureAwait(false);
                size = file.Length;
            }
            var head = new byte[2];
            await using (var check = File.OpenRead(temp))
                if (await check.ReadAsync(head, cancel).ConfigureAwait(false) != 2 || head[0] != 0x1f || head[1] != 0x8b)
                    throw new ArgumentException("That is not a gzip file.", nameof(content));

            await using var upload = new FileStream(temp, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            await _s3.PutObjectAsync(new PutObjectRequest
            {
                BucketName = _bucket, Key = _prefix + name, InputStream = upload, ContentType = "application/gzip", AutoCloseStream = false,
                UseChunkEncoding = false,   // R2 does not take the streaming signature
            }, cancel).ConfigureAwait(false);
            return new Dump(name, size, DateTimeOffset.UtcNow);
        }
        finally { try { File.Delete(temp); } catch (IOException) { } }
    }

    /// <summary>The dumps in the bucket, newest first.</summary>
    public async Task<IReadOnlyList<Dump>> ListAsync(CancellationToken cancel)
    {
        var found = new List<Dump>();
        string? token = null;
        do
        {
            var page = await _s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = _bucket, Prefix = _prefix, ContinuationToken = token }, cancel).ConfigureAwait(false);
            foreach (var o in page.S3Objects ?? new List<S3Object>())
            {
                var name = o.Key[_prefix.Length..];
                if (IsDumpName(name)) found.Add(new Dump(name, o.Size ?? 0, Utc(o.LastModified)));
            }
            token = page.IsTruncated == true ? page.NextContinuationToken : null;
        } while (token != null);
        return found.OrderByDescending(d => d.Name, StringComparer.Ordinal).ToList();
    }

    /// <summary>Opens a dump for reading, or null if there is none of that name. The caller disposes the stream.</summary>
    public async Task<Stream?> OpenAsync(string name, CancellationToken cancel)
    {
        if (!IsDumpName(name)) throw new ArgumentException("A dump is called yaparr-YYYY-MM-DD.sql.gz.", nameof(name));
        try
        {
            var response = await _s3.GetObjectAsync(_bucket, _prefix + name, cancel).ConfigureAwait(false);
            return new OwnedResponseStream(response);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound) { return null; }
    }

    /// <summary>
    /// Removes the dumps made more than <paramref name="days"/> days before <paramref name="now"/> (by the date in the name), but never the newest one, so a script that
    /// stopped making dumps does not also empty the folder. Returns the names removed.
    /// </summary>
    public async Task<IReadOnlyList<string>> PruneAsync(DateTimeOffset now, int days, CancellationToken cancel)
    {
        if (days < 1) throw new ArgumentOutOfRangeException(nameof(days), "Keep at least a day.");
        var all = await ListAsync(cancel).ConfigureAwait(false);
        var cutoff = now.UtcDateTime.Date.AddDays(-days);
        var old = all.Skip(1)
            .Where(d => DateTime.TryParseExact(d.Name[7..17], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date) && date < cutoff)
            .Select(d => d.Name).ToList();
        for (int i = 0; i < old.Count; i += 1000)
            await _s3.DeleteObjectsAsync(new DeleteObjectsRequest
            {
                BucketName = _bucket, Objects = old.Skip(i).Take(1000).Select(n => new KeyVersion { Key = _prefix + n }).ToList(), Quiet = true,
            }, cancel).ConfigureAwait(false);
        return old;
    }

    private sealed class OwnedResponseStream : Stream
    {
        private readonly GetObjectResponse _response;
        private readonly Stream _inner;

        public OwnedResponseStream(GetObjectResponse response) { _response = response; _inner = response.ResponseStream; }

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
            if (disposing) { _inner.Dispose(); _response.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
