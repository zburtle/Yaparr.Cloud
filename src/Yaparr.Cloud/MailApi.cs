namespace Yaparr.Cloud;

/// <summary>
/// The addresses that exist when email is on (<c>Service__Mail__Host</c>): asking for a link, the page that link opens to choose a password, and the page that resets a forgotten one. All
/// answer <c>404</c> when it is off. What a page answers never depends on whether an address has an account, and mail is sent in the background, so neither the words nor the time say.
/// </summary>
public static class MailApi
{
    private static IResult Html(HttpContext http, string page, int status = StatusCodes.Status200OK) => Responses.Html(http, page, status);

    public static string Server(HttpContext http, ServiceOptions options) =>
        options.PublicUrl.Length > 0 ? options.PublicUrl.TrimEnd('/') : $"{http.Request.Scheme}://{http.Request.Host}";

    /// <summary>
    /// What an address is sent, if anything: a link to reset a password for an account that has one; a link to choose one for an account that paid, or was signed up for with a code, and has not
    /// chosen one yet. Nothing for any other address. Said to nobody: the caller answers the same either way.
    /// </summary>
    public static void SendLinkFor(string name, HttpContext http, ServiceOptions options, IAccounts accounts, ISubscriptions subscriptions, ResetLinks resets, MailService mail)
    {
        var server = Server(http, options);
        if (accounts.HasPassword(name))
        {
            mail.Send(MailService.ResetLink(name, $"{server}/account/reset?t={Uri.EscapeDataString(resets.Make(name))}", ResetLinks.Lifetime));
            return;
        }
        if (!accounts.Exists(name)) return;
        string? token = null;
        TimeSpan valid = default;
        if (accounts.PassOf(name) == AccountPlan.None && subscriptions.Of(name).Count > 0)
        {
            valid = TimeSpan.FromHours(Math.Max(1, options.Billing.PendingHours));
            token = accounts.StartPending(name, valid);
        }
        else if (accounts.PassOf(name) == AccountPlan.Lifetime)
        {
            valid = AccountDirectory.SetupLifetime;
            token = accounts.StartSignup(name, null);
        }
        if (token != null) mail.Send(MailService.SetupLink(name, $"{server}/account/setup?t={Uri.EscapeDataString(token)}", valid));
    }

    public static void MapMail(this WebApplication app, ServiceOptions options)
    {
        async ValueTask<object?> OnlyWhenOn(EndpointFilterInvocationContext context, EndpointFilterDelegate next) =>
            context.HttpContext.RequestServices.GetRequiredService<MailService>().Enabled ? await next(context) : Results.NotFound();

        var account = app.MapGroup("/account").AddEndpointFilter(OnlyWhenOn);

        account.MapGet("/forgot", (HttpContext http) => Html(http, MailPages.Forgot()));

        account.MapPost("/forgot", async (HttpContext http, IAccounts accounts, ISubscriptions subscriptions, ResetLinks resets, MailService mail, LoginLimiter limiter) =>
        {
            var form = await http.Request.ReadFormAsync();
            // Each try counts, from one place and for one address, so the page cannot be used to fill someone's inbox.
            var from = "mail:ip:" + (http.Connection.RemoteIpAddress?.ToString() ?? "unknown");
            if (limiter.IsBlocked(from))
                return Html(http, Pages.Message("Too many tries", "Wait a quarter of an hour and try again."), StatusCodes.Status429TooManyRequests);
            limiter.Failed(from);
            if (AccountDirectory.Normalize(form["email"].ToString()) is { } name)
            {
                var address = "mail:to:" + name;
                if (!limiter.IsBlocked(address))
                {
                    limiter.Failed(address);
                    SendLinkFor(name, http, options, accounts, subscriptions, resets, mail);
                }
            }
            return Html(http, MailPages.Sent());   // the same, whatever the address
        }).DisableAntiforgery();

        // The link in the mail to choose a password: shows the form (the form's post uses the link, so a mail scanner that opens it uses up nothing).
        account.MapGet("/setup", (HttpContext http, IAccounts accounts, string? t) =>
            accounts.SetupFor(t) is { } email
                ? Html(http, Pages.SetPassword(t!, email, null, accounts.PassOf(email) == AccountPlan.Lifetime))
                : Html(http, Pages.Message("This link has run out", "Ask for a new one: it works once, and for a limited time."), StatusCodes.Status400BadRequest));

        account.MapGet("/reset", (HttpContext http, ResetLinks resets, string? t) =>
            resets.Peek(t) is { } name
                ? Html(http, MailPages.ResetPassword(t!, name, null))
                : Html(http, Pages.Message("This link has run out", "Ask for a new one: it works once, for an hour."), StatusCodes.Status400BadRequest));

        account.MapPost("/reset", async (HttpContext http, IAccounts accounts, ResetLinks resets, AdminSessions sessions) =>
        {
            var form = await http.Request.ReadFormAsync();
            var token = form["token"].ToString();
            string password = form["password"].ToString(), again = form["again"].ToString();
            if (resets.Peek(token) is not { } name)
                return Html(http, Pages.Message("This link has run out", "Ask for a new one: it works once, for an hour."), StatusCodes.Status400BadRequest);

            // A typing mistake does not use the link up.
            IResult Retry(string problem) => Html(http, MailPages.ResetPassword(token, name, problem), StatusCodes.Status400BadRequest);
            if (Passwords.Check(password) is { } weak) return Retry(weak);
            if (password.Length > 1000) return Retry("That password is too long.");
            if (password != again) return Retry("The two passwords are not the same.");
            if (resets.Take(token) is not { } done || !accounts.Exists(done))
                return Html(http, Pages.Message("This link has run out", "Ask for a new one: it works once, for an hour."), StatusCodes.Status400BadRequest);

            await Task.Run(() => accounts.SetPassword(done, password));   // signs every device out
            sessions.EndAll(done);
            return Html(http, Pages.Message("Password changed", "Every device was signed out. Open Yaparr and sign in again with the new password. What the server holds for you is untouched."));
        }).DisableAntiforgery();
    }
}
