using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Yaparr.Cloud;

/// <summary>One signed-in visit to the admin portal. <paramref name="Csrf"/> is what every form on its pages has to send back.</summary>
public sealed record AdminSession(string User, string Csrf, DateTimeOffset Started, DateTimeOffset LastSeen);

/// <summary>
/// The admin portal's sessions, kept in memory only: restarting the service signs the admin out, which is fine. The cookie holds a long random
/// secret and only its SHA-256 is kept here. A session ends after half an hour without a request, or eight hours after it began, whichever is first.
/// </summary>
public sealed class AdminSessions
{
    public static readonly TimeSpan Idle = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan Longest = TimeSpan.FromHours(8);

    private readonly ConcurrentDictionary<string, AdminSession> _sessions = new();
    private readonly Dictionary<string, long> _lastStep = new();
    private readonly Func<DateTimeOffset> _now;

    public AdminSessions() : this(() => DateTimeOffset.UtcNow) { }
    internal AdminSessions(Func<DateTimeOffset> now) => _now = now;

    private static string HashOf(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>Starts a session for an admin and returns the cookie value and the form token.</summary>
    public (string Token, string Csrf) Start(string user)
    {
        Sweep();
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var csrf = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        var now = _now();
        _sessions[HashOf(token)] = new AdminSession(user, csrf, now, now);
        return (token, csrf);
    }

    /// <summary>The session for a cookie value, which counts as seen now; null if there is none or it has ended.</summary>
    public AdminSession? Get(string? token)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 128) return null;
        var key = HashOf(token);
        if (!_sessions.TryGetValue(key, out var session)) return null;
        var now = _now();
        if (now - session.LastSeen >= Idle || now - session.Started >= Longest)
        {
            _sessions.TryRemove(key, out _);
            return null;
        }
        var seen = session with { LastSeen = now };
        _sessions[key] = seen;
        return seen;
    }

    public void End(string? token)
    {
        if (!string.IsNullOrEmpty(token)) _sessions.TryRemove(HashOf(token), out _);
    }

    /// <summary>Ends every session of a person (their password was changed, or they are no longer an admin).</summary>
    public void EndAll(string user)
    {
        foreach (var (key, session) in _sessions)
            if (session.User == user) _sessions.TryRemove(key, out _);
    }

    /// <summary>True the first time a step's code is used by this person, and not for that step or any earlier one afterwards.</summary>
    public bool TryAcceptStep(string user, long step)
    {
        lock (_lastStep)
        {
            if (_lastStep.TryGetValue(user, out var last) && step <= last) return false;
            _lastStep[user] = step;
            return true;
        }
    }

    private void Sweep()
    {
        var now = _now();
        foreach (var (key, session) in _sessions)
            if (now - session.LastSeen >= Idle || now - session.Started >= Longest) _sessions.TryRemove(key, out _);
    }
}
