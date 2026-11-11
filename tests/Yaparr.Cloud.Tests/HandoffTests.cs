using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Yaparr.Cloud;

namespace Yaparr.Cloud.Tests;

public sealed class HandoffTests : ServiceTest
{
    private string NewCode() => new SignupCodes(Options).Create("", null, null).Code;

    private static async Task<string> SignUpForSetupToken(HttpClient client, string code, string email)
    {
        var page = await (await client.PostAsync("/signup", new FormUrlEncodedContent(new Dictionary<string, string> { ["code"] = code, ["email"] = email }))).Content.ReadAsStringAsync();
        return Regex.Match(page, "name=\"token\" value=\"(set_[A-Za-z0-9_-]+)\"").Groups[1].Value;
    }

    private static Task<HttpResponseMessage> Finish(HttpClient client, string token, string userAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64)")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/account/setup")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = token, ["password"] = "a good long password", ["again"] = "a good long password" }),
        };
        request.Headers.UserAgent.ParseAdd(userAgent);
        return client.SendAsync(request);
    }

    private static string LinkIn(string html)
    {
        var match = Regex.Match(html, "href=\"(yaparr://backup-signin/[^\"]+)\"");
        Assert.True(match.Success, "the page has the button that opens the app");
        return System.Net.WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    private static Task<HttpResponseMessage> Trade(HttpClient client, string? code) => client.PostAsJsonAsync("/v1/token/handoff", new { code });

    [Fact]
    public async Task The_page_after_signing_up_has_a_button_that_opens_the_app_signed_in_as_that_account()
    {
        var client = Start(new() { ["PublicUrl"] = "https://backup.example.org/" });
        var token = await SignUpForSetupToken(client, NewCode(), "sam@example.com");
        var page = await (await Finish(client, token)).Content.ReadAsStringAsync();
        Assert.Contains("Open Yaparr and sign in", page);
        Assert.Contains("Or, to do it by hand", page);                    // the steps are still there

        // yaparr://backup-signin/<code>/<this server, escaped>
        var link = LinkIn(page);
        var parts = Regex.Match(link, "^yaparr://backup-signin/([A-Za-z0-9_-]{43})/(.+)$");
        Assert.True(parts.Success, link);
        Assert.Equal("https://backup.example.org", Uri.UnescapeDataString(parts.Groups[2].Value));

        var traded = await Trade(client, parts.Groups[1].Value);
        Assert.Equal(HttpStatusCode.OK, traded.StatusCode);
        var answer = await traded.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("sam@example.com", answer.GetProperty("username").GetString());

        // The token works, and it is one device of the account.
        var signedIn = answer.GetProperty("token").GetString()!;
        var versions = await Send(client, signedIn, HttpMethod.Get, "/v1/backup/versions");
        Assert.Equal(HttpStatusCode.OK, versions.StatusCode);
        Assert.Equal("sam@example.com", (await versions.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("username").GetString());
        Assert.Equal(1, new AccountDirectory(Options).List().Single().Devices);
        Assert.DoesNotContain(signedIn, File.ReadAllText(Path.Combine(Dir, "accounts.json")));   // only a hash of the token is kept
    }

    [Fact]
    public async Task The_code_works_once()
    {
        var client = Start();
        var code = Regex.Match(LinkIn(await (await Finish(client, await SignUpForSetupToken(client, NewCode(), "sam@example.com"))).Content.ReadAsStringAsync()),
            "backup-signin/([^/]+)/").Groups[1].Value;
        Assert.Equal(HttpStatusCode.OK, (await Trade(client, code)).StatusCode);
        var again = await Trade(client, code);
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
        Assert.Contains("expired or was already used", await again.Content.ReadAsStringAsync());
        Assert.Equal(1, new AccountDirectory(Options).List().Single().Devices);
    }

    [Fact]
    public async Task Each_person_gets_the_code_for_their_own_account()
    {
        var client = Start();
        var code = NewCode();
        var links = new List<(string Email, string Code)>();
        foreach (var email in new[] { "sam@example.com", "alex@example.com" })
        {
            var html = await (await Finish(client, await SignUpForSetupToken(client, code, email))).Content.ReadAsStringAsync();
            links.Add((email, Regex.Match(LinkIn(html), "backup-signin/([^/]+)/").Groups[1].Value));
        }
        Assert.NotEqual(links[0].Code, links[1].Code);
        foreach (var (email, handoffCode) in links)
            Assert.Equal(email, (await (await Trade(client, handoffCode)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("username").GetString());
    }

    [Fact]
    public async Task A_phone_gets_no_button_since_the_android_app_cannot_use_it_yet_and_still_gets_the_steps()
    {
        var client = Start();
        var token = await SignUpForSetupToken(client, NewCode(), "sam@example.com");
        var page = await (await Finish(client, token, "Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 Mobile Safari/537.36")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("yaparr://", page);
        Assert.DoesNotContain("Open Yaparr and sign in", page);
        Assert.Contains("Settings &gt; Backup &gt; Backup server", page);
        Assert.Contains("To use it", page);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("never-made-this-code-never-made-this-code")]
    public async Task A_code_that_was_never_made_is_refused(string? code)
    {
        var client = Start();
        new AccountDirectory(Options).Add("sam@example.com", "a good long password");
        Assert.Equal(HttpStatusCode.BadRequest, (await Trade(client, code)).StatusCode);
        Assert.Equal(0, new AccountDirectory(Options).List().Single().Devices);
    }

    [Fact]
    public async Task A_request_that_is_not_the_right_shape_is_refused()
    {
        var client = Start();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/v1/token/handoff", new StringContent("not json", System.Text.Encoding.UTF8, "application/json"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/v1/token/handoff", new StringContent("{}", System.Text.Encoding.UTF8, "application/json"))).StatusCode);
    }

    [Fact]
    public void A_code_ends_after_five_minutes_and_a_flood_of_them_is_capped()
    {
        var now = DateTimeOffset.UtcNow;
        var handoff = new SignInHandoff(() => now);
        var code = handoff.Create("sam@example.com");
        now = now.AddMinutes(4);
        Assert.Equal("sam@example.com", handoff.Redeem(code));       // in time

        var late = handoff.Create("sam@example.com");
        now = now.AddMinutes(6);
        Assert.Null(handoff.Redeem(late));                           // too late

        // Made-up sign-ups cannot fill the memory: the oldest codes are dropped.
        var first = handoff.Create("first@example.com");
        for (int i = 0; i < 1100; i++)
        {
            now = now.AddMilliseconds(10);          // each a moment later, so "the oldest" means something
            handoff.Create($"person{i}@example.com");
        }
        Assert.Null(handoff.Redeem(first));
        Assert.NotNull(handoff.Redeem(handoff.Create("last@example.com")));
    }

    [Fact]
    public void The_link_carries_the_server_as_one_escaped_part()
    {
        Assert.Equal("yaparr://backup-signin/abc/https%3A%2F%2Fcloud.yaparr.com", SignInHandoff.AppLink("abc", "https://cloud.yaparr.com"));
        Assert.Equal("yaparr://backup-signin/abc/https%3A%2F%2Fcloud.yaparr.com%3A8443", SignInHandoff.AppLink("abc", "https://cloud.yaparr.com:8443"));
    }

    [Fact]
    public async Task The_sign_up_page_without_a_finished_password_has_no_button()
    {
        var client = Start();
        var html = await (await client.PostAsync("/signup", new FormUrlEncodedContent(new Dictionary<string, string> { ["code"] = NewCode(), ["email"] = "sam@example.com" }))).Content.ReadAsStringAsync();
        Assert.DoesNotContain("yaparr://", html);                    // only after the password is chosen
    }
}
