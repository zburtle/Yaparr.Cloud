namespace Yaparr.Cloud;

/// <summary>
/// The addresses of billing (docs/billing.md), all of which answer <c>404</c> unless billing is on: <c>POST /billing/webhook/{provider}</c> (the provider says "look"), <c>POST /billing/start</c>
/// (the website's form: someone with no account pays first), <c>GET /billing/done</c> (where the provider sends them back, to choose a password once the payment has arrived), and the account
/// pages <c>/account/subscription</c> (sign in, then subscribe, renew, or manage what is paid). Nothing here is under <c>/v1</c>: a link to a provider is a page in a browser, behind a password.
/// </summary>
internal static class BillingApi
{
    private static IResult Html(HttpContext http, string page, int status = StatusCodes.Status200OK) => Responses.Html(http, page, status);

    private static string Server(HttpContext http, ServiceOptions options) =>
        options.PublicUrl.Length > 0 ? options.PublicUrl.TrimEnd('/') : $"{http.Request.Scheme}://{http.Request.Host}";

    private static BillingInterval IntervalOf(string? text) => string.Equals(text, "yearly", StringComparison.OrdinalIgnoreCase) ? BillingInterval.Yearly : BillingInterval.Monthly;

    private static IResult Wrong(HttpContext http, string username) => Html(http, BillingPages.SubscriptionLogin(username, "The username or password is wrong."), StatusCodes.Status401Unauthorized);

    public static void MapBilling(this WebApplication app, ServiceOptions options)
    {
        // Billing is off unless a provider is named: then none of these exist.
        async ValueTask<object?> OnlyWhenOn(EndpointFilterInvocationContext context, EndpointFilterDelegate next) =>
            context.HttpContext.RequestServices.GetRequiredService<PlanGate>().Enabled ? await next(context) : Results.NotFound();

        var billing = app.MapGroup("/billing").AddEndpointFilter(OnlyWhenOn);

        // The provider says something happened. It is answered quickly and says little: what to do is decided from the provider's own answer, not from this body.
        billing.MapPost("/webhook/{provider}", async (HttpContext http, string provider, BillingService service, CancellationToken cancel) =>
        {
            if (http.Request.ContentLength is { } declared && declared > BillingService.MaxWebhookBytes) return PushApi.Problem(StatusCodes.Status413PayloadTooLarge, "Too big.");
            using var body = new MemoryStream();
            var buffer = new byte[16384];
            int read;
            while ((read = await http.Request.Body.ReadAsync(buffer, cancel)) > 0)
            {
                if (body.Length + read > BillingService.MaxWebhookBytes) return PushApi.Problem(StatusCodes.Status413PayloadTooLarge, "Too big.");
                body.Write(buffer, 0, read);
            }
            var headers = http.Request.Headers.ToDictionary(h => h.Key.ToLowerInvariant(), h => h.Value.ToString());
            return await service.HandleWebhookAsync(provider, headers, body.ToArray(), cancel) switch
            {
                WebhookOutcome.UnknownProvider => PushApi.Problem(StatusCodes.Status404NotFound, "No such provider."),
                WebhookOutcome.BadSignature => PushApi.Problem(StatusCodes.Status400BadRequest, "The signature is not right."),
                WebhookOutcome.TryAgain => PushApi.Problem(StatusCodes.Status503ServiceUnavailable, "Try again."),
                _ => Results.Ok(),
            };
        }).DisableAntiforgery();

        // The website's form: an address, and monthly or yearly. Someone who has no account here pays first and gets one; someone who has one is sent to sign in.
        billing.MapPost("/start", async (HttpContext http, BillingService service, IAccounts accounts, LoginLimiter limiter, CancellationToken cancel) =>
        {
            var form = await http.Request.ReadFormAsync(cancel);
            // Each try counts, so an address cannot be used to fill the list of accounts that have not been paid for.
            var who = "billing:" + (http.Connection.RemoteIpAddress?.ToString() ?? "unknown");
            if (limiter.IsBlocked(who))
                return Html(http, Pages.Message("Too many tries", "Wait a quarter of an hour and try again."), StatusCodes.Status429TooManyRequests);
            limiter.Failed(who);
            if (AccountDirectory.Normalize(form["email"].ToString()) is not { } email)
                return Html(http, Pages.Message("That email address does not look right", "Go back and check it."), StatusCodes.Status400BadRequest);

            var server = Server(http, options);
            var result = await service.StartNewAsync(email, IntervalOf(form["interval"]), new Uri(server + "/billing/done"), new Uri(server + "/account/subscription"), cancel);
            return result.Outcome switch
            {
                StartOutcome.Redirect => Results.Redirect(result.Url!.AbsoluteUri),
                StartOutcome.HasAccount => Html(http, BillingPages.HasAccount(email), StatusCodes.Status409Conflict),
                _ => Html(http, Pages.Message("The payment page could not be opened", "Try again in a minute."), StatusCodes.Status502BadGateway),
            };
        }).DisableAntiforgery();

        // Where the provider sends the person back. The token in the address is the one made when they started: it stands for the account that was made for them, and the page goes on only
        // once the payment has been stored, so a link on its own gives nothing before then.
        billing.MapGet("/done", async (HttpContext http, string? t, int? n, IAccounts accounts, PlanGate gate, BillingService service, CancellationToken cancel) =>
        {
            if (accounts.SetupFor(t) is not { } email)
                return Html(http, Pages.Message("This link has run out", "Go back to the sign-up page and start again."), StatusCodes.Status400BadRequest);
            if (gate.StateOf(email) != PlanState.Subscribed) await service.RefreshAccountAsync(email, cancel);   // the webhook may be a moment behind
            if (gate.StateOf(email) == PlanState.Subscribed) return Html(http, Pages.SetPassword(t!, email, null, lifetime: false));
            var tries = Math.Clamp(n ?? 0, 0, 1000);
            return Html(http, BillingPages.Waiting(t!, tries, giveUp: tries >= 15));
        });

        // The account page: the password is asked once, and then a ticket stands for it for ten minutes.
        var account = app.MapGroup("/account/subscription").AddEndpointFilter(OnlyWhenOn);

        account.MapGet("", (HttpContext http) => Html(http, BillingPages.SubscriptionLogin("", null)));

        account.MapPost("", async (HttpContext http, IAccounts accounts, LoginLimiter limiter, PlanGate gate, ISubscriptions subscriptions, BillingTickets tickets) =>
        {
            var form = await http.Request.ReadFormAsync();
            var typed = form["username"].ToString();
            var password = form["password"].ToString();
            if (password.Length > 1000 || AccountDirectory.Normalize(typed) is not { } name) return Wrong(http, typed);
            if (limiter.IsBlocked(name)) return Html(http, BillingPages.SubscriptionLogin(typed, "Too many wrong tries. Wait a quarter of an hour and try again."), StatusCodes.Status429TooManyRequests);
            if (!await Task.Run(() => accounts.CheckPassword(name, password)))
            {
                limiter.Failed(name);
                return Wrong(http, typed);
            }
            limiter.Succeeded(name);
            return Html(http, BillingPages.Status(name, gate.Describe(name), tickets.Make(name), subscriptions.Of(name).Count > 0));
        }).DisableAntiforgery();

        account.MapPost("/checkout", async (HttpContext http, BillingService service, BillingTickets tickets, PlanGate gate, ISubscriptions subscriptions, CancellationToken cancel) =>
        {
            var form = await http.Request.ReadFormAsync(cancel);
            if (tickets.AccountOf(form["ticket"]) is not { } name)
                return Html(http, Pages.Message("This page has run out", "Sign in again."), StatusCodes.Status400BadRequest);
            if (gate.StateOf(name) == PlanState.Lifetime)
                return Html(http, Pages.Message("Nothing to pay", "This account has a lifetime pass."), StatusCodes.Status409Conflict);
            // Not a second subscription on top of one that is running: that one is managed, not added to.
            if (subscriptions.Of(name).Any(s => s.Status == SubscriptionStatus.Active && AccountPlan.Covers(s, DateTimeOffset.UtcNow, TimeSpan.Zero)))
                return Html(http, Pages.Message("You are already subscribed", "Use Manage payment and invoices to change or cancel it."), StatusCodes.Status409Conflict);
            var server = Server(http, options);
            var result = await service.StartForAccountAsync(name, IntervalOf(form["interval"]), new Uri(server + "/account/subscription"), new Uri(server + "/account/subscription"), cancel);
            return result.Outcome == StartOutcome.Redirect
                ? Results.Redirect(result.Url!.AbsoluteUri)
                : Html(http, Pages.Message("The payment page could not be opened", "Try again in a minute."), StatusCodes.Status502BadGateway);
        }).DisableAntiforgery();

        account.MapPost("/portal", async (HttpContext http, BillingService service, BillingTickets tickets, CancellationToken cancel) =>
        {
            var form = await http.Request.ReadFormAsync(cancel);
            if (tickets.AccountOf(form["ticket"]) is not { } name)
                return Html(http, Pages.Message("This page has run out", "Sign in again."), StatusCodes.Status400BadRequest);
            return await service.PortalAsync(name, new Uri(Server(http, options) + "/account/subscription"), cancel) is { } url
                ? Results.Redirect(url.AbsoluteUri)
                : Html(http, Pages.Message("Nothing to manage", "This account does not pay for anything through a payment provider."), StatusCodes.Status404NotFound);
        }).DisableAntiforgery();
    }
}
