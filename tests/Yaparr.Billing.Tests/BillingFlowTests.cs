using System.Buffers.Text;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Yaparr.Cloud;

namespace Yaparr.Cloud.Tests;

/// <summary>What the service does with a provider, with no web server in the way: webhooks, reconciling, unpaid sign-ups, cancelling, and sending people to pay.</summary>
public sealed class BillingServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "yaparr-billing-" + Guid.NewGuid().ToString("N"));
    private readonly FakeBilling _fake = new();
    private DateTimeOffset _now = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
    private readonly ServiceOptions _options;
    private readonly AccountDirectory _accounts;
    private readonly SubscriptionStore _store;
    private readonly BillingService _billing;

    public BillingServiceTests()
    {
        _options = new ServiceOptions { DataDirectory = _dir, Billing = { Provider = "fake" } };
        _accounts = new AccountDirectory(_options);
        _store = new SubscriptionStore(_options);
        _billing = new BillingService(_options, _accounts, _store, new[] { _fake }, NullLoggerFactory.Instance, () => _now);
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static readonly IReadOnlyDictionary<string, string> Signed = new Dictionary<string, string> { ["x-fake-signature"] = "valid" };

    private static byte[] Event(string id, string? subscription = "sub_1") =>
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(subscription == null ? new { id } : (object)new { id, subscription }));

    private string Sam(string name = "sam@example.com")
    {
        _accounts.Add(name, "correct horse battery");
        return _store.BillingIdOf(name);
    }

    private SubscriptionState State(string billingId, SubscriptionStatus status = SubscriptionStatus.Active, int endsInDays = 30, bool cancelAtEnd = false, string id = "sub_1") =>
        new("cus_1", id, billingId, "cloud", status, _now.AddDays(endsInDays), cancelAtEnd);

    // ---- webhooks ----

    [Fact]
    public async Task A_webhook_makes_the_service_ask_the_provider_and_store_what_it_says()
    {
        var id = Sam();
        _fake.States["sub_1"] = State(id);

        Assert.Equal(WebhookOutcome.Handled, await _billing.HandleWebhookAsync("fake", Signed, Event("evt_1"), default));
        var stored = Assert.Single(_store.Of("sam@example.com"));
        Assert.Equal(("fake", "cus_1", "sub_1", "cloud", SubscriptionStatus.Active, _now.AddDays(30), _now),
            (stored.Provider, stored.ProviderCustomerId, stored.ProviderSubscriptionId, stored.Plan, stored.Status, stored.PeriodEnd, stored.UpdatedAt));
        Assert.Equal(new[] { "sub_1" }, _fake.Asked);
    }

    [Fact]
    public async Task A_refund_ends_the_subscription_at_the_provider_and_then_the_service_looks_and_finds_it_over()
    {
        var id = Sam();
        _accounts.SetPass("sam@example.com", AccountPlan.None);
        _fake.States["sub_1"] = State(id);
        await _billing.HandleWebhookAsync("fake", Signed, Event("evt_1"), default);
        Assert.Equal(PlanState.Subscribed, new PlanGate(_options, _accounts, _store, () => _now).StateOf("sam@example.com"));

        var refund = Encoding.UTF8.GetBytes("""{"id":"evt_2","subscription":"sub_1","end":true}""");
        Assert.Equal(WebhookOutcome.Handled, await _billing.HandleWebhookAsync("fake", Signed, refund, default));

        Assert.Equal(("sub_1", true), Assert.Single(_fake.Cancelled));
        Assert.Equal(SubscriptionStatus.Ended, _store.Of("sam@example.com").Single().Status);
        Assert.Equal(PlanState.Lapsed, new PlanGate(_options, _accounts, _store, () => _now).StateOf("sam@example.com"));
    }

    [Fact]
    public async Task A_refund_that_cannot_be_acted_on_now_is_tried_again_and_not_taken_for_a_repeat()
    {
        var id = Sam();
        _fake.States["sub_1"] = State(id);
        var refund = Encoding.UTF8.GetBytes("""{"id":"evt_9","subscription":"sub_1","end":true}""");

        _fake.Down = true;
        Assert.Equal(WebhookOutcome.TryAgain, await _billing.HandleWebhookAsync("fake", Signed, refund, default));
        _fake.Down = false;
        Assert.Equal(WebhookOutcome.Handled, await _billing.HandleWebhookAsync("fake", Signed, refund, default));
        Assert.Equal(SubscriptionStatus.Ended, _store.Of("sam@example.com").Single().Status);
    }

    [Fact]
    public async Task An_ordinary_event_never_ends_anything()
    {
        var id = Sam();
        _fake.States["sub_1"] = State(id);
        await _billing.HandleWebhookAsync("fake", Signed, Event("evt_1"), default);
        Assert.Empty(_fake.Cancelled);
    }

    [Fact]
    public async Task Cancelling_at_the_end_of_the_period_is_a_status_here_and_an_ended_one_gives_nothing()
    {
        var id = Sam();
        _fake.States["sub_1"] = State(id, cancelAtEnd: true);
        await _billing.HandleWebhookAsync("fake", Signed, Event("evt_1"), default);
        Assert.Equal(SubscriptionStatus.Canceling, _store.Of("sam@example.com").Single().Status);

        _fake.States["sub_1"] = State(id, SubscriptionStatus.Ended, endsInDays: 20);
        await _billing.HandleWebhookAsync("fake", Signed, Event("evt_2"), default);
        var ended = _store.Of("sam@example.com").Single();
        Assert.Equal(SubscriptionStatus.Ended, ended.Status);
        Assert.False(AccountPlan.Covers(ended, _now, TimeSpan.FromDays(3)));
    }

    [Fact]
    public async Task A_webhook_that_is_not_signed_stores_nothing_and_is_not_remembered()
    {
        var id = Sam();
        _fake.States["sub_1"] = State(id);
        Assert.Equal(WebhookOutcome.BadSignature, await _billing.HandleWebhookAsync("fake", new Dictionary<string, string> { ["x-fake-signature"] = "forged" }, Event("evt_1"), default));
        Assert.Equal(WebhookOutcome.BadSignature, await _billing.HandleWebhookAsync("fake", new Dictionary<string, string>(), Event("evt_1"), default));
        Assert.Empty(_store.Of("sam@example.com"));
        Assert.Empty(_fake.Asked);
        // The real one, with the same id, is still handled: a forgery could not use the id up.
        Assert.Equal(WebhookOutcome.Handled, await _billing.HandleWebhookAsync("fake", Signed, Event("evt_1"), default));
    }

    [Fact]
    public async Task The_same_event_again_is_not_handled_again_and_asks_the_provider_nothing()
    {
        var id = Sam();
        _fake.States["sub_1"] = State(id);
        Assert.Equal(WebhookOutcome.Handled, await _billing.HandleWebhookAsync("fake", Signed, Event("evt_1"), default));
        Assert.Equal(WebhookOutcome.Duplicate, await _billing.HandleWebhookAsync("fake", Signed, Event("evt_1"), default));
        Assert.Single(_fake.Asked);
    }

    [Fact]
    public async Task An_event_that_names_no_subscription_or_one_that_is_nobodys_or_unknown_is_ignored()
    {
        var id = Sam();
        Assert.Equal(WebhookOutcome.Ignored, await _billing.HandleWebhookAsync("fake", Signed, Event("evt_1", null), default));   // names none
        Assert.Equal(WebhookOutcome.Ignored, await _billing.HandleWebhookAsync("fake", Signed, Event("evt_2", "sub_unknown"), default));   // the provider does not know it

        _fake.States["sub_9"] = State("not-a-reference-we-made", id: "sub_9");   // a reference this service never made
        Assert.Equal(WebhookOutcome.Ignored, await _billing.HandleWebhookAsync("fake", Signed, Event("evt_3", "sub_9"), default));

        _fake.States["sub_1"] = State(id);
        _accounts.Remove("sam@example.com");   // its account is gone
        Assert.Equal(WebhookOutcome.Ignored, await _billing.HandleWebhookAsync("fake", Signed, Event("evt_4"), default));
        Assert.Empty(_store.Of("sam@example.com"));
    }

    [Fact]
    public async Task A_provider_that_cannot_be_asked_gets_the_event_back_to_try_again_and_the_retry_is_handled()
    {
        var id = Sam();
        _fake.States["sub_1"] = State(id);
        _fake.Down = true;
        Assert.Equal(WebhookOutcome.TryAgain, await _billing.HandleWebhookAsync("fake", Signed, Event("evt_1"), default));
        Assert.Empty(_store.Of("sam@example.com"));

        _fake.Down = false;
        Assert.Equal(WebhookOutcome.Handled, await _billing.HandleWebhookAsync("fake", Signed, Event("evt_1"), default));   // not taken for a repeat
        Assert.Single(_store.Of("sam@example.com"));
    }

    [Fact]
    public async Task A_late_event_cannot_undo_what_the_provider_now_says_because_the_state_is_always_asked_for_afresh()
    {
        var id = Sam();
        _fake.States["sub_1"] = State(id, SubscriptionStatus.Ended, endsInDays: -1);   // over, as the provider now says
        // The events arrive in the wrong order: the "created" and "paid" ones last.
        await _billing.HandleWebhookAsync("fake", Signed, Event("evt_revoked"), default);
        await _billing.HandleWebhookAsync("fake", Signed, Event("evt_paid"), default);
        await _billing.HandleWebhookAsync("fake", Signed, Event("evt_created"), default);
        Assert.Equal(SubscriptionStatus.Ended, Assert.Single(_store.Of("sam@example.com")).Status);
    }

    [Fact]
    public async Task A_provider_name_that_is_not_one_is_not_handled()
    {
        Sam();
        Assert.Equal(WebhookOutcome.UnknownProvider, await _billing.HandleWebhookAsync("stripe", Signed, Event("evt_1"), default));
        Assert.Equal(WebhookOutcome.UnknownProvider, await _billing.HandleWebhookAsync("", Signed, Event("evt_1"), default));
        Assert.NotNull(_billing.Provider("FAKE"));
    }

    // ---- reconciling ----

    [Fact]
    public async Task Reconciling_asks_about_what_has_not_been_heard_of_and_what_should_have_ended_and_leaves_the_rest()
    {
        var id = Sam();
        void Held(string sub, SubscriptionStatus status, int endsInDays, int updatedDaysAgo) =>
            _store.Upsert(new Subscription("sam@example.com", "fake", "cus_1", sub, "cloud", status, _now.AddDays(endsInDays), _now.AddDays(-updatedDaysAgo)));
        Held("fresh", SubscriptionStatus.Active, 20, 0);
        Held("quiet", SubscriptionStatus.Active, 20, 5);
        Held("overdue", SubscriptionStatus.PastDue, -2, 0);
        Held("over", SubscriptionStatus.Ended, -20, 30);
        foreach (var sub in new[] { "fresh", "quiet", "overdue", "over" })
            _fake.States[sub] = State(id, SubscriptionStatus.Ended, endsInDays: -1, id: sub);   // the provider says they are all over now

        Assert.Equal(2, await _billing.ReconcileAsync(default));
        Assert.Equal(new[] { "overdue", "quiet" }, _fake.Asked.Order());
        var byId = _store.Of("sam@example.com").ToDictionary(s => s.ProviderSubscriptionId);
        Assert.Equal((SubscriptionStatus.Ended, SubscriptionStatus.Ended), (byId["quiet"].Status, byId["overdue"].Status));
        Assert.Equal(SubscriptionStatus.Active, byId["fresh"].Status);   // not asked, not changed
    }

    [Fact]
    public async Task Reconciling_leaves_what_it_cannot_ask_about_and_what_the_provider_does_not_know_and_goes_on()
    {
        var id = Sam();
        void Quiet(string sub, string provider = "fake") =>
            _store.Upsert(new Subscription("sam@example.com", provider, "cus_1", sub, "cloud", SubscriptionStatus.Active, _now.AddDays(20), _now.AddDays(-5)));
        Quiet("known");
        Quiet("unknown");
        Quiet("elsewhere", "stripe");   // a provider this service does not have
        _fake.States["known"] = State(id, SubscriptionStatus.PastDue, id: "known");

        Assert.Equal(1, await _billing.ReconcileAsync(default));
        var byId = _store.Of("sam@example.com").ToDictionary(s => s.ProviderSubscriptionId);
        Assert.Equal(SubscriptionStatus.PastDue, byId["known"].Status);
        Assert.Equal(SubscriptionStatus.Active, byId["unknown"].Status);
        Assert.Equal(SubscriptionStatus.Active, byId["elsewhere"].Status);

        _fake.Down = true;
        Assert.Equal(0, await _billing.ReconcileAsync(default));   // not an error: the next run asks again
    }

    // ---- sign-ups that were never paid ----

    [Fact]
    public void An_account_made_for_someone_who_never_paid_goes_when_its_token_runs_out_and_one_that_paid_or_chose_a_password_stays()
    {
        _accounts.StartPending("never@example.com", TimeSpan.FromHours(24));
        _accounts.StartPending("paid@example.com", TimeSpan.FromHours(24));
        _store.Upsert(new Subscription("paid@example.com", "fake", "cus_2", "sub_2", "cloud", SubscriptionStatus.Active, _now.AddDays(30), _now));
        _accounts.Add("plain@example.com", "correct horse battery");
        var billingId = _store.BillingIdOf("never@example.com");

        _now = DateTimeOffset.UtcNow;
        Assert.Equal(0, _billing.PurgeUnpaid());   // the token is still good

        _now = DateTimeOffset.UtcNow.AddHours(25);
        Assert.Equal(1, _billing.PurgeUnpaid());
        Assert.False(_accounts.Exists("never@example.com"));
        Assert.Null(_store.AccountOf(billingId));
        Assert.True(_accounts.Exists("paid@example.com"));
        Assert.True(_accounts.Exists("plain@example.com"));
    }

    [Fact]
    public void Old_events_are_forgotten_after_thirty_days()
    {
        _store.TryRecordEvent("fake", "old", _now.AddDays(-31));
        _store.TryRecordEvent("fake", "recent", _now.AddDays(-5));
        Assert.Equal(1, _billing.PurgeEvents());
        Assert.False(_store.TryRecordEvent("fake", "recent", _now));
        Assert.True(_store.TryRecordEvent("fake", "old", _now));
    }

    // ---- sending people to pay ----

    [Fact]
    public async Task Someone_with_no_account_gets_one_and_is_sent_to_pay_with_a_random_reference_and_not_their_address()
    {
        var result = await _billing.StartNewAsync("sam@example.com", BillingInterval.Yearly, new Uri("https://cloud.example/billing/done"), new Uri("https://cloud.example/account/subscription"), default);
        Assert.Equal(StartOutcome.Redirect, result.Outcome);
        Assert.Equal("https://pay.example/checkout/1", result.Url!.ToString());

        var request = Assert.Single(_fake.Checkouts);
        Assert.Equal(("sam@example.com", "cloud", BillingInterval.Yearly), (request.Email, request.Plan, request.Interval));
        Assert.Equal(_store.BillingIdOf("sam@example.com"), request.AccountRef);
        Assert.DoesNotContain("sam", request.AccountRef);
        Assert.Equal("https://cloud.example/account/subscription", request.ReturnUrl.ToString());
        Assert.StartsWith("https://cloud.example/billing/done?t=set_", request.SuccessUrl.ToString());

        // The account exists for them to choose a password in, once paid: no pass, no password, and the token from the address works.
        Assert.False(_accounts.HasPassword("sam@example.com"));
        Assert.Equal(AccountPlan.None, _accounts.PassOf("sam@example.com"));
        var token = System.Web.HttpUtility.ParseQueryString(request.SuccessUrl.Query)["t"];
        Assert.Equal("sam@example.com", _accounts.SetupFor(token));
    }

    [Fact]
    public async Task Starting_again_before_choosing_a_password_gives_a_new_token_and_the_old_address_stops_working()
    {
        await _billing.StartNewAsync("sam@example.com", BillingInterval.Monthly, new Uri("https://cloud.example/billing/done"), new Uri("https://cloud.example/x"), default);
        await _billing.StartNewAsync("sam@example.com", BillingInterval.Monthly, new Uri("https://cloud.example/billing/done"), new Uri("https://cloud.example/x"), default);
        string? Token(int i) => System.Web.HttpUtility.ParseQueryString(_fake.Checkouts[i].SuccessUrl.Query)["t"];
        Assert.Null(_accounts.SetupFor(Token(0)));
        Assert.Equal("sam@example.com", _accounts.SetupFor(Token(1)));
        Assert.Equal(_fake.Checkouts[0].AccountRef, _fake.Checkouts[1].AccountRef);   // the same person to the provider
    }

    [Fact]
    public async Task An_address_that_has_an_account_or_a_lifetime_sign_up_is_not_sent_to_pay_as_a_new_one()
    {
        _accounts.Add("sam@example.com", "correct horse battery");
        _accounts.StartSignup("kim@example.com", "code1");
        var done = new Uri("https://cloud.example/billing/done");
        Assert.Equal(StartOutcome.HasAccount, (await _billing.StartNewAsync("sam@example.com", BillingInterval.Monthly, done, done, default)).Outcome);
        Assert.Equal(StartOutcome.HasAccount, (await _billing.StartNewAsync("kim@example.com", BillingInterval.Monthly, done, done, default)).Outcome);
        Assert.Empty(_fake.Checkouts);
    }

    [Fact]
    public async Task A_provider_that_cannot_be_reached_to_start_a_payment_is_a_failure_to_say_not_an_error()
    {
        _fake.Down = true;
        var done = new Uri("https://cloud.example/billing/done");
        Assert.Equal(StartOutcome.ProviderFailed, (await _billing.StartNewAsync("sam@example.com", BillingInterval.Monthly, done, done, default)).Outcome);
        var account = Sam("kim@example.com");
        Assert.Equal(StartOutcome.ProviderFailed, (await _billing.StartForAccountAsync("kim@example.com", BillingInterval.Monthly, done, done, default)).Outcome);
        Assert.NotNull(account);
    }

    [Fact]
    public async Task The_portal_is_the_providers_page_for_the_latest_subscription_and_nothing_for_an_account_that_pays_for_nothing()
    {
        Sam();
        Assert.Null(await _billing.PortalAsync("sam@example.com", new Uri("https://cloud.example/back"), default));

        _store.Upsert(new Subscription("sam@example.com", "fake", "cus_old", "sub_old", "cloud", SubscriptionStatus.Ended, _now.AddDays(-40), _now));
        _store.Upsert(new Subscription("sam@example.com", "fake", "cus_new", "sub_new", "cloud", SubscriptionStatus.Active, _now.AddDays(20), _now));
        Assert.Equal("https://pay.example/portal/cus_new", (await _billing.PortalAsync("sam@example.com", new Uri("https://cloud.example/back"), default))!.ToString());
        Assert.Equal("https://cloud.example/back", _fake.Portals.Single().Return.ToString());

        _fake.Down = true;
        Assert.Null(await _billing.PortalAsync("sam@example.com", new Uri("https://cloud.example/back"), default));
    }

    [Fact]
    public async Task Cancelling_everything_an_account_pays_for_ends_it_at_once_and_says_when_it_could_not()
    {
        Sam();
        Assert.True(await _billing.CancelAllAsync("sam@example.com", default));   // nothing to cancel
        void Held(string sub, SubscriptionStatus status, string provider = "fake") =>
            _store.Upsert(new Subscription("sam@example.com", provider, "cus_1", sub, "cloud", status, _now.AddDays(20), _now));
        Held("sub_1", SubscriptionStatus.Active);
        Held("sub_2", SubscriptionStatus.Canceling);
        Held("sub_3", SubscriptionStatus.Ended);   // over already: not asked

        Assert.True(await _billing.CancelAllAsync("sam@example.com", default));
        Assert.Equal(new[] { ("sub_1", true), ("sub_2", true) }, _fake.Cancelled.OrderBy(c => c.Subscription));

        _fake.Down = true;
        Assert.False(await _billing.CancelAllAsync("sam@example.com", default));
        Held("sub_4", SubscriptionStatus.Active, "stripe");   // a provider this build does not have cannot be cancelled from here
        _fake.Down = false;
        Assert.False(await _billing.CancelAllAsync("sam@example.com", default));
    }

    [Fact]
    public void A_ticket_stands_for_an_account_for_ten_minutes_and_for_no_one_else()
    {
        var now = new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
        var tickets = new BillingTickets(() => now);
        var ticket = tickets.Make("sam@example.com");
        Assert.Equal(48, ticket.Length);
        Assert.NotEqual(ticket, tickets.Make("sam@example.com"));
        Assert.Equal("sam@example.com", tickets.AccountOf(ticket));
        Assert.Null(tickets.AccountOf("0123456789"));
        Assert.Null(tickets.AccountOf(null));
        now = now.AddMinutes(11);
        Assert.Null(tickets.AccountOf(ticket));
    }
}

/// <summary>The addresses of billing, end to end: the webhook, paying first, coming back, the account page, and deleting an account that pays.</summary>
public sealed class BillingApiTests : BillingTestBase
{
    private const string Sam = "sam@example.com";
    private readonly FakeBilling _fake = new();

    private HttpClient Run(Dictionary<string, string>? settings = null, Action<IServiceCollection>? more = null)
    {
        var all = new Dictionary<string, string> { ["Billing:Provider"] = "fake" };
        foreach (var (key, value) in settings ?? new()) all[key] = value;
        return Start(all, services =>
        {
            services.AddSingleton<IBillingProvider>(_fake);
            more?.Invoke(services);
        });
    }

    private static Task<HttpResponseMessage> Form(HttpClient client, string path, params (string, string)[] fields) =>
        client.PostAsync(path, new FormUrlEncodedContent(fields.ToDictionary(f => f.Item1, f => f.Item2)));

    private static async Task<HttpResponseMessage> Hook(HttpClient client, string id, string? subscription = "sub_1", string signature = "valid", string provider = "fake")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/billing/webhook/" + provider)
        {
            Content = new StringContent(JsonSerializer.Serialize(subscription == null ? new { id } : (object)new { id, subscription }), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("x-fake-signature", signature);
        return await client.SendAsync(request);
    }

    private SubscriptionState Paid(string billingId, int endsInDays = 30, string id = "sub_1", SubscriptionStatus status = SubscriptionStatus.Active) =>
        _fake.States[id] = new SubscriptionState("cus_1", id, billingId, "cloud", status, DateTimeOffset.UtcNow.AddDays(endsInDays), false);

    private static string TokenOf(Uri success) => System.Web.HttpUtility.ParseQueryString(success.Query)["t"]!;

    // Starting to pay as someone with no account: the address the provider will send them back to.
    private async Task<(string Email, string Token, string BillingId)> StartPaying(HttpClient client, string email = "new@example.com", string interval = "monthly")
    {
        var response = await Form(client, "/billing/start", ("email", email), ("interval", interval));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var request = _fake.Checkouts.Last();
        return (email, TokenOf(request.SuccessUrl), request.AccountRef);
    }

    // ---- off ----

    [Theory]
    [InlineData("POST", "/billing/start")]
    [InlineData("GET", "/billing/done?t=x")]
    [InlineData("POST", "/billing/webhook/fake")]
    [InlineData("GET", "/account/subscription")]
    [InlineData("POST", "/account/subscription")]
    [InlineData("POST", "/account/subscription/checkout")]
    [InlineData("POST", "/account/subscription/portal")]
    public async Task With_billing_off_none_of_these_addresses_exist(string method, string path)
    {
        var client = Start();
        var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path) { Content = method == "POST" ? new FormUrlEncodedContent(new Dictionary<string, string>()) : null });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---- the webhook ----

    [Fact]
    public async Task A_signed_webhook_stores_the_subscription_and_answers_quickly_and_a_forged_one_is_refused()
    {
        var client = Run();
        Register(Sam);
        var billingId = new SubscriptionStore(Options).BillingIdOf(Sam);
        Paid(billingId);

        Assert.Equal(HttpStatusCode.BadRequest, (await Hook(client, "evt_1", signature: "forged")).StatusCode);
        Assert.Empty(new SubscriptionStore(Options).Of(Sam));

        Assert.Equal(HttpStatusCode.OK, (await Hook(client, "evt_1")).StatusCode);
        Assert.Equal("sub_1", Assert.Single(new SubscriptionStore(Options).Of(Sam)).ProviderSubscriptionId);
        Assert.Equal(HttpStatusCode.OK, (await Hook(client, "evt_1")).StatusCode);   // the same event again
        Assert.Equal(HttpStatusCode.NotFound, (await Hook(client, "evt_2", provider: "stripe")).StatusCode);
    }

    [Fact]
    public async Task A_provider_that_is_down_makes_the_webhook_say_try_again_and_the_retry_works()
    {
        var client = Run();
        Register(Sam);
        Paid(new SubscriptionStore(Options).BillingIdOf(Sam));
        _fake.Down = true;
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await Hook(client, "evt_1")).StatusCode);
        _fake.Down = false;
        Assert.Equal(HttpStatusCode.OK, (await Hook(client, "evt_1")).StatusCode);
        Assert.Single(new SubscriptionStore(Options).Of(Sam));
    }

    [Fact]
    public async Task A_webhook_that_is_too_big_is_refused_without_being_read_to_the_end()
    {
        var client = Run();
        var big = new HttpRequestMessage(HttpMethod.Post, "/billing/webhook/fake") { Content = new ByteArrayContent(new byte[BillingService.MaxWebhookBytes + 1]) };
        big.Headers.Add("x-fake-signature", "valid");
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await client.SendAsync(big)).StatusCode);
        Assert.Empty(_fake.Asked);
    }

    // ---- paying first ----

    [Fact]
    public async Task Someone_with_no_account_pays_comes_back_chooses_a_password_and_signs_in_as_a_subscriber()
    {
        var client = Run();
        var (email, token, billingId) = await StartPaying(client);

        // Sent to the provider's page, and an account is waiting for them with no pass and no password.
        Assert.Equal("https://pay.example/checkout/2", (await Form(client, "/billing/start", ("email", "second@example.com"))).Headers.Location!.ToString());   // the second person's own checkout
        Assert.Equal(AccountPlan.None, new AccountDirectory(Options).PassOf(email));

        // Back from the provider before the webhook has arrived: a page that looks again by itself, and nothing to choose yet.
        var waiting = await client.GetAsync("/billing/done?t=" + Uri.EscapeDataString(token));
        Assert.Equal(HttpStatusCode.OK, waiting.StatusCode);
        var html = await waiting.Content.ReadAsStringAsync();
        Assert.Contains("http-equiv=\"refresh\"", html);
        Assert.DoesNotContain("name=\"password\"", html);

        // The webhook arrives.
        Paid(billingId);
        Assert.Equal(HttpStatusCode.OK, (await Hook(client, "evt_1")).StatusCode);

        // Now the same address goes on: a page to choose a password, that says the payment has arrived and does not claim a lifetime pass.
        var choose = await client.GetAsync("/billing/done?t=" + Uri.EscapeDataString(token));
        var chooseHtml = await choose.Content.ReadAsStringAsync();
        Assert.Contains("name=\"password\"", chooseHtml);
        Assert.Contains("Your payment has arrived", chooseHtml);
        Assert.DoesNotContain("lifetime", chooseHtml);

        const string password = "a long enough password";
        var done = await Form(client, "/account/setup", ("token", token), ("password", password), ("again", password));
        Assert.Equal(HttpStatusCode.OK, done.StatusCode);
        var welcome = await done.Content.ReadAsStringAsync();
        Assert.Contains("is ready", welcome);
        Assert.DoesNotContain("lifetime", welcome);
        Assert.True(new AccountDirectory(Options).CheckPassword(email, password));

        // Signed in as a subscriber: a device can be signed in and the services work.
        var (verifier, challenge) = Pkce();
        var login = await PostLogin(client, challenge, email, password);
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        var tokenResponse = await client.PostAsJsonAsync("/v1/token", new { code = System.Web.HttpUtility.ParseQueryString(login.Headers.Location!.Query)["code"], verifier });
        Assert.Equal(HttpStatusCode.OK, tokenResponse.StatusCode);
        var deviceToken = (await tokenResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
        Assert.Equal(HttpStatusCode.Created, (await Send(client, deviceToken, HttpMethod.Put, "/v1/backup", Locked(), ifMatch: 0)).StatusCode);
    }

    [Fact]
    public async Task The_page_they_come_back_to_asks_the_provider_when_the_webhook_is_late_and_gives_up_waiting_politely()
    {
        var client = Run();
        var (_, token, billingId) = await StartPaying(client);
        var url = "/billing/done?t=" + Uri.EscapeDataString(token);

        // The provider has it, though no webhook has come: the page finds it.
        Paid(billingId);
        Assert.Contains("name=\"password\"", await (await client.GetAsync(url)).Content.ReadAsStringAsync());

        // Nothing paid after fifteen looks: it stops refreshing and says to come back to the same link.
        var other = await StartPaying(client, "other@example.com");
        var late = await (await client.GetAsync("/billing/done?n=15&t=" + Uri.EscapeDataString(other.Token))).Content.ReadAsStringAsync();
        Assert.DoesNotContain("http-equiv", late);
        Assert.Contains("has not arrived yet", late);
        Assert.Contains("Check again", late);
    }

    [Fact]
    public async Task A_link_that_is_not_theirs_gives_nothing_and_a_page_for_someone_who_has_not_paid_gives_no_password_page()
    {
        var client = Run();
        await StartPaying(client);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/billing/done?t=set_notatoken")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/billing/done")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/billing/done?t=" + new string('a', 5000))).StatusCode);
    }

    [Fact]
    public async Task An_address_that_already_has_an_account_is_sent_to_sign_in_and_nothing_is_started()
    {
        var client = Run();
        Register(Sam);
        var response = await Form(client, "/billing/start", ("email", Sam));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("/account/subscription", await response.Content.ReadAsStringAsync());
        Assert.Empty(_fake.Checkouts);

        new AccountDirectory(Options).StartSignup("kim@example.com", "code1");   // a lifetime sign-up that has not chosen a password
        Assert.Equal(HttpStatusCode.Conflict, (await Form(client, "/billing/start", ("email", "kim@example.com"))).StatusCode);
        Assert.Empty(_fake.Checkouts);
        Assert.Equal(AccountPlan.Lifetime, new AccountDirectory(Options).PassOf("kim@example.com"));
    }

    [Fact]
    public async Task The_sign_in_page_for_the_subscription_also_lets_someone_with_no_account_subscribe()
    {
        var client = Run();
        var html = await (await client.GetAsync("/account/subscription")).Content.ReadAsStringAsync();
        Assert.Contains("action=\"/account/subscription\"", html);   // signing in
        Assert.Contains("action=\"/billing/start\"", html);          // and paying first
        Assert.Contains("name=\"interval\"", html);
        Assert.Contains("No account yet?", html);

        // The form on that page is the same one as the website's: it starts a payment.
        var response = await Form(client, "/billing/start", ("email", "new@example.com"), ("interval", "yearly"));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(BillingInterval.Yearly, _fake.Checkouts.Last().Interval);
    }

    [Fact]
    public async Task An_address_that_has_paid_and_not_chosen_a_password_is_not_sent_to_pay_again_and_its_link_is_not_replaced()
    {
        var client = Run();
        var (email, token, billingId) = await StartPaying(client);
        Paid(billingId);
        Assert.Equal(HttpStatusCode.OK, (await Hook(client, "evt_1")).StatusCode);
        var checkouts = _fake.Checkouts.Count;

        // Someone types the address again (the payer who closed the page, or anyone else): nothing is started and the payer's link still works.
        var again = await Form(client, "/billing/start", ("email", email));
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Contains("already paid", await again.Content.ReadAsStringAsync());
        Assert.Equal(checkouts, _fake.Checkouts.Count);
        var page = await client.GetAsync("/billing/done?t=" + Uri.EscapeDataString(token));
        Assert.Contains("name=\"password\"", await page.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_sign_up_code_does_not_take_over_an_address_that_has_paid_and_not_chosen_a_password()
    {
        var client = Run();
        var (email, token, billingId) = await StartPaying(client);
        Paid(billingId);
        await Hook(client, "evt_1");
        var code = new SignupCodes(Options).Create("", null, null).Code;

        var response = await client.PostAsync("/signup", new FormUrlEncodedContent(new Dictionary<string, string> { ["code"] = code, ["email"] = email }));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.DoesNotContain("name=\"token\"", await response.Content.ReadAsStringAsync());
        Assert.Equal(email, new AccountDirectory(Options).SetupFor(token));   // the payer's link is untouched
    }

    [Fact]
    public async Task An_address_that_paid_and_has_a_password_is_sent_to_sign_in()
    {
        var client = Run();
        Register(Sam);
        new SubscriptionStore(Options).Upsert(new Subscription(Sam, "fake", "cus_1", "sub_1", "cloud", SubscriptionStatus.Active, DateTimeOffset.UtcNow.AddDays(10), DateTimeOffset.UtcNow));
        var response = await Form(client, "/billing/start", ("email", Sam));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("/account/subscription", await response.Content.ReadAsStringAsync());
        Assert.Empty(_fake.Checkouts);
    }

    [Fact]
    public async Task A_made_up_address_is_refused_and_so_is_trying_over_and_over_from_one_place()
    {
        var client = Run();
        Assert.Equal(HttpStatusCode.BadRequest, (await Form(client, "/billing/start", ("email", "not an address"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Form(client, "/billing/start", ("email", ""))).StatusCode);

        var statuses = new List<HttpStatusCode>();
        for (int i = 0; i < 12; i++) statuses.Add((await Form(client, "/billing/start", ("email", $"p{i}@example.com"))).StatusCode);
        Assert.Contains(HttpStatusCode.TooManyRequests, statuses);
        Assert.True(_fake.Checkouts.Count < 12);   // the later ones were not started
    }

    [Fact]
    public async Task A_provider_that_is_down_makes_the_start_page_say_so()
    {
        var client = Run();
        _fake.Down = true;
        var response = await Form(client, "/billing/start", ("email", "new@example.com"));
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Contains("could not be opened", await response.Content.ReadAsStringAsync());
    }

    // ---- the account page ----

    private static string Ticket(string html) => System.Text.RegularExpressions.Regex.Match(html, "name=\"ticket\" value=\"([0-9a-f]+)\"").Groups[1].Value;

    private async Task<(string Html, string Ticket)> SignIn(HttpClient client, string name = Sam, string password = Password)
    {
        var response = await Form(client, "/account/subscription", ("username", name), ("password", password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        return (html, Ticket(html));
    }

    [Fact]
    public async Task The_account_page_shows_where_an_account_stands_after_the_password_and_says_nothing_to_a_stranger()
    {
        var client = Run();
        Register(Sam);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/account/subscription")).StatusCode);

        var wrong = await Form(client, "/account/subscription", ("username", Sam), ("password", "another password entirely"));
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        var nobody = await Form(client, "/account/subscription", ("username", "nobody@example.com"), ("password", "another password entirely"));
        Assert.Equal(HttpStatusCode.Unauthorized, nobody.StatusCode);
        Assert.Equal(await wrong.Content.ReadAsStringAsync(), (await nobody.Content.ReadAsStringAsync()).Replace("nobody@example.com", Sam));

        var (lifetime, _) = await SignIn(client);
        Assert.Contains("lifetime pass", lifetime);
        Assert.DoesNotContain("Go to payment", lifetime);

        new AccountDirectory(Options).SetPass(Sam, AccountPlan.None);
        var (lapsed, _) = await SignIn(client);
        Assert.Contains("There is no subscription", lapsed);
        Assert.Contains("Go to payment", lapsed);
        Assert.DoesNotContain("Manage payment", lapsed);   // nothing paid through a provider yet

        new SubscriptionStore(Options).Upsert(new Subscription(Sam, "fake", "cus_1", "sub_1", "cloud", SubscriptionStatus.Active, DateTimeOffset.UtcNow.AddDays(20), DateTimeOffset.UtcNow));
        var (subscribed, _) = await SignIn(client);
        Assert.Contains("Subscribed. It renews after", subscribed);
        Assert.Contains("Manage payment and invoices", subscribed);

        new SubscriptionStore(Options).Upsert(new Subscription(Sam, "fake", "cus_1", "sub_1", "cloud", SubscriptionStatus.Ended, DateTimeOffset.UtcNow.AddDays(-30), DateTimeOffset.UtcNow));
        var (ended, _) = await SignIn(client);
        Assert.Contains("The subscription ended on", ended);
        Assert.Contains("Nothing has been deleted", ended);
    }

    [Fact]
    public async Task Wrong_passwords_on_the_account_page_are_limited_like_signing_in()
    {
        var client = Run();
        Register(Sam);
        var last = HttpStatusCode.OK;
        for (int i = 0; i < 9; i++) last = (await Form(client, "/account/subscription", ("username", Sam), ("password", "another password entirely"))).StatusCode;
        Assert.Equal(HttpStatusCode.TooManyRequests, last);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Form(client, "/account/subscription", ("username", Sam), ("password", Password))).StatusCode);   // even the right one, for now
    }

    [Fact]
    public async Task An_account_that_has_not_paid_goes_to_the_provider_to_pay_with_its_own_reference_and_is_not_asked_for_the_password_again_for_ten_minutes()
    {
        var client = Run();
        Register(Sam);
        new AccountDirectory(Options).SetPass(Sam, AccountPlan.None);
        var (_, ticket) = await SignIn(client);

        var go = await Form(client, "/account/subscription/checkout", ("ticket", ticket), ("interval", "yearly"));
        Assert.Equal(HttpStatusCode.Redirect, go.StatusCode);
        Assert.Equal("https://pay.example/checkout/1", go.Headers.Location!.ToString());
        var request = Assert.Single(_fake.Checkouts);
        Assert.Equal((Sam, BillingInterval.Yearly), (request.Email, request.Interval));
        Assert.Equal(new SubscriptionStore(Options).BillingIdOf(Sam), request.AccountRef);
        Assert.EndsWith("/account/subscription", request.SuccessUrl.AbsolutePath);

        // A page that was not signed in to, or a made-up ticket, goes nowhere.
        Assert.Equal(HttpStatusCode.BadRequest, (await Form(client, "/account/subscription/checkout", ("interval", "monthly"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Form(client, "/account/subscription/checkout", ("ticket", "00" + ticket[2..]), ("interval", "monthly"))).StatusCode);
        Assert.Single(_fake.Checkouts);
    }

    [Fact]
    public async Task A_lifetime_account_and_one_already_subscribed_are_not_sent_to_pay_again_and_a_subscribed_one_can_manage()
    {
        var client = Run();
        Register(Sam);
        // The page of an account with a lifetime pass has no buttons to pay, so a ticket is made as the page would, to see that the address itself refuses.
        var ticket = LastFactory!.Services.GetRequiredService<BillingTickets>().Make(Sam);
        var lifetime = await Form(client, "/account/subscription/checkout", ("ticket", ticket), ("interval", "monthly"));
        Assert.Equal(HttpStatusCode.Conflict, lifetime.StatusCode);

        new AccountDirectory(Options).SetPass(Sam, AccountPlan.None);
        new SubscriptionStore(Options).Upsert(new Subscription(Sam, "fake", "cus_77", "sub_1", "cloud", SubscriptionStatus.Active, DateTimeOffset.UtcNow.AddDays(20), DateTimeOffset.UtcNow));
        var (_, again) = await SignIn(client);
        var twice = await Form(client, "/account/subscription/checkout", ("ticket", again), ("interval", "monthly"));
        Assert.Equal(HttpStatusCode.Conflict, twice.StatusCode);
        Assert.Contains("already subscribed", await twice.Content.ReadAsStringAsync());
        Assert.Empty(_fake.Checkouts);

        var portal = await Form(client, "/account/subscription/portal", ("ticket", again));
        Assert.Equal(HttpStatusCode.Redirect, portal.StatusCode);
        Assert.Equal("https://pay.example/portal/cus_77", portal.Headers.Location!.ToString());
        Assert.EndsWith("/account/subscription", _fake.Portals.Single().Return.AbsolutePath);
    }

    [Fact]
    public async Task An_account_that_pays_for_nothing_has_no_portal_and_a_provider_that_is_down_is_said_not_thrown()
    {
        var client = Run();
        Register(Sam);
        new AccountDirectory(Options).SetPass(Sam, AccountPlan.None);
        var (_, ticket) = await SignIn(client);
        Assert.Equal(HttpStatusCode.NotFound, (await Form(client, "/account/subscription/portal", ("ticket", ticket))).StatusCode);

        _fake.Down = true;
        Assert.Equal(HttpStatusCode.BadGateway, (await Form(client, "/account/subscription/checkout", ("ticket", ticket), ("interval", "monthly"))).StatusCode);
    }

    // ---- deleting an account that pays ----

    private static readonly string DeleteWords = "delete my account";

    [Fact]
    public async Task Deleting_an_account_that_pays_ends_the_subscription_first_and_a_provider_that_is_down_stops_the_delete()
    {
        var client = Run();
        Register(Sam);
        new AccountDirectory(Options).SetPass(Sam, AccountPlan.None);
        new SubscriptionStore(Options).Upsert(new Subscription(Sam, "fake", "cus_1", "sub_1", "cloud", SubscriptionStatus.Active, DateTimeOffset.UtcNow.AddDays(20), DateTimeOffset.UtcNow));

        _fake.Down = true;
        var refused = await Form(client, "/account/delete", ("username", Sam), ("current", Password), ("confirm", DeleteWords));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
        Assert.Contains("nothing was deleted", await refused.Content.ReadAsStringAsync());
        Assert.True(new AccountDirectory(Options).Exists(Sam));
        Assert.Single(new SubscriptionStore(Options).Of(Sam));

        _fake.Down = false;
        Assert.Equal(HttpStatusCode.OK, (await Form(client, "/account/delete", ("username", Sam), ("current", Password), ("confirm", DeleteWords))).StatusCode);
        Assert.Equal(("sub_1", true), Assert.Single(_fake.Cancelled));   // ended at once, so it is not charged again
        Assert.False(new AccountDirectory(Options).Exists(Sam));
        Assert.Empty(new SubscriptionStore(Options).Of(Sam));
    }

    // ---- what is logged ----

    [Fact]
    public async Task What_billing_logs_is_what_happened_and_never_an_address_a_reference_or_a_body()
    {
        var logs = new LogCapture();
        var client = Run(more: services => services.AddSingleton<ILoggerProvider>(logs));
        var (email, token, billingId) = await StartPaying(client, "private.person@example.com");
        Paid(billingId);
        await Hook(client, "evt_secret_1");
        await Hook(client, "evt_secret_1");
        await Hook(client, "evt_secret_2", signature: "forged");
        await client.GetAsync("/billing/done?t=" + Uri.EscapeDataString(token));

        string all;
        lock (logs.Lines) all = string.Join(" | ", logs.Lines);
        Assert.Contains("billing: webhook subscription (fake)", all);
        Assert.Contains("billing: the same event again (fake)", all);
        Assert.Contains("billing: bad signature (fake)", all);
        foreach (var secret in new[] { email, "private.person", billingId, token, "evt_secret", "cus_1", "sub_1" })
            Assert.DoesNotContain(secret, all);
    }

    // ---- signing in, as in the other tests ----

    private const string Redirect = "http://127.0.0.1:51234/callback";

    private static (string Verifier, string Challenge) Pkce()
    {
        var verifier = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        return (verifier, Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))));
    }

    private static Task<HttpResponseMessage> PostLogin(HttpClient client, string challenge, string username, string password) =>
        client.PostAsync("/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["redirect_uri"] = Redirect, ["code_challenge"] = challenge, ["state"] = "state-0123456789", ["device"] = "Sam's laptop", ["username"] = username, ["password"] = password,
        }));
}
