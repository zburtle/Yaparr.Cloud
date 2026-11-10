using System.Collections.Concurrent;

namespace Yaparr.Cloud;

/// <summary>
/// Limits how often one account may save a backup. It is by account (a signed request), and not by address, because it runs after the signature is
/// checked: nobody can use up another account's allowance, and one person on many addresses is still one account.
/// </summary>
public sealed class WriteLimiter
{
    private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> _writes = new();
    private readonly Func<DateTimeOffset> _now;

    public WriteLimiter() : this(() => DateTimeOffset.UtcNow) { }
    internal WriteLimiter(Func<DateTimeOffset> now) => _now = now;

    /// <summary>True, and counts it, if the account has saved fewer than <paramref name="perHour"/> backups in the last hour.</summary>
    public bool TryTake(string account, int perHour)
    {
        var now = _now();
        var queue = _writes.GetOrAdd(account, _ => new Queue<DateTimeOffset>());
        lock (queue)
        {
            while (queue.Count > 0 && now - queue.Peek() >= TimeSpan.FromHours(1)) queue.Dequeue();
            if (queue.Count >= Math.Max(1, perHour)) return false;
            queue.Enqueue(now);
            return true;
        }
    }
}
