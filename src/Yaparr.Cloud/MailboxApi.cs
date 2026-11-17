namespace Yaparr.Cloud;

internal sealed record MailboxItemBody(string? Id, string? Body);

internal sealed record MailboxPostRequest(List<MailboxItemBody>? Items);

/// <summary>
/// The mailboxes of rooms, for signed-in devices: a device leaves the locked messages that a member who was offline has not received, and the member's devices fetch them
/// when they are back. The mailbox id is an opaque value that only the room's members can compute and the items are locked with a key the service never has, so it
/// stores and hands on what it cannot read (see <c>docs/mailbox.md</c> of the Yaparr repository, and the README, "Mailboxes").
/// </summary>
internal static class MailboxApi
{
    private const int MaxBatch = 100, MaxPost = 20;

    public static void MapMailbox(this WebApplication app, ServiceOptions options)
    {
        var mailbox = app.MapGroup("/v1/mailbox");
        var keep = TimeSpan.FromDays(Math.Max(1, options.MailboxKeepDays));

        // What arrived after a cursor (0 for everything kept), oldest first. When there is more, the cursor in the answer is where to ask next.
        mailbox.MapGet("{id}", (HttpContext http, string id, IAccounts accounts, IMailboxStore store, WakeLimiter limiter, long? since, int? limit) =>
        {
            if (accounts.UserOfToken(PushApi.Bearer(http)) is not { } user) return PushApi.Problem(StatusCodes.Status401Unauthorized, "This device is not signed in. Sign in again.");
            if (!MailboxRules.GoodMailboxId(id)) return PushApi.Problem(StatusCodes.Status400BadRequest, "That is not a mailbox id.");
            if (!limiter.TryTake("mailbox-read:" + user, options.MailboxReadsPerMinute, options.MailboxReadsPerDay))
                return PushApi.Problem(StatusCodes.Status429TooManyRequests, "This account has asked a lot lately. Try again in a minute.");
            var page = store.Pull(id, Math.Max(0, since ?? 0), Math.Clamp(limit ?? MaxBatch, 1, MaxBatch), DateTimeOffset.UtcNow, keep);
            return Results.Json(new
            {
                cursor = page.Cursor,
                more = page.More,
                items = page.Items.Select(i => new { id = i.Id, body = i.Body, at = i.At.ToUnixTimeSeconds(), cursor = i.Cursor }),
            });
        });

        // Leaves locked messages. One that is already there is not stored again (the same id), so a device can post again after it was cut off.
        mailbox.MapPost("{id}", async (HttpContext http, string id, IAccounts accounts, IMailboxStore store, WakeLimiter limiter) =>
        {
            if (accounts.UserOfToken(PushApi.Bearer(http)) is not { } user) return PushApi.Problem(StatusCodes.Status401Unauthorized, "This device is not signed in. Sign in again.");
            if (!MailboxRules.GoodMailboxId(id)) return PushApi.Problem(StatusCodes.Status400BadRequest, "That is not a mailbox id.");
            MailboxPostRequest? request;
            try { request = await http.Request.ReadFromJsonAsync<MailboxPostRequest>(); }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException) { request = null; }
            if (request?.Items is not { Count: > 0 } sent) return PushApi.Problem(StatusCodes.Status400BadRequest, "Send the items to leave.");
            if (sent.Count > MaxPost) return PushApi.Problem(StatusCodes.Status400BadRequest, $"At most {MaxPost} items in one call.");

            var items = new List<MailboxItem>(sent.Count);
            foreach (var item in sent)
            {
                if (!MailboxRules.GoodItemId(item.Id)) return PushApi.Problem(StatusCodes.Status400BadRequest, "An item's id is letters, digits, - and _ (up to 64).");
                if (!MailboxRules.GoodBody(item.Body, options.MailboxMaxItemChars)) return PushApi.Problem(StatusCodes.Status400BadRequest, $"An item's body is base64 of at most {options.MailboxMaxItemChars} characters.");
                items.Add(new MailboxItem(item.Id!, item.Body!, DateTimeOffset.UtcNow));
            }

            if (!limiter.TryTake("mailbox:" + user, options.MailboxPostsPerMinute, options.MailboxPostsPerDay))
                return PushApi.Problem(StatusCodes.Status429TooManyRequests, "This account has sent a lot lately. Try again in a minute.");

            var result = store.Post(id, user, items, options.MailboxMaxItems, DateTimeOffset.UtcNow, keep);
            return result.Outcome == MailboxPostOutcome.Full
                ? PushApi.Problem(StatusCodes.Status409Conflict, $"This mailbox already holds {options.MailboxMaxItems} items. Nothing was stored; they expire after {options.MailboxKeepDays} days.")
                : Results.Json(new { cursor = result.Cursor, stored = result.Stored });
        });
    }
}
