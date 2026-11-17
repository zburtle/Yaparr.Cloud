namespace Yaparr.Cloud;

/// <summary>
/// One locked message left in a mailbox. The <see cref="Body"/> is base64 of what a device sealed (this service cannot open it and does not try); the <see cref="Id"/> is the
/// name the device gave the item (the message's own key), so leaving the same message twice stores it once. <see cref="Cursor"/> is where the item stands in the mailbox's
/// order of arrival, given by the service; a device ignores it when posting.
/// </summary>
public sealed record MailboxItem(string Id, string Body, DateTimeOffset At, long Cursor = 0);

/// <summary>The items after a cursor, oldest first. <see cref="Cursor"/> is where the next read starts; <see cref="More"/> is true when there are more than were returned.</summary>
public sealed record MailboxPage(IReadOnlyList<MailboxItem> Items, long Cursor, bool More);

public enum MailboxPostOutcome { Stored, Full }

/// <summary><see cref="Stored"/> is how many of the posted items were new (the rest were already there); <see cref="Cursor"/> is the mailbox's latest cursor after the call.</summary>
public sealed record MailboxPostResult(MailboxPostOutcome Outcome, long Cursor, int Stored);

/// <summary>
/// The mailboxes of rooms: for each mailbox (an opaque id that only the room's members can compute) the locked messages left in it, in the order they arrived, each
/// with a cursor so a device asks for "what came after N". The service reads nothing in an item. An item is kept for <c>keep</c> and then goes, read or not; the account
/// that left it is kept beside it only to count limits and so that deleting the account deletes what it left (it is never given to a reader). Two implementations,
/// <see cref="MailboxStore"/> in files and <c>PostgresMailboxStore</c> in the database, held to the same behaviour by the same tests.
/// </summary>
public interface IMailboxStore
{
    /// <summary>Up to <paramref name="limit"/> items that arrived after <paramref name="since"/> and have not expired, oldest first.</summary>
    MailboxPage Pull(string mailbox, long since, int limit, DateTimeOffset now, TimeSpan keep);

    /// <summary>
    /// Stores the items that are not already in the mailbox, each with the next cursor. Expired items of this mailbox go first. The call takes all or none: if the new
    /// items would take the mailbox past <paramref name="maxItems"/>, nothing is stored.
    /// </summary>
    MailboxPostResult Post(string mailbox, string account, IReadOnlyList<MailboxItem> items, int maxItems, DateTimeOffset now, TimeSpan keep);

    /// <summary>Removes every item, in every mailbox, that has expired. Returns how many.</summary>
    int Expire(DateTimeOffset now, TimeSpan keep);

    /// <summary>Removes what an account left, in every mailbox.</summary>
    void RemoveAccount(string account);
}

internal static class MailboxRules
{
    /// <summary>A mailbox id is what the devices derive: 32 bytes, base64url, 43 characters.</summary>
    public static bool GoodMailboxId(string? id) => id is { Length: 43 } && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    /// <summary>An item's id is the message key (hex, 32), but any short plain name is taken.</summary>
    public static bool GoodItemId(string? id) => id is { Length: > 0 and <= 64 } && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    /// <summary>The body is base64 of what the device sealed, not empty and within the limit.</summary>
    public static bool GoodBody(string? body, int maxChars) => body is { Length: > 0 } && body.Length <= maxChars && System.Buffers.Text.Base64.IsValid(body);
}
