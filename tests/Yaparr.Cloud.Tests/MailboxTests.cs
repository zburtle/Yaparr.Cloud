using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Xunit;
using Yaparr.Cloud;

namespace Yaparr.Cloud.Tests;

/// <summary>What every store of mailbox items does, whichever way it keeps them (see <see cref="FileMailboxStoreTests"/> and <see cref="PostgresMailboxStoreTests"/>).</summary>
public abstract class MailboxStoreContractTests : IDisposable
{
    protected abstract IMailboxStore NewMailbox();

    public virtual void Dispose() { }

    private static readonly DateTimeOffset T0 = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Keep = TimeSpan.FromDays(30);
    private const string Box = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string Other = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";

    private static MailboxItem Item(string id, string body = "AAAA") => new(id, body, T0);

    [SkippableFact]
    public void Items_are_stored_with_the_next_cursor_and_read_in_the_order_they_arrived()
    {
        var box = NewMailbox();
        var post = box.Post(Box, "sam@example.com", new[] { Item("a"), Item("b") }, 100, T0, Keep);
        Assert.Equal((MailboxPostOutcome.Stored, 2L, 2), (post.Outcome, post.Cursor, post.Stored));

        var all = box.Pull(Box, 0, 10, T0, Keep);
        Assert.Equal(new[] { "a", "b" }, all.Items.Select(i => i.Id));
        Assert.Equal(new long[] { 1, 2 }, all.Items.Select(i => i.Cursor));
        Assert.Equal((2L, false), (all.Cursor, all.More));

        box.Post(Box, "alex@example.com", new[] { Item("c", "BBBB") }, 100, T0, Keep);
        var after = Assert.Single(box.Pull(Box, 2, 10, T0, Keep).Items);
        Assert.Equal(("c", "BBBB", 3L), (after.Id, after.Body, after.Cursor));
        Assert.Empty(box.Pull(Box, 3, 10, T0, Keep).Items);
    }

    [SkippableFact]
    public void A_read_is_paged_and_says_where_the_next_one_starts()
    {
        var box = NewMailbox();
        box.Post(Box, "sam@example.com", Enumerable.Range(0, 5).Select(i => Item("m" + i)).ToList(), 100, T0, Keep);
        var first = box.Pull(Box, 0, 2, T0, Keep);
        Assert.Equal((2, 2L, true), (first.Items.Count, first.Cursor, first.More));
        var third = box.Pull(Box, 4, 2, T0, Keep);
        Assert.Equal((1, 5L, false), (third.Items.Count, third.Cursor, third.More));
    }

    [SkippableFact]
    public void The_same_item_posted_again_is_stored_once()
    {
        var box = NewMailbox();
        box.Post(Box, "sam@example.com", new[] { Item("a", "ONE1") }, 100, T0, Keep);
        var again = box.Post(Box, "alex@example.com", new[] { Item("a", "TWO2"), Item("b"), Item("b") }, 100, T0, Keep);
        Assert.Equal((MailboxPostOutcome.Stored, 2L, 1), (again.Outcome, again.Cursor, again.Stored));
        var items = box.Pull(Box, 0, 10, T0, Keep).Items;
        Assert.Equal(new[] { "ONE1", "AAAA" }, items.Select(i => i.Body));
    }

    [SkippableFact]
    public void A_mailbox_that_would_go_past_its_limit_takes_nothing()
    {
        var box = NewMailbox();
        box.Post(Box, "sam@example.com", new[] { Item("a"), Item("b") }, 3, T0, Keep);
        var over = box.Post(Box, "sam@example.com", new[] { Item("c"), Item("d") }, 3, T0, Keep);
        Assert.Equal((MailboxPostOutcome.Full, 0), (over.Outcome, over.Stored));
        Assert.Equal(2, box.Pull(Box, 0, 10, T0, Keep).Items.Count);
        Assert.Equal(MailboxPostOutcome.Stored, box.Post(Box, "sam@example.com", new[] { Item("c") }, 3, T0, Keep).Outcome);
    }

    [SkippableFact]
    public void Mailboxes_are_separate()
    {
        var box = NewMailbox();
        box.Post(Box, "sam@example.com", new[] { Item("a") }, 100, T0, Keep);
        box.Post(Other, "sam@example.com", new[] { Item("a", "ZZZZ") }, 100, T0, Keep);
        Assert.Equal("AAAA", Assert.Single(box.Pull(Box, 0, 10, T0, Keep).Items).Body);
        Assert.Equal("ZZZZ", Assert.Single(box.Pull(Other, 0, 10, T0, Keep).Items).Body);
        Assert.Empty(box.Pull("CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC", 0, 10, T0, Keep).Items);
    }

    [SkippableFact]
    public void An_item_goes_after_the_keeping_time_and_the_cursor_never_goes_back()
    {
        var box = NewMailbox();
        box.Post(Box, "sam@example.com", new[] { Item("old") }, 100, T0, Keep);
        var later = T0 + Keep + TimeSpan.FromMinutes(1);
        Assert.Empty(box.Pull(Box, 0, 10, later, Keep).Items);                                  // not handed out
        var post = box.Post(Box, "sam@example.com", new[] { Item("new") }, 100, later, Keep);   // and gone from the mailbox when it is posted to
        Assert.Equal(2L, post.Cursor);
        Assert.Equal(new[] { "new" }, box.Pull(Box, 0, 10, later, Keep).Items.Select(i => i.Id));
    }

    [SkippableFact]
    public void A_sweep_removes_what_has_expired_everywhere()
    {
        var box = NewMailbox();
        box.Post(Box, "sam@example.com", new[] { Item("a"), Item("b") }, 100, T0, Keep);
        box.Post(Other, "sam@example.com", new[] { Item("c") }, 100, T0 + TimeSpan.FromDays(20), Keep);
        var now = T0 + TimeSpan.FromDays(31);
        Assert.Equal(2, box.Expire(now, Keep));
        Assert.Empty(box.Pull(Box, 0, 10, T0, Keep).Items);
        Assert.Single(box.Pull(Other, 0, 10, T0 + TimeSpan.FromDays(20), Keep).Items);
    }

    [SkippableFact]
    public void Removing_an_account_removes_what_it_left_and_nothing_else()
    {
        var box = NewMailbox();
        box.Post(Box, "sam@example.com", new[] { Item("a") }, 100, T0, Keep);
        box.Post(Box, "alex@example.com", new[] { Item("b") }, 100, T0, Keep);
        box.Post(Other, "sam@example.com", new[] { Item("c") }, 100, T0, Keep);
        box.RemoveAccount("sam@example.com");
        Assert.Equal(new[] { "b" }, box.Pull(Box, 0, 10, T0, Keep).Items.Select(i => i.Id));
        Assert.Empty(box.Pull(Other, 0, 10, T0, Keep).Items);
    }
}

/// <summary>Mailboxes in files.</summary>
public sealed class FileMailboxStoreTests : MailboxStoreContractTests
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "yaparr-mailbox-" + Guid.NewGuid().ToString("N"));

    protected override IMailboxStore NewMailbox() => new MailboxStore(new ServiceOptions { DataDirectory = _dir });

    public override void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }
}

/// <summary>Mailboxes in Postgres: the same cases as for files (skipped unless <c>YAPARR_TEST_POSTGRES</c> is set).</summary>
public sealed class PostgresMailboxStoreTests : MailboxStoreContractTests
{
    private readonly PostgresTestDatabase _database = new();

    protected override IMailboxStore NewMailbox() =>
        Stores.Mailbox(new ServiceOptions { DataDirectory = Path.Combine(Path.GetTempPath(), "yaparr-no-files-" + Guid.NewGuid().ToString("N")), Database = _database.ConnectionString });

    public override void Dispose() => _database.Dispose();
}

public sealed class MailboxApiTests : ServiceTest
{
    private const string Box = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    private static StringContent Json(object body) => new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    private static async Task<HttpResponseMessage> Call(HttpClient client, string? token, HttpMethod method, string path, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (token != null) request.Headers.Authorization = new("Bearer", token);
        if (body != null) request.Content = Json(body);
        return await client.SendAsync(request);
    }

    private static object Item(string id, string body = "AAAA") => new { id, body };

    [Fact]
    public async Task A_device_leaves_items_and_a_device_of_another_account_reads_them()
    {
        var client = Start();
        var sam = Register("sam@example.com");
        var alex = Register("alex@example.com");

        var post = await Call(client, sam, HttpMethod.Post, "/v1/mailbox/" + Box, new { items = new[] { Item("a1", "AAAA"), Item("b2", "BBBB") } });
        Assert.Equal(HttpStatusCode.OK, post.StatusCode);
        var result = await post.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal((2L, 2), (result.GetProperty("cursor").GetInt64(), result.GetProperty("stored").GetInt32()));

        var read = await Call(client, alex, HttpMethod.Get, "/v1/mailbox/" + Box);
        var page = await read.Content.ReadFromJsonAsync<JsonElement>();
        var items = page.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(new[] { "a1", "b2" }, items.Select(i => i.GetProperty("id").GetString()));
        Assert.Equal("BBBB", items[1].GetProperty("body").GetString());
        // The reader is not told who left an item.
        Assert.False(items[0].TryGetProperty("account", out _));

        var none = await (await Call(client, alex, HttpMethod.Get, "/v1/mailbox/" + Box + "?since=2")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, none.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task A_device_that_is_not_signed_in_gets_nothing_and_leaves_nothing()
    {
        var client = Start();
        Assert.Equal(HttpStatusCode.Unauthorized, (await Call(client, null, HttpMethod.Get, "/v1/mailbox/" + Box)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Call(client, "ycl_wrong", HttpMethod.Post, "/v1/mailbox/" + Box, new { items = new[] { Item("a1") } })).StatusCode);
    }

    [Fact]
    public async Task What_is_not_a_mailbox_or_an_item_is_refused()
    {
        var client = Start();
        var sam = Register("sam@example.com");
        Assert.Equal(HttpStatusCode.BadRequest, (await Call(client, sam, HttpMethod.Get, "/v1/mailbox/short")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Call(client, sam, HttpMethod.Post, "/v1/mailbox/" + Box, new { items = new[] { Item("bad id!") } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Call(client, sam, HttpMethod.Post, "/v1/mailbox/" + Box, new { items = new[] { Item("a1", "not base64!") } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Call(client, sam, HttpMethod.Post, "/v1/mailbox/" + Box, new { items = new[] { Item("a1", new string('A', 43704)) } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Call(client, sam, HttpMethod.Post, "/v1/mailbox/" + Box, new { items = Enumerable.Range(0, 21).Select(i => Item("m" + i)).ToArray() })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Call(client, sam, HttpMethod.Post, "/v1/mailbox/" + Box, new { items = Array.Empty<object>() })).StatusCode);
    }

    [Fact]
    public async Task A_full_mailbox_and_a_busy_account_are_told_so()
    {
        var client = Start(new() { ["MailboxMaxItems"] = "2", ["MailboxPostsPerMinute"] = "3" });
        var sam = Register("sam@example.com");
        Assert.Equal(HttpStatusCode.OK, (await Call(client, sam, HttpMethod.Post, "/v1/mailbox/" + Box, new { items = new[] { Item("a1"), Item("b2") } })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Call(client, sam, HttpMethod.Post, "/v1/mailbox/" + Box, new { items = new[] { Item("c3") } })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Call(client, sam, HttpMethod.Post, "/v1/mailbox/" + Box, new { items = new[] { Item("a1") } })).StatusCode);   // already there: nothing new
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Call(client, sam, HttpMethod.Post, "/v1/mailbox/" + Box, new { items = new[] { Item("a1") } })).StatusCode);
    }

    [Fact]
    public async Task Deleting_an_account_removes_what_it_left_in_mailboxes()
    {
        var client = Start();
        var sam = Register("sam@example.com");
        var alex = Register("alex@example.com");
        await Call(client, sam, HttpMethod.Post, "/v1/mailbox/" + Box, new { items = new[] { Item("a1") } });
        await Call(client, alex, HttpMethod.Post, "/v1/mailbox/" + Box, new { items = new[] { Item("b2") } });

        var deleted = await client.PostAsync("/account/delete", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["username"] = "sam@example.com", ["current"] = Password, ["confirm"] = "delete my account",
        }));
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);

        var page = await (await Call(client, alex, HttpMethod.Get, "/v1/mailbox/" + Box)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(new[] { "b2" }, page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()));
    }
}
