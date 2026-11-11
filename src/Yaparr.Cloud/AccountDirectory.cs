using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Yaparr.Cloud;

public sealed record AccountEntry(string Name, DateTimeOffset Created, int Devices, bool HasPassword);

/// <summary>
/// Who may use the service (<c>accounts.json</c> in the data folder): each account, named by its owner's email address, with the hash of its password and the devices that are signed
/// in as it. Accounts are made by the person running the service (<see cref="AdminCommands"/>); nobody can make one by asking. A device that has
/// signed in holds a token, a long random secret; only its SHA-256 is kept here, so the file is of no use for signing in as someone.
/// The file is read again whenever it changes, so the admin commands need no restart of the service.
/// </summary>
public sealed partial class AccountDirectory
{
    private sealed class Device
    {
        public string Hash { get; set; } = "";
        public string Name { get; set; } = "";
        public DateTimeOffset Created { get; set; }
    }

    private sealed class Entry
    {
        public string? PasswordHash { get; set; }
        public DateTimeOffset Created { get; set; }
        public List<Device> Devices { get; set; } = new();
    }

    private sealed class FileModel
    {
        public Dictionary<string, Entry> Users { get; set; } = new();
    }

    private const int MaxDevicesPerAccount = 20;
    private const string TokenPrefix = "ycl_";

    private readonly string _path;
    private readonly object _gate = new();
    private Dictionary<string, Entry> _users = new();
    private Dictionary<string, string> _byTokenHash = new();
    private (DateTime At, long Length)? _loaded;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public AccountDirectory(ServiceOptions options)
    {
        var root = Path.GetFullPath(options.DataDirectory);
        Directory.CreateDirectory(root);
        _path = Path.Combine(root, "accounts.json");
    }

    // The part before the @ (letters, digits and . _ % + - , starting and ending with a letter or digit, no two dots in a row), and a domain of
    // dotted names ending in two or more letters. Plain ASCII: what nearly every address is, and nothing that could be taken for a path or markup.
    [GeneratedRegex(@"^[a-z0-9](?:[a-z0-9._%+-]{0,62}[a-z0-9])?@(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,24}$")]
    private static partial Regex ValidEmail();

    /// <summary>
    /// The form an account's name is kept in: the person's email address, trimmed and in lower case, so " Sam@Example.com " and "sam@example.com" are
    /// one person. Null if it is not a usable address. Nothing is ever sent to it: it only names the account, and the person running the service
    /// makes the account by hand.
    /// </summary>
    public static string? Normalize(string? name)
    {
        var normal = (name ?? "").Normalize(NormalizationForm.FormKC).Trim().ToLowerInvariant();
        return normal.Length <= 254 && !normal.Contains("..", StringComparison.Ordinal) && ValidEmail().IsMatch(normal) ? normal : null;
    }

    private void Refresh()
    {
        var info = new FileInfo(_path);
        (DateTime, long)? now = info.Exists ? (info.LastWriteTimeUtc, info.Length) : null;
        if (_loaded == now) return;
        try
        {
            _users = info.Exists ? JsonSerializer.Deserialize<FileModel>(File.ReadAllText(_path), Json)?.Users ?? new() : new();
            Index();
            _loaded = now;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            // Half written, or being replaced: keep what was known and look again next time.
        }
    }

    private void Index() =>
        _byTokenHash = _users.SelectMany(u => u.Value.Devices.Select(d => (d.Hash, User: u.Key))).GroupBy(p => p.Hash).ToDictionary(g => g.Key, g => g.First().User);

    // Written beside the file and moved over it, so the service never reads half a list.
    private void Save()
    {
        var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(new FileModel { Users = _users }, Json));
        File.Move(temp, _path, overwrite: true);
        Index();
        var info = new FileInfo(_path);
        _loaded = (info.LastWriteTimeUtc, info.Length);
    }

    public bool Exists(string name)
    {
        lock (_gate) { Refresh(); return _users.ContainsKey(name); }
    }

    public IReadOnlyList<AccountEntry> List()
    {
        lock (_gate)
        {
            Refresh();
            return _users.OrderBy(u => u.Key, StringComparer.Ordinal)
                .Select(u => new AccountEntry(u.Key, u.Value.Created, u.Value.Devices.Count, u.Value.PasswordHash != null)).ToList();
        }
    }

    /// <summary>Adds an account with a password. False if the name is taken.</summary>
    public bool Add(string name, string password)
    {
        var hash = Passwords.Hash(password);
        lock (_gate)
        {
            Refresh();
            if (_users.ContainsKey(name)) return false;
            _users[name] = new Entry { PasswordHash = hash, Created = DateTimeOffset.UtcNow };
            Save();
            return true;
        }
    }

    /// <summary>
    /// Gives an account a new password and signs every device out, so whoever knew the old one (or held a device) is out. False if there is
    /// no such account. The backups are untouched: the password does not lock them.
    /// </summary>
    public bool SetPassword(string name, string password)
    {
        var hash = Passwords.Hash(password);
        lock (_gate)
        {
            Refresh();
            if (!_users.TryGetValue(name, out var entry)) return false;
            entry.PasswordHash = hash;
            entry.Devices.Clear();
            Save();
            return true;
        }
    }

    /// <summary>True if this is the account's password. Takes as long for a name that does not exist, so it cannot be used to find accounts.</summary>
    public bool CheckPassword(string name, string password)
    {
        string? stored;
        lock (_gate)
        {
            Refresh();
            stored = _users.TryGetValue(name, out var entry) ? entry.PasswordHash : null;
        }
        return Passwords.Verify(password, stored);   // outside the lock: it is slow on purpose
    }

    /// <summary>Removes an account and signs its devices out. False if there was none.</summary>
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

    private static string HashOf(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>
    /// Signs a device in: makes its token, which is shown to that device once and kept here only as a hash. The oldest device is signed out when
    /// an account has a great many. Null if there is no such account.
    /// </summary>
    public string? IssueToken(string name, string deviceName)
    {
        var token = TokenPrefix + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        lock (_gate)
        {
            Refresh();
            if (!_users.TryGetValue(name, out var entry)) return null;
            entry.Devices.Add(new Device { Hash = HashOf(token), Name = deviceName.Length > 60 ? deviceName[..60] : deviceName, Created = DateTimeOffset.UtcNow });
            while (entry.Devices.Count > MaxDevicesPerAccount) entry.Devices.RemoveAt(0);
            Save();
        }
        return token;
    }

    /// <summary>The account a token signs in as, or null if it is not (or no longer) a token of anyone.</summary>
    public string? UserOfToken(string? token)
    {
        if (token == null || !token.StartsWith(TokenPrefix, StringComparison.Ordinal) || token.Length > 100) return null;
        var hash = HashOf(token);
        lock (_gate)
        {
            Refresh();
            return _byTokenHash.GetValueOrDefault(hash);
        }
    }

    /// <summary>Signs one device out. False if the token was not one.</summary>
    public bool RevokeToken(string token)
    {
        var hash = HashOf(token);
        lock (_gate)
        {
            Refresh();
            if (!_byTokenHash.TryGetValue(hash, out var name) || !_users.TryGetValue(name, out var entry)) return false;
            entry.Devices.RemoveAll(d => d.Hash == hash);
            Save();
            return true;
        }
    }
}
