using System.IO.Compression;
using Amazon.S3;
using Amazon.S3.Model;
using Xunit;
using Yaparr.Cloud;

namespace Yaparr.Cloud.Tests;

/// <summary>
/// The daily dumps of the database in a folder of the bucket of their own (skipped unless <c>YAPARR_TEST_S3</c> names an S3-compatible store, see
/// <see cref="S3BackupStoreTests"/>); each test works under prefixes of its own and removes what it made.
/// </summary>
public sealed class DumpStoreTests : IDisposable
{
    private readonly string _id = Guid.NewGuid().ToString("N");
    private readonly ServiceOptions _options;
    private readonly string[] _parts;

    public DumpStoreTests()
    {
        Skip.If(string.IsNullOrEmpty(Environment.GetEnvironmentVariable(S3BackupStoreTests.Variable)), "Set YAPARR_TEST_S3 to run these.");
        _parts = Environment.GetEnvironmentVariable(S3BackupStoreTests.Variable)!.Split(';');
        _options = new ServiceOptions
        {
            S3Endpoint = _parts[0], S3Bucket = _parts[1], S3AccessKey = _parts[2], S3SecretKey = _parts[3], S3Region = _parts.Length > 4 ? _parts[4] : "auto",
            S3Prefix = "dumptest-" + _id + "/backups/", S3DumpPrefix = "dumptest-" + _id + "/dumps/",
        };
        using var client = S3BackupStore.CreateClient(_options);
        try { client.PutBucketAsync(_options.S3Bucket).GetAwaiter().GetResult(); }
        catch (AmazonS3Exception) { }
    }

    public void Dispose()
    {
        using var client = S3BackupStore.CreateClient(_options);
        var listed = client.ListObjectsV2Async(new ListObjectsV2Request { BucketName = _options.S3Bucket, Prefix = "dumptest-" + _id + "/" }).GetAwaiter().GetResult();
        foreach (var o in listed.S3Objects ?? new List<S3Object>()) client.DeleteObjectAsync(_options.S3Bucket, o.Key).GetAwaiter().GetResult();
    }

    private DumpStore Store(IAmazonS3 client) => new(client, _options);

    private static byte[] Gzip(string text)
    {
        using var buffer = new MemoryStream();
        using (var gz = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
            gz.Write(System.Text.Encoding.UTF8.GetBytes(text));
        return buffer.ToArray();
    }

    private static async Task<byte[]> ReadAll(Stream? stream)
    {
        Assert.NotNull(stream);
        await using (stream)
        {
            using var copy = new MemoryStream();
            await stream!.CopyToAsync(copy);
            return copy.ToArray();
        }
    }

    [SkippableFact]
    public async Task A_dump_goes_in_the_bucket_is_listed_and_comes_back_exactly_and_the_same_name_replaces_it()
    {
        using var client = S3BackupStore.CreateClient(_options);
        var dumps = Store(client);
        Assert.Empty(await dumps.ListAsync(CancellationToken.None));

        var first = Gzip("create table accounts; -- " + new string('x', 5000));
        var put = await dumps.UploadAsync("yaparr-2026-11-15.sql.gz", new MemoryStream(first), CancellationToken.None);
        Assert.Equal(first.Length, put.Size);
        var listed = Assert.Single(await dumps.ListAsync(CancellationToken.None));
        Assert.Equal("yaparr-2026-11-15.sql.gz", listed.Name);
        Assert.Equal(first.Length, listed.Size);
        Assert.Equal(first, await ReadAll(await dumps.OpenAsync("yaparr-2026-11-15.sql.gz", CancellationToken.None)));

        var later = Gzip("a later one");
        await dumps.UploadAsync("yaparr-2026-11-15.sql.gz", new MemoryStream(later), CancellationToken.None);       // the script run twice in a day
        Assert.Equal(later, await ReadAll(await dumps.OpenAsync("yaparr-2026-11-15.sql.gz", CancellationToken.None)));
        Assert.Single(await dumps.ListAsync(CancellationToken.None));
        Assert.Null(await dumps.OpenAsync("yaparr-2026-11-14.sql.gz", CancellationToken.None));
    }

    [SkippableFact]
    public async Task Only_a_gzip_file_with_a_dumps_name_is_put_there_and_nothing_is_stored_otherwise()
    {
        using var client = S3BackupStore.CreateClient(_options);
        var dumps = Store(client);
        foreach (var bad in new[] { "../yaparr-2026-11-15.sql.gz", "yaparr-2026-11-15.sql", "yaparr-2026-11-15.sql.gz/x", "dump.sql.gz", "yaparr-26-11-15.sql.gz", "", "YAPARR-2026-11-15.sql.gz" })
            await Assert.ThrowsAsync<ArgumentException>(() => dumps.UploadAsync(bad, new MemoryStream(Gzip("x")), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => dumps.UploadAsync("yaparr-2026-11-15.sql.gz", new MemoryStream("-- a plain dump with password hashes in it"u8.ToArray()), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => dumps.UploadAsync("yaparr-2026-11-15.sql.gz", new MemoryStream(Array.Empty<byte>()), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => dumps.OpenAsync("../backups/x", CancellationToken.None));
        Assert.Empty(await dumps.ListAsync(CancellationToken.None));
    }

    [SkippableFact]
    public async Task Pruning_removes_the_old_ones_keeps_the_new_ones_and_never_the_newest_or_a_backup()
    {
        using var client = S3BackupStore.CreateClient(_options);
        var dumps = Store(client);
        foreach (var name in new[] { "yaparr-2026-09-01.sql.gz", "yaparr-2026-10-10.sql.gz", "yaparr-2026-10-20.sql.gz", "yaparr-2026-11-14.sql.gz", "yaparr-2026-11-15.sql.gz" })
            await dumps.UploadAsync(name, new MemoryStream(Gzip(name)), CancellationToken.None);
        // A backup beside them, in the other folder, and something in the dumps folder that is not a dump.
        await client.PutObjectAsync(new PutObjectRequest { BucketName = _options.S3Bucket, Key = _options.S3Prefix + new string('a', 64) + "/v00000001.bin", ContentBody = "a backup", UseChunkEncoding = false });
        await client.PutObjectAsync(new PutObjectRequest { BucketName = _options.S3Bucket, Key = _options.S3DumpPrefix + "notes.txt", ContentBody = "not a dump", UseChunkEncoding = false });

        var now = new DateTimeOffset(2026, 11, 15, 3, 17, 0, TimeSpan.Zero);
        var removed = await dumps.PruneAsync(now, 30, CancellationToken.None);
        Assert.Equal(new[] { "yaparr-2026-10-10.sql.gz", "yaparr-2026-09-01.sql.gz" }, removed);        // more than thirty days before the 15th of November
        Assert.Equal(new[] { "yaparr-2026-11-15.sql.gz", "yaparr-2026-11-14.sql.gz", "yaparr-2026-10-20.sql.gz" }, (await dumps.ListAsync(CancellationToken.None)).Select(d => d.Name));

        // A machine that stopped making dumps weeks ago must not have its last ones taken: the newest always stays.
        var much = await dumps.PruneAsync(now.AddDays(200), 1, CancellationToken.None);
        Assert.Equal(2, much.Count);
        Assert.Equal("yaparr-2026-11-15.sql.gz", Assert.Single(await dumps.ListAsync(CancellationToken.None)).Name);

        await client.GetObjectMetadataAsync(_options.S3Bucket, _options.S3Prefix + new string('a', 64) + "/v00000001.bin");       // the backup is still there
        await client.GetObjectMetadataAsync(_options.S3Bucket, _options.S3DumpPrefix + "notes.txt");                              // and so is what is not a dump
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => dumps.PruneAsync(now, 0, CancellationToken.None));
    }

    [SkippableFact]
    public void The_dumps_folder_has_to_be_apart_from_the_backups_folder()
    {
        using var client = S3BackupStore.CreateClient(_options);
        foreach (var (backups, dumps) in new[] { ("backups/", "backups/dumps/"), ("backups/db/", "backups/"), ("same/", "same/"), ("backups/", "") })
        {
            var clash = new ServiceOptions { S3Bucket = "b", S3Prefix = backups, S3DumpPrefix = dumps };
            Assert.Throws<ArgumentException>(() => new DumpStore(client, clash));
        }
        _ = new DumpStore(client, new ServiceOptions { S3Bucket = "b", S3Prefix = "backups/", S3DumpPrefix = "db-dumps/" });
        _ = new DumpStore(client, new ServiceOptions { S3Bucket = "b", S3Prefix = "", S3DumpPrefix = "db-dumps/" });
    }

    // ---- the command the script runs

    [SkippableFact]
    public async Task The_command_puts_lists_gets_and_prunes()
    {
        var gz = Gzip("select 1;");
        var output = new StringWriter();
        Assert.Equal(0, await AdminCommands.RunDumpsAsync(new[] { "upload", "yaparr-2026-11-15.sql.gz" }, _options, output, new MemoryStream(gz), Stream.Null));
        Assert.Contains("Put yaparr-2026-11-15.sql.gz", output.ToString());

        output = new StringWriter();
        Assert.Equal(0, await AdminCommands.RunDumpsAsync(new[] { "list" }, _options, output, Stream.Null, Stream.Null));
        Assert.Contains("yaparr-2026-11-15.sql.gz", output.ToString());

        var got = new MemoryStream();
        Assert.Equal(0, await AdminCommands.RunDumpsAsync(new[] { "get", "yaparr-2026-11-15.sql.gz" }, _options, new StringWriter(), Stream.Null, got));
        Assert.Equal(gz, got.ToArray());
        Assert.Equal(1, await AdminCommands.RunDumpsAsync(new[] { "get", "yaparr-2020-01-01.sql.gz" }, _options, new StringWriter(), Stream.Null, new MemoryStream()));

        output = new StringWriter();
        Assert.Equal(0, await AdminCommands.RunDumpsAsync(new[] { "prune", "--days", "7" }, _options, output, Stream.Null, Stream.Null));
        Assert.Contains("older than 7 days", output.ToString());
    }

    [SkippableFact]
    public async Task The_command_refuses_what_is_wrong_and_says_when_there_is_no_bucket()
    {
        var output = new StringWriter();
        Assert.Equal(2, await AdminCommands.RunDumpsAsync(new[] { "upload", "yaparr-2026-11-15.sql.gz" }, _options, output, new MemoryStream("not gzip"u8.ToArray()), Stream.Null));
        Assert.Contains("gzip", output.ToString());
        Assert.Equal(2, await AdminCommands.RunDumpsAsync(new[] { "upload", "dump.sql.gz" }, _options, new StringWriter(), new MemoryStream(Gzip("x")), Stream.Null));
        Assert.Equal(2, await AdminCommands.RunDumpsAsync(new[] { "upload" }, _options, new StringWriter(), Stream.Null, Stream.Null));
        Assert.Equal(2, await AdminCommands.RunDumpsAsync(new[] { "prune", "--days", "0" }, _options, new StringWriter(), Stream.Null, Stream.Null));
        Assert.Equal(2, await AdminCommands.RunDumpsAsync(new[] { "prune", "--days", "soon" }, _options, new StringWriter(), Stream.Null, Stream.Null));
        Assert.Equal(2, await AdminCommands.RunDumpsAsync(Array.Empty<string>(), _options, new StringWriter(), Stream.Null, Stream.Null));
        Assert.Equal(2, await AdminCommands.RunDumpsAsync(new[] { "nonsense" }, _options, new StringWriter(), Stream.Null, Stream.Null));

        // No bucket: exit code 3, which the script reads as "nothing to do".
        output = new StringWriter();
        Assert.Equal(3, await AdminCommands.RunDumpsAsync(new[] { "upload", "yaparr-2026-11-15.sql.gz" }, new ServiceOptions(), output, new MemoryStream(Gzip("x")), Stream.Null));
        Assert.Contains("stay on the machine", output.ToString());

        // A bucket that cannot be reached: exit code 1, with the reason, not a crash.
        var down = new ServiceOptions { S3Endpoint = "http://127.0.0.1:1", S3Bucket = "b", S3AccessKey = "a", S3SecretKey = "s" };
        output = new StringWriter();
        Assert.Equal(1, await AdminCommands.RunDumpsAsync(new[] { "upload", "yaparr-2026-11-15.sql.gz" }, down, output, new MemoryStream(Gzip("x")), Stream.Null));
        Assert.Contains("could not be used", output.ToString());
    }
}

/// <summary>Whether the last dump reached the bucket, as the admin portal says it.</summary>
public sealed class DumpUploadStatusTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "yaparr-dumpup-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);

    public DumpUploadStatusTests() => Directory.CreateDirectory(_dir);

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private ServiceOptions Options(bool bucket) => new() { DataDirectory = _dir, S3Bucket = bucket ? "b" : "" };

    private void Write(string extra) => File.WriteAllText(Path.Combine(_dir, DumpStatus.FileName), $$"""{"at":"2026-11-15T03:17:02Z","bytes":1721,"file":"x"{{extra}}}""");

    [Fact]
    public void With_a_bucket_a_dump_that_reached_it_is_fine_and_says_so()
    {
        Write(""","uploaded":true""");
        var status = DumpStatus.Read(Options(bucket: true), Now);
        Assert.True(status.Ok);
        Assert.Contains("also in the bucket", status.Detail);
    }

    [Theory]
    [InlineData(""","uploaded":false""")]
    [InlineData("")]
    public void With_a_bucket_a_dump_that_did_not_reach_it_is_a_problem(string extra)
    {
        Write(extra);
        var status = DumpStatus.Read(Options(bucket: true), Now);
        Assert.False(status.Ok);
        Assert.Contains("NOT copied to the bucket", status.Detail);
    }

    [Fact]
    public void Without_a_bucket_the_dump_is_judged_by_its_age_alone()
    {
        Write("");
        var status = DumpStatus.Read(Options(bucket: false), Now);
        Assert.True(status.Ok);
        Assert.DoesNotContain("bucket", status.Detail);
    }
}
