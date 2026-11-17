using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Yaparr.Cloud;

/// <summary><see cref="IMailboxStore"/> in files (a folder <c>mailbox</c> in the data folder, a file for each mailbox), for a service that has no database.</summary>
public sealed class MailboxStore : IMailboxStore
{
    private sealed class Held
    {
        public long Counter { get; set; }
        public List<Entry> Items { get; set; } = new();
    }

    private sealed class Entry
    {
        public string Id { get; set; } = "";
        public string Account { get; set; } = "";
        public string Body { get; set; } = "";
        public DateTimeOffset At { get; set; }
        public long Cursor { get; set; }
    }

    private readonly string _root;
    private readonly object _gate = new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public MailboxStore(ServiceOptions options)
    {
        _root = Path.Combine(Path.GetFullPath(options.DataDirectory), "mailbox");
        Directory.CreateDirectory(_root);
    }

    // The file is named by a hash of the mailbox id, so what a caller sends never becomes part of a path.
    private string PathOf(string mailbox) => Path.Combine(_root, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(mailbox))).ToLowerInvariant() + ".json");

    private static Held Read(string path)
    {
        if (!File.Exists(path)) return new Held();
        try { return JsonSerializer.Deserialize<Held>(File.ReadAllText(path), Json) ?? new Held(); }
        catch (Exception ex) when (ex is IOException or JsonException) { throw new IOException("A mailbox could not be read.", ex); }
    }

    private static void Write(string path, Held held)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(held, Json));
        File.Move(temp, path, overwrite: true);
    }

    public MailboxPage Pull(string mailbox, long since, int limit, DateTimeOffset now, TimeSpan keep)
    {
        limit = Math.Max(1, limit);
        lock (_gate)
        {
            var cutoff = now - keep;
            var newer = Read(PathOf(mailbox)).Items.Where(i => i.Cursor > since && i.At >= cutoff).OrderBy(i => i.Cursor).Take(limit + 1).ToList();
            bool more = newer.Count > limit;
            if (more) newer.RemoveAt(newer.Count - 1);
            return new MailboxPage(newer.Select(i => new MailboxItem(i.Id, i.Body, i.At, i.Cursor)).ToList(), newer.Count > 0 ? newer[^1].Cursor : since, more);
        }
    }

    public MailboxPostResult Post(string mailbox, string account, IReadOnlyList<MailboxItem> items, int maxItems, DateTimeOffset now, TimeSpan keep)
    {
        lock (_gate)
        {
            var path = PathOf(mailbox);
            var held = Read(path);
            var cutoff = now - keep;
            bool changed = held.Items.RemoveAll(i => i.At < cutoff) > 0;
            var have = held.Items.Select(i => i.Id).ToHashSet(StringComparer.Ordinal);
            var take = items.Where(i => have.Add(i.Id)).ToList();   // an id twice in one call counts once
            if (held.Items.Count + take.Count > Math.Max(1, maxItems))
            {
                if (changed) Write(path, held);
                return new MailboxPostResult(MailboxPostOutcome.Full, held.Counter, 0);
            }
            foreach (var item in take) held.Items.Add(new Entry { Id = item.Id, Account = account, Body = item.Body, At = now, Cursor = ++held.Counter });
            if (take.Count > 0 || changed) Write(path, held);
            return new MailboxPostResult(MailboxPostOutcome.Stored, held.Counter, take.Count);
        }
    }

    public int Expire(DateTimeOffset now, TimeSpan keep)
    {
        lock (_gate)
        {
            int removed = 0;
            var cutoff = now - keep;
            foreach (var path in Directory.EnumerateFiles(_root, "*.json"))
            {
                var held = Read(path);
                int gone = held.Items.RemoveAll(i => i.At < cutoff);
                if (gone == 0) continue;
                removed += gone;
                // The counter has to survive so a device's cursor never points at a different item later, but an empty mailbox with no history of use can go.
                Write(path, held);
            }
            return removed;
        }
    }

    public void RemoveAccount(string account)
    {
        lock (_gate)
        {
            foreach (var path in Directory.EnumerateFiles(_root, "*.json"))
            {
                var held = Read(path);
                if (held.Items.RemoveAll(i => i.Account == account) > 0) Write(path, held);
            }
        }
    }
}
