using System.Globalization;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Yaparr.Cloud;

/// <summary>The settings of the Polar provider (<c>Service__Billing__Polar__…</c>). The token and the webhook secret are secrets: they come from the environment and are never written anywhere else.</summary>
public sealed class PolarOptions
{
    /// <summary><c>production</c> (the default) or <c>sandbox</c>: Polar's sandbox is a separate service with its own accounts, products and tokens.</summary>
    public string Server { get; set; } = "production";

    /// <summary>An organization access token (scopes: checkouts:write, customer_sessions:write, subscriptions:read, subscriptions:write).</summary>
    public string AccessToken { get; set; } = "";

    /// <summary>The secret of the webhook endpoint (<c>whsec_…</c>), which points at <c>/billing/webhook/polar</c>.</summary>
    public string WebhookSecret { get; set; } = "";

    /// <summary>The ids of the products, by what the service calls them (the monthly and the yearly one of the cloud plan).</summary>
    public string CloudMonthlyProduct { get; set; } = "";
    public string CloudYearlyProduct { get; set; } = "";

    /// <summary>The version of Polar's API that this adapter was written against. It is sent with every call so that a change by Polar is a change we choose.</summary>
    public string ApiVersion { get; set; } = "2026-10";

    public Uri BaseAddress => new(Server.Equals("sandbox", StringComparison.OrdinalIgnoreCase) ? "https://sandbox-api.polar.sh/v1/" : "https://api.polar.sh/v1/");
}

/// <summary>Polar (a merchant of record). Everything Polar-shaped is in this file: the rest of the service sees only <see cref="IBillingProvider"/>.</summary>
public sealed class PolarBilling(PolarOptions options, HttpClient http, Func<DateTimeOffset>? clock = null) : IBillingProvider
{
    public const string ProviderName = "polar";
    private static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(5);
    private readonly Func<DateTimeOffset> _now = clock ?? (() => DateTimeOffset.UtcNow);

    public string Name => ProviderName;

    /// <summary>Whether the settings are enough to use Polar; says what is missing, for a service that is started with a half of them.</summary>
    public static string? Missing(PolarOptions o) =>
        o.AccessToken.Length == 0 ? "Service__Billing__Polar__AccessToken"
        : o.WebhookSecret.Length == 0 ? "Service__Billing__Polar__WebhookSecret"
        : o.CloudMonthlyProduct.Length == 0 ? "Service__Billing__Polar__CloudMonthlyProduct"
        : o.CloudYearlyProduct.Length == 0 ? "Service__Billing__Polar__CloudYearlyProduct"
        : null;

    public static HttpClient CreateClient(PolarOptions o)
    {
        var client = new HttpClient { BaseAddress = o.BaseAddress, Timeout = TimeSpan.FromSeconds(15), MaxResponseContentBufferSize = 4 * 1024 * 1024 };
        return client;
    }

    private HttpRequestMessage Request(HttpMethod method, string path, object? body = null)
    {
        var request = new HttpRequestMessage(method, new Uri(options.BaseAddress, path));
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + options.AccessToken);
        request.Headers.TryAddWithoutValidation("Polar-Version", options.ApiVersion);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        if (body is not null) request.Content = JsonContent.Create(body);
        return request;
    }

    private async Task<JsonElement?> SendAsync(HttpRequestMessage request, CancellationToken cancel, bool notFoundIsNull = false)
    {
        using var response = await http.SendAsync(request, cancel);
        if (notFoundIsNull && response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        // A refusal says what it was in its status; the body can hold a person's details, so it is not put in the exception.
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"Polar answered {(int)response.StatusCode} to {request.Method} {request.RequestUri!.AbsolutePath}.", null, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>(cancel);
    }

    public async Task<Uri> StartCheckoutAsync(CheckoutRequest request, CancellationToken cancel)
    {
        var product = request.Interval == BillingInterval.Yearly ? options.CloudYearlyProduct : options.CloudMonthlyProduct;
        var body = new Dictionary<string, object?>
        {
            ["products"] = new[] { product },
            ["external_customer_id"] = request.AccountRef,
            ["customer_email"] = request.Email,
            ["allow_trial"] = false,
            ["success_url"] = request.SuccessUrl.ToString(),
            ["return_url"] = request.ReturnUrl.ToString(),
        };
        var made = await SendAsync(Request(HttpMethod.Post, "checkouts/", body), cancel) ?? throw new InvalidOperationException("Polar made no checkout.");
        return Url(made, "url");
    }

    public async Task<Uri> PortalAsync(string providerCustomerId, Uri returnTo, CancellationToken cancel)
    {
        var made = await SendAsync(Request(HttpMethod.Post, "customer-sessions/", new { customer_id = providerCustomerId, return_url = returnTo.ToString() }), cancel)
            ?? throw new InvalidOperationException("Polar made no customer session.");
        return Url(made, "customer_portal_url");
    }

    private static Uri Url(JsonElement from, string property) =>
        from.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String && Uri.TryCreate(v.GetString(), UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            ? uri
            : throw new InvalidOperationException($"Polar's answer has no usable {property}.");

    // ---- webhooks (Standard Webhooks) ----

    public BillingEvent VerifyWebhook(IReadOnlyDictionary<string, string> headers, ReadOnlyMemory<byte> rawBody)
    {
        if (!headers.TryGetValue("webhook-id", out var id) || id.Length == 0 || id.Length > 200
            || !headers.TryGetValue("webhook-timestamp", out var stamp) || !headers.TryGetValue("webhook-signature", out var signatures))
            throw new BadSignatureException("The webhook is not signed.");
        if (!long.TryParse(stamp, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)) throw new BadSignatureException("The timestamp is not a number.");
        if ((_now() - DateTimeOffset.FromUnixTimeSeconds(seconds)).Duration() > Tolerance) throw new BadSignatureException("The timestamp is too far from now.");

        var signed = Encoding.UTF8.GetBytes($"{id}.{stamp}.").Concat(rawBody.ToArray()).ToArray();
        var offered = signatures.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Split(',', 2)).Where(p => p.Length == 2 && p[0] == "v1")
            .Select(p => { try { return Convert.FromBase64String(p[1]); } catch (FormatException) { return null; } })
            .OfType<byte[]>().ToList();

        // A secret made since 8 September 2026 is a Standard Webhooks one (the key is the base64 after "whsec_"); an older one is keyed by the whole text.
        var good = false;
        foreach (var key in Keys(options.WebhookSecret))
        {
            var expected = HMACSHA256.HashData(key, signed);
            foreach (var one in offered) good |= CryptographicOperations.FixedTimeEquals(expected, one);
        }
        if (!good) throw new BadSignatureException("The signature does not match.");

        JsonElement json;
        try { json = JsonSerializer.Deserialize<JsonElement>(rawBody.Span); }
        catch (JsonException) { throw new BadSignatureException("The body is not JSON."); }
        string? type = json.ValueKind == JsonValueKind.Object && json.TryGetProperty("type", out var t) ? t.GetString() : null;
        JsonElement data = json.ValueKind == JsonValueKind.Object && json.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object ? d : default;
        if (type is null || data.ValueKind != JsonValueKind.Object) return new BillingEvent(id, null, null);

        // What to look at, and nothing else: the state itself is asked for from Polar.
        if (type.StartsWith("subscription.", StringComparison.Ordinal))
            return new BillingEvent(id, Text(data, "id"), data.TryGetProperty("customer", out var c) && c.ValueKind == JsonValueKind.Object ? Text(c, "external_id") : null);
        // A refund that went through ends the subscription (the design's rule: money back means over). Not one that only corrects tax.
        if (type is "refund.created" or "refund.updated")
            return new BillingEvent(id, Text(data, "subscription_id"), null,
                EndIt: Text(data, "status") == "succeeded" && Text(data, "reason") is not ("tax_refund" or "vat_reverse_charge_correction"));
        if (type.StartsWith("customer.", StringComparison.Ordinal))
            return new BillingEvent(id, null, Text(data, "external_id"));
        return new BillingEvent(id, null, null);
    }

    private static IEnumerable<byte[]> Keys(string secret)
    {
        var legacy = Encoding.UTF8.GetBytes(secret);
        var text = secret.StartsWith("whsec_", StringComparison.Ordinal) ? secret["whsec_".Length..] : secret;
        byte[]? standard = null;
        try { standard = Convert.FromBase64String(text); } catch (FormatException) { }
        if (standard is { Length: > 0 }) yield return standard;
        yield return legacy;
    }

    // ---- reading subscriptions ----

    public async Task<SubscriptionState?> GetSubscriptionAsync(string providerSubscriptionId, CancellationToken cancel)
    {
        if (!Guid.TryParse(providerSubscriptionId, out var id)) return null;
        var found = await SendAsync(Request(HttpMethod.Get, "subscriptions/" + id.ToString("D")), cancel, notFoundIsNull: true);
        return found is { } one ? Map(one) : null;
    }

    public async Task<IReadOnlyList<SubscriptionState>> FindAsync(string accountRef, CancellationToken cancel)
    {
        var all = new List<SubscriptionState>();
        for (var page = 1; page <= 20; page++)
        {
            var found = await SendAsync(Request(HttpMethod.Get, $"subscriptions/?external_customer_id={Uri.EscapeDataString(accountRef)}&limit=100&page={page}"), cancel);
            if (found is not { } list || !list.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) break;
            foreach (var item in items.EnumerateArray())
                if (Map(item) is { } state) all.Add(state);
            var pages = list.TryGetProperty("pagination", out var p) && p.TryGetProperty("max_page", out var m) && m.TryGetInt32(out var max) ? max : page;
            if (page >= pages) break;
        }
        return all;
    }

    /// <summary>Polar's words to ours. Polar keeps a cancelled-for-later subscription "active" with <c>cancel_at_period_end</c>; one that has ended, or never was paid for, is anything else.</summary>
    internal SubscriptionState? Map(JsonElement s)
    {
        var id = Text(s, "id");
        var customer = s.TryGetProperty("customer", out var c) && c.ValueKind == JsonValueKind.Object ? c : default;
        var reference = customer.ValueKind == JsonValueKind.Object ? Text(customer, "external_id") : null;
        var customerId = Text(s, "customer_id") ?? (customer.ValueKind == JsonValueKind.Object ? Text(customer, "id") : null);
        // A subscription that is not one of an account's (made in Polar's dashboard, say) is not ours to look at.
        if (id is null || reference is null || customerId is null) return null;

        var cancelAtEnd = s.TryGetProperty("cancel_at_period_end", out var ce) && ce.ValueKind == JsonValueKind.True;
        var status = Text(s, "status") switch
        {
            "active" or "trialing" => cancelAtEnd ? SubscriptionStatus.Canceling : SubscriptionStatus.Active,
            "past_due" => SubscriptionStatus.PastDue,
            _ => SubscriptionStatus.Ended, // canceled, unpaid, incomplete, incomplete_expired, paused, trial_ended, and anything Polar adds
        };
        DateTimeOffset? end = null;
        if (Text(s, "current_period_end") is { } e && DateTimeOffset.TryParse(e, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)) end = parsed;
        // There is one plan, "cloud", in a monthly and a yearly product.
        return new SubscriptionState(customerId, id, reference, "cloud", status, end, cancelAtEnd);
    }

    private static string? Text(JsonElement from, string property) =>
        from.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public async Task CancelAsync(string providerSubscriptionId, bool immediately, CancellationToken cancel)
    {
        if (!Guid.TryParse(providerSubscriptionId, out var id)) return;
        var path = "subscriptions/" + id.ToString("D");
        try
        {
            if (immediately) await SendAsync(Request(HttpMethod.Delete, path), cancel);
            else await SendAsync(Request(HttpMethod.Patch, path, new { cancel_at_period_end = true }), cancel);
        }
        catch (HttpRequestException e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound) { }
        // Polar answers 403 to one that is already over, and to a token that may not: only the first is what was wanted.
        catch (HttpRequestException e) when (e.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            var now = await GetSubscriptionAsync(providerSubscriptionId, cancel);
            if (now is { Status: not SubscriptionStatus.Ended }) throw;
        }
    }
}
