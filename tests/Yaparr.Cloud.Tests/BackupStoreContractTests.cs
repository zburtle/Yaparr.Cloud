using System.Security.Cryptography;
using Xunit;
using Yaparr.Cloud;

namespace Yaparr.Cloud.Tests;

/// <summary>
/// What every store of backups does, whether it keeps them as files or in a bucket (see <see cref="FileBackupStoreTests"/> and
/// <see cref="S3BackupStoreTests"/>).
/// </summary>
public abstract class BackupStoreContractTests : IDisposable
{
    protected static readonly string Sam = BackupStore.FolderOf("sam@example.com");
    protected static readonly string Kim = BackupStore.FolderOf("kim@example.com");

    protected abstract IBackupStore NewStore(Action<ServiceOptions>? options = null, Func<DateTimeOffset>? now = null);

    public virtual void Dispose() { }

    protected static byte[] Locked(int length = 1000)
    {
        var body = RandomNumberGenerator.GetBytes(length);
        BackupStore.Magic.CopyTo(body, 0);
        return body;
    }

    private static Task<SaveResult> Save(IBackupStore store, string account, int basedOn, byte[] body) => store.SaveAsync(account, basedOn, new MemoryStream(body), CancellationToken.None);

    private static async Task<byte[]> ReadAll(IBackupStore store, string account, int? version = null)
    {
        var found = await store.OpenAsync(account, version);
        Assert.NotNull(found);
        await using var content = found!.Value.Content;
        using var copy = new MemoryStream();
        await content.CopyToAsync(copy);
        return copy.ToArray();
    }

    [SkippableFact]
    public async Task A_backup_is_saved_listed_and_read_back_exactly()
    {
        var store = NewStore();
        Assert.Empty(await store.VersionsAsync(Sam));
        Assert.Null(await store.OpenAsync(Sam, null));

        var body = Locked(3 * 1024 * 1024 + 17);
        var saved = await Save(store, Sam, 0, body);
        Assert.Equal(SaveOutcome.Saved, saved.Outcome);
        Assert.Equal(1, saved.Version);
        Assert.Equal(body.Length, saved.Size);

        var version = Assert.Single(await store.VersionsAsync(Sam));
        Assert.Equal(1, version.Version);
        Assert.Equal(body.Length, version.Size);
        Assert.InRange(version.At, DateTimeOffset.UtcNow.AddMinutes(-10), DateTimeOffset.UtcNow.AddMinutes(10));
        Assert.Equal(body, await ReadAll(store, Sam));
        Assert.Equal(body, await ReadAll(store, Sam, 1));
    }

    [SkippableFact]
    public async Task Versions_are_numbered_newest_first_and_each_can_be_read()
    {
        var store = NewStore(o => { o.KeepVersions = 5; o.KeepDays = 0; });
        var first = Locked(500);
        var second = Locked(700);
        Assert.Equal(1, (await Save(store, Sam, 0, first)).Version);
        Assert.Equal(2, (await Save(store, Sam, 1, second)).Version);
        var versions = await store.VersionsAsync(Sam);
        Assert.Equal(new[] { 2, 1 }, versions.Select(v => v.Version));
        Assert.Equal(new long[] { 700, 500 }, versions.Select(v => v.Size));
        Assert.Equal(second, await ReadAll(store, Sam));
        Assert.Equal(first, await ReadAll(store, Sam, 1));
        Assert.Equal(second, await ReadAll(store, Sam, 2));
        Assert.Null(await store.OpenAsync(Sam, 3));
        Assert.Null(await store.OpenAsync(Sam, 0));
    }

    [SkippableFact]
    public async Task A_save_based_on_an_old_version_is_a_conflict_that_names_the_newest_and_stores_nothing()
    {
        var store = NewStore(o => { o.KeepVersions = 5; o.KeepDays = 0; });
        await Save(store, Sam, 0, Locked());
        await Save(store, Sam, 1, Locked());
        var conflict = await Save(store, Sam, 1, Locked());
        Assert.Equal(SaveOutcome.Conflict, conflict.Outcome);
        Assert.Equal(2, conflict.Version);
        var ahead = await Save(store, Sam, 7, Locked());
        Assert.Equal(SaveOutcome.Conflict, ahead.Outcome);
        Assert.Equal(2, (await store.VersionsAsync(Sam)).Count);

        // A request that was sent twice is saved once.
        var body = Locked();
        Assert.Equal(SaveOutcome.Saved, (await Save(store, Kim, 0, body)).Outcome);
        Assert.Equal(SaveOutcome.Conflict, (await Save(store, Kim, 0, body)).Outcome);
        Assert.Single(await store.VersionsAsync(Kim));
    }

    [SkippableFact]
    public async Task Something_that_is_not_a_locked_backup_is_refused_and_not_kept()
    {
        var store = NewStore();
        Assert.Equal(SaveOutcome.NotALockedBackup, (await Save(store, Sam, 0, "PK\u0003\u0004 a plain zip with an identity key in it"u8.ToArray())).Outcome);
        Assert.Equal(SaveOutcome.NotALockedBackup, (await Save(store, Sam, 0, Array.Empty<byte>())).Outcome);
        Assert.Equal(SaveOutcome.NotALockedBackup, (await Save(store, Sam, 0, BackupStore.Magic[..4])).Outcome);
        Assert.Empty(await store.VersionsAsync(Sam));
        Assert.Equal(SaveOutcome.Saved, (await Save(store, Sam, 0, Locked(BackupStore.Magic.Length))).Outcome);     // just the header is the smallest that counts
    }

    [SkippableFact]
    public async Task A_backup_that_is_too_big_is_refused_and_not_kept()
    {
        var store = NewStore(o => o.MaxBlobBytes = 10_000);
        Assert.Equal(SaveOutcome.Saved, (await Save(store, Sam, 0, Locked(10_000))).Outcome);
        Assert.Equal(SaveOutcome.TooLarge, (await Save(store, Sam, 1, Locked(10_001))).Outcome);
        Assert.Single(await store.VersionsAsync(Sam));
    }

    [SkippableFact]
    public async Task Only_what_the_retention_rule_keeps_stays()
    {
        var now = new DateTimeOffset(2026, 11, 15, 9, 0, 0, TimeSpan.Zero);
        var store = NewStore(o => { o.KeepVersions = 2; o.KeepDays = 0; }, () => now);
        for (int i = 0; i < 5; i++)
        {
            Assert.Equal(SaveOutcome.Saved, (await Save(store, Sam, i, Locked(100 + i))).Outcome);
            now += TimeSpan.FromMinutes(1);
        }
        Assert.Equal(new[] { 5, 4 }, (await store.VersionsAsync(Sam)).Select(v => v.Version));
        Assert.Null(await store.OpenAsync(Sam, 3));
    }

    [SkippableFact]
    public async Task Deleting_an_account_removes_every_version_and_nothing_of_anyone_else()
    {
        var store = NewStore(o => { o.KeepVersions = 5; o.KeepDays = 0; });
        await Save(store, Sam, 0, Locked());
        await Save(store, Sam, 1, Locked());
        var kims = Locked(333);
        await Save(store, Kim, 0, kims);

        Assert.True(await store.DeleteAsync(Sam, CancellationToken.None));
        Assert.False(await store.DeleteAsync(Sam, CancellationToken.None));
        Assert.Empty(await store.VersionsAsync(Sam));
        Assert.Null(await store.OpenAsync(Sam, null));
        Assert.Equal(kims, await ReadAll(store, Kim));
        Assert.Equal(SaveOutcome.Saved, (await Save(store, Sam, 0, Locked())).Outcome);      // the name starts again from version 1
    }

    [SkippableFact]
    public async Task An_account_that_is_not_a_hash_is_refused_so_nothing_from_a_request_can_name_a_path_or_a_key()
    {
        var store = NewStore();
        foreach (var bad in new[] { "sam@example.com", "../" + Sam[3..], Sam + "/x", Sam[..63], Sam.ToUpperInvariant(), "" })
        {
            await Assert.ThrowsAsync<ArgumentException>(() => store.VersionsAsync(bad));
            await Assert.ThrowsAsync<ArgumentException>(() => store.OpenAsync(bad, null));
            await Assert.ThrowsAsync<ArgumentException>(() => Save(store, bad, 0, Locked()));
            await Assert.ThrowsAsync<ArgumentException>(() => store.DeleteAsync(bad, CancellationToken.None));
        }
    }

    [SkippableFact]
    public async Task Two_accounts_do_not_see_each_others_backups()
    {
        var store = NewStore();
        var sams = Locked(400);
        var kims = Locked(900);
        await Save(store, Sam, 0, sams);
        await Save(store, Kim, 0, kims);
        Assert.Equal(sams, await ReadAll(store, Sam));
        Assert.Equal(kims, await ReadAll(store, Kim));
        Assert.Equal(400, Assert.Single(await store.VersionsAsync(Sam)).Size);
    }

    [SkippableFact]
    public async Task What_every_account_has_stored_comes_in_one_pass()
    {
        var now = new DateTimeOffset(2026, 11, 15, 9, 0, 0, TimeSpan.Zero);
        var store = NewStore(o => { o.KeepVersions = 5; o.KeepDays = 0; }, () => now);
        Assert.Empty(await store.SummariesAsync());
        await Save(store, Sam, 0, Locked(1000));
        await Save(store, Sam, 1, Locked(2500));
        await Save(store, Kim, 0, Locked(400));

        var all = await store.SummariesAsync();
        Assert.Equal(2, all.Count);
        Assert.Equal(2, all[Sam].Versions);
        Assert.Equal(3500, all[Sam].Bytes);
        Assert.NotNull(all[Sam].Last);
        Assert.Equal(1, all[Kim].Versions);
        Assert.Equal(400, all[Kim].Bytes);

        await store.DeleteAsync(Sam, CancellationToken.None);
        var left = await store.SummariesAsync();
        Assert.Equal(new[] { Kim }, left.Keys);
    }

    [SkippableFact]
    public async Task The_store_says_where_it_is_and_that_it_answers()
    {
        var status = await NewStore().StatusAsync();
        Assert.True(status.Ok, status.Detail);
        Assert.NotEmpty(status.Kind);
        Assert.NotEmpty(status.Where);
        Assert.NotEmpty(status.Detail);
    }

    [SkippableFact]
    public async Task Saves_for_different_accounts_at_once_all_succeed()
    {
        var store = NewStore();
        var accounts = Enumerable.Range(0, 8).Select(i => BackupStore.FolderOf($"p{i}@example.com")).ToList();
        var results = await Task.WhenAll(accounts.Select(a => Save(store, a, 0, Locked(2000))));
        Assert.All(results, r => Assert.Equal(SaveOutcome.Saved, r.Outcome));
        foreach (var a in accounts) Assert.Single(await store.VersionsAsync(a));
    }

    [SkippableFact]
    public async Task Two_saves_of_the_same_version_at_once_leave_one_winner()
    {
        var store = NewStore();
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Save(store, Sam, 0, Locked(3000))));
        Assert.Equal(1, results.Count(r => r.Outcome == SaveOutcome.Saved));
        Assert.Equal(3, results.Count(r => r.Outcome == SaveOutcome.Conflict));
        Assert.Single(await store.VersionsAsync(Sam));
    }
}

/// <summary>The store that keeps backups as files.</summary>
public sealed class FileBackupStoreTests : BackupStoreContractTests
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "yaparr-backups-" + Guid.NewGuid().ToString("N"));

    protected override IBackupStore NewStore(Action<ServiceOptions>? options = null, Func<DateTimeOffset>? now = null)
    {
        var o = new ServiceOptions { DataDirectory = _dir };
        options?.Invoke(o);
        return new BackupStore(o, now ?? (() => DateTimeOffset.UtcNow));
    }

    public override void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }
}

/// <summary>
/// The store that keeps backups in a bucket, against whatever S3-compatible store <c>YAPARR_TEST_S3</c> names, as
/// <c>endpoint;bucket;access key;secret key;region</c> (for example <c>http://127.0.0.1:19090;yaparr-test;test;test;us-east-1</c>); skipped when it is not
/// set, run in CI against a store in a container. Each test works under a prefix of its own.
/// </summary>
public sealed class S3BackupStoreTests : BackupStoreContractTests
{
    public const string Variable = "YAPARR_TEST_S3";
    private readonly string _prefix = "test-" + Guid.NewGuid().ToString("N") + "/";
    private readonly ServiceOptions _template;

    public S3BackupStoreTests()
    {
        Skip.If(string.IsNullOrEmpty(Environment.GetEnvironmentVariable(Variable)), $"Set {Variable} to endpoint;bucket;access key;secret key;region to run these.");
        var parts = Environment.GetEnvironmentVariable(Variable)!.Split(';');
        _template = new ServiceOptions
        {
            S3Endpoint = parts[0], S3Bucket = parts[1], S3AccessKey = parts[2], S3SecretKey = parts[3], S3Region = parts.Length > 4 ? parts[4] : "auto", S3Prefix = _prefix,
            S3ConditionalWrites = Environment.GetEnvironmentVariable("YAPARR_TEST_S3_CONDITIONAL") != "0",
        };
        // A test server starts empty; a real bucket is already there (and a key limited to its objects cannot make one, which is fine).
        using var client = S3BackupStore.CreateClient(_template);
        try { client.PutBucketAsync(_template.S3Bucket).GetAwaiter().GetResult(); }
        catch (Amazon.S3.AmazonS3Exception) { }
    }

    protected override IBackupStore NewStore(Action<ServiceOptions>? options = null, Func<DateTimeOffset>? now = null)
    {
        var o = new ServiceOptions
        {
            S3Endpoint = _template.S3Endpoint, S3Bucket = _template.S3Bucket, S3AccessKey = _template.S3AccessKey, S3SecretKey = _template.S3SecretKey,
            S3Region = _template.S3Region, S3Prefix = _prefix, S3ConditionalWrites = _template.S3ConditionalWrites,
        };
        options?.Invoke(o);
        return new S3BackupStore(S3BackupStore.CreateClient(o), o, now ?? (() => DateTimeOffset.UtcNow));
    }

    [SkippableFact]
    public async Task A_put_that_must_be_new_is_refused_with_412_when_the_key_exists()
    {
        Skip.IfNot(_template.S3ConditionalWrites, "The store does not take conditional writes.");
        // What the store relies on: the header reaches the server, and a second put of the same key is refused.
        using var client = S3BackupStore.CreateClient(_template);
        var key = _prefix + "probe-" + Guid.NewGuid().ToString("N");
        async Task Put(string text)
        {
            await client.PutObjectAsync(new Amazon.S3.Model.PutObjectRequest { BucketName = _template.S3Bucket, Key = key, ContentBody = text, IfNoneMatch = "*", UseChunkEncoding = false });
        }
        await Put("first");
        var refused = await Assert.ThrowsAsync<Amazon.S3.AmazonS3Exception>(() => Put("second"));
        Assert.Equal(System.Net.HttpStatusCode.PreconditionFailed, refused.StatusCode);
        var kept = await client.GetObjectAsync(_template.S3Bucket, key);
        using var reader = new StreamReader(kept.ResponseStream);
        Assert.Equal("first", await reader.ReadToEndAsync());
    }

    [SkippableFact]
    public async Task Two_copies_of_the_service_saving_the_same_version_leave_one_winner()
    {
        Skip.IfNot(_template.S3ConditionalWrites, "The store does not take conditional writes.");
        // Whether a server decides a race between two puts atomically is the server's business: R2 says it does, a test emulator may not.
        Skip.IfNot(Environment.GetEnvironmentVariable("YAPARR_TEST_S3_ATOMIC") == "1", "Set YAPARR_TEST_S3_ATOMIC=1 for a store that decides racing conditional puts atomically (R2, MinIO).");
        // Two stores that share nothing but the bucket, as two running copies would: only the store can say which came first.
        var one = NewStore();
        var two = NewStore();
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(i => (i % 2 == 0 ? one : two).SaveAsync(Sam, 0, new MemoryStream(Locked(4000)), CancellationToken.None)));
        Assert.Equal(1, results.Count(r => r.Outcome == SaveOutcome.Saved));
        Assert.Equal(5, results.Count(r => r.Outcome == SaveOutcome.Conflict));
        Assert.Single(await one.VersionsAsync(Sam));
    }

    [SkippableFact]
    public async Task A_store_that_cannot_be_reached_says_so_quickly_without_showing_its_address_or_keys()
    {
        var down = new ServiceOptions
        {
            S3Endpoint = "http://127.0.0.1:1/abc123accountid.r2.cloudflarestorage.com", S3Bucket = "yaparr-backups", S3AccessKey = "AKIAEXAMPLEKEY", S3SecretKey = "supersecretvalue", S3Prefix = "backups/",
        };
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var status = await new S3BackupStore(S3BackupStore.CreateClient(down), down).StatusAsync();
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10));
        Assert.False(status.Ok);
        Assert.Equal("Cloudflare R2", status.Kind);
        Assert.Equal("bucket yaparr-backups", status.Where);
        var shown = status.Kind + status.Where + status.Detail;
        Assert.DoesNotContain("abc123accountid", shown);
        Assert.DoesNotContain("AKIAEXAMPLEKEY", shown);
        Assert.DoesNotContain("supersecretvalue", shown);
    }

    [SkippableFact]
    public async Task A_store_with_a_prefix_of_its_own_names_it()
    {
        var status = await NewStore().StatusAsync();
        Assert.Contains(_prefix, status.Where);
    }

    public override void Dispose()
    {
        // Leave the bucket as found: every object this test made is under its prefix.
        using var client = S3BackupStore.CreateClient(_template);
        var listed = client.ListObjectsV2Async(new Amazon.S3.Model.ListObjectsV2Request { BucketName = _template.S3Bucket, Prefix = _prefix }).GetAwaiter().GetResult();
        foreach (var o in listed.S3Objects ?? new List<Amazon.S3.Model.S3Object>())
            client.DeleteObjectAsync(_template.S3Bucket, o.Key).GetAwaiter().GetResult();
    }
}
