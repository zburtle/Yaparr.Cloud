using System.Globalization;
using System.Text;

namespace Yaparr.Cloud;

/// <summary>
/// The admin portal's pages: plain HTML and one stylesheet, no scripts, and everything that came from a request is encoded before it is shown.
/// They follow the browser's light or dark setting and work down to a phone's width (the tables scroll sideways inside their card).
/// </summary>
internal static class AdminPages
{
    /// <param name="Known">What is stored for the account could be read (it could not when the store of backups was down).</param>
    public sealed record AccountRow(AccountEntry Account, int Versions, long Bytes, DateTimeOffset? Last, bool Known = true, AccountUse? Use = null, PlanInfo? Plan = null);

    /// <summary>Where an account stands on payments (only when billing is on). <see cref="Paid"/>: it has had a subscription at some point. <see cref="Running"/>: one is not over.</summary>
    public sealed record PlanInfo(PlanState State, bool PastDue, bool Canceling, DateTimeOffset? EndsAt, bool Paid, bool Running);

    /// <summary>The counts of the Payments card: how many accounts are in each state.</summary>
    public sealed record BillingSummary(string Provider, int Lifetime, int Subscribed, int Canceling, int PastDue, int Lapsed, int Waiting, int PaidNoPassword);

    /// <summary>What an account holds against its limits besides backups: the kept store (history, emoji, GIFs), pictures left for people who are away, and the gateway today. Null when it could not be read.</summary>
    public sealed record AccountUse(long Kept, long Pictures, long GatewayToday);

    /// <summary>The limits the usage is shown against.</summary>
    public sealed record UseLimits(long Kept, long Pictures, long GatewayPerDay);

    private static string Of(long used, long limit)
    {
        var share = limit > 0 ? (double)used / limit : 0;
        var text = $"{Mib(used)} <span class=\"muted\">of {Mib(limit)}</span>";
        return share >= 0.9 ? $"<span class=\"pill warn\">{text}</span>" : text;
    }

    private static string E(string? text) => Pages.E(text);

    private static string Mib(long bytes) => (bytes / 1024.0 / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " MiB";

    private static string Gib(long bytes) => (bytes / 1024.0 / 1024.0 / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " GiB";

    private static string Plural(long n, string one, string many = "") => n == 1 ? $"{n} {one}" : $"{n} {(many.Length > 0 ? many : one + "s")}";

    // The Yaparr icon (assets/logo/yaparr-icon.svg in the app's repository), drawn into the page: the pages load nothing from anywhere else.
    private const string Logo = """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 256 256" aria-hidden="true"><defs><linearGradient id="yb" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="#6A84F5"/><stop offset="1" stop-color="#3A4FD0"/></linearGradient></defs><rect width="256" height="256" rx="58" fill="url(#yb)"/><g stroke-linecap="round" stroke-width="13" fill="none"><path d="M70 92L50 58" stroke="#F2A93B"/><path d="M88 86L80 48" stroke="#F0605F"/><path d="M106 86L112 50" stroke="#F2A93B"/></g><circle cx="86" cy="140" r="56" fill="#fff"/><circle cx="72" cy="158" r="13" fill="#F2A93B" opacity=".35"/><path d="M134 98C168 90 198 104 198 134C188 122 168 118 134 120Z" fill="#F2A93B" transform="rotate(-14 134 109)"/><path d="M134 142C160 144 182 150 186 166C168 176 148 168 134 158Z" fill="#E08A1E"/><path d="M42 112L102 124M102 124L124 98" stroke="#26306B" stroke-width="6" stroke-linecap="round" fill="none"/><circle cx="102" cy="124" r="15" fill="#26306B"/><g stroke="#fff" stroke-width="10" stroke-linecap="round" fill="none"><path d="M208 122a22 22 0 0 1 0 34"/><path d="M222 108a42 42 0 0 1 0 64" opacity=".75"/></g></svg>""";

    private static string Token(string csrf) => $"<input type=\"hidden\" name=\"csrf\" value=\"{E(csrf)}\">";

    private const string Style = """
        :root {
          color-scheme: light dark;
          --bg: #f4f5f9; --card: #ffffff; --text: #1a1d27; --muted: #626b7f; --line: #e3e6ee; --field: #ffffff;
          --accent: #4057e6; --accent-hover: #3348cc; --on-accent: #ffffff; --accent-soft: #eaedff;
          --danger: #c9302c; --danger-soft: #fdecec;
          --ok: #14753f; --ok-soft: #e4f5ea; --warn: #9a5700; --warn-soft: #fcefdc; --off: #5b6476; --off-soft: #eceef3;
          --shadow: 0 1px 2px rgba(16, 24, 40, .05), 0 2px 8px rgba(16, 24, 40, .05);
        }
        @media (prefers-color-scheme: dark) {
          :root {
            --bg: #0e1015; --card: #171a22; --text: #e9ebf2; --muted: #98a1b3; --line: #272c38; --field: #0f1218;
            --accent: #7389ff; --accent-hover: #8a9cff; --on-accent: #0e1015; --accent-soft: #212849;
            --danger: #ff7b76; --danger-soft: #3a1c1d;
            --ok: #5fd08d; --ok-soft: #14301f; --warn: #f0b45f; --warn-soft: #34270f; --off: #98a1b3; --off-soft: #232834;
            --shadow: none;
          }
        }
        * { box-sizing: border-box; }
        body { margin: 0; background: var(--bg); color: var(--text); font: 15px/1.5 system-ui, -apple-system, "Segoe UI", Roboto, sans-serif; -webkit-font-smoothing: antialiased; }
        a { color: var(--accent); text-underline-offset: 2px; }
        h1 { font-size: 22px; line-height: 1.25; margin: 0 0 6px; letter-spacing: -.01em; }
        h2 { font-size: 16px; margin: 0; letter-spacing: -.005em; }
        p { margin: 0 0 12px; }
        code { font: 13px ui-monospace, SFMono-Regular, Menlo, Consolas, monospace; }
        .muted { color: var(--muted); }
        .small { font-size: 13px; }

        .top { background: var(--card); border-bottom: 1px solid var(--line); }
        .top > div { max-width: 1080px; margin: 0 auto; padding: 12px 20px; display: flex; align-items: center; gap: 12px; flex-wrap: wrap; }
        .brand { font-weight: 700; letter-spacing: -.01em; display: flex; align-items: center; gap: 10px; text-decoration: none; color: var(--text); }
        .brand svg { width: 28px; height: 28px; flex: none; }
        .brand span { color: var(--muted); font-weight: 500; }
        .who { margin-left: auto; display: flex; align-items: center; gap: 12px; color: var(--muted); font-size: 13px; }
        .who form { margin: 0; }

        .wrap { max-width: 1080px; margin: 0 auto; padding: 24px 20px 56px; }
        .narrow { max-width: 440px; margin: 0 auto; padding: 8vh 20px 56px; }
        .card { background: var(--card); border: 1px solid var(--line); border-radius: 12px; box-shadow: var(--shadow); }
        section.card + section.card, .stats + section.card { margin-top: 20px; }
        .card > header { display: flex; align-items: center; justify-content: space-between; gap: 12px; flex-wrap: wrap; padding: 16px 20px; border-bottom: 1px solid var(--line); }
        .card > header p { margin: 2px 0 0; }
        .body { padding: 20px; }
        .login { padding: 28px; }
        .login .brand { margin-bottom: 18px; }

        .stats { display: grid; grid-template-columns: repeat(auto-fit, minmax(190px, 1fr)); gap: 16px; }
        .stat { padding: 16px 20px; }
        .stat .label { color: var(--muted); font-size: 13px; }
        .stat .value { font-size: 26px; font-weight: 650; letter-spacing: -.02em; margin-top: 2px; }
        .stat .note { color: var(--muted); font-size: 13px; }
        .bar { height: 6px; border-radius: 99px; background: var(--off-soft); margin: 10px 0 6px; overflow: hidden; }
        .bar span { display: block; height: 100%; background: var(--accent); border-radius: 99px; }

        .scroll { overflow-x: auto; }
        table { width: 100%; min-width: 660px; border-collapse: collapse; font-size: 14px; }
        th { text-align: left; font-weight: 600; font-size: 12px; text-transform: uppercase; letter-spacing: .04em; color: var(--muted); padding: 10px 20px; white-space: nowrap; border-bottom: 1px solid var(--line); }
        td { padding: 12px 20px; border-bottom: 1px solid var(--line); vertical-align: middle; white-space: nowrap; }
        tr:last-child td { border-bottom: 0; }
        td.actions { white-space: nowrap; text-align: right; }
        td.actions form { display: inline; margin: 0; }
        .empty { padding: 28px 20px; text-align: center; color: var(--muted); }
        .name { font-weight: 550; }

        .pill { display: inline-block; padding: 1px 9px; border-radius: 99px; font-size: 12px; font-weight: 600; white-space: nowrap; background: var(--off-soft); color: var(--off); }
        .pill.ok { background: var(--ok-soft); color: var(--ok); }
        .pill.warn { background: var(--warn-soft); color: var(--warn); }
        .pill.bad { background: var(--danger-soft); color: var(--danger); }
        .pill.admin { background: var(--accent-soft); color: var(--accent); margin-left: 6px; }

        label { display: block; font-size: 13px; font-weight: 600; margin: 0 0 5px; }
        label .hint { font-weight: 400; color: var(--muted); }
        input[type=text], input[type=email], input[type=password], input:not([type]), input[inputmode] {
          width: 100%; font: inherit; padding: 9px 11px; border: 1px solid var(--line); border-radius: 8px; background: var(--field); color: var(--text);
        }
        input:focus-visible, button:focus-visible, a:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; }
        .field { margin-bottom: 16px; }
        .grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(190px, 1fr)); gap: 16px; align-items: end; }
        .grid .field { margin: 0; }
        .row { display: flex; gap: 10px; align-items: end; flex-wrap: wrap; }
        .row .field { flex: 1 1 260px; margin: 0; }

        .btn { font: inherit; font-weight: 600; font-size: 14px; padding: 9px 16px; border: 1px solid transparent; border-radius: 8px; cursor: pointer; background: var(--accent); color: var(--on-accent); text-decoration: none; display: inline-block; line-height: 1.3; }
        .btn:hover { background: var(--accent-hover); }
        .btn.block { width: 100%; padding: 11px 16px; }
        .btn.secondary { background: transparent; color: var(--text); border-color: var(--line); }
        .btn.secondary:hover { background: var(--off-soft); }
        .btn.danger { background: var(--danger); color: #fff; }
        .btn.danger:hover { filter: brightness(.92); }
        .btn.small { padding: 5px 11px; font-size: 13px; }
        .btn.link-danger { background: transparent; color: var(--danger); border-color: transparent; padding: 5px 8px; font-size: 13px; }
        .btn.link-danger:hover { background: var(--danger-soft); }

        .alert { padding: 10px 14px; border-radius: 8px; margin-bottom: 16px; font-size: 14px; background: var(--danger-soft); color: var(--danger); font-weight: 550; }
        .notice { padding: 10px 14px; border-radius: 8px; margin-bottom: 16px; font-size: 14px; background: var(--warn-soft); color: var(--warn); }
        .secret { display: block; user-select: all; padding: 14px 16px; margin: 4px 0 18px; border: 1px dashed var(--line); border-radius: 8px; background: var(--field); font: 15px/1.5 ui-monospace, SFMono-Regular, Menlo, Consolas, monospace; word-break: break-all; }
        .actions-row { display: flex; gap: 10px; flex-wrap: wrap; }

        @media (max-width: 640px) {
          .wrap { padding: 16px 12px 40px; }
          th, td { padding-left: 12px; padding-right: 12px; }
          .card > header, .body { padding-left: 14px; padding-right: 14px; }
          .login { padding: 22px; }
          .stats { grid-template-columns: repeat(2, 1fr); gap: 10px; }
          .stat { padding: 12px 14px; }
          .stat .value { font-size: 20px; }
          section.card + section.card, .stats + section.card { margin-top: 14px; }
          .who { margin-left: 0; width: 100%; justify-content: space-between; }
        }
        """;

    /// <summary>Every page: the stylesheet, a bar with the name (and, when signed in, who and a sign-out button), and the content.</summary>
    private static string Shell(string title, string body, string? user = null, string? csrf = null, bool narrow = false) => $$"""
        <!doctype html>
        <html lang="en">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <meta name="robots" content="noindex">
        <title>{{E(title)}} · Yaparr cloud admin</title>
        <style>
        {{Style}}
        </style>
        </head>
        <body>
        {{(user == null ? "" : $"""
        <div class="top"><div>
        <a class="brand" href="/admin">{Logo}Yaparr cloud <span>Admin</span></a>
        <div class="who">{E(user)}
        <form method="post" action="/admin/logout">{Token(csrf ?? "")}<button class="btn secondary small" type="submit">Sign out</button></form></div>
        </div></div>
        """)}}
        <{{(narrow ? "main class=\"narrow\"" : "main class=\"wrap\"")}}>
        {{body}}
        </main>
        </body>
        </html>
        """;

    private static string Alert(string? problem) => problem == null ? "" : $"<div class=\"alert\" role=\"alert\">{E(problem)}</div>";

    public static string Login(string username, string? problem) => Shell("Sign in", $"""
        <div class="card login">
        <div class="brand">{Logo}Yaparr cloud <span>Admin</span></div>
        <h1>Sign in</h1>
        <p class="muted">With your account's password and the code from your authenticator app.</p>
        {Alert(problem)}
        <form method="post" action="/admin/login">
        <div class="field"><label for="username">Email address</label>
        <input id="username" name="username" type="email" autocomplete="username" autocapitalize="none" spellcheck="false" maxlength="254" required value="{E(username)}"{(username.Length == 0 ? " autofocus" : "")}></div>
        <div class="field"><label for="password">Password</label>
        <input id="password" name="password" type="password" autocomplete="current-password" required{(username.Length == 0 ? "" : " autofocus")}></div>
        <div class="field"><label for="code">Authenticator code</label>
        <input id="code" name="code" inputmode="numeric" autocomplete="one-time-code" maxlength="10" placeholder="6 digits" required></div>
        <button class="btn block" type="submit">Sign in</button>
        </form>
        </div>
        """, narrow: true);

    /// <summary>A short answer (something did not work, or something was done), with the way back.</summary>
    public static string Result(string title, string text, AdminSession? session = null) => Shell(title, $"""
        <div class="card"><div class="body">
        <h1>{E(title)}</h1>
        <p class="muted">{E(text)}</p>
        <a class="btn secondary" href="/admin">Back to the overview</a>
        </div></div>
        """, session?.User, session?.Csrf, narrow: true);

    /// <summary>A page that shows something once (a new password, a signup link): it is never kept.</summary>
    public static string Secret(string title, string text, string secret, AdminSession? session = null) => Shell(title, $"""
        <div class="card"><div class="body">
        <h1>{E(title)}</h1>
        <p class="muted">{E(text)}</p>
        <code class="secret">{E(secret)}</code>
        <div class="notice">Shown once. Copy it now: it is not kept anywhere.</div>
        <a class="btn secondary" href="/admin">Back to the overview</a>
        </div></div>
        """, session?.User, session?.Csrf, narrow: true);

    public static string ConfirmRemove(AdminSession session, string name, bool isAdmin, string? problem = null) => Shell("Remove " + name, $"""
        <div class="card"><div class="body">
        <h1>Remove {E(name)}?</h1>
        {(isAdmin ? "<div class=\"alert\">This account is an admin, so it cannot be removed here. Take the portal away first, on the server: <code>accounts admin " + E(name) + " --remove</code></div>" : $"""
        <p class="muted">This removes the account <strong>and deletes its backup</strong>. It cannot be undone.</p>
        {Alert(problem)}
        <form method="post" action="/admin/accounts/remove">
        {Token(session.Csrf)}
        <input type="hidden" name="name" value="{E(name)}">
        <div class="field"><label for="confirm">Type the account's email address to confirm</label>
        <input id="confirm" name="confirm" type="email" autocomplete="off" autocapitalize="none" spellcheck="false" required autofocus></div>
        <div class="actions-row"><button class="btn danger" type="submit">Remove the account and its backup</button>
        <a class="btn secondary" href="/admin">Cancel</a></div>
        </form>
        """)}
        {(isAdmin ? "<a class=\"btn secondary\" href=\"/admin\">Back to the overview</a>" : "")}
        </div></div>
        """, session.User, session.Csrf, narrow: true);

    public static string ConfirmDeleteCode(AdminSession session, SignupCodeInfo code, DateTimeOffset now) => Shell("Delete a code", $"""
        <div class="card"><div class="body">
        <h1>Delete {(code.Note.Length > 0 ? E(code.Note) : "this code")}?</h1>
        <p class="muted">Code <code>{E(code.Id)}</code>{(code.Problem(now) is null ? ", which still works" : "")}, used by {Plural(code.Uses.Count, "person", "people")}. This takes it out altogether{(code.Problem(now) is null ? ": the link stops working" : "")}, and forgets who signed up with it. Their accounts are not touched. It cannot be undone.</p>
        <form method="post" action="/admin/codes/delete">
        {Token(session.Csrf)}
        <input type="hidden" name="id" value="{E(code.Id)}">
        <div class="actions-row"><button class="btn danger" type="submit">Delete the code</button>
        <a class="btn secondary" href="/admin">Cancel</a></div>
        </form>
        </div></div>
        """, session.User, session.Csrf, narrow: true);

    private static string CodePill(string status) => status switch
    {
        "works" => "<span class=\"pill ok\">Works</span>",
        "revoked" => "<span class=\"pill bad\">Revoked</span>",
        "expired" => "<span class=\"pill warn\">Expired</span>",
        _ => "<span class=\"pill warn\">Used up</span>",
    };

    private static string PlanCell(AccountRow row, DateTimeOffset now)
    {
        var a = row.Account;
        if (row.Plan is not { } plan) return "";
        if (!a.HasPassword && a.Pass == AccountPlan.None)
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

    private static string PaymentActions(AccountRow row, string token)
    {
        var a = row.Account;
        if (row.Plan is not { } plan) return "";
        var name = E(a.Name);
        var buttons = new StringBuilder();
        if (a.Pass == AccountPlan.Lifetime)
            buttons.Append($"<form method=\"post\" action=\"/admin/accounts/pass\">{token}<input type=\"hidden\" name=\"name\" value=\"{name}\"><input type=\"hidden\" name=\"pass\" value=\"none\"><button class=\"btn secondary small\" type=\"submit\" aria-label=\"Make {name} pay from now on\">Make it pay</button></form>");
        else if (a.Pass == AccountPlan.None)
            buttons.Append($"<form method=\"post\" action=\"/admin/accounts/pass\">{token}<input type=\"hidden\" name=\"name\" value=\"{name}\"><input type=\"hidden\" name=\"pass\" value=\"lifetime\"><button class=\"btn secondary small\" type=\"submit\" aria-label=\"Give {name} the lifetime pass\">Give lifetime</button></form>");
        if (!a.HasPassword && a.Pass == AccountPlan.None && plan.Paid)
            buttons.Append($"<form method=\"post\" action=\"/admin/accounts/setup-link\">{token}<input type=\"hidden\" name=\"name\" value=\"{name}\"><button class=\"btn secondary small\" type=\"submit\" aria-label=\"Make a new sign-in link for {name}\">New sign-in link</button></form>");
        if (plan.Paid)
            buttons.Append($"<form method=\"post\" action=\"/admin/accounts/subscription/check\">{token}<input type=\"hidden\" name=\"name\" value=\"{name}\"><button class=\"btn secondary small\" type=\"submit\" aria-label=\"Ask the payment provider about {name}\">Check payment</button></form>");
        if (plan.Running)
            buttons.Append($"<a class=\"btn link-danger\" href=\"/admin/accounts/subscription?name={Uri.EscapeDataString(a.Name)}\" aria-label=\"End the subscription of {name}\">End subscription</a>");
        return buttons.ToString();
    }

    private static string PaymentsCard(BillingSummary b) => $"""
        <section class="card">
        <header><div><h2>Payments</h2><p class="muted small">Through <strong>{E(b.Provider)}</strong>. Refund in the provider's dashboard: the subscription ends by itself when a refund goes through. An account that paid and lost the page it was sent back to gets a new link from its row.</p></div></header>
        <div class="body"><p>
        <span class="pill ok">{b.Lifetime} lifetime</span>
        <span class="pill ok">{b.Subscribed} subscribed</span>
        {(b.Canceling > 0 ? $"<span class=\"pill warn\">{b.Canceling} ending</span>" : "")}
        {(b.PastDue > 0 ? $"<span class=\"pill warn\">{b.PastDue} payment failing</span>" : "")}
        <span class="pill {(b.Lapsed > 0 ? "bad" : "ok")}">{b.Lapsed} lapsed</span>
        {(b.Waiting > 0 ? $"<span class=\"pill warn\">{b.Waiting} waiting for payment</span>" : "")}
        {(b.PaidNoPassword > 0 ? $"<span class=\"pill warn\">{b.PaidNoPassword} paid, no password yet</span>" : "")}
        </p></div>
        </section>
        """;

    /// <summary>Ending what an account pays for: now (it lapses at once) or when the paid period ends. Asked first, because ending is not undone.</summary>
    public static string ConfirmEnd(AdminSession session, string name, DateTimeOffset? endsAt, string? problem = null) => Shell("End the subscription of " + name, $"""
        <div class="card"><div class="body">
        <h1>End the subscription of {E(name)}?</h1>
        <p class="muted">Nothing is deleted either way: the account and its backups stay, and it can subscribe again. {(endsAt is { } end ? $"It has paid until <strong>{end:yyyy-MM-dd}</strong>." : "")}</p>
        {Alert(problem)}
        <form method="post" action="/admin/accounts/subscription/end">
        {Token(session.Csrf)}
        <input type="hidden" name="name" value="{E(name)}">
        <div class="actions-row">
        <button class="btn secondary" type="submit" name="when" value="period">Stop it renewing (it ends when the paid period does)</button>
        <button class="btn danger" type="submit" name="when" value="now">End it now (the account lapses at once)</button>
        <a class="btn secondary" href="/admin">Cancel</a></div>
        <p class="muted small">Ending it now does not refund anything: refund in the provider's dashboard if money is owed.</p>
        </form>
        </div></div>
        """, session.User, session.Csrf, narrow: true);

    public static string Dashboard(AdminSession session, IReadOnlyList<AccountRow> rows, IReadOnlyList<SignupCodeInfo> codes, DateTimeOffset now, long? diskFree, long? diskTotal,
        IReadOnlyList<(string Label, StoreStatus Status)>? storage = null, UseLimits? limits = null, BillingSummary? billing = null)
    {
        var token = Token(session.Csrf);

        var accountRows = new StringBuilder();
        foreach (var row in rows)
        {
            var a = row.Account;
            var state = a.HasPassword ? Plural(a.Devices, "device") + " signed in" : a.SignupCode != null ? "Signed up, no password yet" : "No password";
            var backup = !row.Known ? "<span class=\"muted\">Not available</span>"
                : row.Versions == 0 ? "<span class=\"muted\">No backup</span>"
                : $"{Mib(row.Bytes)} <span class=\"muted\">· {Plural(row.Versions, "version")}, {row.Last:yyyy-MM-dd HH:mm}</span>";
            var unknown = "<span class=\"muted\">Not available</span>";
            var l = limits ?? new UseLimits(0, 0, 0);
            accountRows.Append($"""
                <tr>
                <td><span class="name">{E(a.Name)}</span>{(a.IsAdmin ? "<span class=\"pill admin\">Admin</span>" : "")}</td>
                <td>{E(state)}</td>
                {(billing == null ? "" : $"<td>{PlanCell(row, now)}</td>")}
                <td>{backup}</td>
                <td>{(row.Use is { } u ? Of(u.Kept, l.Kept) : unknown)}</td>
                <td>{(row.Use is { } p ? Of(p.Pictures, l.Pictures) : unknown)}</td>
                <td>{(row.Use is { } g ? Of(g.GatewayToday, l.GatewayPerDay) : unknown)}</td>
                <td class="muted">{a.Created:yyyy-MM-dd}</td>
                <td class="actions">
                <form method="post" action="/admin/accounts/password">{token}<input type="hidden" name="name" value="{E(a.Name)}"><button class="btn secondary small" type="submit" aria-label="Reset the password of {E(a.Name)}">Reset password</button></form>
                {(billing == null ? "" : PaymentActions(row, token))}
                <a class="btn link-danger" href="/admin/accounts/remove?name={Uri.EscapeDataString(a.Name)}" aria-label="Remove {E(a.Name)}">Remove</a>
                </td>
                </tr>
                """);
        }

        var codeRows = new StringBuilder();
        foreach (var c in codes.OrderByDescending(c => c.Created))
        {
            var who = c.Uses.Count == 0 ? "<span class=\"muted\">Nobody yet</span>" : E(string.Join(", ", c.Uses.Select(u => u.Email)));
            codeRows.Append($"""
                <tr>
                <td>{(c.Note.Length > 0 ? $"<span class=\"name\">{E(c.Note)}</span>" : "<span class=\"muted\">No note</span>")}<div class="muted small"><code>{E(c.Id)}</code></div></td>
                <td>{CodePill(c.Problem(now) ?? "works")}</td>
                <td>{c.Uses.Count}{(c.MaxUses is { } max ? $" of {max}" : "")}</td>
                <td>{(c.Expires is { } end ? end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "<span class=\"muted\">Never</span>")}</td>
                <td>{who}</td>
                <td class="actions">{(c.Revoked ? "" : $"<form method=\"post\" action=\"/admin/codes/revoke\">{token}<input type=\"hidden\" name=\"id\" value=\"{E(c.Id)}\"><button class=\"btn link-danger\" type=\"submit\" aria-label=\"Revoke the code {E(c.Id)}\">Revoke</button></form>")}
                <a class="btn link-danger" href="/admin/codes/delete?id={Uri.EscapeDataString(c.Id)}" aria-label="Delete the code {E(c.Id)}">Delete</a></td>
                </tr>
                """);
        }

        var devices = rows.Sum(r => r.Account.Devices);
        var bytes = rows.Sum(r => r.Bytes);
        var backupsKnown = rows.All(r => r.Known);

        var storageRows = new StringBuilder();
        foreach (var (label, status) in storage ?? Array.Empty<(string, StoreStatus)>())
            storageRows.Append($"""
                <tr>
                <td><span class="name">{E(label)}</span></td>
                <td>{E(status.Kind)} <span class="muted">· {E(status.Where)}</span></td>
                <td>{(status.Ok ? "<span class=\"pill ok\">OK</span>" : "<span class=\"pill bad\">Problem</span>")} <span class="muted">{E(status.Detail)}</span></td>
                </tr>
                """);
        var disk = "";
        if (diskFree is { } free && diskTotal is { } total && total > 0)
        {
            var used = Math.Clamp((total - free) * 100.0 / total, 0, 100);
            disk = $"""
                <div class="card stat"><div class="label">Disk</div><div class="value">{Gib(free)} free</div>
                <div class="bar" role="img" aria-label="{used:0}% of the disk is used"><span style="width:{used.ToString("0.#", CultureInfo.InvariantCulture)}%"></span></div>
                <div class="note">{used:0}% of {Gib(total)} used</div></div>
                """;
        }
        var working = codes.Count(c => c.Problem(now) == null);

        return Shell("Overview", $"""
            <div class="stats">
            <div class="card stat"><div class="label">Accounts</div><div class="value">{rows.Count}</div><div class="note">{Plural(devices, "device")} signed in</div></div>
            <div class="card stat"><div class="label">Backups</div><div class="value">{(backupsKnown ? Mib(bytes) : "—")}</div><div class="note">{(backupsKnown ? Plural(rows.Sum(r => r.Versions), "version") + " kept" : "not available")}</div></div>
            <div class="card stat"><div class="label">Signup codes</div><div class="value">{working}</div><div class="note">{(working == 1 ? "works" : "work")} now, {codes.Count} made</div></div>
            {disk}
            </div>

            {(billing == null ? "" : PaymentsCard(billing))}

            {(storageRows.Length == 0 ? "" : $"""
            <section class="card">
            <header><div><h2>Storage</h2><p class="muted small">Where everything is kept, and whether it answers.</p></div></header>
            <div class="scroll"><table>
            <thead><tr><th>What</th><th>Where</th><th>Status</th></tr></thead>
            <tbody>
            {storageRows}
            </tbody></table></div>
            </section>
            """)}

            <section class="card">
            <header><div><h2>Accounts</h2><p class="muted small">Backups are locked on each person's device: nothing here can read one. The columns after Backup are what each account holds against its limits (kept in sync, pictures left for people who are away, and the gateway today); amber is over nine tenths.</p></div></header>
            {(rows.Count == 0 ? "<div class=\"empty\">No accounts yet.</div>" : $"""
            <div class="scroll"><table>
            <thead><tr><th>Account</th><th>State</th>{(billing == null ? "" : "<th>Plan</th>")}<th>Backup</th><th>Kept in sync</th><th>Pictures left</th><th>Gateway today</th><th>Made</th><th></th></tr></thead>
            <tbody>
            {accountRows}
            </tbody></table></div>
            """)}
            <div class="body" style="border-top:1px solid var(--line)">
            <form method="post" action="/admin/accounts/add" class="row">
            {token}
            <div class="field"><label for="email">Add an account <span class="hint">· a password is made up and shown once</span></label>
            <input id="email" name="email" type="email" placeholder="name@example.com" autocapitalize="none" spellcheck="false" maxlength="254" required></div>
            <button class="btn" type="submit">Add account</button>
            </form></div>
            </section>

            <section class="card">
            <header><div><h2>Signup codes</h2><p class="muted small">Send the link a code makes to friends. One code can be used by many people.</p></div></header>
            {(codes.Count == 0 ? "<div class=\"empty\">No codes yet. Make one below.</div>" : $"""
            <div class="scroll"><table>
            <thead><tr><th>Code</th><th>Status</th><th>Used</th><th>Until</th><th>Signed up</th><th></th></tr></thead>
            <tbody>
            {codeRows}
            </tbody></table></div>
            """)}
            <div class="body" style="border-top:1px solid var(--line)">
            <form method="post" action="/admin/codes/add">
            {token}
            <div class="grid">
            <div class="field"><label for="note">Who it is for</label><input id="note" name="note" maxlength="100" placeholder="Book club"></div>
            <div class="field"><label for="uses">How many people <span class="hint">· empty: any</span></label><input id="uses" name="uses" inputmode="numeric" maxlength="6" placeholder="10"></div>
            <div class="field"><label for="days">Works for (days) <span class="hint">· empty: no end</span></label><input id="days" name="days" inputmode="numeric" maxlength="4" placeholder="30"></div>
            <div><button class="btn" type="submit">Make code and link</button></div>
            </div>
            </form></div>
            </section>
            """, session.User, session.Csrf);
    }
}
