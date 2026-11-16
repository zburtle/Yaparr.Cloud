using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Yaparr.Cloud.Tests;

public sealed class SyncApiTests : ServiceTest
{
    private static StringContent Json(object body) => new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    private static object Rec(string id, string clock, string body = "AAAA", bool deleted = false, string kind = "room", string device = "phone") =>
        new { kind, id, clock, device, deleted, body = deleted ? "" : body };

    private static async Task<HttpResponseMessage> Put(HttpClient client, string? token, params object[] records)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, "/v1/sync") { Content = Json(new { records }) };
        if (token != null) request.Headers.Authorization = new("Bearer", token);
        return await client.SendAsync(request);
    }

    private static async Task<JsonElement> Pull(HttpClient client, string? token, string query = "")
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/v1/sync" + query);
        if (token != null) request.Headers.Authorization = new("Bearer", token);
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task A_signed_in_device_sends_records_and_another_device_of_the_account_pulls_them()
    {
        var client = Start();
        var phone = Register("sam@example.com");
        var accounts = new AccountDirectory(Options);
        var laptop = accounts.IssueToken("sam@example.com", "Laptop")!;

        var put = await Put(client, phone, Rec("room-1", "0001", "KEY1"), Rec("name", "0001", "TkFNRQ==", kind: "profile"));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var result = await put.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, result.GetProperty("cursor").GetInt64());
        Assert.Equal(0, result.GetProperty("stale").GetArrayLength());

        var page = await Pull(client, laptop);
        Assert.Equal((2L, false), (page.GetProperty("cursor").GetInt64(), page.GetProperty("more").GetBoolean()));
        var records = page.GetProperty("records").EnumerateArray().ToList();
        Assert.Equal(new[] { "room/room-1", "profile/name" }, records.Select(r => r.GetProperty("kind").GetString() + "/" + r.GetProperty("id").GetString()));
        Assert.Equal("KEY1", records[0].GetProperty("body").GetString());
        Assert.Equal("phone", records[0].GetProperty("device").GetString());

        Assert.Equal(0, (await Pull(client, laptop, "?since=2")).GetProperty("records").GetArrayLength());
    }

    [Fact]
    public async Task Pulls_are_paged_by_the_limit()
    {
        var client = Start();
        var token = Register("sam@example.com");
        await Put(client, token, Enumerable.Range(0, 5).Select(i => Rec("r" + i, "0001")).ToArray());
        var first = await Pull(client, token, "?limit=2");
        Assert.Equal((2, 2L, true), (first.GetProperty("records").GetArrayLength(), first.GetProperty("cursor").GetInt64(), first.GetProperty("more").GetBoolean()));
        var rest = await Pull(client, token, "?since=" + first.GetProperty("cursor").GetInt64());
        Assert.Equal((3, false), (rest.GetProperty("records").GetArrayLength(), rest.GetProperty("more").GetBoolean()));
    }

    [Fact]
    public async Task An_older_write_is_named_stale_and_not_taken_and_the_accounts_do_not_see_each_other()
    {
        var client = Start();
        var sam = Register("sam@example.com");
        var pat = Register("pat@example.com");
        await Put(client, sam, Rec("a", "0005", "NEW1"));
        var result = await (await Put(client, sam, Rec("a", "0004", "OLD1"))).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("room/a", Assert.Single(result.GetProperty("stale").EnumerateArray()).GetString());
        Assert.Equal("NEW1", (await Pull(client, sam)).GetProperty("records")[0].GetProperty("body").GetString());
        Assert.Equal(0, (await Pull(client, pat)).GetProperty("records").GetArrayLength());
    }

    [Fact]
    public async Task A_deleted_record_is_sent_with_no_body_and_comes_back_as_a_marker()
    {
        var client = Start();
        var token = Register("sam@example.com");
        await Put(client, token, Rec("a", "0001", "KEY1"));
        await Put(client, token, Rec("a", "0002", deleted: true));
        var record = (await Pull(client, token)).GetProperty("records")[0];
        Assert.Equal((true, ""), (record.GetProperty("deleted").GetBoolean(), record.GetProperty("body").GetString()));
    }

    [Fact]
    public async Task Sync_needs_a_signed_in_device()
    {
        var client = Start();
        Register("sam@example.com");
        Assert.Equal(HttpStatusCode.Unauthorized, (await Put(client, null, Rec("a", "0001"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Put(client, "not-a-token", Rec("a", "0001"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/v1/sync")).StatusCode);
    }

    [Theory]
    [InlineData("Room", "a", "0001", "AAAA", false)]          // a kind is lowercase
    [InlineData("room", "has space", "0001", "AAAA", false)]  // an id has no spaces
    [InlineData("room", "a", "not ok", "AAAA", false)]        // a clock has no spaces
    [InlineData("room", "a", "0001", "not base64!", false)]   // a body is base64
    [InlineData("room", "a", "0001", "", false)]              // and is not empty unless deleted
    [InlineData("room", "a", "0001", "AAAA", true)]           // and is empty when deleted
    public async Task What_is_not_a_good_record_is_refused_and_nothing_is_stored(string kind, string id, string clock, string body, bool deleted)
    {
        var client = Start();
        var token = Register("sam@example.com");
        var response = await Put(client, token, Rec("fine", "0001"), new { kind, id, clock, device = "phone", deleted, body });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, (await Pull(client, token)).GetProperty("records").GetArrayLength());
    }

    [Fact]
    public async Task A_call_with_nothing_in_it_or_too_much_is_refused()
    {
        var client = Start();
        var token = Register("sam@example.com");
        Assert.Equal(HttpStatusCode.BadRequest, (await Put(client, token)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Put(client, token, Enumerable.Range(0, 101).Select(i => Rec("r" + i, "0001")).ToArray())).StatusCode);
    }

    [Fact]
    public async Task A_body_over_the_limit_is_refused()
    {
        var client = Start(new() { ["SyncMaxRecordBytes"] = "8" });
        var token = Register("sam@example.com");
        Assert.Equal(HttpStatusCode.BadRequest, (await Put(client, token, Rec("a", "0001", "AAAAAAAAAAAA"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Put(client, token, Rec("a", "0001", "AAAAAAAA"))).StatusCode);
    }

    [Fact]
    public async Task An_account_over_its_record_limit_is_refused_with_nothing_stored()
    {
        var client = Start(new() { ["SyncMaxRecords"] = "2" });
        var token = Register("sam@example.com");
        Assert.Equal(HttpStatusCode.OK, (await Put(client, token, Rec("a", "0001"))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Put(client, token, Rec("b", "0001"), Rec("c", "0001"))).StatusCode);
        Assert.Equal(1, (await Pull(client, token)).GetProperty("records").GetArrayLength());
    }

    [Fact]
    public async Task One_account_cannot_send_without_limit()
    {
        var client = Start(new() { ["SyncWritesPerMinute"] = "3" });
        var token = Register("sam@example.com");
        for (int i = 0; i < 3; i++) Assert.Equal(HttpStatusCode.OK, (await Put(client, token, Rec("a", "000" + (i + 1)))).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Put(client, token, Rec("a", "0009"))).StatusCode);
    }
}
