using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Yaparr.Cloud;

public sealed record AccountEntry(string Name, DateTimeOffset Created);

/// <summary>
/// Who may use the service: a list of usernames, each with the public key that signs for it (<c>accounts.json</c> in the data folder). Accounts are made
/// by the person running the service (<see cref="AdminCommands"/>); nobody can make one by asking, and a request for a name that is not here is
/// refused. The file is read again whenever it changes, so adding or removing a person needs no restart, and it never holds a password, only a
/// public key.
/// </summary>
public sealed partial class AccountDirectory
{
    private sealed record Entry(string PublicKey, DateTimeOffset Created);
    private sealed record FileModel(Dictionary<string, Entry> Users);

    private readonly string _path;
    private readonly object _gate = new();
    private Dictionary<string, Entry> _users = new();
    private (DateTime At, long Length)? _loaded;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public AccountDirectory(ServiceOptions options)
    {
        var root = Path.GetFullPath(options.DataDirectory);
        Directory.CreateDirectory(root);
        _path = Path.Combine(root, "accounts.json");
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{1,30}[a-z0-9]$")]
    private static partial Regex ValidName();

    /// <summary>
    /// The form a username is kept and signed in: trimmed and lower case, so "Sam " and "sam" are one person. Null if it is not a usable name
    /// (3 to 32 letters, digits, dots, dashes and underscores). It is also what the key is made from, so it cannot be changed afterwards.
    /// </summary>
    public static string? Normalize(string? name)
    {
        var normal = (name ?? "").Normalize(NormalizationForm.FormKC).Trim().ToLowerInvariant();
        return ValidName().IsMatch(normal) ? normal : null;
    }

    private void Refresh()
    {
        var info = new FileInfo(_path);
        (DateTime, long)? now = info.Exists ? (info.LastWriteTimeUtc, info.Length) : null;
        if (_loaded == now) return;
        try
        {
            _users = info.Exists
                ? JsonSerializer.Deserialize<FileModel>(File.ReadAllText(_path), Json)?.Users ?? new()
                : new();
            _loaded = now;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            // Half written, or being replaced: keep what was known and look again next time.
        }
    }

    /// <summary>The public key that signs for this username, or null if there is no such account.</summary>
    public byte[]? PublicKeyOf(string name)
    {
        lock (_gate)
        {
            Refresh();
            return _users.TryGetValue(name, out var entry) ? Convert.FromBase64String(entry.PublicKey) : null;
        }
    }

    public IReadOnlyList<AccountEntry> List()
    {
        lock (_gate)
        {
            Refresh();
            return _users.OrderBy(u => u.Key, StringComparer.Ordinal).Select(u => new AccountEntry(u.Key, u.Value.Created)).ToList();
        }
    }

    /// <summary>Adds an account. False if the name is taken.</summary>
    public bool Add(string name, byte[] publicKey)
    {
        lock (_gate)
        {
            Refresh();
            if (_users.ContainsKey(name)) return false;
            _users[name] = new Entry(Convert.ToBase64String(publicKey), DateTimeOffset.UtcNow);
            Save();
            return true;
        }
    }

    /// <summary>Gives an account a new key (a changed password). False if there is no such account.</summary>
    public bool SetKey(string name, byte[] publicKey)
    {
        lock (_gate)
        {
            Refresh();
            if (!_users.TryGetValue(name, out var entry)) return false;
            _users[name] = entry with { PublicKey = Convert.ToBase64String(publicKey) };
            Save();
            return true;
        }
    }

    /// <summary>Removes an account. False if there was none.</summary>
    public bool Remove(string name)
    {
        lock (_gate)
        {
            Refresh();
            if (!_users.Remove(name)) return false;
            Save();
            return true;
        }
    }

    // Written beside the file and moved over it, so the service never reads half a list.
    private void Save()
    {
        var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(new FileModel(_users), Json));
        File.Move(temp, _path, overwrite: true);
        var info = new FileInfo(_path);
        _loaded = (info.LastWriteTimeUtc, info.Length);
    }
}
