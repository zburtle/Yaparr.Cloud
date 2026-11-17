using System.Net;
using System.Security.Cryptography;
using Xunit;
using Yaparr.Cloud;

namespace Yaparr.Cloud.Tests;

public sealed class BlobStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "yaparr-blobs-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset T0 = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Keep = TimeSpan.FromDays(30);

    private IBlobStore NewStore() => new BlobStore(new ServiceOptions { DataDirectory = _dir });

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static (string Id, byte[] Data) Blob(int length = 100)
    {
        var data = RandomNumberGenerator.GetBytes(length);
        return (BlobRules.IdOf(data), data);
    }

    [Fact]
    public void A_blob_is_stored_and_read_back_and_a_second_copy_is_not_stored_again()
    {
        var store = NewStore();
        var (id, data) = Blob();
        Assert.Equal(BlobPutOutcome.Stored, store.Put(id, "sam@example.com", data, 1000, T0, Keep));
        Assert.Equal(data, store.Get(id, T0, Keep));
        Assert.Equal(BlobPutOutcome.AlreadyThere, store.Put(id, "alex@example.com", data, 1000, T0, Keep));
        Assert.Null(store.Get(Blob().Id, T0, Keep));
    }

    [Fact]
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

    [Fact]
    public void A_blob_goes_after_the_keeping_time_and_a_sweep_removes_what_has_expired()
    {
        var store = NewStore();
        var (id, data) = Blob();
        store.Put(id, "sam@example.com", data, 1000, T0, Keep);
        Assert.NotNull(store.Get(id, T0 + Keep - TimeSpan.FromMinutes(1), Keep));
        var later = T0 + Keep + TimeSpan.FromMinutes(1);
        Assert.Null(store.Get(id, later, Keep));
        Assert.Equal(1, store.Expire(later, Keep));
        Assert.Empty(Directory.GetFiles(Path.Combine(_dir, "blobs")));
    }

    [Fact]
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
