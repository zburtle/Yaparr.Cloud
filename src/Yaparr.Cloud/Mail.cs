using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;

namespace Yaparr.Cloud;

// Email, for the few things a link can do and nothing else can: prove an address is the person's (before a password is chosen on it) and reset a forgotten password. It is off unless
// Service__Mail__Host names a server to send through, so a service that is run for one's own friends is exactly what it was, and nothing is ever emailed that is not asked for on a page here.
// Never marketing, never a newsletter, never a notice nobody asked for.

/// <summary>The settings of email (<c>Service__Mail__…</c>). Nothing is set by default, and with no <see cref="Host"/> email is off.</summary>
public sealed class MailOptions
{
    /// <summary>The SMTP server to send through (a provider's, or one's own). Empty: email is off.</summary>
    public string Host { get; set; } = "";

    /// <summary>Its port: 587 (the default) for STARTTLS, which is what providers offer for sending.</summary>
    public int Port { get; set; } = 587;

    /// <summary><c>starttls</c> (the default: the connection is encrypted before the password is sent) or <c>none</c> (only for a server on the same machine or a test).</summary>
    public string Security { get; set; } = "starttls";

    public string User { get; set; } = "";

    /// <summary>The SMTP password. A secret: it comes from the environment and is never written anywhere else.</summary>
    public string Password { get; set; } = "";

    /// <summary>The address mail is sent from (on a domain whose SPF, DKIM and DMARC are set for the server, or it lands in spam).</summary>
    public string From { get; set; } = "";

    public string FromName { get; set; } = "Yaparr Cloud";

    /// <summary>What is wrong with the settings, or null. Said at the start, with the name of what is missing.</summary>
    public string? Problem() =>
        Host.Length == 0 ? null
        : From.Length == 0 || !MailAddress.TryCreate(From, out _) ? "Service__Mail__From must be the address mail is sent from"
        : Port is < 1 or > 65535 ? "Service__Mail__Port is not a port"
        : Security is not ("starttls" or "none") ? "Service__Mail__Security is starttls or none"
        : User.Length > 0 && Password.Length == 0 ? "Service__Mail__Password is not set (Service__Mail__User is)"
        : null;
}

/// <summary>One message to one person: plain text.</summary>
public sealed record MailMessage(string To, string Subject, string Body);

/// <summary>A way to send mail. SMTP is the one this build has; anything else (a provider's API) is one more adapter.</summary>
public interface IMailer
{
    Task SendAsync(MailMessage message, CancellationToken cancel);
}

/// <summary>Sends through any SMTP server.</summary>
public sealed class SmtpMailer(MailOptions options) : IMailer
{
    public async Task SendAsync(MailMessage message, CancellationToken cancel)
    {
        using var client = new SmtpClient(options.Host, options.Port)
        {
            EnableSsl = options.Security == "starttls",
            DeliveryMethod = SmtpDeliveryMethod.Network,
            Timeout = 20_000,
        };
        if (options.User.Length > 0) client.Credentials = new NetworkCredential(options.User, options.Password);
        using var mail = new System.Net.Mail.MailMessage(new MailAddress(options.From, options.FromName, Encoding.UTF8), new MailAddress(message.To))
        {
            Subject = message.Subject,
            SubjectEncoding = Encoding.UTF8,
            Body = message.Body,
            BodyEncoding = Encoding.UTF8,
            IsBodyHtml = false,
        };
        await client.SendMailAsync(mail, cancel);
    }
}

/// <summary>
/// Sends the mail that a page asked for, <b>in the background</b>: the page answers at once and the same way whether or not the address has an account, so how long it takes says nothing about who
/// is known here. A failure is logged as that and nothing else (never the address); the person asks again on the same page.
/// </summary>
public sealed class MailService(IMailer? mailer, ILoggerFactory logs)
{
    private readonly ILogger _log = logs.CreateLogger("Yaparr.Cloud.Mail");
    private readonly ConcurrentDictionary<Task, byte> _inFlight = new();

    public bool Enabled => mailer != null;

    public void Send(MailMessage message)
    {
        if (mailer == null) return;
        var task = Task.Run(async () =>
        {
            try
            {
                using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await mailer.SendAsync(message, cancel.Token);
                _log.LogInformation("mail: sent");
            }
            catch (Exception ex) when (ex is SmtpException or IOException or OperationCanceledException or InvalidOperationException or FormatException)
            {
                _log.LogWarning("mail: could not be sent ({Kind})", ex.GetType().Name);
            }
        });
        _inFlight[task] = 0;
        _ = task.ContinueWith(t => _inFlight.TryRemove(t, out _), TaskScheduler.Default);
    }

    /// <summary>Completes when what has been handed to <see cref="Send"/> so far has been tried (for tests).</summary>
    public Task IdleAsync() => Task.WhenAll(_inFlight.Keys.ToArray());

    // ---- what is said ----

    /// <summary>The link to choose a password, for an address that has paid or been given a sign-up link.</summary>
    public static MailMessage SetupLink(string to, string link, TimeSpan valid) => new(to, "Choose your Yaparr Cloud password", $"""
        Open this link to choose the password for your Yaparr Cloud account:

        {link}

        It works once, for {Minutes(valid)}. If you did not ask for it, ignore this email: nothing happens until the link is opened.
        """);

    public static MailMessage ResetLink(string to, string link, TimeSpan valid) => new(to, "Reset your Yaparr Cloud password", $"""
        Open this link to choose a new password for your Yaparr Cloud account:

        {link}

        It works once, for {Minutes(valid)}. Choosing a new password signs every device out; your data is not touched. If you did not ask for it, ignore this email: your password stays as it is.
        """);

    private static string Minutes(TimeSpan span) => span.TotalMinutes >= 90 ? $"{(int)Math.Round(span.TotalHours)} hours" : $"{(int)span.TotalMinutes} minutes";
}

/// <summary>
/// The links that reset a password: random, kept in memory for an hour, good once. A restart ends them all, which only means asking again. Only a hash is held, and asking again replaces
/// the link before.
/// </summary>
public sealed class ResetLinks
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);
    private const int MaxPending = 10_000;
    private readonly ConcurrentDictionary<string, (string Account, DateTimeOffset Expires)> _links = new();
    private readonly Func<DateTimeOffset> _now;

    public ResetLinks() : this(() => DateTimeOffset.UtcNow) { }

    internal ResetLinks(Func<DateTimeOffset> now) => _now = now;

    private static string Key(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public string Make(string account)
    {
        var now = _now();
        foreach (var (key, held) in _links)
            if (held.Expires < now || held.Account == account) _links.TryRemove(key, out _);
        if (_links.Count >= MaxPending) _links.Clear();   // made-up addresses cannot fill the memory
        var token = "rst_" + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        _links[Key(token)] = (account, now + Lifetime);
        return token;
    }

    /// <summary>Who a link is for, without using it (a page shows the form; sending the new password uses it). Null if it is not one, was used, or has run out.</summary>
    public string? Peek(string? token) =>
        token is { Length: > 0 and <= 100 } && token.StartsWith("rst_", StringComparison.Ordinal) && _links.TryGetValue(Key(token), out var held) && held.Expires > _now() ? held.Account : null;

    /// <summary>Uses a link: the account it was for, once.</summary>
    public string? Take(string? token) =>
        Peek(token) is { } account && token != null && _links.TryRemove(Key(token), out _) ? account : null;
}
