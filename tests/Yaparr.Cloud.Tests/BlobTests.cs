using System.Net;
using System.Security.Cryptography;
using Xunit;
using Yaparr.Cloud;

namespace Yaparr.Cloud.Tests;

/// <summary>
/// What every store of pictures does, whether it keeps them as files or in a bucket (see <see cref="FileBlobStoreTests"/> and <see cref="S3BlobStoreTests"/>).
/// </summary>
public abstract class BlobStoreContractTests : IDisposable
{
    protected static readonly DateTimeOffset T0 = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
    protected static readonly TimeSpan Keep = TimeSpan.FromDays(30);

    protected abstract IBlobStore NewStore();

    /// <summary>Asserts that nothing at all is held (no data, no bookkeeping).</summary>
    protected abstract void AssertEmpty();

    public virtual void Dispose() { }

    protected static (string Id, byte[] Data) Blob(int length = 100)
    {
        var data = RandomNumberGenerator.GetBytes(length);
        return (BlobRules.IdOf(data), data);
    }

    [SkippableFact]
    public void A_blob_is_stored_and_read_back_and_a_second_copy_is_not_stored_again()
    {
        var store = NewStore();
        var (id, data) = Blob();
        Assert.Equal(BlobPutOutcome.Stored, store.Put(id, "sam@example.com", data, 1000, T0, Keep));
        Assert.Equal(data, store.Get(id, T0, Keep));
        Assert.Equal(BlobPutOutcome.AlreadyThere, store.Put(id, "alex@example.com", data, 1000, T0, Keep));
        Assert.Null(store.Get(Blob().Id, T0, Keep));
    }

    [SkippableFact]
    public void An_account_cannot_hold_more_than_its_quota_and_expired_blobs_do_not_count()
    {
        var store = NewStore();
        var (a, da) = Blob(600);
        var (b, db) = Blob(600);
        Assert.Equal(BlobPutOutcome.Stored, store.Put(a, "sam@example.com", da, 1000, T0, Keep));
        Assert.Equal(BlobPutOutcome.OverQuota, store.Put(b, "sam@example.com", db, 1000, T0, Keep));
        Assert.Equal(BlobPutOutcome.Stored, store.Put(b, "alex@example.com", db, 1000, T0, Keep));   // another account has a quota of its own
        var later = T0 + Keep + TimeSpan.FromMinutes(1);
        var (c, dc) = Blob(600);
        Assert.Equal(BlobPutOutcome.Stored, store.Put(c, "sam@example.com", dc, 1000, later, Keep));   // the first had expired
        Assert.Null(store.Get(a, later, Keep));
    }

    [SkippableFact]
    public void A_blob_goes_after_the_keeping_time_and_a_sweep_removes_what_has_expired()
    {
        var store = NewStore();
        var (id, data) = Blob();
        store.Put(id, "sam@example.com", data, 1000, T0, Keep);
        Assert.NotNull(store.Get(id, T0 + Keep - TimeSpan.FromMinutes(1), Keep));
        var later = T0 + Keep + TimeSpan.FromMinutes(1);
        Assert.Null(store.Get(id, later, Keep));
        Assert.Equal(1, store.Expire(later, Keep));
        AssertEmpty();
    }

    [SkippableFact]
    public void Removing_an_account_removes_its_blobs_and_not_anyone_elses()
    {
        var store = NewStore();
        var (a, da) = Blob();
        var (b, db) = Blob();
        store.Put(a, "sam@example.com", da, 1000, T0, Keep);
        store.Put(b, "alex@example.com", db, 1000, T0, Keep);
        store.RemoveAccount("sam@example.com");
        Assert.Null(store.Get(a, T0, Keep));
        Assert.NotNull(store.Get(b, T0, Keep));
    }

    [SkippableFact]
    public void A_file_stored_again_after_it_expired_is_not_removed_with_the_old_copy()
    {
        var store = NewStore();
        var (id, data) = Blob();
        store.Put(id, "sam@example.com", data, 1000, T0, Keep);
        var later = T0 + Keep + TimeSpan.FromMinutes(1);
        Assert.Equal(BlobPutOutcome.Stored, store.Put(id, "alex@example.com", data, 1000, later, Keep));   // sam's copy had expired, so alex's is stored
        store.Expire(later + TimeSpan.FromMinutes(1), Keep);
        store.RemoveAccount("sam@example.com");
        Assert.Equal(data, store.Get(id, later + TimeSpan.FromMinutes(1), Keep));
    }
}

public sealed class FileBlobStoreTests : BlobStoreContractTests
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "yaparr-blobs-" + Guid.NewGuid().ToString("N"));

    protected override IBlobStore NewStore() => new BlobStore(new ServiceOptions { DataDirectory = _dir });

    protected override void AssertEmpty() => Assert.Empty(Directory.GetFiles(Path.Combine(_dir, "blobs")));

    public override void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }
}

/// <summary>
/// <see cref="S3BlobStore"/> against a real S3-compatible store: skipped unless <c>YAPARR_TEST_S3</c> names one (the same variable as <see cref="S3BackupStoreTests"/>).
/// Each test works under a folder of its own.
/// </summary>
public sealed class S3BlobStoreTests : BlobStoreContractTests
{
    private readonly string _prefix = "test-" + Guid.NewGuid().ToString("N") + "/";
    private readonly ServiceOptions _options;

    public S3BlobStoreTests()
    {
        Skip.If(string.IsNullOrEmpty(Environment.GetEnvironmentVariable(S3BackupStoreTests.Variable)), $"Set {S3BackupStoreTests.Variable} to endpoint;bucket;access key;secret key;region to run these.");
        var parts = Environment.GetEnvironmentVariable(S3BackupStoreTests.Variable)!.Split(';');
        _options = new ServiceOptions
        {
            S3Endpoint = parts[0], S3Bucket = parts[1], S3AccessKey = parts[2], S3SecretKey = parts[3], S3Region = parts.Length > 4 ? parts[4] : "auto", S3BlobPrefix = _prefix,
        };
        using var client = S3BackupStore.CreateClient(_options);
        try { client.PutBucketAsync(_options.S3Bucket).GetAwaiter().GetResult(); }
        catch (Amazon.S3.AmazonS3Exception) { }
    }

    protected override IBlobStore NewStore() => new S3BlobStore(S3BackupStore.CreateClient(_options), _options);

    private List<string> Keys()
    {
        using var client = S3BackupStore.CreateClient(_options);
        var listed = client.ListObjectsV2Async(new Amazon.S3.Model.ListObjectsV2Request { BucketName = _options.S3Bucket, Prefix = _prefix }).GetAwaiter().GetResult();
        return (listed.S3Objects ?? new List<Amazon.S3.Model.S3Object>()).Select(o => o.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
    }

    protected override void AssertEmpty() => Assert.Empty(Keys());

    public override void Dispose()
    {
        using var client = S3BackupStore.CreateClient(_options);
        foreach (var key in Keys()) client.DeleteObjectAsync(_options.S3Bucket, key).GetAwaiter().GetResult();
    }

    [SkippableFact]
    public void The_bucket_holds_the_bytes_and_an_empty_entry_for_the_account_that_names_no_address()
    {
        var store = NewStore();
        var (id, data) = Blob(250);
        store.Put(id, "sam@example.com", data, 1000, T0, Keep);
        var keys = Keys();
        Assert.Equal(2, keys.Count);
        Assert.Contains(_prefix + "data/" + id, keys);
        var index = Assert.Single(keys, k => k.StartsWith(_prefix + "index/", StringComparison.Ordinal));
        Assert.StartsWith(_prefix + "index/" + BackupStore.FolderOf("sam@example.com") + "/", index);
        Assert.EndsWith(".250." + id, index);
        Assert.DoesNotContain("sam", index);
    }

    [SkippableFact]
    public void A_prefix_that_overlaps_the_backups_or_the_dumps_is_refused()
    {
        var client = S3BackupStore.CreateClient(_options);
        Assert.Throws<InvalidOperationException>(() => new S3BlobStore(client, new ServiceOptions { S3BlobPrefix = "backups/pictures/" }));
        Assert.Throws<InvalidOperationException>(() => new S3BlobStore(client, new ServiceOptions { S3BlobPrefix = "db-dumps/" }));
        Assert.Throws<InvalidOperationException>(() => new S3BlobStore(client, new ServiceOptions { S3BlobPrefix = "" }));
    }
}

public sealed class BlobApiTests : ServiceTest
{
    private static async Task<HttpResponseMessage> Put(HttpClient client, string? token, string id, byte[] body) => await Send(client, token, HttpMethod.Put, "/v1/blob/" + id, body);

    private static (string Id, byte[] Data) Blob(int length = 500)
    {
        var data = RandomNumberGenerator.GetBytes(length);
        return (BlobRules.IdOf(data), data);
    }

    [Fact]
    public async Task A_device_uploads_a_locked_file_and_anyone_who_knows_the_id_fetches_it_without_signing_in()
    {
        var client = Start();
        var sam = Register("sam@example.com");
        var (id, data) = Blob();
        Assert.Equal(HttpStatusCode.OK, (await Put(client, sam, id, data)).StatusCode);

        var fetched = await Send(client, null, HttpMethod.Get, "/v1/blob/" + id);
        Assert.Equal(HttpStatusCode.OK, fetched.StatusCode);
        Assert.Equal(data, await fetched.Content.ReadAsByteArrayAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await Send(client, null, HttpMethod.Get, "/v1/blob/" + Blob().Id)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(client, null, HttpMethod.Get, "/v1/blob/not-an-id")).StatusCode);
    }

    [Fact]
    public async Task Only_a_signed_in_device_uploads_and_the_id_has_to_be_the_hash_of_the_bytes()
    {
        var client = Start();
        var sam = Register("sam@example.com");
        var (id, data) = Blob();
        Assert.Equal(HttpStatusCode.Unauthorized, (await Put(client, null, id, data)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Put(client, "ycl_wrong", id, data)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Put(client, sam, Blob().Id, data)).StatusCode);   // somebody else's id
        Assert.Equal(HttpStatusCode.BadRequest, (await Put(client, sam, "short", data)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Put(client, sam, id, Array.Empty<byte>())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(client, null, HttpMethod.Get, "/v1/blob/" + id)).StatusCode);   // nothing was stored
    }

    [Fact]
    public async Task A_file_over_the_limit_or_past_the_quota_is_refused()
    {
        var client = Start(new() { ["BlobMaxBytes"] = "1000", ["BlobQuotaBytes"] = "1200" });
        var sam = Register("sam@example.com");
        var (bigId, big) = Blob(1001);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await Put(client, sam, bigId, big)).StatusCode);
        var (aId, a) = Blob(800);
        var (bId, b) = Blob(800);
        Assert.Equal(HttpStatusCode.OK, (await Put(client, sam, aId, a)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Put(client, sam, bId, b)).StatusCode);
    }

    [Fact]
    public async Task Deleting_an_account_removes_the_pictures_it_uploaded()
    {
        var client = Start();
        var sam = Register("sam@example.com");
        var alex = Register("alex@example.com");
        var (a, da) = Blob();
        var (b, db) = Blob();
        await Put(client, sam, a, da);
        await Put(client, alex, b, db);
        var deleted = await client.PostAsync("/account/delete", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["username"] = "sam@example.com", ["current"] = Password, ["confirm"] = "delete my account",
        }));
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(client, null, HttpMethod.Get, "/v1/blob/" + a)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(client, null, HttpMethod.Get, "/v1/blob/" + b)).StatusCode);
    }
}
