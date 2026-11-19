using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Yaparr.Cloud;

/// <summary>What an app asks for when it sends a person to the sign-in page.</summary>
public sealed record LoginRequest(string RedirectUri, string Challenge, string State, string Device);

/// <summary>
/// Signing in happens in the person's browser, and the app is handed the result. It is the way programs on a computer sign in to web services
/// (OAuth's authorization code with PKCE, cut down to what is needed here):
/// <list type="number">
/// <item>The app listens on a port of its own computer, makes a random <c>verifier</c>, and opens the browser at
/// <c>/login?redirect_uri=http://127.0.0.1:port/…&amp;code_challenge=SHA-256(verifier)&amp;state=…</c>.</item>
/// <item>The person types their email address and password into the page. The app never sees them.</item>
/// <item>The browser is sent back to the app's port with a <c>code</c> that works once, for two minutes.</item>
/// <item>The app trades the code and its verifier for a token (<c>POST /v1/token</c>) and uses the token from then on.</item>
/// </list>
/// The code is useless to anything that sees it pass by, since only the app that began knows the verifier; and the browser is only ever sent to
/// the computer it is on (127.0.0.1), never to an address a link could choose.
/// </summary>
public sealed class LoginFlow
{
    private sealed record Pending(string User, string Challenge, string RedirectUri, string Device, DateTimeOffset Expires);

    private static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(2);
    private readonly ConcurrentDictionary<string, Pending> _codes = new();
    private readonly Func<DateTimeOffset> _now;

    public LoginFlow() : this(() => DateTimeOffset.UtcNow) { }
    internal LoginFlow(Func<DateTimeOffset> now) => _now = now;

    /// <summary>
    /// Reads what the app asked for, or says what is wrong with it. The address to go back to has to be on the computer the browser is on,
    /// so a link someone made up cannot send a code anywhere else.
    /// </summary>
    public static LoginRequest? Read(string? redirectUri, string? challenge, string? state, string? device, out string problem)
    {
        problem = "This sign-in link is not complete. Start again from Yaparr.";
        if (!IsAppLink(redirectUri) && (!Uri.TryCreate(redirectUri, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttp || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0
            || !IPAddress.TryParse(uri.Host.Trim('[', ']'), out var address) || !IPAddress.IsLoopback(address) || redirectUri!.Length > 300))
            return null;
        if (challenge == null || challenge.Length != 43 || !challenge.All(IsUrlSafe)) return null;
        if (state == null || state.Length is < 8 or > 128 || !state.All(IsUrlSafe)) return null;
        var name = new string((device ?? "").Where(c => !char.IsControl(c)).Take(60).ToArray()).Trim();
        problem = "";
        return new LoginRequest(redirectUri!, challenge, state, name.Length == 0 ? "A device" : name);
    }

    /// <summary>The address of the app itself, for a phone: it cannot be listening on a port while the browser is in front (Android stops an app that is not shown), so the
    /// browser is sent back through the app's own link, which opens it. Only this exact address, so a made-up link cannot send a code to anything else.</summary>
    public const string AppLink = "yaparr://signed-in";

    public static bool IsAppLink(string? redirectUri) => redirectUri == AppLink;

    private static bool IsUrlSafe(char c) => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-' or '_';

    /// <summary>After a good sign-in: where to send the browser, with a code for the app.</summary>
    public string Complete(LoginRequest request, string user)
    {
        var now = _now();
        foreach (var (old, pending) in _codes)
            if (pending.Expires < now) _codes.TryRemove(old, out _);

        var code = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        _codes[code] = new Pending(user, request.Challenge, request.RedirectUri, request.Device, now + CodeLifetime);
        var separator = request.RedirectUri.Contains('?') ? '&' : '?';
        return $"{request.RedirectUri}{separator}code={code}&state={request.State}";
    }

    /// <summary>Trades a code for who signed in. It works once, in time, and only with the verifier the challenge was made from.</summary>
    public (string User, string Device)? Redeem(string? code, string? verifier)
    {
        if (code == null || verifier == null || verifier.Length is < 43 or > 128 || !_codes.TryRemove(code, out var pending)) return null;
        if (pending.Expires < _now()) return null;
        var challenge = Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(challenge), Encoding.ASCII.GetBytes(pending.Challenge))
            ? (pending.User, pending.Device)
            : null;
    }
}

/// <summary>
/// Slows down guessing a password: after a number of wrong tries for a name, that name is refused for a while, whoever asks. (Requests from
/// one address are limited as well, by the service as a whole.)
/// </summary>
public sealed class LoginLimiter
{
    private const int MaxFailures = 8;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(15);
    private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> _failures = new();
    private readonly Func<DateTimeOffset> _now;

    public LoginLimiter() : this(() => DateTimeOffset.UtcNow) { }
    internal LoginLimiter(Func<DateTimeOffset> now) => _now = now;

    public bool IsBlocked(string name)
    {
        if (!_failures.TryGetValue(name, out var queue)) return false;
        lock (queue)
        {
            var now = _now();
            while (queue.Count > 0 && now - queue.Peek() >= Window) queue.Dequeue();
            return queue.Count >= MaxFailures;
        }
    }

    public void Failed(string name)
    {
        if (_failures.Count > 100_000) _failures.Clear();   // made-up names cannot fill the memory
        var queue = _failures.GetOrAdd(name, _ => new Queue<DateTimeOffset>());
        lock (queue) queue.Enqueue(_now());
    }

    public void Succeeded(string name) => _failures.TryRemove(name, out _);
}

/// <summary>The few pages a browser sees. Plain HTML with no scripts, and everything that came from a request is encoded before it is shown.</summary>
public static class Pages
{
    internal static string E(string? text) => WebUtility.HtmlEncode(text ?? "");

    private static string Page(string title, string body) => $$"""
        <!doctype html>
        <html lang="en">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <meta name="robots" content="noindex">
        <title>{{E(title)}}</title>
        <style>
        :root { color-scheme: light dark; }
        body { font: 16px/1.5 system-ui, sans-serif; margin: 0; padding: 24px 16px; background: Canvas; color: CanvasText; }
        main { max-width: 380px; margin: 8vh auto 0; }
        h1 { font-size: 22px; margin: 0 0 4px; }
        p { margin: 8px 0 16px; }
        label { display: block; font-size: 14px; margin: 14px 0 4px; }
        input { box-sizing: border-box; width: 100%; font: inherit; padding: 9px 10px; border: 1px solid GrayText; border-radius: 6px; background: Field; color: FieldText; }
        a.button { display: block; box-sizing: border-box; text-align: center; text-decoration: none; margin-top: 8px; padding: 10px; border-radius: 6px; background: #4f6ef7; color: #fff; font-weight: 600; }
        button { margin-top: 20px; width: 100%; font: inherit; font-weight: 600; padding: 10px; border: 0; border-radius: 6px; background: #4f6ef7; color: #fff; cursor: pointer; }
        .problem { color: #d93b3b; font-weight: 600; }
        .muted { opacity: .7; font-size: 14px; }
        a { color: inherit; }
        </style>
        </head>
        <body><main>
        {{body}}
        </main></body>
        </html>
        """;

    public static string Login(LoginRequest request, string username, string? problem) => Page("Sign in to Yaparr Cloud", $"""
        <h1>Sign in</h1>
        <p class="muted">Yaparr on <strong>{E(request.Device)}</strong> is asking to keep your account in sync on this server. What it keeps is locked on your own device, so the server cannot read it.</p>
        {(problem == null ? "" : $"<p class=\"problem\" role=\"alert\">{E(problem)}</p>")}
        <form method="post" action="/login">
        <input type="hidden" name="redirect_uri" value="{E(request.RedirectUri)}">
        <input type="hidden" name="code_challenge" value="{E(request.Challenge)}">
        <input type="hidden" name="state" value="{E(request.State)}">
        <input type="hidden" name="device" value="{E(request.Device)}">
        <label for="username">Email address</label>
        <input id="username" name="username" type="email" autocomplete="username email" autocapitalize="none" spellcheck="false" maxlength="254" required value="{E(username)}"{(username.Length == 0 ? " autofocus" : "")}>
        <label for="password">Password</label>
        <input id="password" name="password" type="password" autocomplete="current-password" required{(username.Length == 0 ? "" : " autofocus")}>
        <button type="submit">Sign in</button>
        </form>
        <p class="muted"><a href="/account/password">Change your password</a> &middot; <a href="/account/delete">Delete your account</a></p>
        """);

    public static string ChangePassword(string username, string? problem) => Page("Change your password", $"""
        <h1>Change your password</h1>
        <p class="muted">This signs every device out, so sign in again from Yaparr afterwards. What the server holds for you is not affected: it is locked with your Sync key, not with this password.</p>
        {(problem == null ? "" : $"<p class=\"problem\" role=\"alert\">{E(problem)}</p>")}
        <form method="post" action="/account/password">
        <label for="username">Email address</label>
        <input id="username" name="username" type="email" autocomplete="username email" autocapitalize="none" spellcheck="false" maxlength="254" required value="{E(username)}">
        <label for="current">Current password</label>
        <input id="current" name="current" type="password" autocomplete="current-password" required>
        <label for="new">New password (at least {Passwords.MinLength} characters)</label>
        <input id="new" name="new" type="password" autocomplete="new-password" minlength="{Passwords.MinLength}" required>
        <label for="again">New password again</label>
        <input id="again" name="again" type="password" autocomplete="new-password" minlength="{Passwords.MinLength}" required>
        <button type="submit">Change password</button>
        </form>
        """);

    public const string DeleteWords = "delete my account";

    public static string DeleteAccount(string username, string? problem) => Page("Delete your account", $"""
        <h1>Delete your account</h1>
        <p class="muted">This deletes your account on this server and everything it holds for you: your <strong>locked copy</strong>, the synced copy of your name, rooms and blocks, and the devices set up to be woken. Every device is signed out. <strong>It cannot be undone.</strong> Nothing on your own devices is deleted, and messages you sent stay with the people who received them.</p>
        {(problem == null ? "" : $"<p class=\"problem\" role=\"alert\">{E(problem)}</p>")}
        <form method="post" action="/account/delete">
        <label for="username">Email address</label>
        <input id="username" name="username" type="email" autocomplete="username email" autocapitalize="none" spellcheck="false" maxlength="254" required value="{E(username)}">
        <label for="current">Password</label>
        <input id="current" name="current" type="password" autocomplete="current-password" required>
        <label for="confirm">Type <strong>{DeleteWords}</strong> to confirm</label>
        <input id="confirm" name="confirm" type="text" autocomplete="off" autocapitalize="none" spellcheck="false" required>
        <button type="submit">Delete my account</button>
        </form>
        """);

    public static string SetPassword(string token, string email, string? problem) => Page("Choose your password", $"""
        <h1>Choose your password</h1>
        <p class="muted">Your account is <strong>{E(email)}</strong>. You have a lifetime pass. The password is for signing in to this server from Yaparr; it is typed here, on this server's own page, and nowhere else.</p>
        {(problem == null ? "" : $"<p class=\"problem\" role=\"alert\">{E(problem)}</p>")}
        <form method="post" action="/account/setup">
        <input type="hidden" name="token" value="{E(token)}">
        <label for="new">Password (at least {Passwords.MinLength} characters)</label>
        <input id="new" name="password" type="password" autocomplete="new-password" minlength="{Passwords.MinLength}" required autofocus>
        <label for="again">Password again</label>
        <input id="again" name="again" type="password" autocomplete="new-password" minlength="{Passwords.MinLength}" required>
        <button type="submit">Create my account</button>
        </form>
        <p class="muted">What the server holds for you is locked with a Sync key that Yaparr makes on your computer, not with this password, so the server cannot read it.</p>
        """);

    /// <param name="appLink">The link that opens Yaparr and signs it in (<see cref="SignInHandoff.AppLink"/>), or null where it cannot work (a phone: the Android app has no backup server yet).</param>
    public static string Welcome(string email, string server, string? appLink = null) => Page("You're in", $"""
        <h1>You're in</h1>
        <p>The account <strong>{E(email)}</strong> is ready, with a lifetime pass.</p>
        {(appLink == null ? "" : $"""
        <p><a class="button" href="{E(appLink)}">Open Yaparr and sign in</a></p>
        <p class="muted">This opens Yaparr on this computer, which asks you to confirm before it signs in; you do not have to type your password again. It works once, for five minutes. If nothing happens, Yaparr is not installed or needs updating: <a href="https://yaparr.com">get it here</a>, and use the steps below.</p>
        """)}
        <p>{(appLink == null ? "To use it" : "Or, to do it by hand")}, open Yaparr and go to <strong>Settings &gt; Yaparr Cloud</strong>. Type this address, choose <strong>Sign in with your browser</strong>, and sign in with your email address and the password you just chose:</p>
        <p><strong>{E(server)}</strong></p>
        <p class="muted">You can change your password later at <a href="/account/password">{E(server)}/account/password</a>.</p>
        """);

    /// <summary>After a sign-in that is to come back through the app's link: the page that opens Yaparr (a button, and the browser is sent there as well, as
    /// a browser does not follow a redirect from a form to another program's link everywhere).</summary>
    public static string BackToApp(string appLink) => Page("You're signed in", $"""
        <meta http-equiv="refresh" content="0;url={E(appLink)}">
        <h1>Signed in</h1>
        <p>Going back to Yaparr. If nothing happens, tap the button.</p>
        <p><a class="button" href="{E(appLink)}">Back to Yaparr</a></p>
        """);

    public static string Message(string title, string text) => Page(title, $"""
        <h1>{E(title)}</h1>
        <p>{E(text)}</p>
        """);
}
