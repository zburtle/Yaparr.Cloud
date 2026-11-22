using System.Security.Cryptography;

namespace Yaparr.Cloud;

/// <summary>
/// What the service knows of what accounts pay for: the subscriptions a provider has told it about, the random id each account is known by to a provider (never the email address),
/// and the webhook events already handled. Two implementations: <see cref="SubscriptionStore"/> in a file and <c>PostgresSubscriptions</c> in the database, held to the same behaviour by the
/// same tests. Nothing in it is a card number or an invoice: those are the provider's.
/// </summary>
public interface ISubscriptions
{
    /// <summary>Stores a subscription, replacing what was known of the same one (the same provider and subscription id).</summary>
    void Upsert(Subscription subscription);

    /// <summary>Every subscription an account has had, newest period first.</summary>
    IReadOnlyList<Subscription> Of(string account);

    /// <summary>The id an account is known by to a provider: random, made the first time it is asked for, and the same afterwards.</summary>
    string BillingIdOf(string account);

    /// <summary>The account a billing id belongs to, or null.</summary>
    string? AccountOf(string? billingId);

    /// <summary>Notes that a webhook event was handled. False if it had been already (the same event again).</summary>
    bool TryRecordEvent(string provider, string eventId, DateTimeOffset at);

    /// <summary>Forgets an event, so that its next delivery is handled (the provider could not be asked, and the event is to be retried).</summary>
    void ForgetEvent(string provider, string eventId);

    /// <summary>Forgets events received before a time. Returns how many.</summary>
    int PurgeEvents(DateTimeOffset before);

    /// <summary>Forgets everything about an account: its subscriptions and its billing id.</summary>
    void RemoveAccount(string account);
}

internal static class BillingIds
{
    /// <summary>128 random bits, in the URL-safe alphabet.</summary>
    public static string Make() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
