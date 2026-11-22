using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Yaparr.Cloud;

namespace Yaparr.Cloud.Tests;

public sealed class AdminPortalTests : ServiceTest
{
    private const string Boss = "boss@example.com";

    // ---- the one-time codes ----

    [Fact]
    public void A_code_matches_the_published_test_vector()
    {
        // RFC 6238, appendix B: the secret "12345678901234567890", at 59 seconds (the first step), is 94287082; the six digits are the last six.
        const string secret = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";
        Assert.Equal("287082", Totp.Code(secret, 1));
        Assert.Equal("081804", Totp.Code(secret, 37_037_036));   // 1111111109 s
    }

    [Fact]
    public void A_code_is_accepted_for_the_step_before_and_after_but_not_further()
    {
        var secret = Totp.NewSecret();
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var step = Totp.StepOf(now);
        Assert.Equal(step, Totp.Verify(secret, Totp.Code(secret, step), now));
        Assert.Equal(step - 1, Totp.Verify(secret, Totp.Code(secret, step - 1), now));
        Assert.Equal(step + 1, Totp.Verify(secret, Totp.Code(secret, step + 1), now));
        Assert.Null(Totp.Verify(secret, Totp.Code(secret, step - 2), now));
        Assert.Null(Totp.Verify(secret, Totp.Code(secret, step + 2), now));
        Assert.Equal(step, Totp.Verify(secret, Totp.Code(secret, step).Insert(3, " "), now));   // typed in two halves
        Assert.Null(Totp.Verify(secret, "12345", now));
        Assert.Null(Totp.Verify(secret, "abcdef", now));
        Assert.Null(Totp.Verify(secret, null, now));
    }

    [Fact]
    public void A_secret_is_long_random_base_32()
    {
        var secret = Totp.NewSecret();
        Assert.Matches("^[A-Z2-7]{32}$", secret);
        Assert.NotEqual(secret, Totp.NewSecret());
    }

    [Fact]
    public void A_code_works_once()
    {
        var sessions = new AdminSessions();
        Assert.True(sessions.TryAcceptStep("a", 100));
        Assert.False(sessions.TryAcceptStep("a", 100));
        Assert.False(sessions.TryAcceptStep("a", 99));
        Assert.True(sessions.TryAcceptStep("a", 101));
        Assert.True(sessions.TryAcceptStep("b", 100));
    }

    // ---- the sessions ----

    [Fact]
    public void A_session_ends_after_half_an_hour_of_nothing_and_after_eight_hours_whatever_it_does()
    {
        var now = new DateTimeOffset(2026, 11, 15, 9, 0, 0, TimeSpan.Zero);
        var sessions = new AdminSessions(() => now);
        var (token, csrf) = sessions.Start("a@example.com");
        Assert.Equal(csrf, sessions.Get(token)!.Csrf);

        now += TimeSpan.FromMinutes(29);
        Assert.NotNull(sessions.Get(token));           // seen now, so the half hour starts again
        now += TimeSpan.FromMinutes(29);
        Assert.NotNull(sessions.Get(token));
        now += TimeSpan.FromMinutes(31);
        Assert.Null(sessions.Get(token));

        var (busy, _) = sessions.Start("a@example.com");
        for (int i = 0; i < 16; i++) { now += TimeSpan.FromMinutes(29); sessions.Get(busy); }   // 7 hours 44 minutes, never idle
        Assert.NotNull(sessions.Get(busy));
        now += TimeSpan.FromMinutes(29);
        Assert.Null(sessions.Get(busy));               // past eight hours
        Assert.Null(sessions.Get("made-up"));
    }

    [Fact]
    public void Only_a_hash_of_a_session_is_kept_and_signing_out_ends_every_session_of_a_person()
    {
        var sessions = new AdminSessions();
        var (one, _) = sessions.Start("a@example.com");
        var (two, _) = sessions.Start("a@example.com");
        var (other, _) = sessions.Start("b@example.com");
        sessions.End(one);
        Assert.Null(sessions.Get(one));
        sessions.EndAll("a@example.com");
        Assert.Null(sessions.Get(two));
        Assert.NotNull(sessions.Get(other));
    }

    // ---- the portal ----

    private sealed record Admin(HttpClient Client, string Secret, string Account)
    {
        // This step, then the next: both are accepted, each once. (Not the step before: if the clock moves into the next step between making a code and
        // the service checking it, that one is two steps back and refused.)
        private int _next;

        public string NextCode() => Totp.Code(Secret, Totp.StepOf(DateTimeOffset.UtcNow) + _next++);
    }

    private Admin NewAdmin(string name = Boss, Dictionary<string, string>? settings = null)
    {
        var accounts = new AccountDirectory(Options);
        Assert.True(accounts.Add(name, Password));
        var secret = accounts.MakeAdmin(name)!;
        return new Admin(Start(settings), secret, name);
    }

    private static Task<HttpResponseMessage> Form(HttpClient client, string url, Dictionary<string, string> fields, string? cookie = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(fields) };
        if (cookie != null) request.Headers.Add("Cookie", cookie);
        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> Get(HttpClient client, string url, string? cookie = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (cookie != null) request.Headers.Add("Cookie", cookie);
        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> TryLogin(Admin admin, string? password = null, string? code = null, string? account = null) =>
        Form(admin.Client, "/admin/login", new() { ["username"] = account ?? admin.Account, ["password"] = password ?? Password, ["code"] = code ?? admin.NextCode() });

    /// <summary>Signs in and returns the cookie to send.</summary>
    private static async Task<string> SignIn(Admin admin)
    {
        var response = await TryLogin(admin);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/admin", response.Headers.Location!.OriginalString);
        var setCookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        return setCookie.Split(';')[0];
    }

    private static string CsrfIn(string html)
    {
        var match = Regex.Match(html, "name=\"csrf\" value=\"([0-9a-f]+)\"");
        Assert.True(match.Success, "the page has a form token");
        return match.Groups[1].Value;
    }

    private static async Task<(string Cookie, string Csrf, string Html)> Open(Admin admin)
    {
        var cookie = await SignIn(admin);
        var page = await Get(admin.Client, "/admin", cookie);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();
        return (cookie, CsrfIn(html), html);
    }

    [Theory]
    [InlineData("GET", "/admin")]
    [InlineData("GET", "/admin/login")]
    [InlineData("POST", "/admin/login")]
    [InlineData("POST", "/admin/codes/add")]
    [InlineData("GET", "/admin/accounts/remove?name=a@example.com")]
    public async Task There_is_no_portal_until_an_account_is_made_an_admin(string method, string url)
    {
        var client = Start();
        new AccountDirectory(Options).Add("someone@example.com", Password);
        var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), url) { Content = method == "POST" ? new FormUrlEncodedContent(new Dictionary<string, string>()) : null });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task The_overview_needs_a_sign_in()
    {
        var admin = NewAdmin();
        var response = await admin.Client.GetAsync("/admin");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/admin/login", response.Headers.Location!.OriginalString);
        var login = await admin.Client.GetAsync("/admin/login");
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.Contains("authenticator", await login.Content.ReadAsStringAsync());
        Assert.Contains("no-store", login.Headers.CacheControl!.ToString());
    }

    [Fact]
    public async Task Signing_in_takes_the_password_and_the_code_and_gives_a_cookie_that_javascript_cannot_read()
    {
        var admin = NewAdmin();
        var response = await TryLogin(admin);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        Assert.StartsWith("yaparr_admin=", cookie);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/admin", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Every_wrong_try_gets_the_same_answer()
    {
        var admin = NewAdmin();
        new AccountDirectory(Options).Add("plain@example.com", Password);   // an account, but not an admin

        var answers = new List<string>();
        async Task Wrong(Task<HttpResponseMessage> request)
        {
            var response = await request;
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Empty(response.Headers.Contains("Set-Cookie") ? response.Headers.GetValues("Set-Cookie") : Array.Empty<string>());
            answers.Add(Regex.Replace(await response.Content.ReadAsStringAsync(), "value=\"[^\"]*\"", ""));
        }
        await Wrong(TryLogin(admin, password: "not the password"));
        await Wrong(TryLogin(admin, code: "000000"));
        await Wrong(TryLogin(admin, code: ""));
        await Wrong(TryLogin(admin, account: "plain@example.com", code: Totp.Code(admin.Secret, Totp.StepOf(DateTimeOffset.UtcNow))));
        await Wrong(TryLogin(admin, account: "nobody@example.com"));
        await Wrong(TryLogin(admin, account: "not an address"));
        Assert.Single(answers.Distinct());
        Assert.Contains("email address, password or code is wrong", answers[0]);
    }

    [Fact]
    public async Task A_code_that_was_used_does_not_sign_in_again()
    {
        var admin = NewAdmin();
        var code = admin.NextCode();
        Assert.Equal(HttpStatusCode.Redirect, (await TryLogin(admin, code: code)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await TryLogin(admin, code: code)).StatusCode);
    }

    [Fact]
    public async Task Too_many_wrong_tries_lock_the_account_out_even_for_the_right_password_and_code()
    {
        var admin = NewAdmin();
        for (int i = 0; i < 8; i++) Assert.Equal(HttpStatusCode.Unauthorized, (await TryLogin(admin, password: "wrong " + i)).StatusCode);
        var response = await TryLogin(admin);
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
    }

    [Fact]
    public async Task The_overview_lists_accounts_and_codes_and_never_a_secret()
    {
        var admin = NewAdmin();
        var accounts = new AccountDirectory(Options);
        accounts.Add("friend@example.com", Password);
        var (code, info) = new SignupCodes(Options).Create("<script>alert(1)</script>", 5, TimeSpan.FromDays(10));
        var (_, _, html) = await Open(admin);

        Assert.Contains("friend@example.com", html);
        Assert.Contains("pill admin", html);
        Assert.Contains(info.Id, html);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html);       // encoded, not run
        Assert.DoesNotContain("<script>alert", html);
        Assert.DoesNotContain(code, html);
        Assert.DoesNotContain(admin.Secret, html);
        Assert.DoesNotContain("argon2id", html);
        Assert.DoesNotContain("ycl_", html);
    }

    [Fact]
    public async Task The_overview_shows_what_each_account_holds_against_its_limits()
    {
        var admin = NewAdmin();
        new AccountDirectory(Options).Add("friend@example.com", Password);
        var (_, _, html) = await Open(admin);

        Assert.Contains("<th>Kept in sync</th>", html);
        Assert.Contains("<th>Pictures left</th>", html);
        Assert.Contains("<th>Gateway today</th>", html);
        Assert.Contains($"of {Options.KeepQuotaBytes / 1048576.0:0.0} MiB", html);
        Assert.Contains($"of {Options.BlobQuotaBytes / 1048576.0:0.0} MiB", html);
        Assert.Contains($"of {Options.GatewayBytesPerDay / 1048576.0:0.0} MiB", html);
    }

    [Fact]
    public async Task A_form_without_the_session_token_is_refused_and_one_without_a_session_goes_to_the_sign_in()
    {
        var admin = NewAdmin();
        var (cookie, csrf, _) = await Open(admin);

        var noToken = await Form(admin.Client, "/admin/codes/add", new() { ["note"] = "x" }, cookie);
        Assert.Equal(HttpStatusCode.Forbidden, noToken.StatusCode);
        var wrongToken = await Form(admin.Client, "/admin/codes/add", new() { ["note"] = "x", ["csrf"] = new string('0', csrf.Length) }, cookie);
        Assert.Equal(HttpStatusCode.Forbidden, wrongToken.StatusCode);
        Assert.Empty(new SignupCodes(Options).List());

        var noSession = await Form(admin.Client, "/admin/codes/add", new() { ["note"] = "x", ["csrf"] = csrf });
        Assert.Equal(HttpStatusCode.Redirect, noSession.StatusCode);
        Assert.Equal("/admin/login", noSession.Headers.Location!.OriginalString);
        Assert.Empty(new SignupCodes(Options).List());
    }

    [Fact]
    public async Task Making_a_code_shows_the_link_once_and_the_code_works_for_signing_up()
    {
        var admin = NewAdmin(settings: new() { ["PublicUrl"] = "https://cloud.example.com", ["SignupPageUrl"] = "https://example.com/signup" });
        var (cookie, csrf, _) = await Open(admin);
        var made = await Form(admin.Client, "/admin/codes/add", new() { ["csrf"] = csrf, ["note"] = "book club", ["uses"] = "3", ["days"] = "7" }, cookie);
        Assert.Equal(HttpStatusCode.OK, made.StatusCode);
        var html = await made.Content.ReadAsStringAsync();
        var link = Regex.Match(html, @"https://example\.com/signup#server=https%3A%2F%2Fcloud\.example\.com&amp;code=([A-HJ-NP-Z2-9-]+)");
        Assert.True(link.Success, html);
        var code = link.Groups[1].Value;

        var codes = new SignupCodes(Options);
        var info = Assert.Single(codes.List());
        Assert.Equal("book club", info.Note);
        Assert.Equal(3, info.MaxUses);
        Assert.NotNull(codes.Check(code));
        Assert.DoesNotContain(code, File.ReadAllText(Path.Combine(Dir, "codes.json")));

        // The overview afterwards has the code's id and not the code.
        var overview = await (await Get(admin.Client, "/admin", cookie)).Content.ReadAsStringAsync();
        Assert.Contains(info.Id, overview);
        Assert.DoesNotContain(code, overview);
    }

    [Theory]
    [InlineData("0", "")]
    [InlineData("-1", "")]
    [InlineData("many", "")]
    [InlineData("100001", "")]
    [InlineData("", "0")]
    [InlineData("", "3651")]
    [InlineData("", "soon")]
    public async Task A_code_with_a_limit_that_is_not_a_number_is_not_made(string uses, string days)
    {
        var admin = NewAdmin();
        var (cookie, csrf, _) = await Open(admin);
        var response = await Form(admin.Client, "/admin/codes/add", new() { ["csrf"] = csrf, ["uses"] = uses, ["days"] = days }, cookie);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(new SignupCodes(Options).List());
    }

    [Fact]
    public async Task A_code_can_be_revoked()
    {
        var admin = NewAdmin();
        var codes = new SignupCodes(Options);
        var (code, info) = codes.Create("", null, null);
        var (cookie, csrf, _) = await Open(admin);
        var response = await Form(admin.Client, "/admin/codes/revoke", new() { ["csrf"] = csrf, ["id"] = info.Id }, cookie);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Null(codes.Check(code));
        Assert.Equal(HttpStatusCode.NotFound, (await Form(admin.Client, "/admin/codes/revoke", new() { ["csrf"] = csrf, ["id"] = "nothing" }, cookie)).StatusCode);
    }

    [Fact]
    public async Task Adding_an_account_makes_up_a_password_and_shows_it_once()
    {
        var admin = NewAdmin();
        var (cookie, csrf, _) = await Open(admin);
        var response = await Form(admin.Client, "/admin/accounts/add", new() { ["csrf"] = csrf, ["email"] = " New@Example.com " }, cookie);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        var password = Regex.Match(html, "<code class=\"secret\">([^<]+)</code>").Groups[1].Value;
        Assert.True(password.Length >= Passwords.MinLength);
        Assert.True(new AccountDirectory(Options).CheckPassword("new@example.com", password));

        Assert.Equal(HttpStatusCode.Conflict, (await Form(admin.Client, "/admin/accounts/add", new() { ["csrf"] = csrf, ["email"] = "new@example.com" }, cookie)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Form(admin.Client, "/admin/accounts/add", new() { ["csrf"] = csrf, ["email"] = "not an address" }, cookie)).StatusCode);
        var overview = await (await Get(admin.Client, "/admin", cookie)).Content.ReadAsStringAsync();
        Assert.DoesNotContain(password, overview);
    }

    [Fact]
    public async Task Resetting_a_password_signs_the_devices_out_and_leaves_the_backup()
    {
        var admin = NewAdmin();
        var token = Register("friend@example.com");
        var store = new BackupStore(Options);
        Assert.Equal(SaveOutcome.Saved, (await store.SaveAsync(BackupStore.FolderOf("friend@example.com"), 0, new MemoryStream(Locked()), CancellationToken.None)).Outcome);

        var (cookie, csrf, _) = await Open(admin);
        var response = await Form(admin.Client, "/admin/accounts/password", new() { ["csrf"] = csrf, ["name"] = "friend@example.com" }, cookie);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var password = Regex.Match(await response.Content.ReadAsStringAsync(), "<code class=\"secret\">([^<]+)</code>").Groups[1].Value;

        var accounts = new AccountDirectory(Options);
        Assert.True(accounts.CheckPassword("friend@example.com", password));
        Assert.False(accounts.CheckPassword("friend@example.com", Password));
        Assert.Null(accounts.UserOfToken(token));
        Assert.Single(store.Versions(BackupStore.FolderOf("friend@example.com")));

        Assert.Equal(HttpStatusCode.NotFound, (await Form(admin.Client, "/admin/accounts/password", new() { ["csrf"] = csrf, ["name"] = "nobody@example.com" }, cookie)).StatusCode);
    }

    [Fact]
    public async Task Removing_an_account_asks_for_its_address_and_deletes_its_backup()
    {
        var admin = NewAdmin();
        Register("friend@example.com");
        var store = new BackupStore(Options);
        await store.SaveAsync(BackupStore.FolderOf("friend@example.com"), 0, new MemoryStream(Locked()), CancellationToken.None);
        var (cookie, csrf, _) = await Open(admin);

        var ask = await Get(admin.Client, "/admin/accounts/remove?name=friend%40example.com", cookie);
        Assert.Equal(HttpStatusCode.OK, ask.StatusCode);
        Assert.Contains("deletes its backup", await ask.Content.ReadAsStringAsync());

        var wrong = await Form(admin.Client, "/admin/accounts/remove", new() { ["csrf"] = csrf, ["name"] = "friend@example.com", ["confirm"] = "someone else" }, cookie);
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.True(new AccountDirectory(Options).Exists("friend@example.com"));

        var done = await Form(admin.Client, "/admin/accounts/remove", new() { ["csrf"] = csrf, ["name"] = "friend@example.com", ["confirm"] = "Friend@Example.com" }, cookie);
        Assert.Equal(HttpStatusCode.OK, done.StatusCode);
        Assert.False(new AccountDirectory(Options).Exists("friend@example.com"));
        Assert.Empty(store.Versions(BackupStore.FolderOf("friend@example.com")));
    }

    [Fact]
    public async Task An_admin_account_cannot_be_removed_from_the_portal()
    {
        var admin = NewAdmin();
        var (cookie, csrf, _) = await Open(admin);
        var response = await Form(admin.Client, "/admin/accounts/remove", new() { ["csrf"] = csrf, ["name"] = Boss, ["confirm"] = Boss }, cookie);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.True(new AccountDirectory(Options).Exists(Boss));
    }

    [Fact]
    public async Task Signing_out_ends_the_session()
    {
        var admin = NewAdmin();
        var (cookie, csrf, _) = await Open(admin);
        var response = await Form(admin.Client, "/admin/logout", new() { ["csrf"] = csrf }, cookie);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var after = await Get(admin.Client, "/admin", cookie);
        Assert.Equal("/admin/login", after.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Taking_the_portal_away_ends_a_session_that_is_open()
    {
        var admin = NewAdmin();
        var (cookie, _, _) = await Open(admin);
        Assert.True(new AccountDirectory(Options).RemoveAdmin(Boss));
        // No admin is left, so the portal is gone altogether.
        Assert.Equal(HttpStatusCode.NotFound, (await Get(admin.Client, "/admin", cookie)).StatusCode);

        new AccountDirectory(Options).Add("second@example.com", Password);
        new AccountDirectory(Options).MakeAdmin("second@example.com");
        var response = await Get(admin.Client, "/admin", cookie);
        Assert.Equal("/admin/login", response.Headers.Location!.OriginalString);   // the first admin's session no longer counts
    }

    [Fact]
    public async Task A_new_password_signs_the_admin_out_of_the_portal_too()
    {
        var admin = NewAdmin();
        var (cookie, csrf, _) = await Open(admin);
        // Another admin resets the first one's password.
        var accounts = new AccountDirectory(Options);
        accounts.Add("second@example.com", Password);
        accounts.MakeAdmin("second@example.com");
        var second = new Admin(admin.Client, accounts.TotpSecretOf("second@example.com")!, "second@example.com");
        var (cookie2, csrf2, _) = await Open(second);
        var response = await Form(admin.Client, "/admin/accounts/password", new() { ["csrf"] = csrf2, ["name"] = Boss }, cookie2);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await Get(admin.Client, "/admin", cookie)).StatusCode);
    }

    // ---- making an account an admin, from the command line ----

    [Fact]
    public async Task The_command_line_makes_an_admin_shows_the_key_and_can_take_it_away()
    {
        var accounts = new AccountDirectory(Options);
        accounts.Add(Boss, Password);
        var output = new StringWriter();
        Assert.Equal(0, await AdminCommands.RunAsync(new[] { "admin", Boss }, Options, output));
        var secret = Regex.Match(output.ToString(), "the key is  ([A-Z2-7]{32})").Groups[1].Value;
        Assert.NotEmpty(secret);
        Assert.Contains("otpauth://totp/", output.ToString());
        Assert.True(accounts.IsAdmin(Boss));
        Assert.Equal(secret, accounts.TotpSecretOf(Boss));

        var again = new StringWriter();
        await AdminCommands.RunAsync(new[] { "admin", Boss }, Options, again);
        Assert.NotEqual(secret, accounts.TotpSecretOf(Boss));     // a new key, for a lost phone

        var list = new StringWriter();
        await AdminCommands.RunAsync(new[] { "list" }, Options, list);
        Assert.Contains("ADMIN", list.ToString());

        Assert.Equal(0, await AdminCommands.RunAsync(new[] { "admin", Boss, "--remove" }, Options, new StringWriter()));
        Assert.False(accounts.IsAdmin(Boss));
        Assert.Null(accounts.TotpSecretOf(Boss));
        Assert.Equal(1, await AdminCommands.RunAsync(new[] { "admin", Boss, "--remove" }, Options, new StringWriter()));
    }

    [Fact]
    public async Task Only_an_account_with_a_password_can_be_made_an_admin()
    {
        Assert.Equal(1, await AdminCommands.RunAsync(new[] { "admin", "nobody@example.com" }, Options, new StringWriter()));
        Assert.False(new AccountDirectory(Options).AnyAdmin());
    }

    [Fact]
    public void The_secret_is_not_in_the_accounts_list_of_the_overview_but_is_kept_in_the_accounts_file()
    {
        var accounts = new AccountDirectory(Options);
        accounts.Add(Boss, Password);
        var secret = accounts.MakeAdmin(Boss)!;
        Assert.Contains(secret, File.ReadAllText(Path.Combine(Dir, "accounts.json")));   // the service has to be able to check a code
        Assert.True(accounts.List().Single().IsAdmin);
    }

    // ---- deleting a code ----

    [Fact]
    public async Task A_code_is_deleted_after_a_page_that_says_what_that_does()
    {
        var admin = NewAdmin();
        var codes = new SignupCodes(Options);
        var (code, info) = codes.Create("book club", null, null);
        var (keep, keepInfo) = codes.Create("family", null, null);
        var (cookie, csrf, overview) = await Open(admin);
        Assert.Contains($"/admin/codes/delete?id={info.Id}", overview);

        var ask = await Get(admin.Client, $"/admin/codes/delete?id={info.Id}", cookie);
        Assert.Equal(HttpStatusCode.OK, ask.StatusCode);
        var page = await ask.Content.ReadAsStringAsync();
        Assert.Contains("forgets who signed up with it", page);
        Assert.Contains("the link stops working", page);
        Assert.NotNull(codes.Check(code));                                // asking deleted nothing

        var done = await Form(admin.Client, "/admin/codes/delete", new() { ["csrf"] = csrf, ["id"] = info.Id }, cookie);
        Assert.Equal(HttpStatusCode.Redirect, done.StatusCode);
        Assert.Null(codes.Check(code));
        Assert.NotNull(codes.Check(keep));
        Assert.DoesNotContain(codes.List(), c => c.Id == info.Id);
        Assert.Contains(codes.List(), c => c.Id == keepInfo.Id);

        Assert.Equal(HttpStatusCode.NotFound, (await Form(admin.Client, "/admin/codes/delete", new() { ["csrf"] = csrf, ["id"] = info.Id }, cookie)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Get(admin.Client, "/admin/codes/delete?id=nothing", cookie)).StatusCode);
    }

    [Fact]
    public async Task Deleting_a_code_needs_the_session_and_its_token()
    {
        var admin = NewAdmin();
        var (code, info) = new SignupCodes(Options).Create("", null, null);
        var (cookie, csrf, _) = await Open(admin);
        Assert.Equal(HttpStatusCode.Forbidden, (await Form(admin.Client, "/admin/codes/delete", new() { ["id"] = info.Id }, cookie)).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await Form(admin.Client, "/admin/codes/delete", new() { ["csrf"] = csrf, ["id"] = info.Id })).StatusCode);
        Assert.Equal("/admin/login", (await Get(admin.Client, $"/admin/codes/delete?id={info.Id}")).Headers.Location!.OriginalString);
        Assert.NotNull(new SignupCodes(Options).Check(code));
    }

    // ---- payments ----

    private (Admin Admin, FakeBilling Fake) NewBillingAdmin()
    {
        var accounts = new AccountDirectory(Options);
        Assert.True(accounts.Add(Boss, Password));
        var secret = accounts.MakeAdmin(Boss)!;
        var fake = new FakeBilling();
        var client = Start(new() { ["Billing:Provider"] = "fake" }, services => services.AddSingleton<IBillingProvider>(fake));
        return (new Admin(client, secret, Boss), fake);
    }

    private static Subscription Paid(string account, string id = "sub_1", SubscriptionStatus status = SubscriptionStatus.Active, int endsInDays = 20) =>
        new(account, "fake", "cus_" + id, id, "cloud", status, DateTimeOffset.UtcNow.AddDays(endsInDays), DateTimeOffset.UtcNow);

    [Fact]
    public async Task With_billing_off_the_portal_shows_nothing_about_payments_and_its_payment_actions_do_not_exist()
    {
        var admin = NewAdmin();
        var (cookie, csrf, html) = await Open(admin);
        Assert.DoesNotContain("Payments", html);
        Assert.DoesNotContain("<th>Plan</th>", html);
        foreach (var url in new[] { "/admin/accounts/pass", "/admin/accounts/setup-link", "/admin/accounts/subscription/check", "/admin/accounts/subscription/end" })
            Assert.Equal(HttpStatusCode.NotFound, (await Form(admin.Client, url, new() { ["csrf"] = csrf, ["name"] = Boss, ["pass"] = "none", ["when"] = "now" }, cookie)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Get(admin.Client, "/admin/accounts/subscription?name=boss%40example.com", cookie)).StatusCode);
    }

    [Fact]
    public async Task With_billing_on_each_account_shows_where_it_stands_and_the_payments_card_counts_them()
    {
        var (admin, _) = NewBillingAdmin();
        var accounts = new AccountDirectory(Options);
        var subscriptions = new SubscriptionStore(Options);
        accounts.Add("paying@example.com", Password); accounts.SetPass("paying@example.com", AccountPlan.None);
        subscriptions.Upsert(Paid("paying@example.com"));
        accounts.Add("gone@example.com", Password); accounts.SetPass("gone@example.com", AccountPlan.None);
        subscriptions.Upsert(Paid("gone@example.com", "sub_old", SubscriptionStatus.Ended, -40));
        accounts.StartPending("waiting@example.com", TimeSpan.FromHours(24));
        accounts.StartPending("lost@example.com", TimeSpan.FromHours(24));
        subscriptions.Upsert(Paid("lost@example.com", "sub_lost"));

        var (_, _, html) = await Open(admin);
        Assert.Contains("<th>Plan</th>", html);
        Assert.Contains("Lifetime", html);
        Assert.Contains("Subscribed", html);
        Assert.Contains("Lapsed", html);
        Assert.Contains("Waiting for payment", html);
        Assert.Contains("Paid, no password yet", html);
        Assert.Contains("1 lifetime", html);          // the admin: the others were given no pass
        Assert.Contains("1 subscribed", html);
        Assert.Contains("1 lapsed", html);
        Assert.Contains("1 waiting for payment", html);
        Assert.Contains("1 paid, no password yet", html);
        Assert.Contains("New sign-in link", html);
    }

    [Fact]
    public async Task A_new_sign_in_link_is_made_for_someone_who_paid_and_lost_theirs_and_for_nobody_else()
    {
        var (admin, _) = NewBillingAdmin();
        var accounts = new AccountDirectory(Options);
        var subscriptions = new SubscriptionStore(Options);
        var first = accounts.StartPending("lost@example.com", TimeSpan.FromHours(24))!;
        accounts.StartPending("unpaid@example.com", TimeSpan.FromHours(24));
        subscriptions.Upsert(Paid("lost@example.com"));
        var (cookie, csrf, _) = await Open(admin);

        var made = await Form(admin.Client, "/admin/accounts/setup-link", new() { ["csrf"] = csrf, ["name"] = "lost@example.com" }, cookie);
        Assert.Equal(HttpStatusCode.OK, made.StatusCode);
        var link = Regex.Match(await made.Content.ReadAsStringAsync(), "(https?://[^<\\s]*?/billing/done\\?t=set_[A-Za-z0-9_%-]+)").Groups[1].Value;
        Assert.NotEmpty(link);
        Assert.Null(new AccountDirectory(Options).SetupFor(first));
        Assert.Equal("lost@example.com", new AccountDirectory(Options).SetupFor(System.Web.HttpUtility.ParseQueryString(new Uri(link).Query)["t"]));

        Assert.Equal(HttpStatusCode.Conflict, (await Form(admin.Client, "/admin/accounts/setup-link", new() { ["csrf"] = csrf, ["name"] = "unpaid@example.com" }, cookie)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Form(admin.Client, "/admin/accounts/setup-link", new() { ["csrf"] = csrf, ["name"] = Boss }, cookie)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Form(admin.Client, "/admin/accounts/setup-link", new() { ["csrf"] = csrf, ["name"] = "nobody@example.com" }, cookie)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Form(admin.Client, "/admin/accounts/setup-link", new() { ["csrf"] = "wrong", ["name"] = "lost@example.com" }, cookie)).StatusCode);
    }

    [Fact]
    public async Task The_pass_is_given_and_taken_away_from_the_portal_and_it_says_what_is_still_being_charged()
    {
        var (admin, _) = NewBillingAdmin();
        var accounts = new AccountDirectory(Options);
        accounts.Add("sam@example.com", Password);
        new SubscriptionStore(Options).Upsert(Paid("sam@example.com"));
        var (cookie, csrf, _) = await Open(admin);

        var none = await Form(admin.Client, "/admin/accounts/pass", new() { ["csrf"] = csrf, ["name"] = "sam@example.com", ["pass"] = "none" }, cookie);
        Assert.Equal(HttpStatusCode.OK, none.StatusCode);
        Assert.Equal(AccountPlan.None, new AccountDirectory(Options).PassOf("sam@example.com"));

        var lifetime = await Form(admin.Client, "/admin/accounts/pass", new() { ["csrf"] = csrf, ["name"] = "sam@example.com", ["pass"] = "lifetime" }, cookie);
        Assert.Equal(AccountPlan.Lifetime, new AccountDirectory(Options).PassOf("sam@example.com"));
        Assert.Contains("still has 1 subscription", await lifetime.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.NotFound, (await Form(admin.Client, "/admin/accounts/pass", new() { ["csrf"] = csrf, ["name"] = "sam@example.com", ["pass"] = "gold" }, cookie)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Form(admin.Client, "/admin/accounts/pass", new() { ["csrf"] = csrf, ["name"] = "nobody@example.com", ["pass"] = "none" }, cookie)).StatusCode);
    }

    [Fact]
    public async Task A_subscription_is_ended_now_or_at_the_end_of_the_period_after_asking_and_the_stored_state_follows_the_provider()
    {
        var (admin, fake) = NewBillingAdmin();
        var accounts = new AccountDirectory(Options);
        var subscriptions = new SubscriptionStore(Options);
        accounts.Add("sam@example.com", Password); accounts.SetPass("sam@example.com", AccountPlan.None);
        var reference = subscriptions.BillingIdOf("sam@example.com");
        fake.States["sub_1"] = new SubscriptionState("cus_sub_1", "sub_1", reference, "cloud", SubscriptionStatus.Active, DateTimeOffset.UtcNow.AddDays(20), false);
        subscriptions.Upsert(Paid("sam@example.com"));
        var (cookie, csrf, html) = await Open(admin);
        Assert.Contains("End subscription", html);

        var ask = await Get(admin.Client, "/admin/accounts/subscription?name=sam%40example.com", cookie);
        Assert.Equal(HttpStatusCode.OK, ask.StatusCode);
        Assert.Contains("End it now", await ask.Content.ReadAsStringAsync());
        Assert.Empty(fake.Cancelled);   // asking changes nothing

        // At the end of the period: the provider is told, and it now says "ends with the period".
        fake.States["sub_1"] = fake.States["sub_1"] with { CancelAtPeriodEnd = true };
        Assert.Equal(HttpStatusCode.OK, (await Form(admin.Client, "/admin/accounts/subscription/end", new() { ["csrf"] = csrf, ["name"] = "sam@example.com", ["when"] = "period" }, cookie)).StatusCode);
        Assert.Equal(("sub_1", false), Assert.Single(fake.Cancelled));
        Assert.Equal(SubscriptionStatus.Canceling, new SubscriptionStore(Options).Of("sam@example.com").Single().Status);   // what the provider said back: renews no more

        // Now: the provider ends it and the service looks.
        Assert.Equal(HttpStatusCode.OK, (await Form(admin.Client, "/admin/accounts/subscription/end", new() { ["csrf"] = csrf, ["name"] = "sam@example.com", ["when"] = "now" }, cookie)).StatusCode);
        Assert.Equal(("sub_1", true), fake.Cancelled.Last());
        Assert.Equal(SubscriptionStatus.Ended, new SubscriptionStore(Options).Of("sam@example.com").Single().Status);

        // A provider that cannot be reached: said, not pretended.
        fake.Down = true;
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await Form(admin.Client, "/admin/accounts/subscription/end", new() { ["csrf"] = csrf, ["name"] = "sam@example.com", ["when"] = "now" }, cookie)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Form(admin.Client, "/admin/accounts/subscription/end", new() { ["csrf"] = csrf, ["name"] = "sam@example.com", ["when"] = "sometime" }, cookie)).StatusCode);
    }

    [Fact]
    public async Task Checking_an_account_with_the_provider_stores_what_it_says_and_says_when_it_cannot_be_asked()
    {
        var (admin, fake) = NewBillingAdmin();
        var accounts = new AccountDirectory(Options);
        var subscriptions = new SubscriptionStore(Options);
        accounts.Add("sam@example.com", Password); accounts.SetPass("sam@example.com", AccountPlan.None);
        var reference = subscriptions.BillingIdOf("sam@example.com");
        subscriptions.Upsert(Paid("sam@example.com"));
        fake.States["sub_1"] = new SubscriptionState("cus_sub_1", "sub_1", reference, "cloud", SubscriptionStatus.Ended, DateTimeOffset.UtcNow.AddDays(-1), false);
        var (cookie, csrf, _) = await Open(admin);

        var ok = await Form(admin.Client, "/admin/accounts/subscription/check", new() { ["csrf"] = csrf, ["name"] = "sam@example.com" }, cookie);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Contains("lapsed", await ok.Content.ReadAsStringAsync());
        Assert.Equal(SubscriptionStatus.Ended, new SubscriptionStore(Options).Of("sam@example.com").Single().Status);

        fake.Down = true;
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await Form(admin.Client, "/admin/accounts/subscription/check", new() { ["csrf"] = csrf, ["name"] = "sam@example.com" }, cookie)).StatusCode);
    }
}
