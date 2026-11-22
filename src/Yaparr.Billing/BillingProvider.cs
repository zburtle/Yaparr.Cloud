namespace Yaparr.Cloud;

// ---- what a payment provider has to be (design: "The provider interface") ----

public enum BillingInterval { Monthly, Yearly }

/// <summary>What is needed to send someone to pay. <see cref="AccountRef"/> is the account's random billing id: the provider stores it as its reference for the person, and never the email.</summary>
public sealed record CheckoutRequest(string AccountRef, string Email, string Plan, BillingInterval Interval, Uri SuccessUrl, Uri ReturnUrl);

/// <summary>
/// What a webhook is about. It carries no state: the service asks the provider for that (<see cref="IBillingProvider.GetSubscriptionAsync"/>). <see cref="EndIt"/> is for the one thing
/// a provider does not do by itself: money was given back for the subscription (a refund), which the design says ends it, but providers leave the subscription running. The service
/// then ends it at the provider (<see cref="IBillingProvider.CancelAsync"/>, at once) before it looks.
/// </summary>
public sealed record BillingEvent(string EventId, string? ProviderSubscriptionId, string? AccountRef, bool EndIt = false);

/// <summary>A subscription as the provider says it stands now, in the service's own terms.</summary>
public sealed record SubscriptionState(string ProviderCustomerId, string ProviderSubscriptionId, string AccountRef, string Plan, SubscriptionStatus Status,
    DateTimeOffset? CurrentPeriodEnd, bool CancelAtPeriodEnd);

/// <summary>A signature that does not check, or a timestamp that is too old.</summary>
public sealed class BadSignatureException(string message) : Exception(message);

/// <summary>
/// A way to be paid: Polar, and Stripe or Google Play Billing if one is ever needed. This is the only place a provider's words (products, customers, event names, states) appear; everything
/// else in the service is the types above.
/// </summary>
public interface IBillingProvider
{
    /// <summary>Its name: what <c>Service__Billing__Provider</c> says, what is kept with each <see cref="Subscription"/>, and the <c>{provider}</c> in the webhook's address.</summary>
    string Name { get; }

    /// <summary>A page where the person pays.</summary>
    Task<Uri> StartCheckoutAsync(CheckoutRequest request, CancellationToken cancel);

    /// <summary>A page where they update the card, see invoices and cancel. Made fresh at each click: it expires.</summary>
    Task<Uri> PortalAsync(string providerCustomerId, Uri returnTo, CancellationToken cancel);

    /// <summary>Checks the signature on the raw body of a webhook and says what it is about. Throws <see cref="BadSignatureException"/>. Never trusted for the state.</summary>
    BillingEvent VerifyWebhook(IReadOnlyDictionary<string, string> headers, ReadOnlyMemory<byte> rawBody);

    Task<SubscriptionState?> GetSubscriptionAsync(string providerSubscriptionId, CancellationToken cancel);

    /// <summary>The subscriptions the provider has for an account reference (the page people return to after paying, and reconciling).</summary>
    Task<IReadOnlyList<SubscriptionState>> FindAsync(string accountRef, CancellationToken cancel);

    /// <summary>Ends a subscription (deleting an account must not leave it being charged).</summary>
    Task CancelAsync(string providerSubscriptionId, bool immediately, CancellationToken cancel);
}
