using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Yaparr.Cloud;

namespace Yaparr.Cloud.Tests;

public sealed class SignupTests : ServiceTest
{
    private static Task<HttpResponseMessage> SignUp(HttpClient client, string? code, string email) =>
        client.PostAsync("/signup", new FormUrlEncodedContent(new Dictionary<string, string> { ["code"] = code ?? "", ["email"] = email }));

    private static Task<HttpResponseMessage> Setup(HttpClient client, string token, string password, string? again = null) =>
        client.PostAsync("/account/setup", new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = token, ["password"] = password, ["again"] = again ?? password }));

    private static string TokenIn(string html)
    {
        var match = Regex.Match(html, "name=\"token\" value=\"(set_[A-Za-z0-9_-]+)\"");
        Assert.True(match.Success, "the page has a setup token");
        return match.Groups[1].Value;
    }

    private string NewCode(string note = "", int? uses = null, TimeSpan? lifetime = null) => new SignupCodes(Options).Create(note, uses, lifetime).Code;

    // ---- the codes ----

    [Fact]
    public void A_code_is_long_random_and_only_its_hash_is_kept()
    {
        var codes = new SignupCodes(Options);
        var (code, info) = codes.Create("For the book club", null, null);
        Assert.Matches("^[A-HJ-NP-Z2-9]{4}(-[A-HJ-NP-Z2-9]{4}){3}$", code);
        Assert.NotEqual(code, codes.Create("", null, null).Code);

        var file = File.ReadAllText(Path.Combine(Dir, "codes.json"));
        Assert.DoesNotContain(code, file);
        Assert.DoesNotContain(code.Replace("-", ""), file);
        Assert.Contains(info.Id, file);
        Assert.Equal("For the book club", codes.List().Single(c => c.Id == info.Id).Note);
    }

    [Theory]
    [InlineData("K7QD-M3XF-9PLR-2WTE", "K7QDM3XF9PLR2WTE")]
    [InlineData("k7qd m3xf 9plr 2wte", "K7QDM3XF9PLR2WTE")]
    [InlineData("  K7QDM3XF9PLR2WTE ", "K7QDM3XF9PLR2WTE")]
    public void A_code_is_understood_however_it_is_typed(string typed, string normal) => Assert.Equal(normal, SignupCodes.Normalize(typed));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("K7QD-M3XF-9PLR")]
    [InlineData("K7QD-M3XF-9PLR-2WTE-AAAA")]
    [InlineData("K7QD-M3XF-9PLR-2WT0")]     // a zero is not in a code
    [InlineData("'; drop table codes; --")]
    public void Something_that_is_not_a_code_is_refused(string? typed)
    {
        Assert.Null(SignupCodes.Normalize(typed));
        Assert.Null(new SignupCodes(Options).Check(typed));
    }

    [Fact]
    public void One_code_can_be_used_by_many_people_until_it_is_revoked()
    {
        var codes = new SignupCodes(Options);
        var (code, info) = codes.Create("", null, null);

        // Any number of people, however it is typed; the same person twice is counted once.
        for (int i = 0; i < 25; i++) Assert.Equal(info.Id, codes.Use(code.ToLowerInvariant().Replace("-", " "), $"person{i}@example.com"));
        Assert.Equal(info.Id, codes.Use(code, "person3@example.com"));
        Assert.Equal(25, codes.List().Single().Uses.Count);
        Assert.Equal(info.Id, codes.Check(code));

        Assert.True(codes.Revoke(info.Id));
        Assert.Null(codes.Check(code));
        Assert.Null(codes.Use(code, "late@example.com"));
        Assert.Null(codes.Use(code, "person3@example.com"));       // even someone who came before cannot come back for more
        Assert.Equal(25, codes.List().Single().Uses.Count);
        Assert.False(codes.Revoke("no-such-id"));
    }

    [Fact]
    public void A_code_with_a_limit_stops_when_it_is_used_up_and_one_with_a_time_stops_when_it_ends()
    {
        var now = DateTimeOffset.UtcNow;
        var codes = new SignupCodes(Options, () => now);
        var (limited, _) = codes.Create("", 3, null);
        foreach (var n in new[] { "a", "b", "c" }) Assert.NotNull(codes.Use(limited, n + "@example.com"));
        Assert.Null(codes.Check(limited));
        Assert.Null(codes.Use(limited, "d@example.com"));
        Assert.NotNull(codes.Use(limited, "a@example.com"));        // one of the three coming back to finish is not a fourth

        var (timed, info) = codes.Create("", null, TimeSpan.FromDays(7));
        Assert.NotNull(codes.Check(timed));
        now = now.AddDays(8);
        Assert.Null(codes.Check(timed));
        Assert.Equal("expired", codes.List().Single(c => c.Id == info.Id).Problem(now));
    }

    [Fact]
    public void The_list_is_read_again_when_another_program_changes_it()
    {
        var running = new SignupCodes(Options);
        Assert.Empty(running.List());
        var (code, _) = new SignupCodes(Options).Create("", null, null);   // the admin command is another process
        Assert.NotNull(running.Check(code));
    }

    // ---- signing up in the browser ----

    [Fact]
    public async Task A_friend_signs_up_with_a_code_chooses_a_password_and_can_then_sign_in()
    {
        var client = Start();
        var code = NewCode("Sam");

        var page = await SignUp(client, code, "  Sam@Example.COM ");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("sam@example.com", html);
        Assert.Contains("lifetime pass", html);
        Assert.Equal("no-store", page.Headers.CacheControl!.ToString());
        Assert.DoesNotContain(code, html);                                              // the code is not echoed back
        var token = TokenIn(html);

        // The account exists with a lifetime pass, but cannot sign in until a password is chosen.
        var accounts = new AccountDirectory(Options);
        var entry = accounts.List().Single();
        Assert.Equal("sam@example.com", entry.Name);
        Assert.Equal("lifetime", entry.Pass);
        Assert.False(entry.HasPassword);
        Assert.NotNull(entry.SignupCode);
        Assert.False(accounts.CheckPassword("sam@example.com", ""));

        var done = await Setup(client, token, "a good long password");
        Assert.Equal(HttpStatusCode.OK, done.StatusCode);
        var welcome = await done.Content.ReadAsStringAsync();
        Assert.Contains("You&#39;re in", welcome);
        Assert.Contains("Backup server", welcome);
        Assert.True(accounts.CheckPassword("sam@example.com", "a good long password"));
        Assert.Equal("lifetime", accounts.List().Single().Pass);

        // And the password signs in to the server as the browser sign-in does.
        Assert.DoesNotContain("a good long password", File.ReadAllText(Path.Combine(Dir, "accounts.json")));
        Assert.DoesNotContain(code, File.ReadAllText(Path.Combine(Dir, "accounts.json")));
    }

    [Fact]
    public async Task One_code_signs_up_many_friends_who_each_get_their_own_account_and_pass()
    {
        var client = Start();
        var code = NewCode();
        foreach (var name in new[] { "sam", "alex", "kim" })
        {
            var html = await (await SignUp(client, code, name + "@example.com")).Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, (await Setup(client, TokenIn(html), name + " has a long password")).StatusCode);
        }
        var accounts = new AccountDirectory(Options);
        Assert.Equal(new[] { "alex@example.com", "kim@example.com", "sam@example.com" }, accounts.List().Select(a => a.Name).ToArray());
        Assert.All(accounts.List(), a => Assert.Equal("lifetime", a.Pass));
        Assert.Single(accounts.List().Select(a => a.SignupCode).Distinct());
        Assert.Equal(3, new SignupCodes(Options).List().Single().Uses.Count);
    }

    [Fact]
    public async Task A_code_that_does_not_work_gets_the_same_answer_whatever_the_reason()
    {
        var client = Start();
        var revoked = new SignupCodes(Options);
        var (revokedCode, revokedInfo) = revoked.Create("", null, null);
        revoked.Revoke(revokedInfo.Id);
        var (usedUp, _) = revoked.Create("", 1, null);
        revoked.Use(usedUp, "first@example.com");
        var expired = new SignupCodes(Options, () => DateTimeOffset.UtcNow.AddYears(-1)).Create("", null, TimeSpan.FromDays(1)).Code;

        var answers = new List<string>();
        foreach (var code in new[] { "AAAA-BBBB-CCCC-DDDD", revokedCode, usedUp, expired, "not a code", "" })
        {
            var response = await SignUp(client, code, "new@example.com");
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            answers.Add(await response.Content.ReadAsStringAsync());
        }
        Assert.Single(answers.Distinct());
        Assert.Contains("does not work", answers[0]);
        Assert.Empty(new AccountDirectory(Options).List());
    }

    [Fact]
    public async Task An_email_address_that_does_not_look_right_is_sent_back()
    {
        var client = Start();
        var code = NewCode();
        foreach (var email in new[] { "", "sam", "sam@", "sam smith@example.com", "<script>@example.com" })
            Assert.Equal(HttpStatusCode.BadRequest, (await SignUp(client, code, email)).StatusCode);
        Assert.Empty(new AccountDirectory(Options).List());
        Assert.Empty(new SignupCodes(Options).List().Single().Uses);     // nothing was counted
    }

    [Fact]
    public async Task An_address_that_already_has_an_account_is_told_to_sign_in_and_is_not_counted_again()
    {
        var client = Start();
        var code = NewCode();
        new AccountDirectory(Options).Add("sam@example.com", "a good long password");

        var response = await SignUp(client, code, "sam@example.com");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("already has an account", await response.Content.ReadAsStringAsync());
        Assert.Empty(new SignupCodes(Options).List().Single().Uses);
        Assert.True(new AccountDirectory(Options).CheckPassword("sam@example.com", "a good long password"));   // untouched
    }

    [Fact]
    public async Task Coming_back_before_choosing_a_password_gives_a_new_page_and_the_old_one_stops_working()
    {
        var client = Start();
        var code = NewCode(uses: 1);
        var first = TokenIn(await (await SignUp(client, code, "sam@example.com")).Content.ReadAsStringAsync());
        var second = TokenIn(await (await SignUp(client, code, "sam@example.com")).Content.ReadAsStringAsync());   // the one use is not used twice
        Assert.NotEqual(first, second);

        Assert.Equal(HttpStatusCode.BadRequest, (await Setup(client, first, "a good long password")).StatusCode);
        Assert.False(new AccountDirectory(Options).HasPassword("sam@example.com"));
        Assert.Equal(HttpStatusCode.OK, (await Setup(client, second, "a good long password")).StatusCode);
        Assert.Single(new SignupCodes(Options).List().Single().Uses);

        // Someone else with the one-use code is too late.
        Assert.Equal(HttpStatusCode.Forbidden, (await SignUp(client, code, "alex@example.com")).StatusCode);
    }

    [Fact]
    public async Task A_setup_page_works_once_and_a_bad_password_keeps_the_page()
    {
        var client = Start();
        var token = TokenIn(await (await SignUp(client, NewCode(), "sam@example.com")).Content.ReadAsStringAsync());

        var mismatch = await Setup(client, token, "a good long password", "a different long password");
        Assert.Equal(HttpStatusCode.BadRequest, mismatch.StatusCode);
        Assert.Contains("not the same", await mismatch.Content.ReadAsStringAsync());
        var weak = await Setup(client, token, "short");
        Assert.Equal(HttpStatusCode.BadRequest, weak.StatusCode);
        Assert.Equal(token, TokenIn(await weak.Content.ReadAsStringAsync()));                 // the same page, to try again
        Assert.False(new AccountDirectory(Options).HasPassword("sam@example.com"));

        Assert.Equal(HttpStatusCode.OK, (await Setup(client, token, "a good long password")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Setup(client, token, "another long password")).StatusCode);   // not a second time
        Assert.True(new AccountDirectory(Options).CheckPassword("sam@example.com", "a good long password"));

        Assert.Equal(HttpStatusCode.BadRequest, (await Setup(client, "set_never-issued", "a good long password")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Setup(client, "", "a good long password")).StatusCode);
    }

    [Fact]
    public async Task Guessing_codes_is_stopped_after_a_number_of_tries_even_with_a_good_code()
    {
        var client = Start(new() { ["RequestsPerMinute"] = "1000" });
        var code = NewCode();
        for (int i = 0; i < 8; i++)
            Assert.Equal(HttpStatusCode.Forbidden, (await SignUp(client, $"AAAA-BBBB-CCCC-DD{i}{i}".Replace("0", "E").Replace("1", "F"), "x@example.com")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await SignUp(client, code, "sam@example.com")).StatusCode);
    }

    [Fact]
    public async Task The_pages_carry_nothing_from_the_request_unencoded()
    {
        var client = Start();
        var token = TokenIn(await (await SignUp(client, NewCode(), "a.b+c@example.com")).Content.ReadAsStringAsync());
        var page = await Setup(client, token, "short");
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("frame-ancestors 'none'", page.Headers.GetValues("Content-Security-Policy").Single());
        Assert.DoesNotContain("<script", html);
    }

    // ---- the admin ----

    [Fact]
    public async Task The_admin_makes_a_code_with_a_link_and_sees_who_used_it()
    {
        var options = new ServiceOptions { DataDirectory = Dir, PublicUrl = "https://backup.example.org/", SignupPageUrl = "https://yaparr.com/signup" };
        var output = new StringWriter();
        Assert.Equal(0, await AdminCommands.RunCodesAsync(["add", "--note", "For Sam and Alex"], options, output));
        var text = output.ToString();
        var code = Regex.Match(text, "Made the code ([A-Z2-9-]{19})").Groups[1].Value;
        Assert.NotEmpty(code);
        Assert.Contains("Any number of people", text);
        Assert.Contains($"https://yaparr.com/signup#server=https%3A%2F%2Fbackup.example.org&code={code}", text);

        new SignupCodes(options).Use(code, "sam@example.com");
        new SignupCodes(options).Use(code, "alex@example.com");
        var list = new StringWriter();
        Assert.Equal(0, await AdminCommands.RunCodesAsync(["list"], options, list));
        Assert.Contains("works", list.ToString());
        Assert.Contains("2 signed up", list.ToString());
        Assert.Contains("For Sam and Alex", list.ToString());
        Assert.Contains("sam@example.com", list.ToString());
        Assert.DoesNotContain(code, list.ToString());                       // the code is not kept, so it cannot be listed
    }

    [Fact]
    public async Task The_admin_can_limit_a_code_by_people_and_by_days_and_revoke_it()
    {
        var output = new StringWriter();
        Assert.Equal(0, await AdminCommands.RunCodesAsync(["add", "--uses", "5", "--days", "30"], Options, output));
        Assert.Contains("Up to 5 people", output.ToString());
        Assert.Contains("It works until", output.ToString());
        Assert.Contains("replace <your server address>", output.ToString());   // no PublicUrl set: the link says what to fill in

        var id = new SignupCodes(Options).List().Single().Id;
        var revoked = new StringWriter();
        Assert.Equal(0, await AdminCommands.RunCodesAsync(["revoke", id], Options, revoked));
        var list = new StringWriter();
        await AdminCommands.RunCodesAsync(["list"], Options, list);
        Assert.Contains("revoked", list.ToString());
        Assert.Equal(1, await AdminCommands.RunCodesAsync(["revoke", "nothing"], Options, new StringWriter()));
    }

    [Theory]
    [InlineData("--uses", "0")]
    [InlineData("--uses", "many")]
    [InlineData("--days", "0")]
    [InlineData("--days", "99999")]
    public async Task A_limit_that_makes_no_sense_is_refused_and_makes_no_code(string option, string value)
    {
        Assert.Equal(2, await AdminCommands.RunCodesAsync(["add", option, value], Options, new StringWriter()));
        Assert.Empty(new SignupCodes(Options).List());
        Assert.Equal(2, await AdminCommands.RunCodesAsync(["frobnicate"], Options, new StringWriter()));
        Assert.Equal(2, await AdminCommands.RunCodesAsync([], Options, new StringWriter()));
    }

    [Fact]
    public async Task The_account_list_shows_the_pass_and_the_code_an_account_came_from()
    {
        var client = Start();
        var token = TokenIn(await (await SignUp(client, NewCode(), "sam@example.com")).Content.ReadAsStringAsync());
        var pending = new StringWriter();
        await AdminCommands.RunAsync(["list"], Options, pending);
        Assert.Contains("signed up, has not chosen a password yet", pending.ToString());

        await Setup(client, token, "a good long password");
        await AdminCommands.RunAsync(["add", "alex@example.com", "--password", "a good long password"], Options, new StringWriter());
        var list = new StringWriter();
        await AdminCommands.RunAsync(["list"], Options, list);
        Assert.Contains("lifetime pass (code ", list.ToString());          // sam, from a code
        Assert.Matches(@"alex@example\.com.*lifetime pass  ", list.ToString());   // alex, made by hand
    }

    [Fact]
    public void An_account_from_before_passes_were_recorded_has_a_lifetime_pass()
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(Path.Combine(Dir, "accounts.json"), """{ "users": { "sam@example.com": { "passwordHash": null, "created": "2026-11-10T20:49:55+00:00" } } }""");
        Assert.Equal("lifetime", new AccountDirectory(Options).List().Single().Pass);
    }
}
