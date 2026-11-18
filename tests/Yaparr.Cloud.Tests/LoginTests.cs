using System.Buffers.Text;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Yaparr.Cloud;

namespace Yaparr.Cloud.Tests;

public sealed class LoginTests : ServiceTest
{
    private const string Redirect = "http://127.0.0.1:51234/callback";
    private const string State = "state-0123456789";

    // What an app makes before it opens the browser: a secret it keeps, and the hash of it that it shows.
    private static (string Verifier, string Challenge) Pkce()
    {
        var verifier = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        return (verifier, Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))));
    }

    private static string LoginUrl(string challenge, string redirect = Redirect, string state = State, string device = "Sam's laptop") =>
        $"/login?redirect_uri={Uri.EscapeDataString(redirect)}&code_challenge={challenge}&state={state}&device={Uri.EscapeDataString(device)}";

    private static Task<HttpResponseMessage> PostLogin(HttpClient client, string challenge, string username, string password, string redirect = Redirect) =>
        client.PostAsync("/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["redirect_uri"] = redirect, ["code_challenge"] = challenge, ["state"] = State, ["device"] = "Sam's laptop",
            ["username"] = username, ["password"] = password,
        }));

    private static string CodeIn(HttpResponseMessage redirect)
    {
        Assert.Equal(HttpStatusCode.Redirect, redirect.StatusCode);
        var location = redirect.Headers.Location!;
        Assert.StartsWith(Redirect, location.ToString());
        var query = System.Web.HttpUtility.ParseQueryString(location.Query);
        Assert.Equal(State, query["state"]);
        return query["code"]!;
    }

    private static async Task<HttpResponseMessage> Trade(HttpClient client, string? code, string? verifier) =>
        await client.PostAsJsonAsync("/v1/token", new { code, verifier });

    [Fact]
    public async Task A_phone_signs_in_through_the_apps_own_link_because_it_cannot_listen_while_the_browser_is_in_front()
    {
        var client = Start();
        new AccountDirectory(Options).Add("sam@example.com", Password);
        var (verifier, challenge) = Pkce();
        const string appLink = "yaparr://signed-in";

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(LoginUrl(challenge, appLink))).StatusCode);
        var done = await PostLogin(client, challenge, "sam@example.com", Password, appLink);
        // a page that hands the browser to the app (a form's redirect to another program's link is not followed everywhere), carrying the code and the state
        Assert.Equal(HttpStatusCode.OK, done.StatusCode);
        var html = System.Net.WebUtility.HtmlDecode(await done.Content.ReadAsStringAsync());
        var link = System.Text.RegularExpressions.Regex.Match(html, @"href=""(yaparr://signed-in\?[^""]+)""").Groups[1].Value;
        var query = System.Web.HttpUtility.ParseQueryString(new Uri(link).Query);
        Assert.Equal(State, query["state"]);

        var token = await Trade(client, query["code"], verifier);
        Assert.Equal(HttpStatusCode.OK, token.StatusCode);
    }

    [Theory]
    [InlineData("yaparr://signed-in/extra")]
    [InlineData("yaparr://join")]
    [InlineData("https://example.com/callback")]
    public async Task Only_the_apps_exact_link_is_accepted_besides_this_computer(string redirect)
    {
        var client = Start();
        var (_, challenge) = Pkce();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(LoginUrl(challenge, redirect))).StatusCode);
    }

    [Fact]
    public async Task Signing_in_in_the_browser_gives_the_app_a_token_that_works()
    {
        var client = Start();
        new AccountDirectory(Options).Add("sam@example.com", Password);
        var (verifier, challenge) = Pkce();

        // The page the browser shows.
        var page = await client.GetAsync(LoginUrl(challenge));
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("Sam&#39;s laptop", html);
        Assert.Contains("name=\"password\"", html);
        Assert.Equal("no-store", page.Headers.CacheControl!.ToString());
        Assert.Contains("frame-ancestors 'none'", page.Headers.GetValues("Content-Security-Policy").Single());

        // The person signs in (the name typed any way), and the browser is sent back to the app with a code.
        var code = CodeIn(await PostLogin(client, challenge, "  Sam@Example.COM ", Password));

        // The app trades the code for a token, and the token works.
        var traded = await Trade(client, code, verifier);
        Assert.Equal(HttpStatusCode.OK, traded.StatusCode);
        var answer = await traded.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("sam@example.com", answer.GetProperty("username").GetString());
        var token = answer.GetProperty("token").GetString()!;
        Assert.Equal(HttpStatusCode.Created, (await Send(client, token, HttpMethod.Put, "/v1/backup", Locked(), ifMatch: 0)).StatusCode);

        // The token is nowhere in the file, only its hash.
        Assert.DoesNotContain(token, File.ReadAllText(Path.Combine(Dir, "accounts.json")));
        Assert.DoesNotContain(Password, File.ReadAllText(Path.Combine(Dir, "accounts.json")));
    }

    [Fact]
    public async Task A_wrong_password_and_an_unknown_name_get_the_same_page_and_no_code()
    {
        var client = Start();
        new AccountDirectory(Options).Add("sam@example.com", Password);
        var (_, challenge) = Pkce();

        var wrong = await PostLogin(client, challenge, "sam@example.com", "not the password");
        var unknown = await PostLogin(client, challenge, "alex@example.com", Password);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Null(wrong.Headers.Location);
        var a = await wrong.Content.ReadAsStringAsync();
        var b = await unknown.Content.ReadAsStringAsync();
        Assert.Contains("The username or password is wrong.", a);
        Assert.Equal(a.Replace("value=\"sam@example.com\"", ""), b.Replace("value=\"alex@example.com\"", ""));   // the same but for the name typed
    }

    [Fact]
    public async Task A_code_works_once_and_only_with_the_verifier_of_the_app_that_asked()
    {
        var client = Start();
        new AccountDirectory(Options).Add("sam@example.com", Password);
        var (verifier, challenge) = Pkce();

        var stolen = CodeIn(await PostLogin(client, challenge, "sam@example.com", Password));
        Assert.Equal(HttpStatusCode.BadRequest, (await Trade(client, stolen, Pkce().Verifier)).StatusCode);   // someone who saw the code go by
        Assert.Equal(HttpStatusCode.BadRequest, (await Trade(client, stolen, verifier)).StatusCode);          // and it is spent now, even for the app

        var code = CodeIn(await PostLogin(client, challenge, "sam@example.com", Password));
        Assert.Equal(HttpStatusCode.OK, (await Trade(client, code, verifier)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Trade(client, code, verifier)).StatusCode);            // not twice

        Assert.Equal(HttpStatusCode.BadRequest, (await Trade(client, null, null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Trade(client, "made-up", verifier)).StatusCode);
    }

    [Theory]
    [InlineData("https://evil.example/callback")]            // another site
    [InlineData("http://evil.example/callback")]
    [InlineData("http://127.0.0.1.evil.example/callback")]   // looks local, is not
    [InlineData("http://localhost.evil.example:5000/")]
    [InlineData("http://user@127.0.0.1:5000/")]
    [InlineData("yaparr://callback")]
    [InlineData("javascript:alert(1)")]
    [InlineData("")]
    public async Task The_browser_is_only_ever_sent_back_to_the_computer_it_is_on(string redirect)
    {
        var client = Start();
        new AccountDirectory(Options).Add("sam@example.com", Password);
        var (_, challenge) = Pkce();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(LoginUrl(challenge, redirect))).StatusCode);
        var posted = await PostLogin(client, challenge, "sam@example.com", Password, redirect);
        Assert.Equal(HttpStatusCode.BadRequest, posted.StatusCode);   // even with the right password
        Assert.Null(posted.Headers.Location);
    }

    [Theory]
    [InlineData("http://127.0.0.1:5000/")]
    [InlineData("http://127.0.0.1:65000/callback?x=1")]
    [InlineData("http://[::1]:5000/callback")]
    public void An_address_on_the_same_computer_is_accepted(string redirect) =>
        Assert.NotNull(LoginFlow.Read(redirect, new string('a', 43), State, "x", out _));

    [Fact]
    public async Task A_link_that_is_not_complete_or_carries_markup_shows_nothing_of_it()
    {
        var client = Start();
        var (_, challenge) = Pkce();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/login")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(LoginUrl("short"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(LoginUrl(challenge, state: "<script>"))).StatusCode);

        // The name of the device is shown on the page, so it is encoded.
        var page = await (await client.GetAsync(LoginUrl(challenge, device: "<script>alert(1)</script>"))).Content.ReadAsStringAsync();
        Assert.DoesNotContain("<script>", page);
        Assert.Contains("&lt;script&gt;", page);
    }

    [Fact]
    public async Task Guessing_a_password_is_stopped_after_a_number_of_tries_even_with_the_right_one()
    {
        var client = Start(new() { ["RequestsPerMinute"] = "1000" });
        new AccountDirectory(Options).Add("sam@example.com", Password);
        var (_, challenge) = Pkce();
        for (int i = 0; i < 8; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await PostLogin(client, challenge, "sam@example.com", "guess number " + i)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await PostLogin(client, challenge, "sam@example.com", Password)).StatusCode);
    }

    [Fact]
    public void The_challenge_is_checked_as_the_standard_says()
    {
        // The example from the standard (RFC 7636, appendix B), which the app's own test uses too.
        var flow = new LoginFlow();
        var request = LoginFlow.Read(Redirect, "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", State, "x", out _)!;
        var code = System.Web.HttpUtility.ParseQueryString(new Uri(flow.Complete(request, "sam@example.com")).Query)["code"];
        Assert.Equal("sam@example.com", flow.Redeem(code, "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk")?.User);
    }

    [Fact]
    public void The_login_limiter_forgets_after_a_while_and_after_a_good_sign_in()
    {
        var now = DateTimeOffset.UtcNow;
        var limiter = new LoginLimiter(() => now);
        for (int i = 0; i < 8; i++) limiter.Failed("sam@example.com");
        Assert.True(limiter.IsBlocked("sam@example.com"));
        Assert.False(limiter.IsBlocked("alex@example.com"));
        now = now.AddMinutes(16);
        Assert.False(limiter.IsBlocked("sam@example.com"));

        for (int i = 0; i < 7; i++) limiter.Failed("sam@example.com");
        limiter.Succeeded("sam@example.com");
        limiter.Failed("sam@example.com");
        Assert.False(limiter.IsBlocked("sam@example.com"));
    }

    [Fact]
    public void A_code_that_has_waited_too_long_is_refused()
    {
        var now = DateTimeOffset.UtcNow;
        var flow = new LoginFlow(() => now);
        var (verifier, challenge) = Pkce();
        var request = LoginFlow.Read(Redirect, challenge, State, "x", out _)!;
        var code = System.Web.HttpUtility.ParseQueryString(new Uri(flow.Complete(request, "sam@example.com")).Query)["code"];
        now = now.AddMinutes(3);
        Assert.Null(flow.Redeem(code, verifier));
    }

    private static Task<HttpResponseMessage> ChangePassword(HttpClient client, string username, string current, string replacement, string? again = null) =>
        client.PostAsync("/account/password", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["username"] = username, ["current"] = current, ["new"] = replacement, ["again"] = again ?? replacement,
        }));

    [Fact]
    public async Task Changing_the_password_signs_every_device_out_and_keeps_the_backup()
    {
        var client = Start();
        var token = Register("sam@example.com");
        var body = Locked();
        await Send(client, token, HttpMethod.Put, "/v1/backup", body, ifMatch: 0);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/account/password")).StatusCode);

        var changed = await ChangePassword(client, "sam@example.com", Password, "a brand new password");
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        Assert.Contains("Password changed", await changed.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(client, token, HttpMethod.Get, "/v1/backup")).StatusCode);   // the device has to sign in again

        var (verifier, challenge) = Pkce();
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostLogin(client, challenge, "sam@example.com", Password)).StatusCode);       // the old password is done
        var code = CodeIn(await PostLogin(client, challenge, "sam@example.com", "a brand new password"));
        var fresh = (await (await Trade(client, code, verifier)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
        Assert.Equal(body, await (await Send(client, fresh, HttpMethod.Get, "/v1/backup")).Content.ReadAsByteArrayAsync());   // the backup is as it was
    }

    [Fact]
    public async Task A_password_change_needs_the_current_password_and_a_good_new_one()
    {
        var client = Start();
        var token = Register("sam@example.com");
        Assert.Equal(HttpStatusCode.Unauthorized, (await ChangePassword(client, "sam@example.com", "not the password", "a brand new password")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await ChangePassword(client, "nobody@example.com", Password, "a brand new password")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ChangePassword(client, "sam@example.com", Password, "short")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ChangePassword(client, "sam@example.com", Password, "a brand new password", "a different password")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(client, token, HttpMethod.Get, "/v1/backup/versions")).StatusCode);   // nothing changed
    }
}
