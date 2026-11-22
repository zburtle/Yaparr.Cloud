namespace Yaparr.Cloud;

/// <summary>
/// What a signed-in device can ask about its own account: <c>GET /v1/devices</c> (the devices signed in as it, and which one is asking), <c>DELETE /v1/devices/{id}</c> (sign one out,
/// this one included) and <c>GET /v1/usage</c> (what the account holds, against its limits, so the apps can show it).
/// </summary>
internal static class AccountApi
{
    public static void MapAccountApi(this WebApplication app, ServiceOptions options)
    {
        app.MapGet("/v1/devices", (HttpContext http, IAccounts accounts) =>
        {
            var token = PushApi.Bearer(http);
            if (accounts.UserOfToken(token) is not { } user) return PushApi.Problem(StatusCodes.Status401Unauthorized, "This device is not signed in. Sign in again.");
            var mine = AccountDirectory.DeviceIdOf(AccountDirectory.HashOf(token!));
            return Results.Json(new
            {
                devices = accounts.DevicesOf(user).Select(d => new { id = d.Id, name = d.Name, created = d.Created, current = d.Id == mine }),
            });
        });

        app.MapDelete("/v1/devices/{id}", (HttpContext http, string id, IAccounts accounts) =>
        {
            if (accounts.UserOfToken(PushApi.Bearer(http)) is not { } user) return PushApi.Problem(StatusCodes.Status401Unauthorized, "This device is not signed in. Sign in again.");
            return accounts.RevokeDevice(user, id) ? Results.NoContent() : PushApi.Problem(StatusCodes.Status404NotFound, "This account has no such device.");
        });

        // What the account is: with billing off just that, otherwise its state, the plan, when it ends and whether it renews. The apps show it and do nothing else with it.
        app.MapGet("/v1/plan", (HttpContext http, IAccounts accounts, PlanGate gate) =>
        {
            if (accounts.UserOfToken(PushApi.Bearer(http)) is not { } user) return PushApi.Problem(StatusCodes.Status401Unauthorized, "This device is not signed in. Sign in again.");
            var plan = gate.Describe(user);
            if (plan.State == PlanState.Off) return Results.Json(new { billing = "off" });
            return Results.Json(new
            {
                billing = "on",
                state = plan.State.ToString().ToLowerInvariant(),
                plan = plan.Plan,
                endsAt = plan.EndsAt,
                willRenew = plan.WillRenew,
                pastDue = plan.PastDue,
            });
        });

        app.MapGet("/v1/usage", async (HttpContext http, IAccounts accounts, IBackupStore backups, IBlobStore pictures, IKeepStore kept, GatewayRegistry gateway) =>
        {
            if (accounts.UserOfToken(PushApi.Bearer(http)) is not { } user) return PushApi.Problem(StatusCodes.Status401Unauthorized, "This device is not signed in. Sign in again.");
            var now = DateTimeOffset.UtcNow;
            var versions = await backups.VersionsAsync(BackupStore.FolderOf(user), http.RequestAborted);
            var forever = TimeSpan.FromDays(36500);
            return Results.Json(new
            {
                kept = new { usedBytes = kept.UsedBytes(user, now, forever), quotaBytes = options.KeepQuotaBytes },
                pictures = new { usedBytes = pictures.UsedBytes(user, now, TimeSpan.FromDays(Math.Max(1, options.BlobKeepDays))), quotaBytes = options.BlobQuotaBytes, keptDays = Math.Max(1, options.BlobKeepDays) },
                backups = new { count = versions.Count, bytes = versions.Sum(v => v.Size) },
                gateway = new { enabled = options.GatewayEnabled, usedTodayBytes = gateway.UsedToday(user), perDayBytes = options.GatewayBytesPerDay, bytesPerSecond = options.GatewayBytesPerSecond },
            });
        });
    }
}
