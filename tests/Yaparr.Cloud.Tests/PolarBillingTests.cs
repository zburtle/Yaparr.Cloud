using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Yaparr.Cloud;

namespace Yaparr.Cloud.Tests;

/// <summary>The Polar adapter against answers shaped as Polar's API reference (version 2026-10) shows them. Real calls are tried in Polar's sandbox, not here.</summary>
public class PolarBillingTests
{
    private const string Monthly = "11111111-1111-4111-8111-111111111111", Yearly = "22222222-2222-4222-8222-222222222222";
    private const string Sub = "33333333-3333-4333-8333-333333333333", Cust = "44444444-4444-4444-8444-444444444444";
    private static readonly byte[] Key = Enumerable.Range(1, 24).Select(i => (byte)i).ToArray();
    private static readonly string Secret = "whsec_" + Convert.ToBase64String(Key);
    private static readonly DateTimeOffset Now = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

    private sealed class Handler : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Seen { get; } = new();
        public Func<HttpRequestMessage, (HttpStatusCode, string)> Answer { get; set; } = _ => (HttpStatusCode.OK, "{}");

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            Seen.Add((request, request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancel)));
            var (code, body) = Answer(request);
            return new HttpResponseMessage(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private static PolarOptions Options() => new() { Server = "sandbox", AccessToken = "polar_oat_x", WebhookSecret = Secret, CloudMonthlyProduct = Monthly, CloudYearlyProduct = Yearly };

    private static (PolarBilling Polar, Handler Handler) Make(Func<HttpRequestMessage, (HttpStatusCode, string)>? answer = null)
    {
        var handler = new Handler();
        if (answer is not null) handler.Answer = answer;
        var o = Options();
        return (new PolarBilling(o, new HttpClient(handler), () => Now), handler);
    }

    private static string SubscriptionJson(string status = "active", bool cancelAtEnd = false, string? external = "acct-ref", string end = "2026-12-22T12:00:00Z") => $$$"""
        {"id":"{{{Sub}}}","status":"{{{status}}}","cancel_at_period_end":{{{(cancelAtEnd ? "true" : "false")}}},"current_period_start":"2026-11-22T12:00:00Z","current_period_end":"{{{end}}}",
         "customer_id":"{{{Cust}}}","product_id":"{{{Monthly}}}","customer":{"id":"{{{Cust}}}","external_id":{{{(external is null ? "null" : "\"" + external + "\"")}}},"email":"a@b.example"}}
        """;

    private static Dictionary<string, string> Signed(string body, string id = "evt_1", DateTimeOffset? at = null, byte[]? key = null)
    {
        var stamp = (at ?? Now).ToUnixTimeSeconds().ToString();
        var mac = HMACSHA256.HashData(key ?? Key, Encoding.UTF8.GetBytes($"{id}.{stamp}.{body}"));
        return new() { ["webhook-id"] = id, ["webhook-timestamp"] = stamp, ["webhook-signature"] = "v1," + Convert.ToBase64String(mac) };
    }

    private static ReadOnlyMemory<byte> Bytes(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public async Task A_checkout_names_the_product_of_the_interval_and_the_account_by_reference_and_not_by_name()
    {
        var (polar, handler) = Make(_ => (HttpStatusCode.Created, """{"id":"c1","url":"https://sandbox.polar.sh/checkout/abc"}"""));
        var url = await polar.StartCheckoutAsync(new CheckoutRequest("acct-ref", "a@b.example", "cloud", BillingInterval.Yearly, new("https://cloud.example/billing/done"), new("https://cloud.example/")), default);

        Assert.Equal("https://sandbox.polar.sh/checkout/abc", url.ToString());
        var (request, body) = Assert.Single(handler.Seen);
        Assert.Equal("https://sandbox-api.polar.sh/v1/checkouts/", request.RequestUri!.ToString());
        Assert.Equal("Bearer polar_oat_x", request.Headers.Authorization!.ToString());
        Assert.Equal("2026-10", request.Headers.GetValues("Polar-Version").Single());
        var json = JsonSerializer.Deserialize<JsonElement>(body);
        Assert.Equal(Yearly, json.GetProperty("products")[0].GetString());
        Assert.Equal("acct-ref", json.GetProperty("external_customer_id").GetString());
        Assert.False(json.GetProperty("allow_trial").GetBoolean());
        Assert.Equal("https://cloud.example/billing/done", json.GetProperty("success_url").GetString());
    }

    [Fact]
    public async Task A_checkout_page_that_is_not_https_is_not_followed()
    {
        var (polar, _) = Make(_ => (HttpStatusCode.Created, """{"url":"javascript:alert(1)"}"""));
        await Assert.ThrowsAsync<InvalidOperationException>(() => polar.StartCheckoutAsync(new CheckoutRequest("r", "a@b.example", "cloud", BillingInterval.Monthly, new("https://x.example/"), new("https://x.example/")), default));
    }

    [Fact]
    public async Task The_portal_is_a_customer_session_of_the_customer()
    {
        var (polar, handler) = Make(_ => (HttpStatusCode.Created, $$"""{"id":"s","token":"t","customer_portal_url":"https://sandbox.polar.sh/portal?token=t","customer_id":"{{Cust}}"}"""));
        var url = await polar.PortalAsync(Cust, new("https://cloud.example/account"), default);

        Assert.Equal("https://sandbox.polar.sh/portal?token=t", url.ToString());
        Assert.EndsWith("/customer-sessions/", handler.Seen[0].Request.RequestUri!.AbsolutePath);
        var json = JsonSerializer.Deserialize<JsonElement>(handler.Seen[0].Body);
        Assert.Equal(Cust, json.GetProperty("customer_id").GetString());
        Assert.Equal("https://cloud.example/account", json.GetProperty("return_url").GetString());
    }

    [Theory]
    [InlineData("active", false, SubscriptionStatus.Active)]
    [InlineData("active", true, SubscriptionStatus.Canceling)]
    [InlineData("trialing", false, SubscriptionStatus.Active)]
    [InlineData("past_due", false, SubscriptionStatus.PastDue)]
    [InlineData("canceled", false, SubscriptionStatus.Ended)]
    [InlineData("unpaid", false, SubscriptionStatus.Ended)]
    [InlineData("incomplete", false, SubscriptionStatus.Ended)]
    [InlineData("incomplete_expired", false, SubscriptionStatus.Ended)]
    [InlineData("paused", false, SubscriptionStatus.Ended)]
    [InlineData("something_new", false, SubscriptionStatus.Ended)]
    public async Task A_subscription_is_read_in_the_services_words(string status, bool cancelAtEnd, SubscriptionStatus expected)
    {
        var (polar, handler) = Make(_ => (HttpStatusCode.OK, SubscriptionJson(status, cancelAtEnd)));
        var state = await polar.GetSubscriptionAsync(Sub, default);

        Assert.NotNull(state);
        Assert.Equal(expected, state.Status);
        Assert.Equal(cancelAtEnd, state.CancelAtPeriodEnd);
        Assert.Equal("acct-ref", state.AccountRef);
        Assert.Equal(Cust, state.ProviderCustomerId);
        Assert.Equal(Sub, state.ProviderSubscriptionId);
        Assert.Equal(new DateTimeOffset(2026, 12, 22, 12, 0, 0, TimeSpan.Zero), state.CurrentPeriodEnd);
        Assert.EndsWith("/subscriptions/" + Sub, handler.Seen[0].Request.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task A_subscription_polar_does_not_know_is_null_and_an_outage_is_an_error()
    {
        var (gone, _) = Make(_ => (HttpStatusCode.NotFound, """{"error":"ResourceNotFound","detail":"Not found"}"""));
        Assert.Null(await gone.GetSubscriptionAsync(Sub, default));
        Assert.Null(await gone.GetSubscriptionAsync("not-an-id", default));

        var (down, _) = Make(_ => (HttpStatusCode.BadGateway, "no"));
        await Assert.ThrowsAsync<HttpRequestException>(() => down.GetSubscriptionAsync(Sub, default));
    }

    [Fact]
    public async Task A_subscription_with_no_account_reference_is_not_ours()
    {
        var (polar, _) = Make(_ => (HttpStatusCode.OK, SubscriptionJson(external: null)));
        Assert.Null(await polar.GetSubscriptionAsync(Sub, default));
    }

    [Fact]
    public async Task Finding_an_accounts_subscriptions_asks_by_its_reference_and_follows_the_pages()
    {
        var (polar, handler) = Make(r => (HttpStatusCode.OK, r.RequestUri!.Query.Contains("page=1")
            ? $$$"""{"items":[{{{SubscriptionJson("canceled")}}}],"pagination":{"total_count":2,"max_page":2}}"""
            : $$$"""{"items":[{{{SubscriptionJson()}}}],"pagination":{"total_count":2,"max_page":2}}"""));
        var found = await polar.FindAsync("acct ref&", default);

        Assert.Equal(2, found.Count);
        Assert.Equal(2, handler.Seen.Count);
        Assert.Contains("external_customer_id=acct%20ref%26", handler.Seen[0].Request.RequestUri!.Query);
    }

    [Fact]
    public async Task Cancelling_later_patches_and_cancelling_now_deletes_and_one_already_over_is_fine()
    {
        var (polar, handler) = Make(_ => (HttpStatusCode.OK, SubscriptionJson()));
        await polar.CancelAsync(Sub, immediately: false, default);
        await polar.CancelAsync(Sub, immediately: true, default);
        Assert.Equal(HttpMethod.Patch, handler.Seen[0].Request.Method);
        Assert.True(JsonSerializer.Deserialize<JsonElement>(handler.Seen[0].Body).GetProperty("cancel_at_period_end").GetBoolean());
        Assert.Equal(HttpMethod.Delete, handler.Seen[1].Request.Method);

        var (over, _) = Make(r => r.Method == HttpMethod.Get ? (HttpStatusCode.OK, SubscriptionJson("canceled")) : (HttpStatusCode.Forbidden, """{"error":"AlreadyCanceledSubscription"}"""));
        await over.CancelAsync(Sub, immediately: true, default);

        // A token that may not is a 403 as well, and that one is not "already over".
        var (denied, _) = Make(r => r.Method == HttpMethod.Get ? (HttpStatusCode.OK, SubscriptionJson()) : (HttpStatusCode.Forbidden, "{}"));
        await Assert.ThrowsAsync<HttpRequestException>(() => denied.CancelAsync(Sub, immediately: true, default));
    }

    [Fact]
    public void A_signed_subscription_event_says_which_subscription_and_account_to_look_at()
    {
        var (polar, _) = Make();
        var body = $$"""{"type":"subscription.updated","timestamp":"2026-11-22T12:00:00Z","data":{{SubscriptionJson()}}}""";
        var seen = polar.VerifyWebhook(Signed(body), Bytes(body));
        Assert.Equal(new BillingEvent("evt_1", Sub, "acct-ref"), seen);
    }

    [Fact]
    public void A_customer_event_names_only_the_account()
    {
        var (polar, _) = Make();
        var body = """{"type":"customer.state_changed","timestamp":"2026-11-22T12:00:00Z","data":{"id":"c","external_id":"acct-ref","active_subscriptions":[]}}""";
        Assert.Equal(new BillingEvent("evt_2", null, "acct-ref"), polar.VerifyWebhook(Signed(body, "evt_2"), Bytes(body)));
    }

    [Fact]
    public void An_event_of_another_kind_is_accepted_and_names_nothing()
    {
        var (polar, _) = Make();
        var body = """{"type":"order.paid","timestamp":"2026-11-22T12:00:00Z","data":{"id":"o"}}""";
        Assert.Equal(new BillingEvent("evt_3", null, null), polar.VerifyWebhook(Signed(body, "evt_3"), Bytes(body)));
    }

    [Fact]
    public void A_secret_from_before_the_standard_is_keyed_by_its_whole_text()
    {
        var (polar, _) = Make();
        var body = """{"type":"order.paid","data":{}}""";
        var legacy = Encoding.UTF8.GetBytes(Secret);
        Assert.Equal("evt_1", polar.VerifyWebhook(Signed(body, key: legacy), Bytes(body)).EventId);
    }

    [Fact]
    public void A_signature_that_is_wrong_or_missing_or_for_another_body_or_too_old_is_refused()
    {
        var (polar, _) = Make();
        var body = """{"type":"order.paid","data":{}}""";

        Assert.Throws<BadSignatureException>(() => polar.VerifyWebhook(Signed(body, key: new byte[24]), Bytes(body)));
        Assert.Throws<BadSignatureException>(() => polar.VerifyWebhook(Signed(body), Bytes(body + " ")));
        Assert.Throws<BadSignatureException>(() => polar.VerifyWebhook(new Dictionary<string, string>(), Bytes(body)));
        Assert.Throws<BadSignatureException>(() => polar.VerifyWebhook(Signed(body, at: Now.AddMinutes(-6)), Bytes(body)));
        Assert.Throws<BadSignatureException>(() => polar.VerifyWebhook(Signed(body, at: Now.AddMinutes(6)), Bytes(body)));
        var other = Signed(body, "evt_other");
        other["webhook-id"] = "evt_1";
        Assert.Throws<BadSignatureException>(() => polar.VerifyWebhook(other, Bytes(body)));
        var unversioned = Signed(body);
        unversioned["webhook-signature"] = unversioned["webhook-signature"][3..];
        Assert.Throws<BadSignatureException>(() => polar.VerifyWebhook(unversioned, Bytes(body)));
    }

    [Fact]
    public void Several_signatures_are_allowed_and_one_good_one_is_enough()
    {
        var (polar, _) = Make();
        var body = """{"type":"order.paid","data":{}}""";
        var headers = Signed(body);
        headers["webhook-signature"] = "v1,AAAA " + headers["webhook-signature"];
        Assert.Equal("evt_1", polar.VerifyWebhook(headers, Bytes(body)).EventId);
    }

    [Fact]
    public void A_half_set_up_polar_is_named()
    {
        Assert.Null(PolarBilling.Missing(Options()));
        var o = Options();
        o.WebhookSecret = "";
        Assert.Equal("Service__Billing__Polar__WebhookSecret", PolarBilling.Missing(o));
    }

    [Fact]
    public void The_sandbox_and_production_are_different_addresses_and_production_is_the_default()
    {
        Assert.Equal("https://api.polar.sh/v1/", new PolarOptions().BaseAddress.ToString());
        Assert.Equal("https://sandbox-api.polar.sh/v1/", new PolarOptions { Server = "Sandbox" }.BaseAddress.ToString());
    }
}
