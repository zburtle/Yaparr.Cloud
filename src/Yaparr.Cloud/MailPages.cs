using System.Net;

namespace Yaparr.Cloud;

/// <summary>The pages that only exist when email is on.</summary>
public static class MailPages
{
    private static string E(string text) => WebUtility.HtmlEncode(text);

    /// <summary>Asks for an address, for a link to choose or change a password.</summary>
    public static string Forgot(string email = "") => Pages.Page("Email me a link", $"""
        <h1>Email me a link</h1>
        <p class="muted">Type the email address of your Yaparr Cloud account. If it has an account here, a link to choose a new password is sent to it. It works once, for an hour. Nothing else is ever emailed.</p>
        <form method="post" action="/account/forgot">
        <label for="email">Email address</label>
        <input id="email" name="email" type="email" autocomplete="username email" autocapitalize="none" spellcheck="false" maxlength="254" required value="{E(email)}" autofocus>
        <button type="submit">Email me the link</button>
        </form>
        """);

    /// <summary>The answer, the same whether or not the address has an account (so the page cannot be used to find out who does).</summary>
    public static string Sent() => Pages.Message("Check your email",
        "If that address has an account here, a link has been sent to it. It can take a minute, and it may be in the spam folder. The link works once, for an hour. If nothing comes, you can ask again.");

    public static string ResetPassword(string token, string email, string? problem) => Pages.Page("Choose a new password", $"""
        <h1>Choose a new password</h1>
        <p class="muted">For <strong>{E(email)}</strong>. Every device is signed out when you do, and you sign in again with the new password. What the server holds for you is locked with a key on your own devices, so it is untouched.</p>
        {(problem == null ? "" : $"<p class=\"problem\" role=\"alert\">{E(problem)}</p>")}
        <form method="post" action="/account/reset">
        <input type="hidden" name="token" value="{E(token)}">
        <label for="new">New password (at least {Passwords.MinLength} characters)</label>
        <input id="new" name="password" type="password" autocomplete="new-password" minlength="{Passwords.MinLength}" required autofocus>
        <label for="again">Password again</label>
        <input id="again" name="again" type="password" autocomplete="new-password" minlength="{Passwords.MinLength}" required>
        <button type="submit">Change my password</button>
        </form>
        """);
}
