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
/// A provider for tests: it turns billing on, keeps the subscriptions a test sets as "what the provider says", records what it was asked, and can be made unreachable. A webhook is JSON
/// <c>{"id": "evt_1", "subscription": "sub_1"}</c> with the header <c>x-fake-signature: valid</c> (anything else is a bad signature).
/// </summary>
internal sealed class FakeBilling(string name = "fake") : IBillingProvider
{
    public string Name => name;

    /// <summary>What the provider says each subscription is now, by its id. Absent: it does not know it.</summary>
    public Dictionary<string, SubscriptionState> States { get; } = new();

    public List<CheckoutRequest> Checkouts { get; } = new();
    public List<(string Customer, Uri Return)> Portals { get; } = new();
    public List<(string Subscription, bool Immediately)> Cancelled { get; } = new();
    public List<string> Asked { get; } = new();

    /// <summary>True: every call that goes to the provider fails as a network failure does.</summary>
    public bool Down { get; set; }

    private void Reach()
    {
        if (Down) throw new HttpRequestException("The provider could not be reached.");
    }

    public Task<Uri> StartCheckoutAsync(CheckoutRequest request, CancellationToken cancel)
    {
        Reach();
        Checkouts.Add(request);
        return Task.FromResult(new Uri("https://pay.example/checkout/" + Checkouts.Count));
    }

    public Task<Uri> PortalAsync(string providerCustomerId, Uri returnTo, CancellationToken cancel)
    {
        Reach();
        Portals.Add((providerCustomerId, returnTo));
        return Task.FromResult(new Uri("https://pay.example/portal/" + providerCustomerId));
    }

    public BillingEvent VerifyWebhook(IReadOnlyDictionary<string, string> headers, ReadOnlyMemory<byte> rawBody)
    {
        if (!headers.TryGetValue("x-fake-signature", out var signature) || signature != "valid") throw new BadSignatureException("not signed");
        var json = JsonSerializer.Deserialize<JsonElement>(rawBody.Span);
        return new BillingEvent(json.GetProperty("id").GetString()!, json.TryGetProperty("subscription", out var sub) ? sub.GetString() : null, null,
            EndIt: json.TryGetProperty("end", out var end) && end.GetBoolean());
    }

    public Task<SubscriptionState?> GetSubscriptionAsync(string providerSubscriptionId, CancellationToken cancel)
    {
        Reach();
        Asked.Add(providerSubscriptionId);
        return Task.FromResult(States.GetValueOrDefault(providerSubscriptionId));
    }

    public Task<IReadOnlyList<SubscriptionState>> FindAsync(string accountRef, CancellationToken cancel)
    {
        Reach();
        return Task.FromResult<IReadOnlyList<SubscriptionState>>(States.Values.Where(s => s.AccountRef == accountRef).ToList());
    }

    public Task CancelAsync(string providerSubscriptionId, bool immediately, CancellationToken cancel)
    {
        Reach();
        Cancelled.Add((providerSubscriptionId, immediately));
        // As a provider does: an ended one reports itself ended.
        if (immediately && States.TryGetValue(providerSubscriptionId, out var held)) States[providerSubscriptionId] = held with { Status = SubscriptionStatus.Ended };
        return Task.CompletedTask;
    }
}
