using System.Text.Json;

namespace Yaparr.Cloud;

/// <summary><see cref="ISyncStore"/> in files (a folder <c>sync</c> in the data folder, a file for each account), for a service that has no database.</summary>
public sealed class SyncStore : ISyncStore
{
    private sealed class Held
    {
        public long Counter { get; set; }
        public Dictionary<string, SyncRecord> Records { get; set; } = new();
    }

    private readonly string _root;
    private readonly object _gate = new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public SyncStore(ServiceOptions options)
    {
        _root = Path.Combine(Path.GetFullPath(options.DataDirectory), "sync");
        Directory.CreateDirectory(_root);
    }

    private string PathOf(string account) => Path.Combine(_root, BackupStore.FolderOf(account) + ".json");

    private Held Load(string account)
    {
        var path = PathOf(account);
        if (!File.Exists(path)) return new Held();
        try { return JsonSerializer.Deserialize<Held>(File.ReadAllText(path), Json) ?? new Held(); }
        catch (Exception ex) when (ex is IOException or JsonException) { throw new IOException("The synced state of this account could not be read.", ex); }
    }

    private void Save(string account, Held held)
    {
        var path = PathOf(account);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(held, Json));
        File.Move(temp, path, overwrite: true);
    }

    public SyncPage Pull(string account, long since, int limit)
    {
        limit = Math.Max(1, limit);
        lock (_gate)
        {
            var newer = Load(account).Records.Values.Where(r => r.Cursor > since).OrderBy(r => r.Cursor).Take(limit + 1).ToList();
            bool more = newer.Count > limit;
            if (more) newer.RemoveAt(newer.Count - 1);
            return new SyncPage(newer, newer.Count > 0 ? newer[^1].Cursor : since, more);
        }
    }

    public SyncPutResult Put(string account, IReadOnlyList<SyncRecord> records, int maxRecords)
    {
        lock (_gate)
        {
            var held = Load(account);
            var stale = new List<string>();
            var take = new List<SyncRecord>();
            var pending = new Dictionary<string, SyncRecord>();
            int added = 0;
            foreach (var record in records)
            {
                var key = SyncRules.KeyOf(record.Kind, record.Id);
                // Within one call a later copy of the same record is compared with the earlier one, as if they had come one after the other.
                var current = pending.TryGetValue(key, out var p) ? p : held.Records.GetValueOrDefault(key);
                int order = current == null ? 1 : string.CompareOrdinal(record.Clock, current.Clock);
                if (order < 0) { stale.Add(key); continue; }
                if (order == 0) continue;
                if (current == null) added++;
                pending[key] = record;
                take.Add(record);
            }
            if (held.Records.Count + added > Math.Max(1, maxRecords)) return new SyncPutResult(SyncPutOutcome.TooMany, held.Counter, stale);
            foreach (var record in take) held.Records[SyncRules.KeyOf(record.Kind, record.Id)] = record with { Cursor = ++held.Counter };
            if (take.Count > 0) Save(account, held);
            return new SyncPutResult(SyncPutOutcome.Stored, held.Counter, stale);
        }
    }

    public void RemoveAccount(string account)
    {
        lock (_gate)
        {
            try { File.Delete(PathOf(account)); }
            catch (IOException) { }
        }
    }
}
