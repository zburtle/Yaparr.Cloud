namespace Yaparr.Cloud;

/// <summary>What the person running the service does about payments, shared by the admin commands and the admin portal so that both say and do the same.</summary>
public static class BillingAdmin
{
    /// <summary>
    /// A new link to choose a password, for someone who paid and lost the page they were sent back to. The old link stops working. Only for an account that has paid for something and has no
    /// password yet: anything else is a problem to say, and no link.
    /// </summary>
    public static (string? Link, string? Problem) SetupLink(IAccounts accounts, ISubscriptions subscriptions, ServiceOptions options, string name)
    {
        if (!accounts.Exists(name)) return (null, "There is no such account.");
        if (subscriptions.Of(name).Count == 0) return (null, "It has paid for nothing, so there is nothing to finish: it starts again from the subscribe page.");
        if (accounts.StartPending(name, TimeSpan.FromHours(Math.Max(1, options.Billing.PendingHours))) is not { } token)
            return (null, "It already has a password (or a pass of its own), so it signs in. To change the password, reset it.");
        return ($"{options.PublicUrl.Trim().TrimEnd('/')}/billing/done?t={Uri.EscapeDataString(token)}", null);
    }

    /// <summary>What to tell someone who has just been given the lifetime pass while something is still being charged, or null.</summary>
    public static string? StillCharged(ISubscriptions subscriptions, string name)
    {
        var subs = subscriptions.Of(name).Where(s => s.Status != SubscriptionStatus.Ended).ToList();
        return subs.Count == 0 ? null
            : $"It still has {subs.Count} subscription{(subs.Count == 1 ? "" : "s")} at {string.Join(", ", subs.Select(s => s.Provider).Distinct())}: end {(subs.Count == 1 ? "it" : "them")} so that it is not charged again, and refund the unused part if that is what is owed.";
    }
}
