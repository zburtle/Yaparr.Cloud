using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Yaparr.Cloud;

/// <summary>An account as the admin sees it. <paramref name="Pass"/> is what kind of pass it has ("lifetime" for everyone so far).</summary>
public sealed record AccountEntry(string Name, DateTimeOffset Created, int Devices, bool HasPassword, string Pass, string? SignupCode, bool IsAdmin = false);

/// <summary>
/// Who may use the service (<c>accounts.json</c> in the data folder): each account, named by its owner's email address, with the hash of its password and the devices that are signed
/// in as it. Accounts are made by the person running the service (<see cref="AdminCommands"/>); nobody can make one by asking. A device that has
/// signed in holds a token, a long random secret; only its SHA-256 is kept here, so the file is of no use for signing in as someone.
/// The file is read again whenever it changes, so the admin commands need no restart of the service.
/// </summary>
public sealed partial class AccountDirectory : IAccounts
{
    private sealed class Device
    {
        public string Hash { get; set; } = "";
        public string Name { get; set; } = "";
        public DateTimeOffset Created { get; set; }
        /// <summary>The hash of the id the app gave for itself (see <see cref="IAccounts.IssueToken"/>), or empty for a device that did not.</summary>
        public string Key { get; set; } = "";
        /// <summary>The hash of the id derived from the machine (see <see cref="IAccounts.IssueToken"/>), or empty.</summary>
        public string Machine { get; set; } = "";
    }

    /// <summary>The pass every account has: it does not run out. (Nothing here ends an account on a date; the field says what the person was promised.)</summary>
    public const string LifetimePass = "lifetime";

    private sealed class Entry
    {
        public string? PasswordHash { get; set; }
        public DateTimeOffset Created { get; set; }
        public List<Device> Devices { get; set; } = new();
        /// <summary>What kind of pass this account has. Accounts from before passes were recorded are lifetime ones.</summary>
        public string Pass { get; set; } = LifetimePass;
        /// <summary>The id of the signup code the person used, if they signed up with one.</summary>
        public string? SignupCode { get; set; }
        /// <summary>For an account made by signing up that has not chosen a password yet: the hash of the one-use token that lets them, and when it ends.</summary>
        public string? SetupHash { get; set; }
        public DateTimeOffset? SetupExpires { get; set; }
        /// <summary>Whether this account may sign in to the admin portal (set only from the command line), and the secret of its authenticator app.</summary>
        public bool Admin { get; set; }
        public string? TotpSecret { get; set; }
    }

    private sealed class FileModel
    {
        public Dictionary<string, Entry> Users { get; set; } = new();
    }

    internal const int MaxDevicesPerAccount = 20;
    internal const string TokenPrefix = "ycl_";

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

    /// <summary>True if the account exists and has chosen a password, so it is for signing in and not for signing up.</summary>
    public bool HasPassword(string name)
    {
        lock (_gate) { Refresh(); return _users.TryGetValue(name, out var entry) && entry.PasswordHash != null; }
    }

    /// <summary>Everything about every account, as it is kept (for moving the accounts to a database).</summary>
    internal IReadOnlyList<ExportedAccount> Export()
    {
        lock (_gate)
        {
            Refresh();
            return _users.Select(u => new ExportedAccount(u.Key, u.Value.PasswordHash, u.Value.Created, u.Value.Pass, u.Value.SignupCode, u.Value.SetupHash, u.Value.SetupExpires,
                u.Value.Admin, u.Value.TotpSecret, u.Value.Devices.Select(d => new ExportedDevice(d.Hash, d.Name, d.Created)).ToList())).ToList();
        }
    }

    public Task<StoreStatus> StatusAsync(CancellationToken cancel = default)
    {
        try
        {
            var count = List().Count;
            return Task.FromResult(new StoreStatus("Files", "accounts.json and codes.json in the data folder", true, Plural(count)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(new StoreStatus("Files", "accounts.json and codes.json in the data folder", false, "cannot read: " + ex.Message));
        }
        static string Plural(int n) => n == 1 ? "1 account" : n + " accounts";
    }

    public IReadOnlyList<AccountEntry> List()
    {
        lock (_gate)
        {
            Refresh();
            return _users.OrderBy(u => u.Key, StringComparer.Ordinal)
                .Select(u => new AccountEntry(u.Key, u.Value.Created, u.Value.Devices.Count, u.Value.PasswordHash != null, u.Value.Pass, u.Value.SignupCode, u.Value.Admin)).ToList();
        }
    }

    /// <summary>
    /// Lets an account sign in to the admin portal, and returns the secret for its authenticator app (a new one each time, so running this again
    /// replaces a lost phone). Null if there is no such account or it has no password yet.
    /// </summary>
    public string? MakeAdmin(string name)
    {
        lock (_gate)
        {
            Refresh();
            if (!_users.TryGetValue(name, out var entry) || entry.PasswordHash == null) return null;
            entry.Admin = true;
            entry.TotpSecret = Totp.NewSecret();
            Save();
            return entry.TotpSecret;
        }
    }

    /// <summary>Takes the portal away from an account. False if there is no such account or it was not an admin.</summary>
    public bool RemoveAdmin(string name)
    {
        lock (_gate)
        {
            Refresh();
            if (!_users.TryGetValue(name, out var entry) || !entry.Admin) return false;
            entry.Admin = false;
            entry.TotpSecret = null;
            Save();
            return true;
        }
    }

    public bool IsAdmin(string name)
    {
        lock (_gate) { Refresh(); return _users.TryGetValue(name, out var entry) && entry.Admin; }
    }

    /// <summary>Whether anyone may use the admin portal. Until someone does, the portal does not exist (every address under /admin answers 404).</summary>
    public bool AnyAdmin()
    {
        lock (_gate) { Refresh(); return _users.Values.Any(u => u.Admin); }
    }

    /// <summary>The authenticator secret of an admin, or null for anyone else.</summary>
    public string? TotpSecretOf(string name)
    {
        lock (_gate) { Refresh(); return _users.TryGetValue(name, out var entry) && entry.Admin ? entry.TotpSecret : null; }
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

    internal static readonly TimeSpan SetupLifetime = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Begins an account made by signing up: it exists, with a lifetime pass, but cannot sign in until the person has chosen a password with the one-use
    /// token returned here (kept only as a hash, good for 30 minutes). Someone who comes back for the same address before choosing a password is given
    /// a new token, and the old one stops working. Null if the address already has an account with a password, which is for signing in.
    /// </summary>
    public string? StartSignup(string name, string? signupCodeId)
    {
        var token = "set_" + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        lock (_gate)
        {
            Refresh();
            if (!_users.TryGetValue(name, out var entry))
                _users[name] = entry = new Entry { Created = DateTimeOffset.UtcNow, Pass = LifetimePass, SignupCode = signupCodeId };
            else if (entry.PasswordHash != null) return null;
            entry.SetupHash = HashOf(token);
            entry.SetupExpires = DateTimeOffset.UtcNow + SetupLifetime;
            Save();
        }
        return token;
    }

    /// <summary>The account a setup token is for, or null if it is not one, has been used, or has run out. Does not use it.</summary>
    public string? SetupFor(string? token)
    {
        if (token == null || !token.StartsWith("set_", StringComparison.Ordinal) || token.Length > 100) return null;
        var hash = HashOf(token);
        lock (_gate)
        {
            Refresh();
            return _users.FirstOrDefault(u => u.Value.SetupHash == hash && u.Value.SetupExpires > DateTimeOffset.UtcNow).Key;
        }
    }

    /// <summary>Gives the account a setup token is for its password, once. Returns the account, or null if the token is not good.</summary>
    public string? FinishSetup(string? token, string password)
    {
        if (SetupFor(token) is null) return null;
        var hash = Passwords.Hash(password);   // slow on purpose, so outside the lock
        lock (_gate)
        {
            Refresh();
            var tokenHash = HashOf(token!);
            var found = _users.FirstOrDefault(u => u.Value.SetupHash == tokenHash && u.Value.SetupExpires > DateTimeOffset.UtcNow);
            if (found.Value == null) return null;
            found.Value.PasswordHash = hash;
            found.Value.Devices.Clear();
            found.Value.SetupHash = null;
            found.Value.SetupExpires = null;
            Save();
            return found.Key;
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

    /// <summary>The hash kept for a device's own id, or empty when there is none or it is not a good one (16 to 64 letters, digits, - and _).</summary>
    internal static string KeyHashOf(string? deviceKey, string purpose = "device-key:") =>
        deviceKey is { Length: >= 16 and <= 64 } && deviceKey.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_') ? HashOf(purpose + deviceKey) : "";

    /// <summary>The public name of a device: the start of its token's hash.</summary>
    internal static string DeviceIdOf(string hash) => hash[..16];

    internal static string HashOf(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>
    /// Signs a device in: makes its token, which is shown to that device once and kept here only as a hash. The oldest device is signed out when
    /// an account has a great many. Null if there is no such account.
    /// </summary>
    public string? IssueToken(string name, string deviceName, string? deviceKey = null, string? machineKey = null)
    {
        var key = KeyHashOf(deviceKey);
        var machine = KeyHashOf(machineKey, "machine-key:");
        var shown = deviceName.Length > 60 ? deviceName[..60] : deviceName;
        var token = TokenPrefix + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        lock (_gate)
        {
            Refresh();
            if (!_users.TryGetValue(name, out var entry)) return null;
            // The same device signing in again takes the place of its earlier sign-in: by the id it keeps for itself, or (it lost that) by the machine it is, with the same name.
            if (key.Length > 0 || machine.Length > 0)
                entry.Devices.RemoveAll(d => (key.Length > 0 && d.Key == key) || (machine.Length > 0 && d.Machine == machine && d.Name == shown));
            entry.Devices.Add(new Device { Hash = HashOf(token), Name = shown, Created = DateTimeOffset.UtcNow, Key = key, Machine = machine });
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

    public IReadOnlyList<DeviceInfo> DevicesOf(string name)
    {
        lock (_gate)
        {
            Refresh();
            return _users.TryGetValue(name, out var entry)
                ? entry.Devices.OrderByDescending(d => d.Created).Select(d => new DeviceInfo(DeviceIdOf(d.Hash), d.Name, d.Created)).ToList()
                : new List<DeviceInfo>();
        }
    }

    public bool RevokeDevice(string name, string deviceId)
    {
        if (deviceId.Length != 16) return false;
        lock (_gate)
        {
            Refresh();
            if (!_users.TryGetValue(name, out var entry) || entry.Devices.RemoveAll(d => DeviceIdOf(d.Hash) == deviceId) == 0) return false;
            Save();
            return true;
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
