using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Yaparr.Cloud;

namespace Yaparr.Cloud.Tests;

/// <summary>The payment side of the admin portal, which the billing plug-in adds (the portal itself is tested with the server).</summary>
public sealed class AdminPaymentsTests : BillingTestBase
{
    private const string Boss = "boss@example.com";

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
