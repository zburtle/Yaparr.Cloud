using System.Buffers.Text;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Yaparr.Cloud;

namespace Yaparr.Cloud.Tests;

/// <summary>
/// A provider that turns billing on in a test and does nothing else: the first step of paid plans (docs/billing.md) is only what an account may do, and nothing in it asks a provider anything.
/// </summary>
internal sealed class FakeBilling(string name = "fake") : IBillingProvider
{
    public string Name => name;

    public Task<Uri> StartCheckoutAsync(CheckoutRequest request, CancellationToken cancel) => throw new NotSupportedException();

    public Task<Uri> PortalAsync(string providerCustomerId, Uri returnTo, CancellationToken cancel) => throw new NotSupportedException();

    public BillingEvent VerifyWebhook(IReadOnlyDictionary<string, string> headers, ReadOnlyMemory<byte> rawBody) => throw new NotSupportedException();

    public Task<SubscriptionState?> GetSubscriptionAsync(string providerSubscriptionId, CancellationToken cancel) => throw new NotSupportedException();

    public Task<IReadOnlyList<SubscriptionState>> FindAsync(string accountRef, CancellationToken cancel) => throw new NotSupportedException();

    public Task CancelAsync(string providerSubscriptionId, bool immediately, CancellationToken cancel) => throw new NotSupportedException();
}

/// <summary>What an account is, from its pass and its subscriptions (nothing but the rules).</summary>
public sealed class AccountPlanTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Grace = TimeSpan.FromDays(3);

    private static Subscription Sub(SubscriptionStatus status, double endsInDays) =>
        new("sam@example.com", "polar", "cus_1", "sub_1", "cloud", status, Now.AddDays(endsInDays), Now);

    private static PlanState Of(string? pass, params Subscription[] subs) => AccountPlan.Of(true, pass, subs, Now, Grace);

    [Fact]
    public void With_billing_off_every_account_is_off_whatever_it_has()
    {
        Assert.Equal(PlanState.Off, AccountPlan.Of(false, "none", Array.Empty<Subscription>(), Now, Grace));
        Assert.Equal(PlanState.Off, AccountPlan.Of(false, "lifetime", new[] { Sub(SubscriptionStatus.Ended, -9) }, Now, Grace));
    }

    [Fact]
    public void The_lifetime_pass_is_never_asked_to_pay_and_nothing_a_provider_says_changes_it()
    {
        Assert.Equal(PlanState.Lifetime, Of("lifetime"));
        Assert.Equal(PlanState.Lifetime, Of("lifetime", Sub(SubscriptionStatus.Ended, -400)));
        Assert.Equal(PlanState.Lifetime, Of("lifetime", Sub(SubscriptionStatus.Active, 20)));
    }

    [Fact]
    public void Without_the_pass_an_account_with_nothing_paid_is_lapsed()
    {
        Assert.Equal(PlanState.Lapsed, Of("none"));
        Assert.Equal(PlanState.Lapsed, Of(null));
        Assert.Equal(PlanState.Lapsed, Of("none", Sub(SubscriptionStatus.Ended, 20)));   // over is over, whatever the date says
    }

    [Theory]
    [InlineData(SubscriptionStatus.Active, 20, PlanState.Subscribed)]
    [InlineData(SubscriptionStatus.Canceling, 20, PlanState.Subscribed)]     // paid until the end of the period
    [InlineData(SubscriptionStatus.PastDue, 1, PlanState.Subscribed)]
    [InlineData(SubscriptionStatus.PastDue, -2, PlanState.Subscribed)]        // inside the grace days
    [InlineData(SubscriptionStatus.Active, -2.9, PlanState.Subscribed)]
    [InlineData(SubscriptionStatus.Active, -3.1, PlanState.Lapsed)]           // past them
    [InlineData(SubscriptionStatus.Canceling, -3.1, PlanState.Lapsed)]
    [InlineData(SubscriptionStatus.PastDue, -10, PlanState.Lapsed)]
    [InlineData(SubscriptionStatus.Ended, 20, PlanState.Lapsed)]
    public void A_subscription_covers_an_account_until_its_period_and_the_grace_days_are_over(SubscriptionStatus status, double endsInDays, PlanState expected) =>
        Assert.Equal(expected, Of("none", Sub(status, endsInDays)));

    [Fact]
    public void Any_one_subscription_that_covers_is_enough_and_the_grace_days_are_a_setting()
    {
        Assert.Equal(PlanState.Subscribed, Of("none", Sub(SubscriptionStatus.Ended, 20), Sub(SubscriptionStatus.Active, 5)));
        Assert.Equal(PlanState.Lapsed, AccountPlan.Of(true, "none", new[] { Sub(SubscriptionStatus.Active, -2) }, Now, TimeSpan.Zero));
        Assert.Equal(PlanState.Subscribed, AccountPlan.Of(true, "none", new[] { Sub(SubscriptionStatus.Active, -2) }, Now, TimeSpan.FromDays(7)));
    }

    [Fact]
    public void The_current_subscription_is_the_one_that_covers_for_longest()
    {
        var short1 = Sub(SubscriptionStatus.Active, 5);
        var long1 = new Subscription("sam@example.com", "polar", "cus_1", "sub_2", "cloud", SubscriptionStatus.Canceling, Now.AddDays(25), Now);
        Assert.Equal("sub_2", AccountPlan.Current(new[] { short1, long1, Sub(SubscriptionStatus.Ended, 90) }, Now, Grace)!.ProviderSubscriptionId);
        Assert.Null(AccountPlan.Current(new[] { Sub(SubscriptionStatus.Ended, 90) }, Now, Grace));
    }

    [Fact]
    public void Only_two_passes_exist()
    {
        Assert.True(AccountPlan.IsPass("lifetime"));
        Assert.True(AccountPlan.IsPass("none"));
        Assert.False(AccountPlan.IsPass("gold"));
        Assert.False(AccountPlan.IsPass(""));
        Assert.False(AccountPlan.IsPass(null));
    }
}

/// <summary>What the service does with an account that has paid, and one that has not (billing on), and nothing different at all when billing is off.</summary>
public sealed class PlanGateTests : ServiceTest
{
    private const string Sam = "sam@example.com";
    private static readonly string BlobId = new('a', 64);
    private static readonly string MailboxId = new('b', 43);

    private HttpClient Run(Action<IServiceCollection>? more = null, Dictionary<string, string>? settings = null)
    {
        var all = new Dictionary<string, string> { ["Billing:Provider"] = "fake" };
        foreach (var (key, value) in settings ?? new()) all[key] = value;
        return Start(all, services =>
        {
            services.AddSingleton<IBillingProvider>(new FakeBilling());
            more?.Invoke(services);
        });
    }

    // An account with no pass, and a device signed in as it.
    private string WithoutPass(string name = Sam)
    {
        var token = Register(name);
        Assert.True(new AccountDirectory(Options).SetPass(name, AccountPlan.None));
        return token;
    }

    private void Paid(string name, double endsInDays, SubscriptionStatus status = SubscriptionStatus.Active, string id = "sub_1") =>
        new SubscriptionStore(Options).Upsert(new Subscription(name, "fake", "cus_1", id, "cloud", status, DateTimeOffset.UtcNow.AddDays(endsInDays), DateTimeOffset.UtcNow));

    private static StringContent Json(object body) => new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    private static async Task<HttpResponseMessage> Call(HttpClient client, string token, HttpMethod method, string path, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.Authorization = new("Bearer", token);
        return await client.SendAsync(request);
    }

    // Every service a subscription pays for, each as a device would call it.
    private static IEnumerable<(string Name, Func<HttpClient, string, Task<HttpResponseMessage>> Call)> Services() => new (string, Func<HttpClient, string, Task<HttpResponseMessage>>)[]
    {
        ("backup save", (c, t) => Send(c, t, HttpMethod.Put, "/v1/backup", Locked(), ifMatch: 0)),
        ("sync pull", (c, t) => Call(c, t, HttpMethod.Get, "/v1/sync")),
        ("sync push", (c, t) => Call(c, t, HttpMethod.Put, "/v1/sync", Json(new { records = new[] { new { kind = "profile", id = "name", clock = "00000000000100000000", device = "phone", deleted = false, body = "AAAA" } } }))),
        ("mailbox read", (c, t) => Call(c, t, HttpMethod.Get, "/v1/mailbox/" + MailboxId)),
        ("mailbox post", (c, t) => Call(c, t, HttpMethod.Post, "/v1/mailbox/" + MailboxId, Json(new { items = new[] { new { id = "m1", body = "AAAA" } } }))),
        ("picture upload", (c, t) => Call(c, t, HttpMethod.Put, "/v1/blob/" + BlobId, new ByteArrayContent(new byte[] { 1, 2, 3 }))),
        ("kept piece upload", (c, t) => Call(c, t, HttpMethod.Put, "/v1/keep/" + BlobId, new ByteArrayContent(new byte[] { 1, 2, 3 }))),
        ("push registration", (c, t) => Call(c, t, HttpMethod.Put, "/v1/push", Json(new { deviceId = "phone", kind = "unifiedpush", endpoint = "https://ntfy.example.org/upabc" }))),
    };

    private static async Task AssertRefused(HttpResponseMessage response, string what)
    {
        Assert.True(response.StatusCode == HttpStatusCode.PaymentRequired, $"{what}: expected 402, got {(int)response.StatusCode}");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("subscription_required", body.GetProperty("code").GetString());
        Assert.Contains("subscription has ended", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task With_billing_off_nothing_is_refused_whatever_the_pass_and_the_plan_says_so()
    {
        var client = Start();
        var token = WithoutPass();

        Assert.Equal(HttpStatusCode.Created, (await Send(client, token, HttpMethod.Put, "/v1/backup", Locked(), ifMatch: 0)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Call(client, token, HttpMethod.Get, "/v1/sync")).StatusCode);
        var plan = await (await Call(client, token, HttpMethod.Get, "/v1/plan")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("off", plan.GetProperty("billing").GetString());
        Assert.False(plan.TryGetProperty("state", out _));
    }

    [Fact]
    public async Task The_lifetime_pass_is_never_refused_and_the_plan_says_so()
    {
        var client = Run();
        var token = Register(Sam);   // an account has the lifetime pass unless it is taken away

        foreach (var service in Services().Take(2))
            Assert.NotEqual(HttpStatusCode.PaymentRequired, (await service.Call(client, token)).StatusCode);
        var plan = await (await Call(client, token, HttpMethod.Get, "/v1/plan")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(("on", "lifetime"), (plan.GetProperty("billing").GetString(), plan.GetProperty("state").GetString()));
        Assert.Equal(JsonValueKind.Null, plan.GetProperty("plan").ValueKind);
        Assert.False(plan.GetProperty("willRenew").GetBoolean());
    }

    [Fact]
    public async Task A_subscribed_account_is_not_refused_and_its_plan_says_what_it_has_and_whether_it_renews()
    {
        var client = Run();
        var token = WithoutPass();
        Paid(Sam, 20);

        Assert.Equal(HttpStatusCode.Created, (await Send(client, token, HttpMethod.Put, "/v1/backup", Locked(), ifMatch: 0)).StatusCode);
        var plan = await (await Call(client, token, HttpMethod.Get, "/v1/plan")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(("subscribed", "cloud", true), (plan.GetProperty("state").GetString(), plan.GetProperty("plan").GetString(), plan.GetProperty("willRenew").GetBoolean()));
        Assert.InRange((plan.GetProperty("endsAt").GetDateTimeOffset() - DateTimeOffset.UtcNow).TotalDays, 19.9, 20.1);

        Paid(Sam, 20, SubscriptionStatus.Canceling);   // cancelled: paid until then, and does not renew
        plan = await (await Call(client, token, HttpMethod.Get, "/v1/plan")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(("subscribed", false), (plan.GetProperty("state").GetString(), plan.GetProperty("willRenew").GetBoolean()));
    }

    [Fact]
    public async Task A_lapsed_account_is_refused_each_service_with_a_402_that_says_why_and_nothing_is_stored()
    {
        var client = Run();
        var token = WithoutPass();
        Paid(Sam, -30, SubscriptionStatus.Ended);

        foreach (var service in Services())
            await AssertRefused(await service.Call(client, token), service.Name);
        Assert.Empty(BackupFiles());
        Assert.Empty(new PushRegistry(Options).List(Sam));
    }

    [Fact]
    public async Task A_lapsed_account_can_still_see_where_it_stands_take_its_data_away_and_delete_things()
    {
        var client = Run();
        var token = WithoutPass();
        Paid(Sam, 20);
        Assert.Equal(HttpStatusCode.Created, (await Send(client, token, HttpMethod.Put, "/v1/backup", Locked(500), ifMatch: 0)).StatusCode);
        Paid(Sam, -30, SubscriptionStatus.Ended);   // it ran out

        var plan = await (await Call(client, token, HttpMethod.Get, "/v1/plan")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(("lapsed", "cloud", false), (plan.GetProperty("state").GetString(), plan.GetProperty("plan").GetString(), plan.GetProperty("willRenew").GetBoolean()));
        Assert.InRange((DateTimeOffset.UtcNow - plan.GetProperty("endsAt").GetDateTimeOffset()).TotalDays, 29.9, 30.1);   // when it ran out

        Assert.Equal(HttpStatusCode.OK, (await Call(client, token, HttpMethod.Get, "/v1/backup/versions")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Call(client, token, HttpMethod.Get, "/v1/backup")).StatusCode);                  // the data is theirs to take
        Assert.Equal(HttpStatusCode.NotFound, (await Call(client, token, HttpMethod.Get, "/v1/keep/" + BlobId)).StatusCode);   // no piece, but not a refusal
        Assert.Equal(HttpStatusCode.OK, (await Call(client, token, HttpMethod.Get, "/v1/devices")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Call(client, token, HttpMethod.Get, "/v1/usage")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Call(client, token, HttpMethod.Get, "/v1/push")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Call(client, token, HttpMethod.Delete, "/v1/push/phone")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Call(client, token, HttpMethod.Delete, "/v1/backup")).StatusCode);          // and to delete
        Assert.Equal(HttpStatusCode.NoContent, (await Call(client, token, HttpMethod.Delete, "/v1/token")).StatusCode);
    }

    [Fact]
    public async Task Nothing_is_removed_when_an_account_lapses_and_renewing_brings_the_services_back_with_no_sign_in()
    {
        var client = Run();
        var token = WithoutPass();
        Paid(Sam, 20);
        var saved = Locked(800);
        Assert.Equal(HttpStatusCode.Created, (await Send(client, token, HttpMethod.Put, "/v1/backup", saved, ifMatch: 0)).StatusCode);

        Paid(Sam, -10, SubscriptionStatus.Ended);
        await AssertRefused(await Send(client, token, HttpMethod.Put, "/v1/backup", Locked(800), ifMatch: 1), "save while lapsed");
        Assert.Equal(saved, await (await Call(client, token, HttpMethod.Get, "/v1/backup")).Content.ReadAsByteArrayAsync());   // still there

        Paid(Sam, 30, id: "sub_2");   // renewed: a new subscription, the same device, the same token
        Assert.Equal(HttpStatusCode.Created, (await Send(client, token, HttpMethod.Put, "/v1/backup", Locked(800), ifMatch: 1)).StatusCode);
    }

    [Fact]
    public async Task A_payment_a_day_late_is_covered_by_the_grace_days_and_one_that_is_later_is_not()
    {
        var client = Run();
        var late = WithoutPass("late@example.com");
        var gone = WithoutPass("gone@example.com");
        Paid("late@example.com", -2, SubscriptionStatus.PastDue, id: "sub_late");
        Paid("gone@example.com", -4, SubscriptionStatus.PastDue, id: "sub_gone");

        Assert.Equal(HttpStatusCode.Created, (await Send(client, late, HttpMethod.Put, "/v1/backup", Locked(), ifMatch: 0)).StatusCode);
        await AssertRefused(await Send(client, gone, HttpMethod.Put, "/v1/backup", Locked(), ifMatch: 0), "four days late");

        // The grace is the owner's to set, and applies to subscriptions already stored.
        var strict = Run(settings: new() { ["Billing:GraceDays"] = "0" });
        await AssertRefused(await Send(strict, late, HttpMethod.Put, "/v1/backup", Locked(), ifMatch: 1), "no grace");
    }

    [Fact]
    public async Task A_lapsed_account_is_not_woken_and_the_sender_is_told_what_it_is_told_for_a_token_that_leads_nowhere_until_it_is_renewed()
    {
        var sender = new FakePushSender();
        var client = Run(services =>
        {
            foreach (var d in services.Where(d => d.ServiceType == typeof(IPushSender)).ToList()) services.Remove(d);
            services.AddSingleton<IPushSender>(sender);
        });
        var token = WithoutPass();
        Paid(Sam, 20);
        var register = await Call(client, token, HttpMethod.Put, "/v1/push", Json(new { deviceId = "phone", kind = "unifiedpush", endpoint = "https://ntfy.example.org/upabc" }));
        Assert.Equal(HttpStatusCode.Created, register.StatusCode);
        var wake = (await register.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsync("/v1/push/wake/" + wake, null)).StatusCode);

        Paid(Sam, -10, SubscriptionStatus.Ended);
        var refused = await client.PostAsync("/v1/push/wake/" + wake, null);
        Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
        Assert.DoesNotContain("subscription", await refused.Content.ReadAsStringAsync());   // nothing about the account
        Assert.Single(sender.Sent);
        Assert.Single(new PushRegistry(Options).List(Sam));   // kept for when it is renewed

        Paid(Sam, 30, id: "sub_2");
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsync("/v1/push/wake/" + wake, null)).StatusCode);
    }

    [Fact]
    public async Task A_lapsed_account_cannot_open_the_gateway()
    {
        var client = Run(settings: new() { ["GatewayEnabled"] = "true", ["GatewayAllowPrivate"] = "true" });
        var token = WithoutPass();
        Paid(Sam, -30, SubscriptionStatus.Ended);

        var socket = LastFactory!.Server.CreateWebSocketClient();
        socket.ConfigureRequest = r => r.Headers["Authorization"] = "Bearer " + token;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => socket.ConnectAsync(new Uri("http://localhost/v1/gateway"), CancellationToken.None));
        Assert.Contains("402", error.Message);

        Paid(Sam, 30, id: "sub_2");
        using var open = await socket.ConnectAsync(new Uri("http://localhost/v1/gateway"), CancellationToken.None);
        Assert.Equal(System.Net.WebSockets.WebSocketState.Open, open.State);
    }

    // ---- signing in ----

    private const string Redirect = "http://127.0.0.1:51234/callback";
    private const string State = "state-0123456789";

    private static (string Verifier, string Challenge) Pkce()
    {
        var verifier = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        return (verifier, Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))));
    }

    private static Task<HttpResponseMessage> PostLogin(HttpClient client, string challenge, string username, string password) =>
        client.PostAsync("/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["redirect_uri"] = Redirect, ["code_challenge"] = challenge, ["state"] = State, ["device"] = "Sam's laptop", ["username"] = username, ["password"] = password,
        }));

    private static string CodeIn(HttpResponseMessage redirect)
    {
        Assert.Equal(HttpStatusCode.Redirect, redirect.StatusCode);
        return System.Web.HttpUtility.ParseQueryString(redirect.Headers.Location!.Query)["code"]!;
    }

    [Fact]
    public async Task A_lapsed_account_with_the_right_password_is_told_why_it_cannot_sign_in_on_a_device_and_gets_no_code()
    {
        var client = Run();
        WithoutPass();
        Paid(Sam, -30, SubscriptionStatus.Ended);
        var (_, challenge) = Pkce();

        var page = await PostLogin(client, challenge, Sam, Password);
        Assert.Equal(HttpStatusCode.PaymentRequired, page.StatusCode);
        Assert.Null(page.Headers.Location);
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("subscription has ended", html);
        Assert.Contains("Nothing has been deleted", html);
        Assert.DoesNotContain("code=", html);
        Assert.Single(new AccountDirectory(Options).DevicesOf(Sam));   // only the one the test signed in to set up

        // A wrong password still says only that it is wrong: the page does not tell a stranger who has an account, or who has lapsed.
        var wrong = await PostLogin(client, challenge, Sam, "another password entirely");
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.DoesNotContain("subscription", await wrong.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_subscribed_account_signs_in_and_one_that_lapses_between_the_browser_and_the_app_is_refused_a_token()
    {
        var client = Run();
        WithoutPass();
        Paid(Sam, 20);
        var (verifier, challenge) = Pkce();

        var code = CodeIn(await PostLogin(client, challenge, Sam, Password));
        Paid(Sam, -30, SubscriptionStatus.Ended);   // it ran out before the app traded its code

        var traded = await client.PostAsJsonAsync("/v1/token", new { code, verifier });
        await AssertRefused(traded, "token for a lapsed account");
        Assert.Single(new AccountDirectory(Options).DevicesOf(Sam));   // still only the one the test signed in to set up: no device was added

        // Renewed, a fresh sign-in works.
        Paid(Sam, 30, id: "sub_2");
        var (verifier2, challenge2) = Pkce();
        var again = await client.PostAsJsonAsync("/v1/token", new { code = CodeIn(await PostLogin(client, challenge2, Sam, Password)), verifier = verifier2 });
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
    }

    [Fact]
    public async Task A_device_that_is_already_signed_in_keeps_its_token_when_the_account_lapses_and_can_sign_itself_out()
    {
        var client = Run();
        var token = WithoutPass();
        Paid(Sam, -30, SubscriptionStatus.Ended);
        Assert.Equal(HttpStatusCode.OK, (await Call(client, token, HttpMethod.Get, "/v1/devices")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Call(client, token, HttpMethod.Delete, "/v1/token")).StatusCode);
        // And now it is out, it is out: it cannot come back in until the account is renewed.
        Assert.Equal(HttpStatusCode.Unauthorized, (await Call(client, token, HttpMethod.Get, "/v1/devices")).StatusCode);
    }

    // ---- the rest ----

    [Fact]
    public void A_provider_name_this_build_has_no_provider_for_stops_the_service_at_once_and_names_the_ones_it_has()
    {
        var failure = Record.Exception(() => Start(new() { ["Billing:Provider"] = "polar" }, services => services.AddSingleton<IBillingProvider>(new FakeBilling())).GetAsync("/healthz").GetAwaiter().GetResult());
        Assert.NotNull(failure);
        var all = new List<string>();
        for (var e = failure; e != null; e = e.InnerException) all.Add(e.Message);
        var message = string.Join(" | ", all);
        Assert.Contains("\"polar\"", message);
        Assert.Contains("no such billing provider", message);
        Assert.Contains("fake", message);
    }

    [Fact]
    public async Task A_provider_is_found_whatever_the_case_of_its_name_in_the_settings()
    {
        var client = Start(new() { ["Billing:Provider"] = "FAKE" }, services => services.AddSingleton<IBillingProvider>(new FakeBilling()));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/healthz")).StatusCode);
    }

    [Fact]
    public async Task Deleting_an_account_removes_what_it_paid_for_and_the_id_a_provider_knew_it_by()
    {
        var client = Run();
        Register(Sam);
        var store = new SubscriptionStore(Options);
        Paid(Sam, 20);
        var billingId = store.BillingIdOf(Sam);

        var deleted = await client.PostAsync("/account/delete", new FormUrlEncodedContent(new Dictionary<string, string> { ["username"] = Sam, ["current"] = Password, ["confirm"] = "delete my account" }));
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        var after = new SubscriptionStore(Options);
        Assert.Empty(after.Of(Sam));
        Assert.Null(after.AccountOf(billingId));
    }

    [Fact]
    public async Task The_admin_command_gives_and_takes_the_pass_and_says_what_is_still_being_paid_for()
    {
        var output = new StringWriter();
        await AdminCommands.RunAsync(["add", Sam, "--password", Password], Options, new StringWriter());
        Assert.Equal(AccountPlan.Lifetime, new AccountDirectory(Options).PassOf(Sam));

        Assert.Equal(0, await AdminCommands.RunAsync(["pass", "Sam@Example.com", "none"], Options, output));
        Assert.Equal("none", new AccountDirectory(Options).PassOf(Sam));
        Assert.Contains("no longer has the lifetime pass", output.ToString());

        // Something is being paid for: giving the pass says to stop it where it is charged.
        Paid(Sam, 20);
        output = new StringWriter();
        Assert.Equal(0, await AdminCommands.RunAsync(["pass", Sam, "lifetime"], Options, output));
        Assert.Equal(AccountPlan.Lifetime, new AccountDirectory(Options).PassOf(Sam));
        Assert.Contains("never asked to pay", output.ToString());
        Assert.Contains("1 subscription at fake: cancel it there", output.ToString());

        Assert.Equal(2, await AdminCommands.RunAsync(["pass", Sam, "gold"], Options, new StringWriter()));
        Assert.Equal(2, await AdminCommands.RunAsync(["pass", Sam], Options, new StringWriter()));
        Assert.Equal(1, await AdminCommands.RunAsync(["pass", "nobody@example.com", "none"], Options, new StringWriter()));
        Assert.Equal(AccountPlan.Lifetime, new AccountDirectory(Options).PassOf(Sam));
    }

    [Fact]
    public async Task The_account_list_shows_where_each_account_stands_only_when_billing_is_on()
    {
        await AdminCommands.RunAsync(["add", Sam, "--password", Password], Options, new StringWriter());
        await AdminCommands.RunAsync(["add", "kim@example.com", "--password", Password], Options, new StringWriter());
        new AccountDirectory(Options).SetPass("kim@example.com", AccountPlan.None);

        var off = new StringWriter();
        await AdminCommands.RunAsync(["list"], Options, off);
        Assert.DoesNotContain("lapsed", off.ToString());

        var on = new StringWriter();
        var billing = Options;
        billing.Billing.Provider = "fake";
        await AdminCommands.RunAsync(["list"], billing, on);
        var lines = on.ToString().Split('\n');
        Assert.Contains("lifetime pass, lifetime", lines.Single(l => l.StartsWith(Sam)));
        Assert.Contains("none pass, lapsed", lines.Single(l => l.StartsWith("kim@example.com")));
    }
}
