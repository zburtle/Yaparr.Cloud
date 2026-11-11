using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Yaparr.Cloud;

/// <summary>
/// Lets a person who has just signed up carry on in the Yaparr app without typing their password again. When they finish choosing it, the page
/// offers a button, <c>yaparr://backup-signin/&lt;code&gt;/&lt;this server&gt;</c>, which opens the app; the app asks them to confirm, trades the code
/// for a token here (<c>POST /v1/token/handoff</c>) and is signed in.
///
/// This is weaker than <see cref="LoginFlow"/>, and is only for this one moment. There, the app starts the sign-in and keeps a secret (the PKCE
/// verifier) that makes a code useless to anything else. Here the browser starts it and the app is not yet involved, so there is no secret to keep:
/// the code alone is the proof, and another program on the same computer that has also claimed <c>yaparr://</c> links could take it. What limits that:
/// the code is 256 random bits, works once and for five minutes, is made only for an account whose person has just chosen its password, gives a token
/// for that account's locked backups and nothing else, and the app shows the server and the account and asks before it trades the code.
/// </summary>
public sealed class SignInHandoff
{
    private sealed record Pending(string User, DateTimeOffset Expires);

    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
    private const int MaxPending = 1000;
    private readonly ConcurrentDictionary<string, Pending> _codes = new();
    private readonly Func<DateTimeOffset> _now;

    public SignInHandoff() : this(() => DateTimeOffset.UtcNow) { }
    internal SignInHandoff(Func<DateTimeOffset> now) => _now = now;

    /// <summary>A code for a person who has just chosen their password.</summary>
    public string Create(string user)
    {
        var now = _now();
        foreach (var (old, pending) in _codes)
            if (pending.Expires < now) _codes.TryRemove(old, out _);
        // Made-up sign-ups cannot fill the memory: the oldest are dropped.
        while (_codes.Count >= MaxPending && _codes.OrderBy(c => c.Value.Expires).FirstOrDefault().Key is { } oldest) _codes.TryRemove(oldest, out _);

        var code = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        _codes[code] = new Pending(user, now + Lifetime);
        return code;
    }

    /// <summary>Trades a code for the account it was made for. It works once and in time; otherwise null.</summary>
    public string? Redeem(string? code)
    {
        if (code == null || code.Length is < 20 or > 100 || !_codes.TryRemove(code, out var pending)) return null;
        return pending.Expires < _now() ? null : pending.User;
    }

    /// <summary>The link for the button on the page: it opens Yaparr, which asks before it does anything. The server is escaped, so it is one part of the link.</summary>
    public static string AppLink(string code, string server) => $"yaparr://backup-signin/{code}/{Uri.EscapeDataString(server)}";
}
