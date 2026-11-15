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
            context.HttpContext.RequestServices.GetRequiredService<AccountDirectory>().AnyAdmin() ? await next(context) : Results.NotFound());

        // ---- signing in ----

        admin.MapGet("/login", (HttpContext http, AccountDirectory accounts, AdminSessions sessions) =>
            Current(http, accounts, sessions) != null ? Results.Redirect("/admin") : Html(http, AdminPages.Login("", null)));

        admin.MapPost("/login", async (HttpContext http, AccountDirectory accounts, AdminSessions sessions, LoginLimiter limiter) =>
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

        admin.MapPost("/logout", (HttpContext http, AccountDirectory accounts, AdminSessions sessions) => Post(http, accounts, sessions, (session, _) =>
        {
            sessions.End(http.Request.Cookies[CookieName]);
            http.Response.Cookies.Delete(CookieName, new CookieOptions { Path = "/admin" });
            return Task.FromResult(Results.Redirect("/admin/login"));
        })).DisableAntiforgery();

        // ---- the overview ----

        admin.MapGet("", (HttpContext http, AccountDirectory accounts, AdminSessions sessions, BackupStore store, SignupCodes codes) =>
            Current(http, accounts, sessions) is { } session
                ? Html(http, Dashboard(session, options, accounts, store, codes))
                : Results.Redirect("/admin/login"));

        // ---- accounts ----

        admin.MapPost("/accounts/add", (HttpContext http, AccountDirectory accounts, AdminSessions sessions) => Post(http, accounts, sessions, async (session, form) =>
        {
            if (AccountDirectory.Normalize(form["email"].ToString()) is not { } email)
                return Html(http, AdminPages.Result("That email address does not look right", "Go back and check it."), StatusCodes.Status400BadRequest);
            var password = Passwords.Generate();
            if (!await Task.Run(() => accounts.Add(email, password)))
                return Html(http, AdminPages.Result("There is already an account called " + email, "Reset its password from the list instead."), StatusCodes.Status409Conflict);
            log.LogInformation("Admin {Admin} made the account {Account}", session.User, email);
            return Html(http, AdminPages.Secret($"Made the account {email}", "Its password is below. It is not kept anywhere, so this is the only time it is shown. Give it to them; they can change it on the sign-in page.", password));
        })).DisableAntiforgery();

        admin.MapPost("/accounts/password", (HttpContext http, AccountDirectory accounts, AdminSessions sessions) => Post(http, accounts, sessions, async (session, form) =>
        {
            if (AccountDirectory.Normalize(form["name"].ToString()) is not { } name || !accounts.Exists(name))
                return Html(http, AdminPages.Result("There is no such account", "It may have been removed."), StatusCodes.Status404NotFound);
            var password = Passwords.Generate();
            await Task.Run(() => accounts.SetPassword(name, password));
            sessions.EndAll(name);
            log.LogInformation("Admin {Admin} reset the password of {Account}", session.User, name);
            return Html(http, AdminPages.Secret($"New password for {name}", "Their devices were signed out and their backups are untouched. The password is not kept anywhere, so this is the only time it is shown.", password));
        })).DisableAntiforgery();

        admin.MapGet("/accounts/remove", (HttpContext http, AccountDirectory accounts, AdminSessions sessions, string? name) =>
            Current(http, accounts, sessions) is not { } session ? Results.Redirect("/admin/login")
            : AccountDirectory.Normalize(name) is { } who && accounts.Exists(who)
                ? Html(http, AdminPages.ConfirmRemove(session.Csrf, who, accounts.IsAdmin(who)))
                : Html(http, AdminPages.Result("There is no such account", "It may have been removed."), StatusCodes.Status404NotFound));

        admin.MapPost("/accounts/remove", (HttpContext http, AccountDirectory accounts, AdminSessions sessions, BackupStore store) => Post(http, accounts, sessions, async (session, form) =>
        {
            if (AccountDirectory.Normalize(form["name"].ToString()) is not { } name || !accounts.Exists(name))
                return Html(http, AdminPages.Result("There is no such account", "It may have been removed."), StatusCodes.Status404NotFound);
            if (accounts.IsAdmin(name))
                return Html(http, AdminPages.Result("This account is an admin", "Take the portal away first, on the server: accounts admin " + name + " --remove"), StatusCodes.Status409Conflict);
            if (!string.Equals(form["confirm"].ToString().Trim(), name, StringComparison.OrdinalIgnoreCase))
                return Html(http, AdminPages.ConfirmRemove(session.Csrf, name, false, "That is not the account's address, so nothing was removed."), StatusCodes.Status400BadRequest);
            accounts.Remove(name);
            var hadBackup = await store.DeleteAsync(BackupStore.FolderOf(name), CancellationToken.None);
            log.LogInformation("Admin {Admin} removed the account {Account} ({Backup})", session.User, name, hadBackup ? "its backup was deleted" : "it had no backup");
            return Html(http, AdminPages.Result($"Removed {name}", hadBackup ? "Its backup was deleted." : "It had no backup."));
        })).DisableAntiforgery();

        // ---- signup codes ----

        admin.MapPost("/codes/add", (HttpContext http, AccountDirectory accounts, AdminSessions sessions, SignupCodes codes) => Post(http, accounts, sessions, (session, form) =>
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

        admin.MapPost("/codes/revoke", (HttpContext http, AccountDirectory accounts, AdminSessions sessions, SignupCodes codes) => Post(http, accounts, sessions, (session, form) =>
        {
            var id = form["id"].ToString();
            if (!codes.Revoke(id)) return Task.FromResult(Html(http, AdminPages.Result("There is no such code", "It may have been revoked already."), StatusCodes.Status404NotFound));
            log.LogInformation("Admin {Admin} revoked the signup code {Code}", session.User, id);
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
    private static AdminSession? Current(HttpContext http, AccountDirectory accounts, AdminSessions sessions)
    {
        var cookie = http.Request.Cookies[CookieName];
        if (sessions.Get(cookie) is not { } session) return null;
        if (accounts.IsAdmin(session.User)) return session;
        sessions.End(cookie);
        return null;
    }

    /// <summary>A form post of a signed-in admin that carries the token of its session; anything else is not done.</summary>
    private static async Task<IResult> Post(HttpContext http, AccountDirectory accounts, AdminSessions sessions, Func<AdminSession, IFormCollection, Task<IResult>> action)
    {
        if (Current(http, accounts, sessions) is not { } session) return Results.Redirect("/admin/login");
        var form = await http.Request.ReadFormAsync();
        var sent = Encoding.UTF8.GetBytes(form["csrf"].ToString());
        if (!CryptographicOperations.FixedTimeEquals(sent, Encoding.UTF8.GetBytes(session.Csrf)))
            return Html(http, AdminPages.Result("That did not work", "The page was out of date. Go back to the overview and try again."), StatusCodes.Status403Forbidden);
        return await action(session, form);
    }

    private static string Dashboard(AdminSession session, ServiceOptions options, AccountDirectory accounts, BackupStore store, SignupCodes codes)
    {
        var rows = accounts.List().Select(a =>
        {
            var versions = store.Versions(BackupStore.FolderOf(a.Name));
            return new AdminPages.AccountRow(a, versions.Count, versions.Sum(v => v.Size), versions.Count == 0 ? null : versions[0].At);
        }).ToList();

        long? free = null, total = null;
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(options.DataDirectory))!);
            free = drive.AvailableFreeSpace;
            total = drive.TotalSize;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { }

        return AdminPages.Dashboard(session, rows, codes.List(), DateTimeOffset.UtcNow, free, total);
    }
}

/// <summary>The admin portal's pages: plain HTML with no scripts, everything that came from a request encoded before it is shown.</summary>
internal static class AdminPages
{
    public sealed record AccountRow(AccountEntry Account, int Versions, long Bytes, DateTimeOffset? Last);

    private static string E(string? text) => Pages.E(text);

    private static string Mib(long bytes) => (bytes / 1024.0 / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " MiB";

    private static string Gib(long bytes) => (bytes / 1024.0 / 1024.0 / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " GiB";

    private static string Token(string csrf) => $"<input type=\"hidden\" name=\"csrf\" value=\"{E(csrf)}\">";

    public static string Login(string username, string? problem) => Pages.Page("Admin sign-in", $"""
        <h1>Admin</h1>
        {(problem == null ? "" : $"<p class=\"problem\" role=\"alert\">{E(problem)}</p>")}
        <form method="post" action="/admin/login">
        <label for="username">Email address</label>
        <input id="username" name="username" type="email" autocomplete="username" autocapitalize="none" spellcheck="false" maxlength="254" required value="{E(username)}"{(username.Length == 0 ? " autofocus" : "")}>
        <label for="password">Password</label>
        <input id="password" name="password" type="password" autocomplete="current-password" required{(username.Length == 0 ? "" : " autofocus")}>
        <label for="code">Code from your authenticator app</label>
        <input id="code" name="code" inputmode="numeric" autocomplete="one-time-code" maxlength="10" required>
        <button type="submit">Sign in</button>
        </form>
        """);

    public static string Result(string title, string text) => Pages.Page(title, $"""
        <h1>{E(title)}</h1>
        <p>{E(text)}</p>
        <p><a href="/admin">Back to the overview</a></p>
        """);

    /// <summary>A page that shows something once (a new password, a signup link): it is never kept.</summary>
    public static string Secret(string title, string text, string secret) => Pages.Page(title, $"""
        <h1>{E(title)}</h1>
        <p>{E(text)}</p>
        <p><code>{E(secret)}</code></p>
        <p><a href="/admin">Back to the overview</a></p>
        """);

    public static string ConfirmRemove(string csrf, string name, bool isAdmin, string? problem = null) => Pages.Page("Remove " + name, $"""
        <h1>Remove {E(name)}?</h1>
        {(isAdmin ? "<p class=\"problem\">This account is an admin, so it cannot be removed here. Take the portal away first, on the server: <code>accounts admin " + E(name) + " --remove</code></p>" : $"""
        <p>This removes the account <strong>and deletes its backup</strong>. It cannot be undone.</p>
        {(problem == null ? "" : $"<p class=\"problem\" role=\"alert\">{E(problem)}</p>")}
        <form method="post" action="/admin/accounts/remove">
        {Token(csrf)}
        <input type="hidden" name="name" value="{E(name)}">
        <label for="confirm">Type the account's email address to confirm</label>
        <input id="confirm" name="confirm" type="email" autocomplete="off" autocapitalize="none" spellcheck="false" required autofocus>
        <button type="submit" class="danger">Remove the account and its backup</button>
        </form>
        """)}
        <p><a href="/admin">Back to the overview</a></p>
        """);

    public static string Dashboard(AdminSession session, IReadOnlyList<AccountRow> rows, IReadOnlyList<SignupCodeInfo> codes, DateTimeOffset now, long? diskFree, long? diskTotal)
    {
        var token = Token(session.Csrf);
        var accountRows = new StringBuilder();
        foreach (var row in rows)
        {
            var a = row.Account;
            var state = a.HasPassword ? $"{a.Devices} device{(a.Devices == 1 ? "" : "s")}" : a.SignupCode != null ? "signed up, no password yet" : "no password";
            var backup = row.Versions == 0 ? "no backup" : $"{row.Versions} version{(row.Versions == 1 ? "" : "s")}, {Mib(row.Bytes)}, last {row.Last:yyyy-MM-dd HH:mm}";
            accountRows.Append($"""
                <tr><td>{E(a.Name)}{(a.IsAdmin ? " <strong>(admin)</strong>" : "")}</td><td>{a.Created:yyyy-MM-dd}</td><td>{E(state)}</td><td>{E(backup)}</td><td>{E(a.Pass)}{(a.SignupCode != null ? $" (code {E(a.SignupCode)})" : "")}</td>
                <td><form method="post" action="/admin/accounts/password">{token}<input type="hidden" name="name" value="{E(a.Name)}"><button type="submit">Reset password</button></form>
                <a href="/admin/accounts/remove?name={Uri.EscapeDataString(a.Name)}">Remove…</a></td></tr>
                """);
        }

        var codeRows = new StringBuilder();
        foreach (var c in codes.OrderByDescending(c => c.Created))
        {
            var status = c.Problem(now) ?? "works";
            var who = c.Uses.Count == 0 ? "" : string.Join(", ", c.Uses.Select(u => u.Email));
            codeRows.Append($"""
                <tr><td><code>{E(c.Id)}</code></td><td>{E(c.Note)}</td><td>{E(status)}</td><td>{c.Uses.Count}{(c.MaxUses is { } max ? " of " + max : "")}</td><td>{(c.Expires is { } end ? end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "never")}</td><td>{E(who)}</td>
                <td>{(c.Revoked ? "" : $"<form method=\"post\" action=\"/admin/codes/revoke\">{token}<input type=\"hidden\" name=\"id\" value=\"{E(c.Id)}\"><button type=\"submit\" class=\"danger\">Revoke</button></form>")}</td></tr>
                """);
        }

        var devices = rows.Sum(r => r.Account.Devices);
        var bytes = rows.Sum(r => r.Bytes);
        var disk = diskFree is { } f && diskTotal is { } t ? $", {Gib(f)} free of {Gib(t)} on the disk" : "";
        return Pages.Page("Admin", $"""
            <h1>Admin</h1>
            <p class="muted">Signed in as {E(session.User)}. <form method="post" action="/admin/logout" class="inline" style="display:inline">{token}<button type="submit">Sign out</button></form></p>
            <p>{rows.Count} account{(rows.Count == 1 ? "" : "s")}, {devices} device{(devices == 1 ? "" : "s")} signed in, {Mib(bytes)} of backups{E(disk)}.</p>

            <h2>Accounts</h2>
            <table>
            <tr><th>Account</th><th>Made</th><th>State</th><th>Backup</th><th>Pass</th><th></th></tr>
            {accountRows}
            </table>
            <form method="post" action="/admin/accounts/add" class="inline">
            {token}
            <label for="email">Add an account by email address (a password is made up and shown once)</label>
            <input id="email" name="email" type="email" autocapitalize="none" spellcheck="false" maxlength="254" required> <button type="submit">Add</button>
            </form>

            <h2>Signup codes</h2>
            <table>
            <tr><th>Id</th><th>Note</th><th>Status</th><th>Used</th><th>Until</th><th>Signed up</th><th></th></tr>
            {codeRows}
            </table>
            <form method="post" action="/admin/codes/add" class="inline">
            {token}
            <label for="note">Make a code: who it is for</label>
            <input id="note" name="note" maxlength="100">
            <label for="uses">How many people (empty for any number)</label>
            <input id="uses" name="uses" inputmode="numeric" maxlength="6">
            <label for="days">How many days it works (empty for no end)</label>
            <input id="days" name="days" inputmode="numeric" maxlength="4">
            <button type="submit">Make a code and its link</button>
            </form>
            """, wide: true);
    }
}
