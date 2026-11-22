using System.Text.Json;

namespace Yaparr.Cloud;

/// <summary><see cref="ISubscriptions"/> in a file (<c>subscriptions.json</c> in the data folder), for a service that has no database.</summary>
public sealed class SubscriptionStore : ISubscriptions
{
    private sealed class Data
    {
        public List<Subscription> Subscriptions { get; set; } = new();
        public Dictionary<string, string> BillingIds { get; set; } = new();
        public List<EventEntry> Events { get; set; } = new();
    }

    private sealed class EventEntry
    {
        public string Provider { get; set; } = "";
        public string EventId { get; set; } = "";
        public DateTimeOffset At { get; set; }
    }

    private readonly string _path;
    private readonly object _gate = new();
    private Data _data = new();
    private (DateTime At, long Length)? _loaded;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public SubscriptionStore(ServiceOptions options)
    {
        var root = Path.GetFullPath(options.DataDirectory);
        Directory.CreateDirectory(root);
        _path = Path.Combine(root, "subscriptions.json");
    }

    private void Refresh()
    {
        var info = new FileInfo(_path);
        (DateTime, long)? stamp = info.Exists ? (info.LastWriteTimeUtc, info.Length) : null;
        if (_loaded == stamp) return;
        try
        {
            _data = info.Exists ? JsonSerializer.Deserialize<Data>(File.ReadAllText(_path), Json) ?? new() : new();
            _loaded = stamp;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            // Half written, or being replaced: keep what was known and look again next time.
        }
    }

    private void Save()
    {
        var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_data, Json));
        File.Move(temp, _path, overwrite: true);
        var info = new FileInfo(_path);
        _loaded = (info.LastWriteTimeUtc, info.Length);
    }

    public void Upsert(Subscription subscription)
    {
        lock (_gate)
        {
            Refresh();
            _data.Subscriptions.RemoveAll(s => s.Provider == subscription.Provider && s.ProviderSubscriptionId == subscription.ProviderSubscriptionId);
            _data.Subscriptions.Add(subscription);
            Save();
        }
    }

    public IReadOnlyList<Subscription> Of(string account)
    {
        lock (_gate)
        {
            Refresh();
            return _data.Subscriptions.Where(s => s.Account == account).OrderByDescending(s => s.PeriodEnd).ToList();
        }
    }

    public string BillingIdOf(string account)
    {
        lock (_gate)
        {
            Refresh();
            if (_data.BillingIds.TryGetValue(account, out var id)) return id;
            id = BillingIds.Make();
            _data.BillingIds[account] = id;
            Save();
            return id;
        }
    }

    public string? AccountOf(string? billingId)
    {
        if (string.IsNullOrEmpty(billingId)) return null;
        lock (_gate)
        {
            Refresh();
            return _data.BillingIds.FirstOrDefault(p => p.Value == billingId) is { Key: not null } found ? found.Key : null;
        }
    }

    public bool TryRecordEvent(string provider, string eventId, DateTimeOffset at)
    {
        lock (_gate)
        {
            Refresh();
            if (_data.Events.Any(e => e.Provider == provider && e.EventId == eventId)) return false;
            _data.Events.Add(new EventEntry { Provider = provider, EventId = eventId, At = at });
            Save();
            return true;
        }
    }

    public void ForgetEvent(string provider, string eventId)
    {
        lock (_gate)
        {
            Refresh();
            if (_data.Events.RemoveAll(e => e.Provider == provider && e.EventId == eventId) > 0) Save();
        }
    }

    public int PurgeEvents(DateTimeOffset before)
    {
        lock (_gate)
        {
            Refresh();
            var removed = _data.Events.RemoveAll(e => e.At < before);
            if (removed > 0) Save();
            return removed;
        }
    }

    public void RemoveAccount(string account)
    {
        lock (_gate)
        {
            Refresh();
            var changed = _data.Subscriptions.RemoveAll(s => s.Account == account) > 0;
            changed |= _data.BillingIds.Remove(account);
            if (changed) Save();
        }
    }
}
