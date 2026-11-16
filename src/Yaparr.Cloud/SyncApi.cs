namespace Yaparr.Cloud;

internal sealed record SyncRecordBody(string? Kind, string? Id, string? Clock, string? Device, bool Deleted, string? Body);

internal sealed record SyncPutRequest(List<SyncRecordBody>? Records);

/// <summary>
/// The synced state of an account, for its signed-in devices: one record for each thing that should be the same on all of them (a room joined, the name, a block).
/// The service keeps the newest write of each record and hands out what changed after a cursor; the records are locked by the devices, which also do all the merging
/// (see the README, "Synced state").
/// </summary>
internal static class SyncApi
{
    private const int MaxBatch = 100;

    public static void MapSync(this WebApplication app, ServiceOptions options)
    {
        var sync = app.MapGroup("/v1/sync");

        // What changed after a cursor (0 for everything), oldest first. When there is more, the cursor in the answer is where to ask next.
        sync.MapGet("", (HttpContext http, IAccounts accounts, ISyncStore store, long? since, int? limit) =>
        {
            if (accounts.UserOfToken(PushApi.Bearer(http)) is not { } user) return PushApi.Problem(StatusCodes.Status401Unauthorized, "This device is not signed in. Sign in again.");
            var page = store.Pull(user, Math.Max(0, since ?? 0), Math.Clamp(limit ?? MaxBatch, 1, MaxBatch));
            return Results.Json(new
            {
                cursor = page.Cursor,
                more = page.More,
                records = page.Records.Select(r => new { kind = r.Kind, id = r.Id, clock = r.Clock, device = r.Device, deleted = r.Deleted, body = r.Body, cursor = r.Cursor }),
            });
        });

        // Sends records. Those that are not newer than what the service holds are named in "stale" and not taken; the device pulls and finds the newer ones.
        sync.MapPut("", async (HttpContext http, IAccounts accounts, ISyncStore store, WakeLimiter limiter) =>
        {
            if (accounts.UserOfToken(PushApi.Bearer(http)) is not { } user) return PushApi.Problem(StatusCodes.Status401Unauthorized, "This device is not signed in. Sign in again.");
            SyncPutRequest? request;
            try { request = await http.Request.ReadFromJsonAsync<SyncPutRequest>(); }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException) { request = null; }
            if (request?.Records is not { Count: > 0 } sent) return PushApi.Problem(StatusCodes.Status400BadRequest, "Send the records to store.");
            if (sent.Count > MaxBatch) return PushApi.Problem(StatusCodes.Status400BadRequest, $"At most {MaxBatch} records in one call.");

            var records = new List<SyncRecord>(sent.Count);
            foreach (var r in sent)
            {
                if (!SyncRules.GoodKind(r.Kind)) return PushApi.Problem(StatusCodes.Status400BadRequest, "A record's kind is lowercase letters, digits and - (up to 32).");
                if (!SyncRules.GoodId(r.Id)) return PushApi.Problem(StatusCodes.Status400BadRequest, "A record's id is letters, digits and . _ - : @ + = / | (up to 128).");
                if (!SyncRules.GoodClock(r.Clock)) return PushApi.Problem(StatusCodes.Status400BadRequest, "A record's clock is letters, digits and . _ - (up to 96).");
                if (!WakeTokens.GoodDeviceId(r.Device)) return PushApi.Problem(StatusCodes.Status400BadRequest, "A record names the device that wrote it, with an id of letters, digits and - _ . (up to 64).");
                if (!SyncRules.GoodBody(r.Body, r.Deleted, options.SyncMaxRecordBytes)) return PushApi.Problem(StatusCodes.Status400BadRequest, $"A record's body is base64 of at most {options.SyncMaxRecordBytes} characters, and empty only when the record is deleted.");
                records.Add(new SyncRecord(r.Kind!, r.Id!, r.Clock!, r.Device!, r.Deleted, r.Body!));
            }

            if (!limiter.TryTake("sync:" + user, options.SyncWritesPerMinute, options.SyncWritesPerDay))
                return PushApi.Problem(StatusCodes.Status429TooManyRequests, "This account has sent a lot lately. Try again in a minute.");

            var result = store.Put(user, records, options.SyncMaxRecords);
            return result.Outcome == SyncPutOutcome.TooMany
                ? PushApi.Problem(StatusCodes.Status409Conflict, $"This account already holds {options.SyncMaxRecords} synced records. Nothing was stored.")
                : Results.Json(new { cursor = result.Cursor, stale = result.Stale });
        });
    }
}
