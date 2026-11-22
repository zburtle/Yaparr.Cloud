using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Yaparr.Cloud;

/// <summary>
/// The admin portal (<c>/admin</c>): what <see cref="AdminCommands"/> does, in a browser, for the person running the service. It asks for the email
/// address and password of an account that was made an admin from the command line (<c>accounts admin</c>) <b>and</b> a code from that
/// account's authenticator app; nothing else gets in, and every wrong try (a wrong password, a wrong code, not an admin) gets the same answer. Until
/// an account is made an admin the portal does not exist: every address under <c>/admin</c> answers 404.
/// It can see accounts, their devices and how much space their backups take, and make, list and revoke signup codes. It can never read a
/// backup: they are locked on the person's device, and the service does not hold the key.
/// </summary>
public static class AdminPortal
{
    private const string CookieName = "yaparr_admin";
    private const string Issuer = "Yaparr cloud";

    public static void MapAdminPortal(this WebApplication app, ServiceOptions options)
    {
        var log = app.Logger;
        var admin = app.MapGroup("/admin");
        admin.AddEndpointFilter(async (context, next) =>
            context.HttpContext.RequestServices.GetRequiredService<IAccounts>().AnyAdmin() ? await next(context) : Results.NotFound());

        // ---- signing in ----

        admin.MapGet("/login", (HttpContext http, IAccounts accounts, AdminSessions sessions) =>
            Current(http, accounts, sessions) != null ? Results.Redirect("/admin") : Html(http, AdminPages.Login("", null)));

        admin.MapPost("/login", async (HttpContext http, IAccounts accounts, AdminSessions sessions, LoginLimiter limiter) =>
        {
            var form = await http.Request.ReadFormAsync();
            string typed = form["username"].ToString(), password = form["password"].ToString(), code = form["code"].ToString();
            var ip = "admin-ip:" + (http.Connection.RemoteIpAddress?.ToString() ?? "unknown");
            var name = AccountDirectory.Normalize(typed);
            var who = "admin:" + (name ?? "");
            // One answer for everything that can be wrong, so nothing says which of the three was.
            const string wrong = "The email address, password or code is wrong.";

            if (limiter.IsBlocked(ip) || (name != null && limiter.IsBlocked(who)))
                return Html(http, AdminPages.Login(typed, "Too many wrong tries. Wait a quarter of an hour and try again."), StatusCodes.Status429TooManyRequests);
            if (password.Length > 1000 || code.Length > 20)
                return Html(http, AdminPages.Login(typed, wrong), StatusCodes.Status401Unauthorized);

            // All three are checked whatever the others said (the password check takes as long for a name that does not exist).
            bool passwordOk = await Task.Run(() => accounts.CheckPassword(name ?? "", password));
            var secret = name == null ? null : accounts.TotpSecretOf(name);
            long? step = secret == null ? null : Totp.Verify(secret, code, DateTimeOffset.UtcNow);
            bool codeOk = step is { } s && sessions.TryAcceptStep(name!, s);
            if (!passwordOk || secret == null || !codeOk)
            {
                limiter.Failed(ip);
                if (name != null) limiter.Failed(who);
                log.LogWarning("Admin sign-in failed from {Address}", http.Connection.RemoteIpAddress);
                return Html(http, AdminPages.Login(typed, wrong), StatusCodes.Status401Unauthorized);
            }

            limiter.Succeeded(who);
            var (token, _) = sessions.Start(name!);
            http.Response.Cookies.Append(CookieName, token, new CookieOptions
            {
                HttpOnly = true, Secure = http.Request.IsHttps, SameSite = SameSiteMode.Strict, Path = "/admin", MaxAge = AdminSessions.Longest, IsEssential = true,
            });
            log.LogInformation("Admin {Admin} signed in from {Address}", name, http.Connection.RemoteIpAddress);
            return Results.Redirect("/admin");
        }).DisableAntiforgery();

        admin.MapPost("/logout", (HttpContext http, IAccounts accounts, AdminSessions sessions) => Post(http, accounts, sessions, (session, _) =>
        {
            sessions.End(http.Request.Cookies[CookieName]);
            http.Response.Cookies.Delete(CookieName, new CookieOptions { Path = "/admin" });
            return Task.FromResult(Results.Redirect("/admin/login"));
        })).DisableAntiforgery();

        // ---- the overview ----

        admin.MapGet("", async (HttpContext http, IAccounts accounts, AdminSessions sessions, IBackupStore store, ISignupCodes codes, IKeepStore kept, IBlobStore pictures, GatewayRegistry gateway, PlanGate gate, ISubscriptions subscriptions) =>
            Current(http, accounts, sessions) is { } session
                ? Html(http, await DashboardAsync(session, options, accounts, store, codes, kept, pictures, gateway, gate, subscriptions))
                : Results.Redirect("/admin/login"));

        // ---- accounts ----

        admin.MapPost("/accounts/add", (HttpContext http, IAccounts accounts, AdminSessions sessions) => Post(http, accounts, sessions, async (session, form) =>
        {
            if (AccountDirectory.Normalize(form["email"].ToString()) is not { } email)
                return Html(http, AdminPages.Result("That email address does not look right", "Go back and check it."), StatusCodes.Status400BadRequest);
            var password = Passwords.Generate();
            if (!await Task.Run(() => accounts.Add(email, password)))
                return Html(http, AdminPages.Result("There is already an account called " + email, "Reset its password from the list instead."), StatusCodes.Status409Conflict);
            log.LogInformation("Admin {Admin} made the account {Account}", session.User, email);
            return Html(http, AdminPages.Secret($"Made the account {email}", "Its password is below. It is not kept anywhere, so this is the only time it is shown. Give it to them; they can change it on the sign-in page.", password));
        })).DisableAntiforgery();

        admin.MapPost("/accounts/password", (HttpContext http, IAccounts accounts, AdminSessions sessions) => Post(http, accounts, sessions, async (session, form) =>
        {
            if (AccountDirectory.Normalize(form["name"].ToString()) is not { } name || !accounts.Exists(name))
                return Html(http, AdminPages.Result("There is no such account", "It may have been removed."), StatusCodes.Status404NotFound);
            var password = Passwords.Generate();
            await Task.Run(() => accounts.SetPassword(name, password));
            sessions.EndAll(name);
            log.LogInformation("Admin {Admin} reset the password of {Account}", session.User, name);
            return Html(http, AdminPages.Secret($"New password for {name}", "Their devices were signed out and their backups are untouched. The password is not kept anywhere, so this is the only time it is shown.", password));
        })).DisableAntiforgery();

        admin.MapGet("/accounts/remove", (HttpContext http, IAccounts accounts, AdminSessions sessions, string? name) =>
            Current(http, accounts, sessions) is not { } session ? Results.Redirect("/admin/login")
            : AccountDirectory.Normalize(name) is { } who && accounts.Exists(who)
                ? Html(http, AdminPages.ConfirmRemove(session, who, accounts.IsAdmin(who)))
                : Html(http, AdminPages.Result("There is no such account", "It may have been removed."), StatusCodes.Status404NotFound));

        admin.MapPost("/accounts/remove", (HttpContext http, IAccounts accounts, AdminSessions sessions, IBackupStore store, IPushRegistry push, ISyncStore sync, IMailboxStore mailbox, IBlobStore blobs, IKeepStore kept, ISubscriptions subscriptions, BillingService billing) => Post(http, accounts, sessions, async (session, form) =>
        {
            if (AccountDirectory.Normalize(form["name"].ToString()) is not { } name || !accounts.Exists(name))
                return Html(http, AdminPages.Result("There is no such account", "It may have been removed."), StatusCodes.Status404NotFound);
            if (accounts.IsAdmin(name))
                return Html(http, AdminPages.Result("This account is an admin", "Take the portal away first, on the server: accounts admin " + name + " --remove"), StatusCodes.Status409Conflict);
            if (!string.Equals(form["confirm"].ToString().Trim(), name, StringComparison.OrdinalIgnoreCase))
                return Html(http, AdminPages.ConfirmRemove(session, name, false, "That is not the account's address, so nothing was removed."), StatusCodes.Status400BadRequest);
            // Not while it is still being charged (see /account/delete).
            if (!await billing.CancelAllAsync(name, http.RequestAborted))
                return Html(http, AdminPages.Result("Its subscription could not be ended", "The payment provider could not be reached, so nothing was removed. Try again, or end the subscription at the provider first."), StatusCodes.Status503ServiceUnavailable);
            var (_, hadBackup) = await AccountErasure.EraseAsync(name, accounts, store, push, sync, mailbox: mailbox, blobs: blobs, kept: kept, subscriptions: subscriptions);
            log.LogInformation("Admin {Admin} removed the account {Account} ({Backup})", session.User, name, hadBackup ? "its backup was deleted" : "it had no backup");
            return Html(http, AdminPages.Result($"Removed {name}", hadBackup ? "Its backup was deleted." : "It had no backup."));
        })).DisableAntiforgery();

        // ---- payments (only when billing is on) ----

        admin.MapPost("/accounts/pass", (HttpContext http, IAccounts accounts, AdminSessions sessions, PlanGate gate, ISubscriptions subscriptions) => Post(http, accounts, sessions, (session, form) =>
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

        admin.MapPost("/accounts/setup-link", (HttpContext http, IAccounts accounts, AdminSessions sessions, PlanGate gate, ISubscriptions subscriptions) => Post(http, accounts, sessions, (session, form) =>
        {
            if (!gate.Enabled) return Task.FromResult(Results.NotFound());
            if (AccountDirectory.Normalize(form["name"].ToString()) is not { } name)
                return Task.FromResult(Html(http, AdminPages.Result("There is no such account", "It may have been removed."), StatusCodes.Status404NotFound));
            var (link, problem) = BillingAdmin.SetupLink(accounts, subscriptions, options, name);
            if (link == null) return Task.FromResult(Html(http, AdminPages.Result("No link was made", problem!, session), StatusCodes.Status409Conflict));
            log.LogInformation("Admin {Admin} made a new sign-in link for {Account}", session.User, name);
            return Task.FromResult(Html(http, AdminPages.Secret($"New sign-in link for {name}", $"Give it to them and nobody else: whoever opens it chooses the password. It works once and for {Math.Max(1, options.Billing.PendingHours)} hours, and the link they had stops working.", link, session)));
        })).DisableAntiforgery();

        admin.MapPost("/accounts/subscription/check", (HttpContext http, IAccounts accounts, AdminSessions sessions, PlanGate gate, BillingService billing) => Post(http, accounts, sessions, async (session, form) =>
        {
            if (!gate.Enabled) return Results.NotFound();
            if (AccountDirectory.Normalize(form["name"].ToString()) is not { } name || !accounts.Exists(name))
                return Html(http, AdminPages.Result("There is no such account", "It may have been removed."), StatusCodes.Status404NotFound);
            if (!await billing.RefreshAccountAsync(name, http.RequestAborted))
                return Html(http, AdminPages.Result("The payment provider could not be reached", "Nothing was changed. Try again in a minute.", session), StatusCodes.Status503ServiceUnavailable);
            var now = gate.Describe(name);
            return Html(http, AdminPages.Result("Asked the payment provider", $"{name} is {now.State.ToString().ToLowerInvariant()}{(now.EndsAt is { } end ? $", to {end:yyyy-MM-dd}" : "")}.", session));
        })).DisableAntiforgery();

        admin.MapGet("/accounts/subscription", (HttpContext http, IAccounts accounts, AdminSessions sessions, PlanGate gate, string? name) =>
            Current(http, accounts, sessions) is not { } session ? Results.Redirect("/admin/login")
            : !gate.Enabled ? Results.NotFound()
            : AccountDirectory.Normalize(name) is { } who && accounts.Exists(who)
                ? Html(http, AdminPages.ConfirmEnd(session, who, gate.Describe(who).EndsAt))
                : Html(http, AdminPages.Result("There is no such account", "It may have been removed."), StatusCodes.Status404NotFound));

        admin.MapPost("/accounts/subscription/end", (HttpContext http, IAccounts accounts, AdminSessions sessions, PlanGate gate, BillingService billing) => Post(http, accounts, sessions, async (session, form) =>
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

        // ---- signup codes ----

        admin.MapPost("/codes/add", (HttpContext http, IAccounts accounts, AdminSessions sessions, ISignupCodes codes) => Post(http, accounts, sessions, (session, form) =>
        {
            if (!TryWhole(form["uses"].ToString(), 100_000, out var uses))
                return Task.FromResult(Html(http, AdminPages.Result("How many people?", "Leave it empty for no limit, or give a whole number from 1 to 100000."), StatusCodes.Status400BadRequest));
            if (!TryWhole(form["days"].ToString(), 3650, out var days))
                return Task.FromResult(Html(http, AdminPages.Result("How many days?", "Leave it empty for no end, or give a whole number from 1 to 3650."), StatusCodes.Status400BadRequest));

            var note = new string(form["note"].ToString().Where(c => !char.IsControl(c)).ToArray()).Trim();
            var (code, info) = codes.Create(note, uses, days is { } n ? TimeSpan.FromDays(n) : null);
            // The address in the link: the one set for the service, or else the one this page was reached by.
            var link = AdminCommands.SignupLink(options.PublicUrl.Length > 0 ? options
                : new ServiceOptions { PublicUrl = $"{http.Request.Scheme}://{http.Request.Host}", SignupPageUrl = options.SignupPageUrl }, code);
            log.LogInformation("Admin {Admin} made the signup code {Code}", session.User, info.Id);
            return Task.FromResult(Html(http, AdminPages.Secret($"Made the code {code}",
                $"Send this link. Up to {(uses is { } max ? max.ToString(CultureInfo.InvariantCulture) + " people" : "any number of people")} can sign up with it" +
                (days is { } left ? $", for {left} day{(left == 1 ? "" : "s")}" : "") + ". The code is not kept anywhere, so this is the only time the link is shown.", link)));
        })).DisableAntiforgery();

        admin.MapPost("/codes/revoke", (HttpContext http, IAccounts accounts, AdminSessions sessions, ISignupCodes codes) => Post(http, accounts, sessions, (session, form) =>
        {
            var id = form["id"].ToString();
            if (!codes.Revoke(id)) return Task.FromResult(Html(http, AdminPages.Result("There is no such code", "It may have been revoked already."), StatusCodes.Status404NotFound));
            log.LogInformation("Admin {Admin} revoked the signup code {Code}", session.User, id);
            return Task.FromResult(Results.Redirect("/admin"));
        })).DisableAntiforgery();

        admin.MapGet("/codes/delete", (HttpContext http, IAccounts accounts, AdminSessions sessions, ISignupCodes codes, string? id) =>
            Current(http, accounts, sessions) is not { } session ? Results.Redirect("/admin/login")
            : codes.List().FirstOrDefault(c => c.Id == (id ?? "").Trim().ToLowerInvariant()) is { } code
                ? Html(http, AdminPages.ConfirmDeleteCode(session, code, DateTimeOffset.UtcNow))
                : Html(http, AdminPages.Result("There is no such code", "It may have been deleted already."), StatusCodes.Status404NotFound));

        admin.MapPost("/codes/delete", (HttpContext http, IAccounts accounts, AdminSessions sessions, ISignupCodes codes) => Post(http, accounts, sessions, (session, form) =>
        {
            var id = form["id"].ToString();
            if (!codes.Delete(id)) return Task.FromResult(Html(http, AdminPages.Result("There is no such code", "It may have been deleted already."), StatusCodes.Status404NotFound));
            log.LogInformation("Admin {Admin} deleted the signup code {Code}", session.User, id);
            return Task.FromResult(Results.Redirect("/admin"));
        })).DisableAntiforgery();
    }

    private static IResult Html(HttpContext http, string page, int status = StatusCodes.Status200OK) => Responses.Html(http, page, status);

    /// <summary>An optional whole number from 1 to <paramref name="max"/>: empty is null (and fine); anything else that is not one is not.</summary>
    private static bool TryWhole(string typed, int max, out int? value)
    {
        value = null;
        typed = typed.Trim();
        if (typed.Length == 0) return true;
        if (!int.TryParse(typed, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n < 1 || n > max) return false;
        value = n;
        return true;
    }

    /// <summary>The signed-in admin of this request, or null: no cookie, a session that has ended, or an account that is no longer an admin.</summary>
    private static AdminSession? Current(HttpContext http, IAccounts accounts, AdminSessions sessions)
    {
        var cookie = http.Request.Cookies[CookieName];
        if (sessions.Get(cookie) is not { } session) return null;
        if (accounts.IsAdmin(session.User)) return session;
        sessions.End(cookie);
        return null;
    }

    /// <summary>A form post of a signed-in admin that carries the token of its session; anything else is not done.</summary>
    private static async Task<IResult> Post(HttpContext http, IAccounts accounts, AdminSessions sessions, Func<AdminSession, IFormCollection, Task<IResult>> action)
    {
        if (Current(http, accounts, sessions) is not { } session) return Results.Redirect("/admin/login");
        var form = await http.Request.ReadFormAsync();
        var sent = Encoding.UTF8.GetBytes(form["csrf"].ToString());
        if (!CryptographicOperations.FixedTimeEquals(sent, Encoding.UTF8.GetBytes(session.Csrf)))
            return Html(http, AdminPages.Result("That did not work", "The page was out of date. Go back to the overview and try again."), StatusCodes.Status403Forbidden);
        return await action(session, form);
    }

    private static AdminPages.PlanInfo PlanOf(string name, ServiceOptions options, PlanGate gate, ISubscriptions subscriptions, DateTimeOffset now)
    {
        var grace = TimeSpan.FromDays(Math.Max(0, options.Billing.GraceDays));
        var subs = subscriptions.Of(name);
        var covering = subs.Where(s => AccountPlan.Covers(s, now, grace)).ToList();
        var described = gate.Describe(name);
        return new AdminPages.PlanInfo(described.State,
            PastDue: covering.Any(s => s.Status == SubscriptionStatus.PastDue) && !covering.Any(s => s.Status == SubscriptionStatus.Active),
            Canceling: covering.Count > 0 && covering.All(s => s.Status == SubscriptionStatus.Canceling),
            described.EndsAt, Paid: subs.Count > 0, Running: subs.Any(s => s.Status != SubscriptionStatus.Ended));
    }

    private static AdminPages.BillingSummary Summary(ServiceOptions options, IReadOnlyList<AccountEntry> accounts, IReadOnlyList<AdminPages.AccountRow> rows)
    {
        int Count(Func<AdminPages.AccountRow, bool> where) => rows.Count(where);
        bool Pending(AdminPages.AccountRow r) => !r.Account.HasPassword && r.Account.Pass == AccountPlan.None;
        return new AdminPages.BillingSummary(options.Billing.Provider,
            Lifetime: Count(r => !Pending(r) && r.Plan?.State == PlanState.Lifetime),
            Subscribed: Count(r => !Pending(r) && r.Plan?.State == PlanState.Subscribed && !r.Plan.Canceling && !r.Plan.PastDue),
            Canceling: Count(r => !Pending(r) && r.Plan?.State == PlanState.Subscribed && r.Plan.Canceling),
            PastDue: Count(r => !Pending(r) && r.Plan?.State == PlanState.Subscribed && r.Plan.PastDue),
            Lapsed: Count(r => !Pending(r) && r.Plan?.State == PlanState.Lapsed),
            Waiting: Count(r => Pending(r) && r.Plan is { Paid: false }),
            PaidNoPassword: Count(r => Pending(r) && r.Plan is { Paid: true }));
    }

    private static async Task<string> DashboardAsync(AdminSession session, ServiceOptions options, IAccounts accounts, IBackupStore store, ISignupCodes codes, IKeepStore kept, IBlobStore pictures, GatewayRegistry gateway, PlanGate gate, ISubscriptions subscriptions)
    {
        // What is stored comes in one pass, not one request for each account (backups may be in a bucket across the internet); if the store is down the page still
        // comes, with the problem in the Storage card and the backup columns marked as not available.
        IReadOnlyDictionary<string, AccountBackups>? summaries = null;
        try { summaries = await store.SummariesAsync(); }
        catch (Exception ex) when (ex is not OperationCanceledException) { }

        // The kept store and the pictures are each added up in one pass over their store, not one for each account (a bucket across the internet is asked once, not dozens of times). If a
        // store cannot be read, the accounts' rows say so and the page still comes.
        var listed = accounts.List();
        var names = listed.Select(a => a.Name).ToList();
        var nowUtc = DateTimeOffset.UtcNow;
        IReadOnlyDictionary<string, long>? keptUse = null, pictureUse = null;
        try
        {
            keptUse = kept.UsedBytesOf(names, nowUtc, TimeSpan.FromDays(36500));
            pictureUse = pictures.UsedBytesOf(names, nowUtc, TimeSpan.FromDays(Math.Max(1, options.BlobKeepDays)));
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { keptUse = pictureUse = null; }

        var rows = listed.Select(a =>
        {
            var found = summaries?.GetValueOrDefault(BackupStore.FolderOf(a.Name));
            var use = keptUse != null && pictureUse != null
                ? new AdminPages.AccountUse(keptUse.GetValueOrDefault(a.Name), pictureUse.GetValueOrDefault(a.Name), gateway.UsedToday(a.Name))
                : null;
            return new AdminPages.AccountRow(a, found?.Versions ?? 0, found?.Bytes ?? 0, found?.Last, Known: summaries != null, Use: use, Plan: gate.Enabled ? PlanOf(a.Name, options, gate, subscriptions, nowUtc) : null);
        }).ToList();
        var billing = gate.Enabled ? Summary(options, listed, rows) : null;

        // Where things are kept, and whether they answer, all asked at once.
        var accountsStatus = accounts.StatusAsync();
        var backupsStatus = store.StatusAsync();
        var storage = new List<(string Label, StoreStatus Status)>
        {
            ("Accounts and signup codes", await accountsStatus),
            ("Backups", await backupsStatus),
        };
        if (options.Database.Length > 0) storage.Add(("Database dump", DumpStatus.Read(options, DateTimeOffset.UtcNow)));

        // The disk matters when backups are files on it.
        long? free = null, total = null;
        if (options.S3Bucket.Length == 0)
        {
            try
            {
                var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(options.DataDirectory))!);
                free = drive.AvailableFreeSpace;
                total = drive.TotalSize;
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { }
        }

        return AdminPages.Dashboard(session, rows, codes.List(), DateTimeOffset.UtcNow, free, total, storage, new AdminPages.UseLimits(options.KeepQuotaBytes, options.BlobQuotaBytes, options.GatewayBytesPerDay), billing);
    }
}
