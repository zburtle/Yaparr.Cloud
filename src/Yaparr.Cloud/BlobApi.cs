namespace Yaparr.Cloud;

/// <summary>
/// Locked pictures for people who are offline (see <c>docs/mailbox.md</c> of the Yaparr repository, and the README, "Blobs"). A device uploads a file it has locked with a key the
/// service never has, under the id <c>SHA-256 of the locked bytes</c> (checked here, so nobody picks an id); a member of the room fetches it by that id, which only
/// they know, and opens it. Fetching needs no sign-in, so a reader whose server is another one can still fetch it, and so that the service cannot tie a read to an account.
/// </summary>
internal static class BlobApi
{
    public static void MapBlobs(this WebApplication app, ServiceOptions options)
    {
        var blobs = app.MapGroup("/v1/blob");
        var keep = TimeSpan.FromDays(Math.Max(1, options.BlobKeepDays));

        blobs.MapPut("{id}", async (HttpContext http, string id, IAccounts accounts, IBlobStore store, WakeLimiter limiter, PlanGate gate) =>
        {
            if (accounts.UserOfToken(PushApi.Bearer(http)) is not { } user) return PushApi.Problem(StatusCodes.Status401Unauthorized, "This device is not signed in. Sign in again.");
            if (gate.Refuse(user) is { } refused) return refused;
            if (!BlobRules.GoodId(id)) return PushApi.Problem(StatusCodes.Status400BadRequest, "That is not a blob id.");
            if (http.Request.ContentLength is { } declared && declared > options.BlobMaxBytes) return PushApi.Problem(StatusCodes.Status413PayloadTooLarge, $"A blob is at most {options.BlobMaxBytes} bytes.");
            if (!limiter.TryTake("blob:" + user, options.BlobUploadsPerMinute, options.BlobUploadsPerDay))
                return PushApi.Problem(StatusCodes.Status429TooManyRequests, "This account has uploaded a lot lately. Try again in a minute.");

            using var body = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = await http.Request.Body.ReadAsync(buffer)) > 0)
            {
                if (body.Length + read > options.BlobMaxBytes) return PushApi.Problem(StatusCodes.Status413PayloadTooLarge, $"A blob is at most {options.BlobMaxBytes} bytes.");
                body.Write(buffer, 0, read);
            }
            var data = body.ToArray();
            if (data.Length == 0) return PushApi.Problem(StatusCodes.Status400BadRequest, "Send the locked file as the body.");
            if (BlobRules.IdOf(data) != id) return PushApi.Problem(StatusCodes.Status400BadRequest, "The id is the SHA-256 of the bytes, in lower case hex.");

            return store.Put(id, user, data, options.BlobQuotaBytes, DateTimeOffset.UtcNow, keep) switch
            {
                BlobPutOutcome.OverQuota => PushApi.Problem(StatusCodes.Status409Conflict, $"This account already holds {options.BlobQuotaBytes / (1024 * 1024)} MB of pictures that have not expired. Nothing was stored."),
                BlobPutOutcome.AlreadyThere => Results.Json(new { stored = false }),
                _ => Results.Json(new { stored = true }),
            };
        });

        blobs.MapGet("{id}", (string id, IBlobStore store) =>
            BlobRules.GoodId(id) && store.Get(id, DateTimeOffset.UtcNow, keep) is { } data
                ? Results.Bytes(data, "application/octet-stream")
                : PushApi.Problem(StatusCodes.Status404NotFound, "There is no such blob."));
    }
}
