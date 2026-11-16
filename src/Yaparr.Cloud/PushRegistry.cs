using System.Text.Json;

namespace Yaparr.Cloud;

/// <summary><see cref="IPushRegistry"/> in a file (<c>push.json</c> in the data folder), for a service that has no database.</summary>
public sealed class PushRegistry : IPushRegistry
{
    private sealed class Entry
    {
        public string Account { get; set; } = "";
        public string DeviceId { get; set; } = "";
        public PushKind Kind { get; set; }
        public string Endpoint { get; set; } = "";
        public string TokenHash { get; set; } = "";
        public DateTimeOffset Created { get; set; }
    }

    private readonly string _path;
    private readonly object _gate = new();
    private readonly Func<DateTimeOffset> _now;
    private List<Entry> _entries = new();
    private (DateTime At, long Length)? _loaded;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public PushRegistry(ServiceOptions options) : this(options, () => DateTimeOffset.UtcNow) { }

    internal PushRegistry(ServiceOptions options, Func<DateTimeOffset> now)
    {
        var root = Path.GetFullPath(options.DataDirectory);
        Directory.CreateDirectory(root);
        _path = Path.Combine(root, "push.json");
        _now = now;
    }

    private void Refresh()
    {
        var info = new FileInfo(_path);
        (DateTime, long)? stamp = info.Exists ? (info.LastWriteTimeUtc, info.Length) : null;
        if (_loaded == stamp) return;
        try
        {
            _entries = info.Exists ? JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(_path), Json) ?? new() : new();
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
        File.WriteAllText(temp, JsonSerializer.Serialize(_entries, Json));
        File.Move(temp, _path, overwrite: true);
        var info = new FileInfo(_path);
        _loaded = (info.LastWriteTimeUtc, info.Length);
    }

    public PushRegisterResult Register(string account, string deviceId, PushKind kind, string endpoint, int maxPerAccount)
    {
        lock (_gate)
        {
            Refresh();
            var existing = _entries.FirstOrDefault(e => e.Account == account && e.DeviceId == deviceId);
            if (existing != null)
            {
                existing.Kind = kind;
                existing.Endpoint = endpoint;
                Save();
                return new PushRegisterResult(PushRegisterOutcome.Updated, null);
            }
            if (_entries.Count(e => e.Account == account) >= Math.Max(1, maxPerAccount)) return new PushRegisterResult(PushRegisterOutcome.TooMany, null);
            var token = WakeTokens.Make();
            _entries.Add(new Entry { Account = account, DeviceId = deviceId, Kind = kind, Endpoint = endpoint, TokenHash = WakeTokens.HashOf(token), Created = _now() });
            Save();
            return new PushRegisterResult(PushRegisterOutcome.Created, token);
        }
    }

    public string? Rotate(string account, string deviceId)
    {
        lock (_gate)
        {
            Refresh();
            var existing = _entries.FirstOrDefault(e => e.Account == account && e.DeviceId == deviceId);
            if (existing == null) return null;
            var token = WakeTokens.Make();
            existing.TokenHash = WakeTokens.HashOf(token);
            Save();
            return token;
        }
    }

    public bool Remove(string account, string deviceId)
    {
        lock (_gate)
        {
            Refresh();
            if (_entries.RemoveAll(e => e.Account == account && e.DeviceId == deviceId) == 0) return false;
            Save();
            return true;
        }
    }

    public IReadOnlyList<PushRegistration> List(string account)
    {
        lock (_gate)
        {
            Refresh();
            return _entries.Where(e => e.Account == account).OrderBy(e => e.Created).Select(e => new PushRegistration(e.DeviceId, e.Kind, e.Created)).ToList();
        }
    }

    public PushTarget? Find(string? token)
    {
        if (!WakeTokens.Looks(token)) return null;
        var hash = WakeTokens.HashOf(token!);
        lock (_gate)
        {
            Refresh();
            return _entries.FirstOrDefault(e => e.TokenHash == hash) is { } e ? new PushTarget(e.Account, e.DeviceId, e.Kind, e.Endpoint) : null;
        }
    }

    public bool RemoveByToken(string token)
    {
        if (!WakeTokens.Looks(token)) return false;
        var hash = WakeTokens.HashOf(token);
        lock (_gate)
        {
            Refresh();
            if (_entries.RemoveAll(e => e.TokenHash == hash) == 0) return false;
            Save();
            return true;
        }
    }

    public void RemoveAccount(string account)
    {
        lock (_gate)
        {
            Refresh();
            if (_entries.RemoveAll(e => e.Account == account) > 0) Save();
        }
    }
}
