namespace Yaparr.Cloud;

/// <summary>
/// Locked pieces an account keeps for good (see the README, "Kept pieces"): the devices of an account lock a piece of what they keep in sync (a day of one room's history, an
/// emoji, a sound, a GIF) with a key this service never has, and give it the id <c>SHA-256 of the locked bytes</c>; which pieces there are, and what each is, is said by the
/// account's synced records, which carry the ids. Unlike a picture left for someone who is away, a piece never expires, counts against <see cref="ServiceOptions.KeepQuotaBytes"/>
/// and is read only by the account's own devices (a sign-in is needed), and an account removes the ones it no longer needs.
/// </summary>
internal static class KeepApi
{
    public static void MapKeep(this WebApplication app, ServiceOptions options)
    {
        var keep = app.MapGroup("/v1/keep");
        // Nothing expires: a keeping time no clock reaches.
        var forever = TimeSpan.FromDays(36500);

        keep.MapPut("{id}", async (HttpContext http, string id, IAccounts accounts, IKeepStore store, WakeLimiter limiter, PlanGate gate) =>
        {
            if (accounts.UserOfToken(PushApi.Bearer(http)) is not { } user) return PushApi.Problem(StatusCodes.Status401Unauthorized, "This device is not signed in. Sign in again.");
            if (gate.Refuse(user) is { } refused) return refused;
            if (!BlobRules.GoodId(id)) return PushApi.Problem(StatusCodes.Status400BadRequest, "That is not a piece id.");
            if (http.Request.ContentLength is { } declared && declared > options.KeepMaxBytes) return PushApi.Problem(StatusCodes.Status413PayloadTooLarge, $"A piece is at most {options.KeepMaxBytes} bytes.");
            if (!limiter.TryTake("keep:" + user, options.KeepUploadsPerMinute, options.KeepUploadsPerDay))
                return PushApi.Problem(StatusCodes.Status429TooManyRequests, "This account has uploaded a lot lately. Try again in a minute.");

            using var body = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = await http.Request.Body.ReadAsync(buffer)) > 0)
            {
                if (body.Length + read > options.KeepMaxBytes) return PushApi.Problem(StatusCodes.Status413PayloadTooLarge, $"A piece is at most {options.KeepMaxBytes} bytes.");
                body.Write(buffer, 0, read);
            }
            var data = body.ToArray();
            if (data.Length == 0) return PushApi.Problem(StatusCodes.Status400BadRequest, "Send the locked piece as the body.");
            if (BlobRules.IdOf(data) != id) return PushApi.Problem(StatusCodes.Status400BadRequest, "The id is the SHA-256 of the bytes, in lower case hex.");

            return store.Put(id, user, data, options.KeepQuotaBytes, DateTimeOffset.UtcNow, forever) switch
            {
                BlobPutOutcome.OverQuota => PushApi.Problem(StatusCodes.Status409Conflict, $"This account already keeps {options.KeepQuotaBytes / (1024 * 1024)} MB. Nothing was stored."),
                BlobPutOutcome.AlreadyThere => Results.Json(new { stored = false }),
                _ => Results.Json(new { stored = true }),
            };
        });

        keep.MapGet("{id}", (HttpContext http, string id, IAccounts accounts, IKeepStore store) =>
        {
            if (accounts.UserOfToken(PushApi.Bearer(http)) is null) return PushApi.Problem(StatusCodes.Status401Unauthorized, "This device is not signed in. Sign in again.");
            return BlobRules.GoodId(id) && store.Get(id, DateTimeOffset.UtcNow, forever) is { } data
                ? Results.Bytes(data, "application/octet-stream")
                : PushApi.Problem(StatusCodes.Status404NotFound, "There is no such piece.");
        });

        keep.MapDelete("{id}", (HttpContext http, string id, IAccounts accounts, IKeepStore store) =>
        {
            if (accounts.UserOfToken(PushApi.Bearer(http)) is not { } user) return PushApi.Problem(StatusCodes.Status401Unauthorized, "This device is not signed in. Sign in again.");
            return store.Remove(id, user) ? Results.NoContent() : PushApi.Problem(StatusCodes.Status404NotFound, "There is no such piece of yours.");
        });
    }
}
