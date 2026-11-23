namespace Yaparr.Cloud;

// Paid plans for the hosted service (design: docs/billing.md in the Yaparr repository). Nothing here knows a payment provider: the service reasons about what an account
// may use, and a provider (an IBillingProvider) is only how that gets decided. Billing is off unless Service__Billing__Provider names one, so a service that is run for one's own
// friends or one's own network is exactly what it was.

/// <summary>Where a subscription stands, in the service's own words. A provider's states are mapped to these in its adapter and nowhere else.</summary>
public enum SubscriptionStatus
{
    /// <summary>Paid, and renews.</summary>
    Active,

    /// <summary>A payment failed and is being retried: still paid for until the end of the period (and the grace days).</summary>
    PastDue,

    /// <summary>Paid until the end of the period, and then it ends: the person cancelled.</summary>
    Canceling,

    /// <summary>Over: ran out, was revoked, was refunded. It gives nothing, whatever its dates say.</summary>
    Ended,
}

/// <summary>
/// One subscription an account has had, as the provider last said it stood. The provider's own ids are kept so that it can be asked about it again and so that the account can be
/// sent to the provider's page for it. <see cref="PeriodEnd"/> is the end of the period that was paid for; what the account may use is decided from it by <see cref="AccountPlan"/>.
/// </summary>
public sealed record Subscription(string Account, string Provider, string ProviderCustomerId, string ProviderSubscriptionId, string Plan, SubscriptionStatus Status,
    DateTimeOffset PeriodEnd, DateTimeOffset UpdatedAt);

/// <summary>What an account may do.</summary>
public enum PlanState
{
    /// <summary>Billing is off on this service: everything is allowed, as it always was.</summary>
    Off,

    /// <summary>The account has the lifetime pass (everyone who had an account before billing, and anyone the owner gives it to): it is never asked to pay.</summary>
    Lifetime,

    /// <summary>Some subscription of the account still covers now.</summary>
    Subscribed,

    /// <summary>Billing is on, and nothing covers the account now: no signing in on a device and no syncing, but nothing is removed and the account can still take its data away and delete itself.</summary>
    Lapsed,
}

/// <summary>What an account is, and until when. The only place that decides.</summary>
public static class AccountPlan
{
    /// <summary>The pass of an account that is never asked to pay.</summary>
    public const string Lifetime = AccountDirectory.LifetimePass;

    /// <summary>The pass of an account that is, once billing is on: it follows its subscriptions.</summary>
    public const string None = "none";

    public static bool IsPass(string? pass) => pass is Lifetime or None;

    /// <summary>Whether a subscription covers <paramref name="now"/>: not over, and not past the end of its period by more than the grace days (a payment that lands a day late is not a lock-out).</summary>
    public static bool Covers(Subscription subscription, DateTimeOffset now, TimeSpan grace) =>
        subscription.Status != SubscriptionStatus.Ended && subscription.PeriodEnd + grace > now;

    public static PlanState Of(bool billingOn, string? pass, IReadOnlyList<Subscription> subscriptions, DateTimeOffset now, TimeSpan grace) =>
        !billingOn ? PlanState.Off
        : pass == Lifetime ? PlanState.Lifetime
        : subscriptions.Any(s => Covers(s, now, grace)) ? PlanState.Subscribed
        : PlanState.Lapsed;

    /// <summary>The subscription that covers the account for longest, or null.</summary>
    public static Subscription? Current(IReadOnlyList<Subscription> subscriptions, DateTimeOffset now, TimeSpan grace) =>
        subscriptions.Where(s => Covers(s, now, grace)).OrderByDescending(s => s.PeriodEnd).FirstOrDefault();
}

/// <summary>What <c>GET /v1/plan</c> says.</summary>
/// <summary><see cref="PastDue"/>: a payment failed and is being retried, with nothing else paid for: still covered until <see cref="EndsAt"/>, and the person should update their card.</summary>
public sealed record PlanDescription(PlanState State, string? Plan, DateTimeOffset? EndsAt, bool WillRenew, bool PastDue = false);

/// <summary>
/// Who may sign in on a device and use the cloud's services, once billing is on. One question (<see cref="StateOf"/>) and one answer for a refusal (<see cref="Refuse"/>), asked at the places
/// a device token is made and at each service a subscription pays for. With billing off every question answers "yes" without looking anything up.
/// </summary>
public sealed class PlanGate(ServiceOptions options, IAccounts accounts, ISubscriptions subscriptions)
{
    private readonly Func<DateTimeOffset> _now = () => DateTimeOffset.UtcNow;

    internal PlanGate(ServiceOptions options, IAccounts accounts, ISubscriptions subscriptions, Func<DateTimeOffset> now) : this(options, accounts, subscriptions) => _now = now;

    public bool Enabled => options.Billing.Provider.Length > 0;

    private TimeSpan Grace => TimeSpan.FromDays(Math.Max(0, options.Billing.GraceDays));

    public PlanState StateOf(string account) =>
        !Enabled ? PlanState.Off : AccountPlan.Of(true, accounts.PassOf(account), subscriptions.Of(account), _now(), Grace);

    public PlanDescription Describe(string account)
    {
        var state = StateOf(account);
        if (state is PlanState.Off or PlanState.Lifetime) return new PlanDescription(state, null, null, false);
        var subs = subscriptions.Of(account);
        if (AccountPlan.Current(subs, _now(), Grace) is { } current)
        {
            var covering = subs.Where(s => AccountPlan.Covers(s, _now(), Grace)).ToList();
            var renews = covering.Any(s => s.Status == SubscriptionStatus.Active);
            return new PlanDescription(state, current.Plan, current.PeriodEnd, renews, PastDue: !renews && covering.Any(s => s.Status == SubscriptionStatus.PastDue));
        }
        // Lapsed: when the last one ran out, if it ever was one.
        var last = subs.OrderByDescending(s => s.PeriodEnd).FirstOrDefault();
        return new PlanDescription(state, last?.Plan, last?.PeriodEnd, false);
    }

    public bool IsLapsed(string account) => StateOf(account) == PlanState.Lapsed;

    /// <summary>The answer for an account that may not (<c>402</c>, the code is for programs and the sentence is for people), or null when it may.</summary>
    public IResult? Refuse(string account) => IsLapsed(account) ? Refusal : null;

    public static IResult Refusal { get; } = Results.Json(
        new { error = "This account's Yaparr Cloud subscription has ended, so it cannot sync or sign in on a new device until it is renewed. Nothing is deleted.", code = "subscription_required" },
        statusCode: StatusCodes.Status402PaymentRequired);
}

/// <summary>The settings of billing (<c>Service__Billing__…</c>). Nothing is set by default, and with no <see cref="Provider"/> billing is off.</summary>
public sealed class BillingOptions
{
    /// <summary>The payment provider, by name (a provider this build has). Empty: billing is off.</summary>
    public string Provider { get; set; } = "";

    /// <summary>Days after the end of a paid period that an account is still covered (3), so that a payment that lands a day late does not lock anyone out.</summary>
    public int GraceDays { get; set; } = 3;

    /// <summary>Hours that an account made for someone who has not paid yet is kept (24).</summary>
    public int PendingHours { get; set; } = 24;
}
