using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Yaparr.Cloud;

/// <summary>What became of a webhook (the endpoint turns it into an answer).</summary>
public enum WebhookOutcome
{
    /// <summary>A subscription was stored.</summary>
    Handled,

    /// <summary>Nothing to do: the event names no subscription, or one that is nobody's here, or that the provider does not know.</summary>
    Ignored,

    /// <summary>The same event again.</summary>
    Duplicate,

    /// <summary>The signature did not check, or the timestamp was too old.</summary>
    BadSignature,

    /// <summary>No provider of that name.</summary>
    UnknownProvider,

    /// <summary>The provider could not be asked just now: the event was forgotten, so its next delivery is handled.</summary>
    TryAgain,
}

/// <summary>Where a person is sent to pay, or why not.</summary>
public enum StartOutcome { Redirect, HasAccount, ProviderFailed, PaidNotFinished }

public sealed record StartResult(StartOutcome Outcome, Uri? Url = null);

/// <summary>
/// What the service does with a payment provider (docs/billing.md): turns a webhook into what is stored, sends people to pay and to manage what they pay, cancels what an account pays
/// for when it is deleted, and keeps what is stored right when a webhook is lost. It knows no provider by name: it only uses <see cref="IBillingProvider"/>.
/// </summary>
public sealed class BillingService
{
    /// <summary>The most a webhook may carry. A provider's event is a few kilobytes; anything near this is not one.</summary>
    public const int MaxWebhookBytes = 256 * 1024;

    private readonly ServiceOptions _options;
    private readonly IAccounts _accounts;
    private readonly ISubscriptions _subscriptions;
    private readonly Dictionary<string, IBillingProvider> _providers;
    private readonly Func<DateTimeOffset> _now;
    private readonly ILogger _log;

    public BillingService(ServiceOptions options, IAccounts accounts, ISubscriptions subscriptions, IEnumerable<IBillingProvider> providers, ILoggerFactory logs)
        : this(options, accounts, subscriptions, providers, logs, () => DateTimeOffset.UtcNow) { }

    internal BillingService(ServiceOptions options, IAccounts accounts, ISubscriptions subscriptions, IEnumerable<IBillingProvider> providers, ILoggerFactory logs, Func<DateTimeOffset> now)
    {
        _options = options;
        _accounts = accounts;
        _subscriptions = subscriptions;
        _providers = providers.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
        _now = now;
        _log = logs.CreateLogger("Yaparr.Cloud.Billing");
    }

    public bool Enabled => _options.Billing.Provider.Length > 0;

    public IBillingProvider? Provider(string? name) => name != null && _providers.TryGetValue(name, out var provider) ? provider : null;

    /// <summary>The provider new payments go to.</summary>
    public IBillingProvider? Default => Provider(_options.Billing.Provider);

    // ---- what a provider says becomes what is stored ----

    /// <summary>
    /// Stores a subscription as the provider now says it stands. False when it is nobody's here: the reference the provider holds is not one this service made, or its account is gone.
    /// A provider's own states are already the service's (the adapter mapped them), except that cancel-at-period-end is a flag there and a status here.
    /// </summary>
    internal bool Store(string provider, SubscriptionState state)
    {
        if (_subscriptions.AccountOf(state.AccountRef) is not { } account || !_accounts.Exists(account))
        {
            _log.LogInformation("billing: a subscription that is nobody's here ({Provider})", provider);
            return false;
        }
        var now = _now();
        var status = state.Status == SubscriptionStatus.Active && state.CancelAtPeriodEnd ? SubscriptionStatus.Canceling : state.Status;
        // No end given means nothing is paid for beyond now (the grace days still apply).
        _subscriptions.Upsert(new Subscription(account, provider, state.ProviderCustomerId, state.ProviderSubscriptionId, state.Plan, status, state.CurrentPeriodEnd ?? now, now));
        return true;
    }

    private static bool Unreachable(Exception ex, CancellationToken cancel) =>
        ex is HttpRequestException or IOException or TimeoutException || ex is OperationCanceledException && !cancel.IsCancellationRequested;

    /// <summary>
    /// A webhook, in the order the design says: the signature on the raw bytes, the same event only once, then the provider is asked what the subscription is now and that is what is
    /// stored. Nothing is taken from the body but which subscription it is about, so the order events arrive in, repeats, and what each event is called do not matter.
    /// </summary>
    public async Task<WebhookOutcome> HandleWebhookAsync(string providerName, IReadOnlyDictionary<string, string> headers, ReadOnlyMemory<byte> rawBody, CancellationToken cancel)
    {
        if (Provider(providerName) is not { } provider) return WebhookOutcome.UnknownProvider;
        BillingEvent received;
        try { received = provider.VerifyWebhook(headers, rawBody); }
        catch (BadSignatureException)
        {
            _log.LogInformation("billing: bad signature ({Provider})", provider.Name);
            return WebhookOutcome.BadSignature;
        }
        if (!_subscriptions.TryRecordEvent(provider.Name, received.EventId, _now()))
        {
            _log.LogInformation("billing: the same event again ({Provider})", provider.Name);
            return WebhookOutcome.Duplicate;
        }
        if (received.ProviderSubscriptionId is not { } id)
        {
            _log.LogInformation("billing: an event that names no subscription ({Provider})", provider.Name);
            return WebhookOutcome.Ignored;
        }
        SubscriptionState? state;
        try
        {
            // Money given back ends the subscription; the provider leaves it running, so it is ended there first (ending one that is over is not an error).
            if (received.EndIt)
            {
                await provider.CancelAsync(id, immediately: true, cancel);
                _log.LogInformation("billing: ended a refunded subscription ({Provider})", provider.Name);
            }
            state = await provider.GetSubscriptionAsync(id, cancel);
        }
        catch (Exception ex) when (Unreachable(ex, cancel))
        {
            // So that the provider's next try is handled and not taken for a repeat.
            _subscriptions.ForgetEvent(provider.Name, received.EventId);
            _log.LogWarning("billing: could not ask the provider ({Provider})", provider.Name);
            return WebhookOutcome.TryAgain;
        }
        if (state == null) return WebhookOutcome.Ignored;
        var stored = Store(provider.Name, state);
        if (stored) _log.LogInformation("billing: webhook subscription ({Provider})", provider.Name);
        return stored ? WebhookOutcome.Handled : WebhookOutcome.Ignored;
    }

    // ---- keeping what is stored right ----

    /// <summary>
    /// Asks the provider again about every subscription that is not over and has not been heard of for three days, or whose period ended a day ago: the safety net under a lost webhook.
    /// A provider that cannot be reached, or does not know the subscription, leaves it as it was. Returns how many were updated.
    /// </summary>
    public async Task<int> ReconcileAsync(CancellationToken cancel)
    {
        var now = _now();
        var updated = 0;
        foreach (var held in _subscriptions.Stale(now - TimeSpan.FromDays(3), now - TimeSpan.FromDays(1)))
        {
            if (Provider(held.Provider) is not { } provider) continue;
            try
            {
                if (await provider.GetSubscriptionAsync(held.ProviderSubscriptionId, cancel) is { } state && Store(provider.Name, state)) updated++;
            }
            catch (Exception ex) when (Unreachable(ex, cancel))
            {
                _log.LogWarning("billing: could not ask the provider ({Provider})", provider.Name);
            }
        }
        if (updated > 0) _log.LogInformation("billing: reconciled {Count}", updated);
        return updated;
    }

    /// <summary>Asks the provider what it holds for an account (the page people come back to after paying, before the webhook has arrived). False if it could not be asked.</summary>
    public async Task<bool> RefreshAccountAsync(string account, CancellationToken cancel)
    {
        if (Default is not { } provider) return false;
        try
        {
            foreach (var state in await provider.FindAsync(_subscriptions.BillingIdOf(account), cancel)) Store(provider.Name, state);
            return true;
        }
        catch (Exception ex) when (Unreachable(ex, cancel))
        {
            _log.LogWarning("billing: could not ask the provider ({Provider})", provider.Name);
            return false;
        }
    }

    /// <summary>Removes the accounts made for someone who never paid and never came back: the setup token ran out and nothing was ever paid for. Returns how many.</summary>
    public int PurgeUnpaid()
    {
        var removed = 0;
        foreach (var name in _accounts.ExpiredPending(_now()))
        {
            if (_subscriptions.Of(name).Count > 0) continue;   // paid, and has not chosen a password yet: theirs
            if (!_accounts.Remove(name)) continue;
            _subscriptions.RemoveAccount(name);
            removed++;
        }
        if (removed > 0) _log.LogInformation("billing: removed {Count} sign-ups that were never paid", removed);
        return removed;
    }

    /// <summary>Forgets the webhook events of more than 30 days ago (a provider does not repeat one after that).</summary>
    public int PurgeEvents() => _subscriptions.PurgeEvents(_now() - TimeSpan.FromDays(30));

    // ---- sending people to the provider ----

    /// <summary>
    /// Someone with no account pays first: an account is made for the address (no pass, no password) with a one-use token, which is in the address the provider sends them back to; they choose
    /// a password there once the payment has arrived. An address that already has an account is not touched.
    /// </summary>
    public async Task<StartResult> StartNewAsync(string email, BillingInterval interval, Uri successBase, Uri returnUrl, CancellationToken cancel)
    {
        if (Default is not { } provider) return new StartResult(StartOutcome.ProviderFailed);
        // An address that has paid is never sent to pay again, and its one-use link is never replaced from outside: whoever types the address would otherwise cancel the payer's link and be
        // charged on top. An address with a password is told to sign in; one that paid and has not chosen a password yet is told to finish with the link it was given (or ask the person who runs the service).
        if (_subscriptions.Of(email).Count > 0)
            return new StartResult(_accounts.HasPassword(email) ? StartOutcome.HasAccount : StartOutcome.PaidNotFinished);
        if (_accounts.StartPending(email, TimeSpan.FromHours(Math.Max(1, _options.Billing.PendingHours))) is not { } token) return new StartResult(StartOutcome.HasAccount);
        var success = new UriBuilder(successBase) { Query = "t=" + Uri.EscapeDataString(token) }.Uri;
        return await CheckoutAsync(provider, email, interval, success, returnUrl, cancel);
    }

    /// <summary>An account that exists subscribes or renews.</summary>
    public async Task<StartResult> StartForAccountAsync(string account, BillingInterval interval, Uri successUrl, Uri returnUrl, CancellationToken cancel) =>
        Default is { } provider ? await CheckoutAsync(provider, account, interval, successUrl, returnUrl, cancel) : new StartResult(StartOutcome.ProviderFailed);

    private async Task<StartResult> CheckoutAsync(IBillingProvider provider, string account, BillingInterval interval, Uri success, Uri back, CancellationToken cancel)
    {
        try
        {
            var url = await provider.StartCheckoutAsync(new CheckoutRequest(_subscriptions.BillingIdOf(account), account, "cloud", interval, success, back), cancel);
            return new StartResult(StartOutcome.Redirect, url);
        }
        catch (Exception ex) when (Unreachable(ex, cancel))
        {
            _log.LogWarning("billing: could not start a checkout ({Provider})", provider.Name);
            return new StartResult(StartOutcome.ProviderFailed);
        }
    }

    /// <summary>The provider's page for managing what an account pays, or null when it pays for nothing through one.</summary>
    public async Task<Uri?> PortalAsync(string account, Uri returnTo, CancellationToken cancel)
    {
        var latest = _subscriptions.Of(account).FirstOrDefault();
        if (latest == null || Provider(latest.Provider) is not { } provider) return null;
        try { return await provider.PortalAsync(latest.ProviderCustomerId, returnTo, cancel); }
        catch (Exception ex) when (Unreachable(ex, cancel))
        {
            _log.LogWarning("billing: could not open the portal ({Provider})", provider.Name);
            return null;
        }
    }

    /// <summary>
    /// Ends everything an account is paying for, at once, so that deleting it does not leave it being charged. True when nothing is left running (there was nothing, or it was all ended);
    /// false when a provider could not be asked, and then the account is not to be deleted.
    /// </summary>
    public async Task<bool> CancelAllAsync(string account, CancellationToken cancel)
    {
        var allEnded = true;
        foreach (var s in _subscriptions.Of(account).Where(s => s.Status != SubscriptionStatus.Ended))
        {
            if (Provider(s.Provider) is not { } provider) { allEnded = false; continue; }   // a provider this build does not have: it cannot be cancelled from here
            try { await provider.CancelAsync(s.ProviderSubscriptionId, immediately: true, cancel); }
            catch (Exception ex) when (Unreachable(ex, cancel))
            {
                _log.LogWarning("billing: could not cancel a subscription ({Provider})", provider.Name);
                allEnded = false;
            }
        }
        return allEnded;
    }
}

/// <summary>
/// The pass between signing in to the account page and what it does: a random value, kept in memory for ten minutes, that stands for an account whose password was just right. A restart
/// ends them all, which only means typing the password again.
/// </summary>
public sealed class BillingTickets
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    private readonly ConcurrentDictionary<string, (string Account, DateTimeOffset Expires)> _tickets = new();
    private readonly Func<DateTimeOffset> _now;

    public BillingTickets() : this(() => DateTimeOffset.UtcNow) { }

    internal BillingTickets(Func<DateTimeOffset> now) => _now = now;

    public string Make(string account)
    {
        var now = _now();
        if (_tickets.Count > 10_000) foreach (var old in _tickets.Where(t => t.Value.Expires < now).Select(t => t.Key).ToList()) _tickets.TryRemove(old, out _);
        var ticket = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
        _tickets[ticket] = (account, now + Lifetime);
        return ticket;
    }

    /// <summary>The account a ticket stands for, or null if it is not one or has run out.</summary>
    public string? AccountOf(string? ticket) =>
        ticket != null && _tickets.TryGetValue(ticket, out var found) && found.Expires > _now() ? found.Account : null;
}

/// <summary>Does the regular work of billing while the service runs: asks the provider about subscriptions that have not been heard of, and clears what has gone stale.</summary>
public sealed class BillingJobs(BillingService billing, ILogger<BillingJobs> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stopping)
    {
        try
        {
            // A minute after starting, and then every six hours.
            await Task.Delay(TimeSpan.FromMinutes(1), stopping);
            while (!stopping.IsCancellationRequested)
            {
                if (billing.Enabled)
                {
                    try
                    {
                        await billing.ReconcileAsync(stopping);
                        billing.PurgeEvents();
                        billing.PurgeUnpaid();
                    }
                    catch (Exception ex) when (ex is IOException or Npgsql.NpgsqlException or InvalidOperationException)
                    {
                        log.LogWarning("billing: the regular work did not finish: {Message}", ex.Message);
                    }
                }
                await Task.Delay(TimeSpan.FromHours(6), stopping);
            }
        }
        catch (OperationCanceledException) { }
    }
}
