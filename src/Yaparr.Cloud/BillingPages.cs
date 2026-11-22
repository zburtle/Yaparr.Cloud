namespace Yaparr.Cloud;

/// <summary>The pages of billing, in the style of the others: plain HTML with no script, and everything that came from a request encoded.</summary>
public static class BillingPages
{
    private static string E(string? text) => Pages.E(text);

    /// <summary>After paying, before the webhook has arrived: it looks again every two seconds (no script), and after a while says to come back to the same link.</summary>
    public static string Waiting(string token, int tries, bool giveUp) => Pages.Page("Waiting for your payment", giveUp
        ? $"""
        <h1>Your payment has not arrived yet</h1>
        <p>This can take a minute. If you have paid, come back to this link in a little while and it will go on.</p>
        <p><a class="button" href="/billing/done?t={Uri.EscapeDataString(token)}">Check again</a></p>
        """
        : $"""
        <meta http-equiv="refresh" content="2;url=/billing/done?t={Uri.EscapeDataString(token)}&amp;n={tries + 1}">
        <h1>Waiting for your payment</h1>
        <p>Your payment is being confirmed. This page looks again by itself.</p>
        """);

    /// <summary>The address already has an account here: it signs in, and renews from the account page.</summary>
    public static string HasAccount(string email) => Pages.Page("You already have an account", $"""
        <h1>You already have an account</h1>
        <p><strong>{E(email)}</strong> already has an account on this server. To subscribe or renew, sign in to it.</p>
        <p><a class="button" href="/account/subscription">Sign in to your subscription</a></p>
        """);

    /// <summary>The address has paid and has not chosen a password: nothing new is started, and the link it was given after paying is the way on.</summary>
    public static string PaidNotFinished(string email) => Pages.Page("This address has already paid", $"""
        <h1>This address has already paid</h1>
        <p>A payment for <strong>{E(email)}</strong> has already been received, so nothing new was started and you have not been charged again.</p>
        <p>To finish, open the page you were sent back to after paying and choose a password there. If you no longer have that page, ask whoever runs this service to give you a new link.</p>
        """);

    public static string SubscriptionLogin(string username, string? problem) => Pages.Page("Your Yaparr Cloud subscription", $"""
        <h1>Your subscription</h1>
        <p class="muted">Sign in to see where your Yaparr Cloud account stands, to subscribe or renew, and to manage what you pay.</p>
        {(problem == null ? "" : $"<p class=\"problem\" role=\"alert\">{E(problem)}</p>")}
        <form method="post" action="/account/subscription">
        <label for="username">Email address</label>
        <input id="username" name="username" type="email" autocomplete="username email" autocapitalize="none" spellcheck="false" maxlength="254" required value="{E(username)}"{(username.Length == 0 ? " autofocus" : "")}>
        <label for="password">Password</label>
        <input id="password" name="password" type="password" autocomplete="current-password" required{(username.Length == 0 ? "" : " autofocus")}>
        <button type="submit">Continue</button>
        </form>
        """);

    /// <param name="ticket">What stands for the password just typed, for the buttons below (<see cref="BillingTickets"/>).</param>
    /// <param name="paysThroughAProvider">Whether there is something to manage: a subscription at a provider.</param>
    public static string Status(string email, PlanDescription plan, string ticket, bool paysThroughAProvider)
    {
        var line = plan.State switch
        {
            PlanState.Lifetime => "This account has a lifetime pass. It never has to pay.",
            PlanState.Subscribed when plan.WillRenew => $"Subscribed. It renews after {plan.EndsAt:d MMMM yyyy}.",
            PlanState.Subscribed => $"Subscribed until {plan.EndsAt:d MMMM yyyy}. It does not renew.",
            _ => plan.EndsAt is { } ended ? $"The subscription ended on {ended:d MMMM yyyy}. Syncing is paused and no new device can sign in. Nothing has been deleted." : "There is no subscription. Syncing is paused and no new device can sign in.",
        };
        var pay = plan.State is PlanState.Lifetime or PlanState.Off ? "" : $"""
            <form method="post" action="/account/subscription/checkout">
            <input type="hidden" name="ticket" value="{E(ticket)}">
            <label for="interval">{(plan.State == PlanState.Subscribed ? "Add a subscription" : "Subscribe or renew")}</label>
            <select id="interval" name="interval"><option value="monthly">Monthly</option><option value="yearly">Yearly</option></select>
            <button type="submit">Go to payment</button>
            </form>
            """;
        var manage = !paysThroughAProvider ? "" : $"""
            <form method="post" action="/account/subscription/portal">
            <input type="hidden" name="ticket" value="{E(ticket)}">
            <button type="submit">Manage payment and invoices</button>
            </form>
            """;
        return Pages.Page("Your Yaparr Cloud subscription", $"""
            <h1>Your subscription</h1>
            <p>The account <strong>{E(email)}</strong>.</p>
            <p>{E(line)}</p>
            {pay}
            {manage}
            <p class="muted">Payments are taken by our payment provider, which also sends your receipts. Nothing here shows a card number.</p>
            """);
    }
}
