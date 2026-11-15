using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;
using Yaparr.Cloud;

namespace Yaparr.Cloud.Tests;

/// <summary>When the last dump of the database was made, as the admin portal reads it.</summary>
public sealed class DumpStatusTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "yaparr-dump-" + Guid.NewGuid().ToString("N"));
    private ServiceOptions Options => new() { DataDirectory = _dir };
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);

    public DumpStatusTests() => Directory.CreateDirectory(_dir);

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private void Write(string json) => File.WriteAllText(Path.Combine(_dir, DumpStatus.FileName), json);

    [Fact]
    public void No_record_of_a_dump_is_a_problem_that_says_what_to_check()
    {
        var status = DumpStatus.Read(Options, Now);
        Assert.False(status.Ok);
        Assert.Contains("pg-backup.sh", status.Detail);
    }

    [Fact]
    public void A_recent_dump_is_fine_and_says_when_and_how_big()
    {
        Write("""{"at":"2026-11-15T03:17:02Z","bytes":1721,"file":"yaparr-2026-11-15.sql.gz"}""");
        var status = DumpStatus.Read(Options, Now);
        Assert.True(status.Ok);
        Assert.Contains("2026-11-15 03:17 UTC", status.Detail);
        Assert.Contains("1 KiB", status.Detail);
    }

    [Fact]
    public void A_dump_more_than_two_days_old_is_a_problem()
    {
        Write("""{"at":"2026-11-12T03:17:02Z","bytes":5000000,"file":"x"}""");
        var status = DumpStatus.Read(Options, Now);
        Assert.False(status.Ok);
        Assert.Contains("3 days ago", status.Detail);
        Assert.Contains("4.8 MiB", status.Detail);
        Write("""{"at":"2026-11-13T12:30:00Z","bytes":10,"file":"x"}""");
        Assert.True(DumpStatus.Read(Options, Now).Ok);        // a day and a half
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("")]
    public void A_record_that_cannot_be_read_is_a_problem_not_a_crash(string text)
    {
        Write(text);
        Assert.False(DumpStatus.Read(Options, Now).Ok);
    }
}

/// <summary>The admin portal's Storage card and the single pass over what is stored, with the stores replaced by ones that count or fail.</summary>
public sealed class StoragePortalTests : ServiceTest
{
    private const string Boss = "boss@example.com";

    // A store of backups that counts what it is asked, and can be down.
    private sealed class FakeBackups : IBackupStore
    {
        public int Summaries, Versions, Statuses;
        public bool Down;

        public Task<IReadOnlyList<BackupVersion>> VersionsAsync(string account, CancellationToken cancel = default) { Versions++; return Task.FromResult<IReadOnlyList<BackupVersion>>(Array.Empty<BackupVersion>()); }
        public Task<(Stream Content, BackupVersion Info)?> OpenAsync(string account, int? version, CancellationToken cancel = default) => Task.FromResult<(Stream, BackupVersion)?>(null);
        public Task<SaveResult> SaveAsync(string account, int basedOn, Stream body, CancellationToken cancel) => Task.FromResult(new SaveResult(SaveOutcome.TooLarge));
        public Task<bool> DeleteAsync(string account, CancellationToken cancel) => Task.FromResult(false);

        public Task<IReadOnlyDictionary<string, AccountBackups>> SummariesAsync(CancellationToken cancel = default)
        {
            Summaries++;
            if (Down) throw new IOException("the bucket is down");
            return Task.FromResult<IReadOnlyDictionary<string, AccountBackups>>(new Dictionary<string, AccountBackups>
            {
                [BackupStore.FolderOf("friend@example.com")] = new(3, 7 * 1024 * 1024, new DateTimeOffset(2026, 11, 14, 8, 30, 0, TimeSpan.Zero)),
            });
        }

        public Task<StoreStatus> StatusAsync(CancellationToken cancel = default)
        {
            Statuses++;
            return Task.FromResult(Down ? new StoreStatus("Cloudflare R2", "bucket yaparr-backups", false, "403 AccessDenied") : new StoreStatus("Cloudflare R2", "bucket yaparr-backups", true, "answers in 41 ms"));
        }
    }

    private async Task<(string Cookie, string Html)> OpenAsync(HttpClient client, string secret)
    {
        var code = Totp.Code(secret, Totp.StepOf(DateTimeOffset.UtcNow));
        var login = await client.PostAsync("/admin/login", new FormUrlEncodedContent(new Dictionary<string, string> { ["username"] = Boss, ["password"] = Password, ["code"] = code }));
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        var cookie = login.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
        var request = new HttpRequestMessage(HttpMethod.Get, "/admin");
        request.Headers.Add("Cookie", cookie);
        var page = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        return (cookie, await page.Content.ReadAsStringAsync());
    }

    private string MakeAdmin()
    {
        var accounts = new AccountDirectory(Options);
        accounts.Add(Boss, Password);
        accounts.Add("friend@example.com", Password);
        return accounts.MakeAdmin(Boss)!;
    }

    [Fact]
    public async Task The_overview_asks_for_what_is_stored_once_and_not_once_for_each_account()
    {
        var secret = MakeAdmin();
        var fake = new FakeBackups();
        var client = Start(services: s => s.AddSingleton<IBackupStore>(fake));
        var (_, html) = await OpenAsync(client, secret);

        Assert.Equal(1, fake.Summaries);
        Assert.Equal(0, fake.Versions);
        Assert.Equal(1, fake.Statuses);
        Assert.Contains("7.0 MiB", html);                 // the friend's three versions, from the one pass
        Assert.Contains("3 versions", html);
        Assert.Contains("2026-11-14 08:30", html);
    }

    [Fact]
    public async Task The_storage_card_says_where_everything_is_kept_and_that_it_answers()
    {
        var secret = MakeAdmin();
        var client = Start(services: s => s.AddSingleton<IBackupStore>(new FakeBackups()));
        var (_, html) = await OpenAsync(client, secret);

        Assert.Contains("<h2>Storage</h2>", html);
        Assert.Contains("Accounts and signup codes", html);
        Assert.Contains("accounts.json and codes.json", html);
        Assert.Contains("2 accounts", html);
        Assert.Contains("Cloudflare R2", html);
        Assert.Contains("bucket yaparr-backups", html);
        Assert.Contains("answers in 41 ms", html);
        Assert.Contains("pill ok", html);
        Assert.DoesNotContain("pill bad", html);
        Assert.DoesNotContain("Database dump", html);      // there is no database here, so there is nothing to dump
        Assert.Contains("of the disk is used", html);        // no bucket is set, so the backups are files on the disk: it is what to watch
    }

    [Fact]
    public async Task A_store_that_is_down_is_a_problem_in_the_card_and_the_page_still_comes()
    {
        var secret = MakeAdmin();
        var client = Start(services: s => s.AddSingleton<IBackupStore>(new FakeBackups { Down = true }));
        var (_, html) = await OpenAsync(client, secret);

        Assert.Contains("pill bad", html);
        Assert.Contains("403 AccessDenied", html);
        Assert.Contains("Not available", html);               // the backup column of each account
        Assert.Contains("not available", html);               // the Backups tile
        Assert.Contains("friend@example.com", html);
    }

    [Fact]
    public async Task Where_backups_are_in_a_bucket_the_disk_tile_is_left_out()
    {
        var secret = MakeAdmin();
        var client = Start(new() { ["S3Bucket"] = "some-bucket", ["S3Endpoint"] = "http://127.0.0.1:1" }, s => s.AddSingleton<IBackupStore>(new FakeBackups()));
        var (_, html) = await OpenAsync(client, secret);
        Assert.DoesNotContain("of the disk is used", html);
        Assert.DoesNotContain("GiB free", html);
    }

    [Fact]
    public async Task What_the_card_shows_never_includes_a_key_an_address_or_a_password()
    {
        var secret = MakeAdmin();
        var client = Start(new() { ["S3Bucket"] = "yaparr-backups", ["S3Endpoint"] = "http://127.0.0.1:1/abc123accountid.r2.cloudflarestorage.com", ["S3AccessKey"] = "AKIAEXAMPLEKEY", ["S3SecretKey"] = "supersecretvalue" });
        var (_, html) = await OpenAsync(client, secret);
        Assert.DoesNotContain("abc123accountid", html);
        Assert.DoesNotContain("AKIAEXAMPLEKEY", html);
        Assert.DoesNotContain("supersecretvalue", html);
        Assert.Contains("Cloudflare R2", html);           // named from its address, which is not shown
        Assert.Contains("pill bad", html);                // the address cannot be reached from a test: the problem is shown, not the address
    }
}
