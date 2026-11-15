using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Yaparr.Cloud;

/// <summary>One person who signed up with a code.</summary>
public sealed record CodeUse(string Email, DateTimeOffset At);

/// <summary>A signup code as the admin sees it. The code itself is shown once, when it is made, and is not kept: only its hash is.</summary>
public sealed record SignupCodeInfo(string Id, string Note, DateTimeOffset Created, int? MaxUses, DateTimeOffset? Expires, bool Revoked, IReadOnlyList<CodeUse> Uses)
{
    /// <summary>Why the code does not work now, or null if it does.</summary>
    public string? Problem(DateTimeOffset now) =>
        Revoked ? "revoked"
        : Expires is { } end && end <= now ? "expired"
        : MaxUses is { } max && Uses.Count >= max ? "used up"
        : null;
}

/// <summary>
/// The codes that let a friend make an account (<c>codes.json</c> in the data folder). The admin makes a code (<see cref="AdminCommands"/>) and sends
/// a link with it in; anyone who has the link can sign up with it, as many people as the code allows (any number unless the admin set a limit),
/// until it is revoked or runs out. A code only ever comes to the server in someone's sign-up, never to the website that hosts the page. Like the
/// accounts, the file is read again whenever it changes, so the admin commands need no restart.
/// </summary>
public sealed class SignupCodes
{
    private sealed class UseRecord
    {
        public string Email { get; set; } = "";
        public DateTimeOffset At { get; set; }
    }

    private sealed class Entry
    {
        public string Note { get; set; } = "";
        public DateTimeOffset Created { get; set; }
        public int? MaxUses { get; set; }
        public DateTimeOffset? Expires { get; set; }
        public bool Revoked { get; set; }
        public List<UseRecord> Uses { get; set; } = new();
    }

    private sealed class FileModel
    {
        // By the first eight hex digits of the code's hash: enough to name it in a list, and to revoke it, without being the code.
        public Dictionary<string, Entry> Codes { get; set; } = new();
        public Dictionary<string, string> Hashes { get; set; } = new();   // full hash -> id
    }

    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    private readonly string _path;
    private readonly object _gate = new();
    private readonly Func<DateTimeOffset> _now;
    private FileModel _model = new();
    private (DateTime At, long Length)? _loaded;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public SignupCodes(ServiceOptions options) : this(options, () => DateTimeOffset.UtcNow) { }

    internal SignupCodes(ServiceOptions options, Func<DateTimeOffset> now)
    {
        var root = Path.GetFullPath(options.DataDirectory);
        Directory.CreateDirectory(root);
        _path = Path.Combine(root, "codes.json");
        _now = now;
    }

    /// <summary>The form a code is checked in, however it was typed: upper case, without dashes or spaces. Null if it cannot be a code.</summary>
    public static string? Normalize(string? typed)
    {
        var letters = new string((typed ?? "").Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
        return letters.Length == 16 && letters.All(Alphabet.Contains) ? letters : null;
    }

    private static string Show(string letters) => string.Join('-', Enumerable.Range(0, 4).Select(g => letters.Substring(g * 4, 4)));
    private static string HashOf(string letters) => Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes("yaparr-signup:" + letters)));

    private void Refresh()
    {
        var info = new FileInfo(_path);
        (DateTime, long)? now = info.Exists ? (info.LastWriteTimeUtc, info.Length) : null;
        if (_loaded == now) return;
        try
        {
            _model = info.Exists ? JsonSerializer.Deserialize<FileModel>(File.ReadAllText(_path), Json) ?? new() : new();
            _loaded = now;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            // Half written, or being replaced: keep what was known and look again next time.
        }
    }

    private void Save()
    {
        var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_model, Json));
        File.Move(temp, _path, overwrite: true);
        var info = new FileInfo(_path);
        _loaded = (info.LastWriteTimeUtc, info.Length);
    }

    private static SignupCodeInfo Info(string id, Entry e) =>
        new(id, e.Note, e.Created, e.MaxUses, e.Expires, e.Revoked, e.Uses.Select(u => new CodeUse(u.Email, u.At)).ToList());

    /// <summary>Makes a code (16 letters and digits, about 80 bits) and returns it with how it will be listed. It is not kept anywhere but in what is returned.</summary>
    public (string Code, SignupCodeInfo Info) Create(string note, int? maxUses, TimeSpan? lifetime)
    {
        var letters = new string(Enumerable.Range(0, 16).Select(_ => Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)]).ToArray());
        var hash = HashOf(letters);
        var id = hash[..8];
        var entry = new Entry { Note = note.Length > 100 ? note[..100] : note, Created = _now(), MaxUses = maxUses, Expires = lifetime == null ? null : _now() + lifetime };
        lock (_gate)
        {
            Refresh();
            _model.Codes[id] = entry;
            _model.Hashes[hash] = id;
            Save();
        }
        return (Show(letters), Info(id, entry));
    }

    public IReadOnlyList<SignupCodeInfo> List()
    {
        lock (_gate)
        {
            Refresh();
            return _model.Codes.OrderBy(c => c.Value.Created).Select(c => Info(c.Key, c.Value)).ToList();
        }
    }

    /// <summary>Stops a code from working, by its id. People who already signed up keep their accounts. False if there is no such code.</summary>
    public bool Revoke(string id)
    {
        lock (_gate)
        {
            Refresh();
            if (!_model.Codes.TryGetValue(id.Trim().ToLowerInvariant(), out var entry)) return false;
            entry.Revoked = true;
            Save();
            return true;
        }
    }

    /// <summary>
    /// Takes a code out altogether, by its id: it stops working (as if revoked) and the record of it, including who signed up with it, is gone. People
    /// who already signed up keep their accounts. False if there is no such code.
    /// </summary>
    public bool Delete(string id)
    {
        lock (_gate)
        {
            Refresh();
            id = id.Trim().ToLowerInvariant();
            if (!_model.Codes.Remove(id)) return false;
            foreach (var hash in _model.Hashes.Where(h => h.Value == id).Select(h => h.Key).ToList()) _model.Hashes.Remove(hash);
            Save();
            return true;
        }
    }

    /// <summary>
    /// Whether this code works now, and which one it is (its id), without using it. Null for a code that does not exist, was revoked, has expired or
    /// is used up: the caller says the same thing for all of them, so a stranger learns nothing about codes.
    /// </summary>
    public string? Check(string? typed, string? email = null)
    {
        if (Normalize(typed) is not { } letters) return null;
        lock (_gate)
        {
            Refresh();
            if (!_model.Hashes.TryGetValue(HashOf(letters), out var id) || !_model.Codes.TryGetValue(id, out var entry)) return null;
            if (Info(id, entry).Problem(_now()) == null) return id;
            // A code that is used up still lets back in someone who already used it (coming back to finish), as long as it has not been taken back or ended.
            return email != null && !entry.Revoked && !(entry.Expires is { } end && end <= _now()) && entry.Uses.Any(u => u.Email == email) ? id : null;
        }
    }

    /// <summary>
    /// Counts a person against a code, if it still works, and returns its id; null if it does not. A person who has already used this code is not
    /// counted twice (the same email coming back to finish setting up an account).
    /// </summary>
    public string? Use(string? typed, string email)
    {
        if (Normalize(typed) is not { } letters) return null;
        lock (_gate)
        {
            Refresh();
            if (!_model.Hashes.TryGetValue(HashOf(letters), out var id) || !_model.Codes.TryGetValue(id, out var entry)) return null;
            var already = entry.Uses.Any(u => u.Email == email);
            if (!already && Info(id, entry).Problem(_now()) != null) return null;
            if (already && (entry.Revoked || (entry.Expires is { } end && end <= _now()))) return null;
            if (!already)
            {
                entry.Uses.Add(new UseRecord { Email = email, At = _now() });
                Save();
            }
            return id;
        }
    }
}
