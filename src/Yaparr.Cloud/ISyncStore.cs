namespace Yaparr.Cloud;

/// <summary>
/// One record of an account's synced state, as the server holds it. The <see cref="Body"/> is locked by the devices (this service cannot read it, and does not try);
/// what the server sees is the kind and id, the <see cref="Clock"/> that orders writes, which device wrote it, and whether it was deleted. <see cref="Cursor"/> is where
/// the record stands in the account's order of changes: the service gives it when a record is stored, and a device ignores it when sending.
/// </summary>
public sealed record SyncRecord(string Kind, string Id, string Clock, string Device, bool Deleted, string Body, long Cursor = 0);

/// <summary>The records after a cursor, oldest change first. <see cref="Cursor"/> is where the next pull starts; <see cref="More"/> is true when there are more than were returned.</summary>
public sealed record SyncPage(IReadOnlyList<SyncRecord> Records, long Cursor, bool More);

public enum SyncPutOutcome { Stored, TooMany }

/// <summary>
/// The result of sending records. <see cref="Stale"/> are the kind and id (<c>kind/id</c>) of those that were not taken because the service holds a newer write of the
/// same record; the device pulls and finds it. A record whose clock equals the one held is the same write sent again, and is quietly ignored. <see cref="Cursor"/> is the
/// account's latest cursor after the call.
/// </summary>
public sealed record SyncPutResult(SyncPutOutcome Outcome, long Cursor, IReadOnlyList<string> Stale);

/// <summary>
/// The synced state of accounts: records kept by <c>(kind, id)</c>, the newest write of each (by clock, compared as plain text, so clocks are made to sort that way),
/// and a deleted record is kept as a marker so a device that was away still learns of it. Every stored change gets the account's next cursor, so a device asks for
/// "what changed after N". The service never merges: which write is newest is all it decides, and a clock that is not newer than the one held is not taken. Two
/// implementations, <see cref="SyncStore"/> in files and <c>PostgresSyncStore</c> in the database, held to the same behaviour by the same tests.
/// </summary>
public interface ISyncStore
{
    /// <summary>Up to <paramref name="limit"/> records changed after <paramref name="since"/>, oldest change first.</summary>
    SyncPage Pull(string account, long since, int limit);

    /// <summary>
    /// Stores the records that are newer than what is held, in the order given, each with the next cursor. The call takes all or none of the new records: if they would
    /// take the account past <paramref name="maxRecords"/> (records and markers both count), nothing is stored.
    /// </summary>
    SyncPutResult Put(string account, IReadOnlyList<SyncRecord> records, int maxRecords);

    /// <summary>Forgets everything of an account.</summary>
    void RemoveAccount(string account);
}

internal static class SyncRules
{
    public static bool GoodKind(string? kind) => kind is { Length: > 0 and <= 32 } && char.IsAsciiLetterLower(kind[0]) && kind.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-');

    public static bool GoodId(string? id) => id is { Length: > 0 and <= 128 } && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or ':' or '@' or '+' or '=' or '/' or '|');

    /// <summary>A clock is plain characters that sort as the order of the writes (the devices make them so); the service only compares them.</summary>
    public static bool GoodClock(string? clock) => clock is { Length: > 0 and <= 96 } && clock.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');

    /// <summary>The body is base64 (what the devices locked), within the limit; a deleted record has none.</summary>
    public static bool GoodBody(string? body, bool deleted, int maxChars) =>
        body != null && body.Length <= maxChars && (deleted ? body.Length == 0 : body.Length > 0 && System.Buffers.Text.Base64.IsValid(body));

    public static string KeyOf(string kind, string id) => kind + "/" + id;
}
