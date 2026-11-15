using System.Security.Cryptography;
using Npgsql;

namespace Yaparr.Cloud;

/// <summary>
/// <see cref="ISignupCodes"/> in a database. Only the hash of a code is kept. Using a code takes a lock on its row, so two people using the last place of a
/// limited code cannot both get it.
/// </summary>
public sealed class PostgresSignupCodes : ISignupCodes
{
    private readonly NpgsqlDataSource _db;
    private readonly Func<DateTimeOffset> _now;

    public PostgresSignupCodes(NpgsqlDataSource db) : this(db, () => DateTimeOffset.UtcNow) { }

    internal PostgresSignupCodes(NpgsqlDataSource db, Func<DateTimeOffset> now)
    {
        _db = db;
        _now = now;
    }

    private sealed record Row(string Id, string Note, DateTimeOffset Created, int? MaxUses, DateTimeOffset? Expires, bool Revoked);

    private static Row ReadRow(NpgsqlDataReader r, int at = 0) =>
        new(r.GetString(at), r.GetString(at + 1), r.GetFieldValue<DateTimeOffset>(at + 2), r.IsDBNull(at + 3) ? null : r.GetInt32(at + 3),
            r.IsDBNull(at + 4) ? null : r.GetFieldValue<DateTimeOffset>(at + 4), r.GetBoolean(at + 5));

    private const string Columns = "id, note, created, max_uses, expires, revoked";

    private static SignupCodeInfo Info(Row row, IReadOnlyList<CodeUse> uses) => new(row.Id, row.Note, row.Created, row.MaxUses, row.Expires, row.Revoked, uses);

    private static List<CodeUse> UsesOf(NpgsqlConnection c, NpgsqlTransaction? t, string id)
    {
        using var command = new NpgsqlCommand("select email, at from signup_code_uses where code_id = @i order by at, email", c, t);
        command.Parameters.AddWithValue("i", id);
        using var reader = command.ExecuteReader();
        var uses = new List<CodeUse>();
        while (reader.Read()) uses.Add(new CodeUse(reader.GetString(0), reader.GetFieldValue<DateTimeOffset>(1)));
        return uses;
    }

    public (string Code, SignupCodeInfo Info) Create(string note, int? maxUses, TimeSpan? lifetime)
    {
        var letters = new string(Enumerable.Range(0, 16).Select(_ => SignupCodes.Alphabet[RandomNumberGenerator.GetInt32(SignupCodes.Alphabet.Length)]).ToArray());
        var hash = SignupCodes.HashOf(letters);
        var id = hash[..8];
        var row = new Row(id, note.Length > 100 ? note[..100] : note, _now(), maxUses, lifetime == null ? null : _now() + lifetime, false);
        using var c = _db.OpenConnection();
        using var command = new NpgsqlCommand($"insert into signup_codes ({Columns}, hash) values (@i, @n, @c, @m, @e, false, @h)", c);
        command.Parameters.AddWithValue("i", row.Id);
        command.Parameters.AddWithValue("n", row.Note);
        command.Parameters.AddWithValue("c", row.Created);
        command.Parameters.AddWithValue("m", (object?)row.MaxUses ?? DBNull.Value);
        command.Parameters.AddWithValue("e", (object?)row.Expires ?? DBNull.Value);
        command.Parameters.AddWithValue("h", hash);
        command.ExecuteNonQuery();
        return (SignupCodes.Show(letters), Info(row, Array.Empty<CodeUse>()));
    }

    public IReadOnlyList<SignupCodeInfo> List()
    {
        using var c = _db.OpenConnection();
        var rows = new List<Row>();
        using (var command = new NpgsqlCommand($"select {Columns} from signup_codes order by created, id", c))
        using (var reader = command.ExecuteReader())
            while (reader.Read()) rows.Add(ReadRow(reader));
        return rows.Select(row => Info(row, UsesOf(c, null, row.Id))).ToList();
    }

    public bool Revoke(string id)
    {
        using var c = _db.OpenConnection();
        using var command = new NpgsqlCommand("update signup_codes set revoked = true where id = @i", c);
        command.Parameters.AddWithValue("i", id.Trim().ToLowerInvariant());
        return command.ExecuteNonQuery() == 1;
    }

    public bool Delete(string id)
    {
        using var c = _db.OpenConnection();
        using var command = new NpgsqlCommand("delete from signup_codes where id = @i", c);
        command.Parameters.AddWithValue("i", id.Trim().ToLowerInvariant());
        return command.ExecuteNonQuery() == 1;
    }

    // The code with this hash, with its uses; locked for the rest of the transaction when there is one, so the count cannot change under a decision.
    private (Row Row, List<CodeUse> Uses)? Find(NpgsqlConnection c, NpgsqlTransaction? t, string hash, bool forUpdate)
    {
        Row? row;
        using (var command = new NpgsqlCommand($"select {Columns} from signup_codes where hash = @h{(forUpdate ? " for update" : "")}", c, t))
        {
            command.Parameters.AddWithValue("h", hash);
            using var reader = command.ExecuteReader();
            row = reader.Read() ? ReadRow(reader) : null;
        }
        return row == null ? null : (row, UsesOf(c, t, row.Id));
    }

    public string? Check(string? typed, string? email = null)
    {
        if (SignupCodes.Normalize(typed) is not { } letters) return null;
        using var c = _db.OpenConnection();
        if (Find(c, null, SignupCodes.HashOf(letters), forUpdate: false) is not { } found) return null;
        var info = Info(found.Row, found.Uses);
        if (info.Problem(_now()) == null) return found.Row.Id;
        // A code that is used up still lets back in someone who already used it (coming back to finish), as long as it has not been taken back or ended.
        return email != null && !found.Row.Revoked && !(found.Row.Expires is { } end && end <= _now()) && found.Uses.Any(u => u.Email == email) ? found.Row.Id : null;
    }

    public string? Use(string? typed, string email)
    {
        if (SignupCodes.Normalize(typed) is not { } letters) return null;
        using var c = _db.OpenConnection();
        using var transaction = c.BeginTransaction();
        if (Find(c, transaction, SignupCodes.HashOf(letters), forUpdate: true) is not { } found) return null;
        var already = found.Uses.Any(u => u.Email == email);
        var ended = found.Row.Revoked || (found.Row.Expires is { } end && end <= _now());
        if (!already && Info(found.Row, found.Uses).Problem(_now()) != null) return null;
        if (already && ended) return null;
        if (!already)
        {
            using var insert = new NpgsqlCommand("insert into signup_code_uses (code_id, email, at) values (@i, @e, @a)", c, transaction);
            insert.Parameters.AddWithValue("i", found.Row.Id);
            insert.Parameters.AddWithValue("e", email);
            insert.Parameters.AddWithValue("a", _now());
            insert.ExecuteNonQuery();
        }
        transaction.Commit();
        return found.Row.Id;
    }
}
