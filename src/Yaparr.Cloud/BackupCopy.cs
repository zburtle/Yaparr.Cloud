using System.Globalization;
using System.Net;
using Amazon.S3;
using Amazon.S3.Model;

namespace Yaparr.Cloud;

/// <summary>
/// Copies the backups that are files in the data folder into the bucket (<c>backups copy-to-s3</c>), for moving a service that has kept them as files over to
/// an S3-compatible store. It can be run again and again: a version already in the bucket with the same size is left alone, one with another size is reported and not
/// touched, and nothing is removed from the data folder (after checking, that is for the person to do).
/// </summary>
public static class BackupCopy
{
    public sealed record Result(int Copied, int AlreadyThere, int Different, long Bytes);

    public static async Task<Result> CopyAsync(ServiceOptions options, IAmazonS3 s3, TextWriter output, bool dryRun, CancellationToken cancel)
    {
        var prefix = options.S3Prefix.Trim().TrimStart('/');
        if (prefix.Length > 0 && !prefix.EndsWith('/')) prefix += "/";
        var root = Path.GetFullPath(options.DataDirectory);
        int copied = 0, there = 0, different = 0;
        long bytes = 0;
        if (!Directory.Exists(root)) return new Result(0, 0, 0, 0);

        foreach (var folder in Directory.EnumerateDirectories(root).OrderBy(f => f, StringComparer.Ordinal))
        {
            var account = Path.GetFileName(folder);
            if (account.Length != 64 || !account.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'))) continue;
            foreach (var path in Directory.EnumerateFiles(folder, "v????????.bin").OrderBy(f => f, StringComparer.Ordinal))
            {
                var name = Path.GetFileName(path);
                if (!int.TryParse(name.AsSpan(1, 8), NumberStyles.None, CultureInfo.InvariantCulture, out _)) continue;
                var key = prefix + account + "/" + name;
                var size = new FileInfo(path).Length;

                long? remote = null;
                try { remote = (await s3.GetObjectMetadataAsync(options.S3Bucket, key, cancel)).ContentLength; }
                catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound) { }

                if (remote == size) { there++; continue; }
                if (remote != null) { different++; output.WriteLine($"{account[..8]}/{name}: the bucket has {remote} bytes, the file {size}. Left alone."); continue; }

                if (!dryRun)
                {
                    await using var upload = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
                    var request = new PutObjectRequest { BucketName = options.S3Bucket, Key = key, InputStream = upload, ContentType = "application/octet-stream", AutoCloseStream = false, UseChunkEncoding = false };
                    if (options.S3ConditionalWrites) request.IfNoneMatch = "*";
                    try { await s3.PutObjectAsync(request, cancel); }
                    catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.PreconditionFailed) { there++; continue; }   // put there meanwhile
                    // Checked by asking the bucket what it holds, not by trusting the upload.
                    var kept = (await s3.GetObjectMetadataAsync(options.S3Bucket, key, cancel)).ContentLength;
                    if (kept != size) throw new InvalidOperationException($"{account[..8]}/{name} is {kept} bytes in the bucket after the copy, but {size} here.");
                }
                copied++;
                bytes += size;
                output.WriteLine($"{(dryRun ? "would copy" : "copied")} {account[..8]}/{name} ({size / 1024.0 / 1024.0:0.0} MiB)");
            }
        }
        return new Result(copied, there, different, bytes);
    }
}
