using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Yaparr.Cloud;

namespace Yaparr.Cloud.Tests;

/// <summary>Starts the service in memory on a data folder of its own.</summary>
public abstract class ServiceTest : IDisposable
{
    protected readonly string Dir = Path.Combine(Path.GetTempPath(), "yaparr-cloud-" + Guid.NewGuid().ToString("N"));
    private readonly List<WebApplicationFactory<Program>> _factories = new();
    protected const string Password = "correct horse battery";

    protected ServiceOptions Options => new() { DataDirectory = Dir };

    /// <summary>The service started by the last <see cref="Start"/>, for a test that needs more than an HTTP client (a WebSocket).</summary>
    protected WebApplicationFactory<Program>? LastFactory { get; private set; }

    protected HttpClient Start(Dictionary<string, string>? settings = null, Action<Microsoft.Extensions.DependencyInjection.IServiceCollection>? services = null)
    {
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Service:DataDirectory", Dir);
            foreach (var (key, value) in settings ?? new()) b.UseSetting("Service:" + key, value);
            if (services != null) b.ConfigureServices(services);   // what a test puts in place of a part of the service
        });
        _factories.Add(factory);
        LastFactory = factory;
        // A browser is sent from page to page; the tests look at each step, so they do not follow. Cookies are sent by hand, so a test says when one is.
        return factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
    }

    public void Dispose()
    {
        foreach (var f in _factories) f.Dispose();
        try { Directory.Delete(Dir, true); } catch (IOException) { }
    }

    /// <summary>An account, and a device that is signed in as it (its token).</summary>
    protected string Register(string name, string password = Password)
    {
        var accounts = new AccountDirectory(Options);
        Assert.True(accounts.Add(name, password));
        return accounts.IssueToken(name, "A test")!;
    }

    // What the service accepts as a backup: it starts as a locked one does.
    protected static byte[] Locked(int length = 1000)
    {
        var body = RandomNumberGenerator.GetBytes(length);
        BackupStore.Magic.CopyTo(body, 0);
        return body;
    }

    protected static async Task<HttpResponseMessage> Send(HttpClient client, string? token, HttpMethod method, string path, byte[]? body = null, int? ifMatch = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (token != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (ifMatch != null) request.Headers.Add("If-Match", "\"" + ifMatch.Value + "\"");
        if (body != null) request.Content = new ByteArrayContent(body);
        return await client.SendAsync(request);
    }

    protected static async Task<JsonElement[]> Versions(HttpClient client, string token) =>
        (await (await Send(client, token, HttpMethod.Get, "/v1/backup/versions")).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("versions").EnumerateArray().ToArray();

    // The files of backups, not the list of accounts that sits beside them.
    protected IEnumerable<string> BackupFiles() =>
        Directory.Exists(Dir) ? Directory.EnumerateDirectories(Dir).SelectMany(d => Directory.EnumerateFiles(d)) : Enumerable.Empty<string>();
}

public sealed class BackupApiTests : ServiceTest
{
    private static async Task<int> VersionOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("version").GetInt32();

    [Fact]
    public async Task A_backup_saved_comes_back_the_same()
    {
        var client = Start();
        var sam = Register("sam@example.com");
        var body = Locked(200_000);

        var put = await Send(client, sam, HttpMethod.Put, "/v1/backup", body, ifMatch: 0);
        Assert.Equal(HttpStatusCode.Created, put.StatusCode);
        Assert.Equal(1, await VersionOf(put));

        var get = await Send(client, sam, HttpMethod.Get, "/v1/backup");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal("1", get.Headers.GetValues("X-Yaparr-Version").Single());
        Assert.Equal(body, await get.Content.ReadAsByteArrayAsync());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ycl_not-a-token-anyone-was-given")]
    [InlineData("something else entirely")]
    public async Task Nothing_is_answered_without_a_token(string? token)
    {
        var client = Start();
        Register("sam@example.com");
        foreach (var (method, path) in new[] { (HttpMethod.Get, "/v1/backup"), (HttpMethod.Get, "/v1/backup/versions"), (HttpMethod.Delete, "/v1/backup"), (HttpMethod.Delete, "/v1/token") })
            Assert.Equal(HttpStatusCode.Unauthorized, (await Send(client, token, method, path)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(client, token, HttpMethod.Put, "/v1/backup", Locked(), ifMatch: 0)).StatusCode);
        Assert.Empty(BackupFiles());
    }

    [Fact]
    public async Task One_account_cannot_see_or_remove_another_accounts_backup()
    {
        var client = Start();
        var sam = Register("sam@example.com");
        var alex = Register("alex@example.com");
        await Send(client, sam, HttpMethod.Put, "/v1/backup", Locked(), ifMatch: 0);

        Assert.Equal(HttpStatusCode.NotFound, (await Send(client, alex, HttpMethod.Get, "/v1/backup")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(client, alex, HttpMethod.Delete, "/v1/backup")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(client, sam, HttpMethod.Get, "/v1/backup")).StatusCode);
    }

    [Fact]
    public async Task A_removed_account_and_a_signed_out_device_are_refused()
    {
        var client = Start();
        var sam = Register("sam@example.com");
        var second = new AccountDirectory(Options).IssueToken("sam@example.com", "Another device")!;
        Assert.Equal(HttpStatusCode.Created, (await Send(client, sam, HttpMethod.Put, "/v1/backup", Locked(), ifMatch: 0)).StatusCode);

        // One device signs out: it is refused, the other carries on.
        Assert.Equal(HttpStatusCode.NoContent, (await Send(client, second, HttpMethod.Delete, "/v1/token")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(client, second, HttpMethod.Get, "/v1/backup")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(client, sam, HttpMethod.Get, "/v1/backup")).StatusCode);

        new AccountDirectory(Options).Remove("sam@example.com");           // by the admin, while the service runs
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(client, sam, HttpMethod.Get, "/v1/backup")).StatusCode);
    }

    [Fact]
    public async Task A_plain_backup_is_never_stored()
    {
        var client = Start();
        var sam = Register("sam@example.com");
        var zip = new byte[] { 0x50, 0x4B, 3, 4, 1, 2, 3, 4, 5, 6, 7, 8, 9 };
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(client, sam, HttpMethod.Put, "/v1/backup", zip, ifMatch: 0)).StatusCode);
        Assert.Empty(BackupFiles());
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(client, sam, HttpMethod.Put, "/v1/backup", new byte[] { 1, 2 }, ifMatch: 0)).StatusCode);
        Assert.Empty(BackupFiles());
    }

    [Fact]
    public async Task A_save_must_say_what_it_is_based_on_and_two_devices_cannot_overwrite_each_other()
    {
        var client = Start();
        var sam = Register("sam@example.com");

        Assert.Equal((HttpStatusCode)428, (await Send(client, sam, HttpMethod.Put, "/v1/backup", Locked())).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Send(client, sam, HttpMethod.Put, "/v1/backup", Locked(), ifMatch: 0)).StatusCode);
        // The second device had not seen version 1. A request that is sent twice ends the same way.
        var stale = await Send(client, sam, HttpMethod.Put, "/v1/backup", Locked(), ifMatch: 0);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(1, await VersionOf(stale));

        Assert.Equal(HttpStatusCode.Created, (await Send(client, sam, HttpMethod.Put, "/v1/backup", Locked(), ifMatch: 1)).StatusCode);
        Assert.Equal(2, (await Versions(client, sam)).Length);
    }

    [Fact]
    public async Task The_list_of_versions_says_whose_it_is()
    {
        var client = Start();
        var sam = Register("sam@example.com");
        var answer = await (await Send(client, sam, HttpMethod.Get, "/v1/backup/versions")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("sam@example.com", answer.GetProperty("username").GetString());
        Assert.Equal(0, answer.GetProperty("versions").GetArrayLength());
    }

    [Fact]
    public async Task Only_the_newest_versions_are_kept_and_an_old_one_can_be_fetched()
    {
        var client = Start(new() { ["KeepVersions"] = "3", ["WritesPerHour"] = "100" });
        var sam = Register("sam@example.com");
        var bodies = Enumerable.Range(0, 5).Select(_ => Locked()).ToArray();
        for (int i = 0; i < bodies.Length; i++)
            Assert.Equal(HttpStatusCode.Created, (await Send(client, sam, HttpMethod.Put, "/v1/backup", bodies[i], ifMatch: i)).StatusCode);

        Assert.Equal(new[] { 5, 4, 3 }, (await Versions(client, sam)).Select(v => v.GetProperty("version").GetInt32()).ToArray());
        var old = await Send(client, sam, HttpMethod.Get, "/v1/backup?version=3");
        Assert.Equal(bodies[2], await old.Content.ReadAsByteArrayAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await Send(client, sam, HttpMethod.Get, "/v1/backup?version=1")).StatusCode);
    }

    [Fact]
    public async Task A_backup_over_the_size_limit_is_refused_and_leaves_nothing()
    {
        var client = Start(new() { ["MaxBlobBytes"] = "5000" });
        var sam = Register("sam@example.com");
        Assert.Equal(HttpStatusCode.Created, (await Send(client, sam, HttpMethod.Put, "/v1/backup", Locked(4000), ifMatch: 0)).StatusCode);
        Assert.Equal((HttpStatusCode)413, (await Send(client, sam, HttpMethod.Put, "/v1/backup", Locked(6000), ifMatch: 1)).StatusCode);
        Assert.DoesNotContain(BackupFiles(), f => f.EndsWith(".tmp"));
    }

    [Fact]
    public async Task Saving_is_limited_per_hour_for_an_account_and_not_for_another()
    {
        var client = Start(new() { ["WritesPerHour"] = "2" });
        var sam = Register("sam@example.com");
        var alex = Register("alex@example.com");
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
        var sam = Register("sam@example.com");
        await Send(client, sam, HttpMethod.Put, "/v1/backup", Locked(), ifMatch: 0);
        await Send(client, sam, HttpMethod.Put, "/v1/backup", Locked(), ifMatch: 1);

        Assert.Equal(HttpStatusCode.NoContent, (await Send(client, sam, HttpMethod.Delete, "/v1/backup")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(client, sam, HttpMethod.Get, "/v1/backup")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(client, sam, HttpMethod.Delete, "/v1/backup")).StatusCode);
        Assert.Empty(BackupFiles());
    }

    [Fact]
    public void The_store_refuses_an_account_that_is_not_a_hash_so_nothing_can_name_a_path()
    {
        var store = new BackupStore(Options);
        Assert.Throws<ArgumentException>(() => store.Versions("../../etc"));
        Assert.Throws<ArgumentException>(() => store.Versions(new string('A', 64)));   // upper case is not what FolderOf makes
        Assert.Empty(store.Versions(new string('a', 64)));
        Assert.Matches("^[0-9a-f]{64}$", BackupStore.FolderOf("sam@example.com"));
        Assert.NotEqual(BackupStore.FolderOf("sam@example.com"), BackupStore.FolderOf("alex@example.com"));
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
