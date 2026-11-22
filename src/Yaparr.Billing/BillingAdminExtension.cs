using System.Text;

namespace Yaparr.Cloud;

/// <summary>Adds the payment side to the admin portal's overview: a Plan column, a Payments card and buttons for each account. Nothing shows while billing is off.</summary>
internal sealed class BillingAdminExtension(PlanGate gate, ISubscriptions subscriptions, ServiceOptions options) : IAdminExtension
{
    public IAdminView Begin(IReadOnlyList<AccountEntry> accounts, DateTimeOffset now)
    {
        if (!gate.Enabled) return new NothingView();
        var grace = TimeSpan.FromDays(Math.Max(0, options.Billing.GraceDays));
        var plans = accounts.ToDictionary(a => a.Name, a => PlanOf(a.Name, grace, now));
        return new View(plans, accounts, options.Billing.Provider);
    }

    /// <summary>Where an account stands on payments. <see cref="Paid"/>: it has had a subscription at some point. <see cref="Running"/>: one is not over.</summary>
    internal sealed record Plan(PlanState State, bool PastDue, bool Canceling, DateTimeOffset? EndsAt, bool Paid, bool Running);

    private Plan PlanOf(string name, TimeSpan grace, DateTimeOffset now)
    {
        var subs = subscriptions.Of(name);
        var covering = subs.Where(s => AccountPlan.Covers(s, now, grace)).ToList();
        var described = gate.Describe(name);
        return new Plan(described.State,
            PastDue: covering.Any(s => s.Status == SubscriptionStatus.PastDue) && !covering.Any(s => s.Status == SubscriptionStatus.Active),
            Canceling: covering.Count > 0 && covering.All(s => s.Status == SubscriptionStatus.Canceling),
            described.EndsAt, Paid: subs.Count > 0, Running: subs.Any(s => s.Status != SubscriptionStatus.Ended));
    }

    private sealed class NothingView : IAdminView
    {
        public string? ColumnTitle => null;
        public string Cell(AccountEntry account) => "";
        public string Actions(AccountEntry account, string csrf) => "";
        public string? Card() => null;
    }

    private sealed class View(Dictionary<string, Plan> plans, IReadOnlyList<AccountEntry> accounts, string provider) : IAdminView
    {
        private static string E(string? text) => AdminPages.E(text);

        public string? ColumnTitle => "Plan";

        public string Cell(AccountEntry a)
        {
            if (!plans.TryGetValue(a.Name, out var plan)) return "";
            if (Pending(a))
                return plan.Paid ? "<span class=\"pill warn\">Paid, no password yet</span>" : "<span class=\"pill warn\">Waiting for payment</span>";
            return plan.State switch
            {
                PlanState.Lifetime => "<span class=\"pill ok\">Lifetime</span>",
                PlanState.Subscribed when plan.PastDue => $"<span class=\"pill warn\">Payment failing</span> <span class=\"muted\">· until {plan.EndsAt:yyyy-MM-dd}</span>",
                PlanState.Subscribed when plan.Canceling => $"<span class=\"pill warn\">Ending</span> <span class=\"muted\">· until {plan.EndsAt:yyyy-MM-dd}, then lapses</span>",
                PlanState.Subscribed => $"<span class=\"pill ok\">Subscribed</span> <span class=\"muted\">· renews {plan.EndsAt:yyyy-MM-dd}</span>",
                PlanState.Lapsed => $"<span class=\"pill bad\">Lapsed</span>{(plan.EndsAt is { } end ? $" <span class=\"muted\">· ended {end:yyyy-MM-dd}</span>" : " <span class=\"muted\">· never paid</span>")}",
                _ => "",
            };
        }

        // No password and no pass of its own: the account page cannot sign in yet.
        private static bool Pending(AccountEntry a) => !a.HasPassword && a.Pass == AccountPlan.None;

        public string Actions(AccountEntry a, string csrf)
        {
            if (!plans.TryGetValue(a.Name, out var plan)) return "";
            var token = AdminPages.Token(csrf);
            var name = E(a.Name);
            var buttons = new StringBuilder();
            if (a.Pass == AccountPlan.Lifetime)
                buttons.Append($"<form method=\"post\" action=\"/admin/accounts/pass\">{token}<input type=\"hidden\" name=\"name\" value=\"{name}\"><input type=\"hidden\" name=\"pass\" value=\"none\"><button class=\"btn secondary small\" type=\"submit\" aria-label=\"Make {name} pay from now on\">Make it pay</button></form>");
            else if (a.Pass == AccountPlan.None)
                buttons.Append($"<form method=\"post\" action=\"/admin/accounts/pass\">{token}<input type=\"hidden\" name=\"name\" value=\"{name}\"><input type=\"hidden\" name=\"pass\" value=\"lifetime\"><button class=\"btn secondary small\" type=\"submit\" aria-label=\"Give {name} the lifetime pass\">Give lifetime</button></form>");
            if (Pending(a) && plan.Paid)
                buttons.Append($"<form method=\"post\" action=\"/admin/accounts/setup-link\">{token}<input type=\"hidden\" name=\"name\" value=\"{name}\"><button class=\"btn secondary small\" type=\"submit\" aria-label=\"Make a new sign-in link for {name}\">New sign-in link</button></form>");
            if (plan.Paid)
                buttons.Append($"<form method=\"post\" action=\"/admin/accounts/subscription/check\">{token}<input type=\"hidden\" name=\"name\" value=\"{name}\"><button class=\"btn secondary small\" type=\"submit\" aria-label=\"Ask the payment provider about {name}\">Check payment</button></form>");
            if (plan.Running)
                buttons.Append($"<a class=\"btn link-danger\" href=\"/admin/accounts/subscription?name={Uri.EscapeDataString(a.Name)}\" aria-label=\"End the subscription of {name}\">End subscription</a>");
            return buttons.ToString();
        }

        public string? Card()
        {
            int Count(Func<AccountEntry, Plan, bool> where) => accounts.Count(a => plans.TryGetValue(a.Name, out var p) && where(a, p));
            int lifetime = Count((a, p) => !Pending(a) && p.State == PlanState.Lifetime),
                subscribed = Count((a, p) => !Pending(a) && p.State == PlanState.Subscribed && !p.Canceling && !p.PastDue),
                ending = Count((a, p) => !Pending(a) && p.State == PlanState.Subscribed && p.Canceling),
                failing = Count((a, p) => !Pending(a) && p.State == PlanState.Subscribed && p.PastDue),
                lapsed = Count((a, p) => !Pending(a) && p.State == PlanState.Lapsed),
                waiting = Count((a, p) => Pending(a) && !p.Paid),
                paidNoPassword = Count((a, p) => Pending(a) && p.Paid);
            return $"""
                <section class="card">
                <header><div><h2>Payments</h2><p class="muted small">Through <strong>{E(provider)}</strong>. Refund in the provider's dashboard: the subscription ends by itself when a refund goes through. An account that paid and lost the page it was sent back to gets a new link from its row.</p></div></header>
                <div class="body"><p>
                <span class="pill ok">{lifetime} lifetime</span>
                <span class="pill ok">{subscribed} subscribed</span>
                {(ending > 0 ? $"<span class=\"pill warn\">{ending} ending</span>" : "")}
                {(failing > 0 ? $"<span class=\"pill warn\">{failing} payment failing</span>" : "")}
                <span class="pill {(lapsed > 0 ? "bad" : "ok")}">{lapsed} lapsed</span>
                {(waiting > 0 ? $"<span class=\"pill warn\">{waiting} waiting for payment</span>" : "")}
                {(paidNoPassword > 0 ? $"<span class=\"pill warn\">{paidNoPassword} paid, no password yet</span>" : "")}
                </p></div>
                </section>
                """;
        }
    }
}

/// <summary>The addresses the payment buttons of the admin portal post to. Each answers 404 while billing is off, and needs a signed-in admin and the session's form token (<see cref="AdminGuard"/>).</summary>
internal static class BillingAdminRoutes
{
    private static IResult Html(HttpContext http, string page, int status = StatusCodes.Status200OK) => Responses.Html(http, page, status);

    public static void Map(WebApplication app, ServiceOptions options)
    {
        var log = app.Logger;
        var admin = app.MapGroup("/admin");
        admin.AddEndpointFilter(async (context, next) =>
            context.HttpContext.RequestServices.GetRequiredService<IAccounts>().AnyAdmin() ? await next(context) : Results.NotFound());

        admin.MapPost("/accounts/pass", (HttpContext http, IAccounts accounts, PlanGate gate, ISubscriptions subscriptions) => AdminGuard.Post(http, (session, form) =>
        {
            if (!gate.Enabled) return Task.FromResult(Results.NotFound());
            var pass = form["pass"].ToString();
            if (AccountDirectory.Normalize(form["name"].ToString()) is not { } name || !AccountPlan.IsPass(pass) || !accounts.SetPass(name, pass))
                return Task.FromResult(Html(http, AdminPages.Result("There is no such account", "It may have been removed."), StatusCodes.Status404NotFound));
            log.LogInformation("Admin {Admin} set the pass of {Account} to {Pass}", session.User, name, pass);
            var text = pass == AccountPlan.Lifetime
                ? $"{name} has the lifetime pass now: it is never asked to pay."
                : $"{name} no longer has the lifetime pass: it follows its subscriptions, and lapses if it has none.";
            if (pass == AccountPlan.Lifetime && BillingAdmin.StillCharged(subscriptions, name) is { } charged) text += " " + charged;
            return Task.FromResult(Html(http, AdminPages.Result("The pass was changed", text, session)));
        })).DisableAntiforgery();

        admin.MapPost("/accounts/setup-link", (HttpContext http, IAccounts accounts, PlanGate gate, ISubscriptions subscriptions) => AdminGuard.Post(http, (session, form) =>
        {
            if (!gate.Enabled) return Task.FromResult(Results.NotFound());
            if (AccountDirectory.Normalize(form["name"].ToString()) is not { } name)
                return Task.FromResult(Html(http, AdminPages.Result("There is no such account", "It may have been removed."), StatusCodes.Status404NotFound));
            var (link, problem) = BillingAdmin.SetupLink(accounts, subscriptions, options, name);
            if (link == null) return Task.FromResult(Html(http, AdminPages.Result("No link was made", problem!, session), StatusCodes.Status409Conflict));
            log.LogInformation("Admin {Admin} made a new sign-in link for {Account}", session.User, name);
            return Task.FromResult(Html(http, AdminPages.Secret($"New sign-in link for {name}", $"Give it to them and nobody else: whoever opens it chooses the password. It works once and for {Math.Max(1, options.Billing.PendingHours)} hours, and the link they had stops working.", link, session)));
        })).DisableAntiforgery();

        admin.MapPost("/accounts/subscription/check", (HttpContext http, IAccounts accounts, PlanGate gate, BillingService billing) => AdminGuard.Post(http, async (session, form) =>
        {
            if (!gate.Enabled) return Results.NotFound();
            if (AccountDirectory.Normalize(form["name"].ToString()) is not { } name || !accounts.Exists(name))
                return Html(http, AdminPages.Result("There is no such account", "It may have been removed."), StatusCodes.Status404NotFound);
            if (!await billing.RefreshAccountAsync(name, http.RequestAborted))
                return Html(http, AdminPages.Result("The payment provider could not be reached", "Nothing was changed. Try again in a minute.", session), StatusCodes.Status503ServiceUnavailable);
            var now = gate.Describe(name);
            return Html(http, AdminPages.Result("Asked the payment provider", $"{name} is {now.State.ToString().ToLowerInvariant()}{(now.EndsAt is { } end ? $", to {end:yyyy-MM-dd}" : "")}.", session));
        })).DisableAntiforgery();

        admin.MapGet("/accounts/subscription", (HttpContext http, IAccounts accounts, PlanGate gate, string? name) =>
            AdminGuard.Current(http) is not { } session ? Results.Redirect("/admin/login")
            : !gate.Enabled ? Results.NotFound()
            : AccountDirectory.Normalize(name) is { } who && accounts.Exists(who)
                ? Html(http, ConfirmEnd(session, who, gate.Describe(who).EndsAt))
                : Html(http, AdminPages.Result("There is no such account", "It may have been removed."), StatusCodes.Status404NotFound));

        admin.MapPost("/accounts/subscription/end", (HttpContext http, IAccounts accounts, PlanGate gate, BillingService billing) => AdminGuard.Post(http, async (session, form) =>
        {
            if (!gate.Enabled) return Results.NotFound();
            var when = form["when"].ToString();
            if (AccountDirectory.Normalize(form["name"].ToString()) is not { } name || !accounts.Exists(name) || when is not ("now" or "period"))
                return Html(http, AdminPages.Result("There is no such account", "It may have been removed."), StatusCodes.Status404NotFound);
            if (!await billing.EndAsync(name, immediately: when == "now", http.RequestAborted))
                return Html(http, AdminPages.Result("It could not be ended everywhere", "The payment provider could not be reached or did not answer for every subscription. Check the account again in a minute, or end it in the provider's dashboard.", session), StatusCodes.Status503ServiceUnavailable);
            log.LogInformation("Admin {Admin} ended the subscription of {Account} ({When})", session.User, name, when == "now" ? "now" : "at the end of the period");
            return Html(http, AdminPages.Result("Ended", when == "now" ? $"{name}'s subscription was ended; it lapses now." : $"{name}'s subscription will not renew; it ends with the paid period.", session));
        })).DisableAntiforgery();
    }

    /// <summary>Ending what an account pays for: now (it lapses at once) or when the paid period ends. Asked first, because ending is not undone.</summary>
    private static string ConfirmEnd(AdminSession session, string name, DateTimeOffset? endsAt, string? problem = null) => AdminPages.Shell("End the subscription of " + name, $"""
        <div class="card"><div class="body">
        <h1>End the subscription of {AdminPages.E(name)}?</h1>
        <p class="muted">Nothing is deleted either way: the account and its backups stay, and it can subscribe again. {(endsAt is { } end ? $"It has paid until <strong>{end:yyyy-MM-dd}</strong>." : "")}</p>
        {AdminPages.Alert(problem)}
        <form method="post" action="/admin/accounts/subscription/end">
        {AdminPages.Token(session.Csrf)}
        <input type="hidden" name="name" value="{AdminPages.E(name)}">
        <div class="actions-row">
        <button class="btn secondary" type="submit" name="when" value="period">Stop it renewing (it ends when the paid period does)</button>
        <button class="btn danger" type="submit" name="when" value="now">End it now (the account lapses at once)</button>
        <a class="btn secondary" href="/admin">Cancel</a></div>
        <p class="muted small">Ending it now does not refund anything: refund in the provider's dashboard if money is owed.</p>
        </form>
        </div></div>
        """, session.User, session.Csrf, narrow: true);
}
