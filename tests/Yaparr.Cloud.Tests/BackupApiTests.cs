using System.Buffers.Text;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Yaparr.Cloud;

namespace Yaparr.Cloud.Tests;

public sealed class BackupApiTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "yaparr-service-" + Guid.NewGuid().ToString("N"));
    private readonly List<WebApplicationFactory<Program>> _factories = new();
    private const string Password = "correct horse battery";

    private HttpClient Start(Dictionary<string, string>? settings = null)
    {
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Service:DataDirectory", _dir);
            foreach (var (key, value) in settings ?? new()) b.UseSetting("Service:" + key, value);
        });
        _factories.Add(factory);
        return factory.CreateClient();
    }

    public void Dispose()
    {
        foreach (var f in _factories) f.Dispose();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    /// <summary>A person with an account: the username and the key their password leads to (made with small settings, which are quicker).</summary>
    private sealed record Person(string Name, byte[] Seed);

    private Person Register(string name, string password = Password)
    {
        var seed = LoginKeys.SigningSeed(name, password, 8 * 1024, 1);
        Assert.True(new AccountDirectory(new ServiceOptions { DataDirectory = _dir }).Add(name, LoginKeys.PublicKeyOf(seed)));
        return new Person(name, seed);
    }

    // What the service accepts as a backup: it starts as a locked one does.
    private static byte[] Locked(int length = 1000)
    {
        var body = RandomNumberGenerator.GetBytes(length);
        BackupStore.Magic.CopyTo(body, 0);
        return body;
    }

    private static async Task<HttpResponseMessage> Send(HttpClient client, Person who, HttpMethod method, string path, byte[]? body = null, int? ifMatch = null,
        DateTimeOffset? at = null, string? signedPath = null)
    {
        var hash = body == null ? null : Convert.ToHexStringLower(SHA256.HashData(body));
        var request = new HttpRequestMessage(method, path);
        foreach (var (name, value) in RequestSigner.Sign(who.Seed, who.Name, method.Method, signedPath ?? path, at ?? DateTimeOffset.UtcNow, hash))
            request.Headers.Add(name, value);
        if (ifMatch != null) request.Headers.Add("If-Match", "\"" + ifMatch.Value + "\"");
        if (body != null) request.Content = new ByteArrayContent(body);
        return await client.SendAsync(request);
    }

    private static async Task<int> VersionOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("version").GetInt32();

    // The files of backups, not the list of accounts that sits beside them.
    private IEnumerable<string> BackupFiles() =>
        Directory.Exists(_dir) ? Directory.EnumerateDirectories(_dir).SelectMany(d => Directory.EnumerateFiles(d)) : Enumerable.Empty<string>();

    [Fact]
    public async Task A_backup_saved_comes_back_the_same()
    {
        var client = Start();
        var sam = Register("sam");
        var body = Locked(200_000);

        var put = await Send(client, sam, HttpMethod.Put, "/v1/backup", body, ifMatch: 0);
        Assert.Equal(HttpStatusCode.Created, put.StatusCode);
        Assert.Equal(1, await VersionOf(put));

        var get = await Send(client, sam, HttpMethod.Get, "/v1/backup");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal("1", get.Headers.GetValues("X-Yaparr-Version").Single());
        Assert.Equal(body, await get.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Nothing_is_answered_without_a_good_signature()
    {
        var client = Start();
        var sam = Register("sam");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/v1/backup")).StatusCode);

        // Signed for another path, or a time long ago or far ahead: all refused.
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(client, sam, HttpMethod.Get, "/v1/backup", signedPath: "/v1/backup/versions")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(client, sam, HttpMethod.Get, "/v1/backup", at: DateTimeOffset.UtcNow.AddHours(-1))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(client, sam, HttpMethod.Get, "/v1/backup", at: DateTimeOffset.UtcNow.AddHours(1))).StatusCode);

        // A signature for DELETE does not read.
        var request = new HttpRequestMessage(HttpMethod.Get, "/v1/backup");
        foreach (var (name, value) in RequestSigner.Sign(sam.Seed, "sam", "DELETE", "/v1/backup", DateTimeOffset.UtcNow)) request.Headers.Add(name, value);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task A_wrong_password_and_an_unknown_name_are_refused_in_the_same_words()
    {
        var client = Start();
        Register("sam");
        var wrongPassword = new Person("sam", LoginKeys.SigningSeed("sam", "not the right one!", 8 * 1024, 1));
        var stranger = new Person("alex", LoginKeys.SigningSeed("alex", Password, 8 * 1024, 1));

        var a = await Send(client, wrongPassword, HttpMethod.Get, "/v1/backup");
        var b = await Send(client, stranger, HttpMethod.Get, "/v1/backup");
        Assert.Equal(HttpStatusCode.Unauthorized, a.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, b.StatusCode);
        Assert.Equal(await a.Content.ReadAsStringAsync(), await b.Content.ReadAsStringAsync());   // nothing says who has an account
    }

    [Fact]
    public async Task Nobody_can_make_an_account_by_asking_and_a_removed_one_is_refused()
    {
        var client = Start();
        var stranger = new Person("alex", LoginKeys.SigningSeed("alex", Password, 8 * 1024, 1));
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(client, stranger, HttpMethod.Put, "/v1/backup", Locked(), ifMatch: 0)).StatusCode);
        Assert.Empty(BackupFiles());

        var sam = Register("sam");
        Assert.Equal(HttpStatusCode.Created, (await Send(client, sam, HttpMethod.Put, "/v1/backup", Locked(), ifMatch: 0)).StatusCode);
        new AccountDirectory(new ServiceOptions { DataDirectory = _dir }).Remove("sam");           // by the admin, while the service runs
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(client, sam, HttpMethod.Get, "/v1/backup")).StatusCode);
    }

    [Fact]
    public async Task One_account_cannot_see_or_remove_another_accounts_backup()
    {
        var client = Start();
        var sam = Register("sam");
        var alex = Register("alex");
        await Send(client, sam, HttpMethod.Put, "/v1/backup", Locked(), ifMatch: 0);

        Assert.Equal(HttpStatusCode.NotFound, (await Send(client, alex, HttpMethod.Get, "/v1/backup")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(client, alex, HttpMethod.Delete, "/v1/backup")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(client, sam, HttpMethod.Get, "/v1/backup")).StatusCode);

        // Signing as sam with alex's key does not work either.
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(client, new Person("sam", alex.Seed), HttpMethod.Get, "/v1/backup")).StatusCode);
    }

    [Fact]
    public async Task A_plain_backup_is_never_stored()
    {
        var client = Start();
        var sam = Register("sam");
        var zip = new byte[] { 0x50, 0x4B, 3, 4, 1, 2, 3, 4, 5, 6, 7, 8, 9 };
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(client, sam, HttpMethod.Put, "/v1/backup", zip, ifMatch: 0)).StatusCode);
        Assert.Empty(BackupFiles());
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(client, sam, HttpMethod.Put, "/v1/backup", new byte[] { 1, 2 }, ifMatch: 0)).StatusCode);
    }

    [Fact]
    public async Task What_arrives_must_be_what_was_signed()
    {
        var client = Start();
        var sam = Register("sam");
        var signed = Locked();
        var other = Locked();

        var request = new HttpRequestMessage(HttpMethod.Put, "/v1/backup");
        foreach (var (name, value) in RequestSigner.Sign(sam.Seed, "sam", "PUT", "/v1/backup", DateTimeOffset.UtcNow, Convert.ToHexStringLower(SHA256.HashData(signed))))
            request.Headers.Add(name, value);
        request.Headers.Add("If-Match", "\"0\"");
        request.Content = new ByteArrayContent(other);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(request)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(client, sam, HttpMethod.Get, "/v1/backup")).StatusCode);   // nothing was kept
        Assert.Empty(BackupFiles());                                                                                   // not even the half-written file
    }

    [Fact]
    public async Task A_save_must_say_what_it_is_based_on_and_two_devices_cannot_overwrite_each_other()
    {
        var client = Start();
        var sam = Register("sam");

        Assert.Equal((HttpStatusCode)428, (await Send(client, sam, HttpMethod.Put, "/v1/backup", Locked())).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Send(client, sam, HttpMethod.Put, "/v1/backup", Locked(), ifMatch: 0)).StatusCode);
        // The second device had not seen version 1.
        var stale = await Send(client, sam, HttpMethod.Put, "/v1/backup", Locked(), ifMatch: 0);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(1, await VersionOf(stale));

        Assert.Equal(HttpStatusCode.Created, (await Send(client, sam, HttpMethod.Put, "/v1/backup", Locked(), ifMatch: 1)).StatusCode);
        Assert.Equal(2, (await (await Send(client, sam, HttpMethod.Get, "/v1/backup/versions")).Content.ReadFromJsonAsync<JsonElement[]>())!.Length);
    }

    [Fact]
    public async Task A_request_sent_again_is_not_saved_twice()
    {
        var client = Start();
        var sam = Register("sam");
        var body = Locked();
        var at = DateTimeOffset.UtcNow;
        Assert.Equal(HttpStatusCode.Created, (await Send(client, sam, HttpMethod.Put, "/v1/backup", body, ifMatch: 0, at: at)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Send(client, sam, HttpMethod.Put, "/v1/backup", body, ifMatch: 0, at: at)).StatusCode);   // the same bytes, replayed
        Assert.Single((await (await Send(client, sam, HttpMethod.Get, "/v1/backup/versions")).Content.ReadFromJsonAsync<JsonElement[]>())!);
    }

    [Fact]
    public async Task Only_the_newest_versions_are_kept_and_an_old_one_can_be_fetched()
    {
        var client = Start(new() { ["KeepVersions"] = "3", ["WritesPerHour"] = "100" });
        var sam = Register("sam");
        var bodies = Enumerable.Range(0, 5).Select(_ => Locked()).ToArray();
        for (int i = 0; i < bodies.Length; i++)
            Assert.Equal(HttpStatusCode.Created, (await Send(client, sam, HttpMethod.Put, "/v1/backup", bodies[i], ifMatch: i)).StatusCode);

        var versions = (await (await Send(client, sam, HttpMethod.Get, "/v1/backup/versions")).Content.ReadFromJsonAsync<JsonElement[]>())!;
        Assert.Equal(new[] { 5, 4, 3 }, versions.Select(v => v.GetProperty("version").GetInt32()).ToArray());

        var old = await Send(client, sam, HttpMethod.Get, "/v1/backup?version=3");
        Assert.Equal(bodies[2], await old.Content.ReadAsByteArrayAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await Send(client, sam, HttpMethod.Get, "/v1/backup?version=1")).StatusCode);
    }

    [Fact]
    public async Task A_backup_over_the_size_limit_is_refused_and_leaves_nothing()
    {
        var client = Start(new() { ["MaxBlobBytes"] = "5000" });
        var sam = Register("sam");
        Assert.Equal(HttpStatusCode.Created, (await Send(client, sam, HttpMethod.Put, "/v1/backup", Locked(4000), ifMatch: 0)).StatusCode);
        Assert.Equal((HttpStatusCode)413, (await Send(client, sam, HttpMethod.Put, "/v1/backup", Locked(6000), ifMatch: 1)).StatusCode);
        Assert.DoesNotContain(BackupFiles(), f => f.EndsWith(".tmp"));
    }

    [Fact]
    public async Task Saving_is_limited_per_hour_for_an_account_and_not_for_another()
    {
        var client = Start(new() { ["WritesPerHour"] = "2" });
        var sam = Register("sam");
        var alex = Register("alex");
        Assert.Equal(HttpStatusCode.Created, (await Send(client, sam, HttpMethod.Put, "/v1/backup", Locked(), ifMatch: 0)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Send(client, sam, HttpMethod.Put, "/v1/backup", Locked(), ifMatch: 1)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Send(client, sam, HttpMethod.Put, "/v1/backup", Locked(), ifMatch: 2)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Send(client, alex, HttpMethod.Put, "/v1/backup", Locked(), ifMatch: 0)).StatusCode);
    }

    [Fact]
    public async Task Requests_from_one_address_are_limited()
    {
        var client = Start(new() { ["RequestsPerMinute"] = "5" });
        var statuses = new List<HttpStatusCode>();
        for (int i = 0; i < 8; i++) statuses.Add((await client.GetAsync("/healthz")).StatusCode);
        Assert.Equal(5, statuses.Count(s => s == HttpStatusCode.OK));
        Assert.Equal(3, statuses.Count(s => s == HttpStatusCode.TooManyRequests));
    }

    [Fact]
    public async Task Deleting_removes_every_version()
    {
        var client = Start();
        var sam = Register("sam");
        await Send(client, sam, HttpMethod.Put, "/v1/backup", Locked(), ifMatch: 0);
        await Send(client, sam, HttpMethod.Put, "/v1/backup", Locked(), ifMatch: 1);

        Assert.Equal(HttpStatusCode.NoContent, (await Send(client, sam, HttpMethod.Delete, "/v1/backup")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(client, sam, HttpMethod.Get, "/v1/backup")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(client, sam, HttpMethod.Delete, "/v1/backup")).StatusCode);
        Assert.Empty(BackupFiles());
    }

    private static async Task<HttpResponseMessage> ChangeKey(HttpClient client, Person who, byte[] newPublicKey)
    {
        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { publicKey = Base64Url.EncodeToString(newPublicKey) }));
        var request = new HttpRequestMessage(HttpMethod.Put, "/v1/account/key") { Content = new ByteArrayContent(body) };
        foreach (var (name, value) in RequestSigner.Sign(who.Seed, who.Name, "PUT", "/v1/account/key", DateTimeOffset.UtcNow, Convert.ToHexStringLower(SHA256.HashData(body))))
            request.Headers.Add(name, value);
        return await client.SendAsync(request);
    }

    [Fact]
    public async Task A_changed_password_replaces_the_key_and_keeps_the_backup()
    {
        var client = Start();
        var sam = Register("sam");
        var body = Locked();
        await Send(client, sam, HttpMethod.Put, "/v1/backup", body, ifMatch: 0);

        var newSeed = LoginKeys.SigningSeed("sam", "a brand new password", 8 * 1024, 1);
        Assert.Equal(HttpStatusCode.NoContent, (await ChangeKey(client, sam, LoginKeys.PublicKeyOf(newSeed))).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(client, sam, HttpMethod.Get, "/v1/backup")).StatusCode);   // the old password is done
        var now = await Send(client, new Person("sam", newSeed), HttpMethod.Get, "/v1/backup");
        Assert.Equal(body, await now.Content.ReadAsByteArrayAsync());                                                     // the new one reaches the same backup
    }

    [Fact]
    public async Task A_key_change_needs_a_real_key_and_cannot_be_made_by_someone_else()
    {
        var client = Start();
        var sam = Register("sam");
        var alex = Register("alex");
        Assert.Equal(HttpStatusCode.BadRequest, (await ChangeKey(client, sam, new byte[5])).StatusCode);

        // Alex signing a change for sam's account is refused: it is checked against sam's key.
        var attacker = new Person("sam", alex.Seed);
        Assert.Equal(HttpStatusCode.Unauthorized, (await ChangeKey(client, attacker, LoginKeys.PublicKeyOf(alex.Seed))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(client, sam, HttpMethod.Get, "/v1/backup")).StatusCode);   // sam's key still works
    }

    [Fact]
    public void The_store_refuses_an_account_that_is_not_a_hash_so_nothing_can_name_a_path()
    {
        var store = new BackupStore(new ServiceOptions { DataDirectory = _dir });
        Assert.Throws<ArgumentException>(() => store.Versions("../../etc"));
        Assert.Throws<ArgumentException>(() => store.Versions(new string('A', 64)));   // upper case is not what FolderOf makes
        Assert.Empty(store.Versions(new string('a', 64)));
    }

    [Fact]
    public void The_write_limiter_forgets_after_an_hour()
    {
        var now = DateTimeOffset.UtcNow;
        var limiter = new WriteLimiter(() => now);
        Assert.True(limiter.TryTake("a", 2));
        Assert.True(limiter.TryTake("a", 2));
        Assert.False(limiter.TryTake("a", 2));
        now = now.AddMinutes(61);
        Assert.True(limiter.TryTake("a", 2));
    }
}
