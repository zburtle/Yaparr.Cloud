using System.Collections.Concurrent;

namespace Yaparr.Cloud;

/// <summary>
/// How often one wake token may be used: so many a minute and so many a day. A token is secret and only given to the people its owner talks to, so this
/// is for the one who would make a phone buzz all night, not for strangers (who cannot guess a token). It is by token and counts only tokens that exist.
/// </summary>
public sealed class WakeLimiter
{
    private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> _uses = new();
    private readonly Func<DateTimeOffset> _now;

    public WakeLimiter() : this(() => DateTimeOffset.UtcNow) { }
    internal WakeLimiter(Func<DateTimeOffset> now) => _now = now;

    /// <summary>True, and counts it, if the token has been used fewer than <paramref name="perMinute"/> times in the last minute and <paramref name="perDay"/> in the last day.</summary>
    public bool TryTake(string key, int perMinute, int perDay)
    {
        var now = _now();
        var queue = _uses.GetOrAdd(key, _ => new Queue<DateTimeOffset>());
        lock (queue)
        {
            while (queue.Count > 0 && now - queue.Peek() >= TimeSpan.FromDays(1)) queue.Dequeue();
            if (queue.Count >= Math.Max(1, perDay)) return false;
            if (queue.Count(at => now - at < TimeSpan.FromMinutes(1)) >= Math.Max(1, perMinute)) return false;
            queue.Enqueue(now);
            return true;
        }
    }
}

internal sealed record PushRegisterRequest(string? DeviceId, string? Kind, string? Endpoint);

/// <summary>
/// Waking a phone so it connects and picks up what it missed. Yaparr's messages go from phone to phone and never pass through this service, so it cannot know
/// that one is waiting: the person sending finds that the other is not connected, and asks for a wake-up with the token the other gave to the people in
/// their rooms. The service sends a push with nothing in it and forgets it. The sender needs no account here; the one who is woken does.
/// </summary>
internal static class PushApi
{
    public static void MapPush(this WebApplication app, ServiceOptions options)
    {
        var push = app.MapGroup("/v1/push");

        // Which ways of waking a phone this service has, for the app to choose from.
        push.MapGet("/options", (IPushSender sender) => Results.Json(new { unifiedPush = true, fcm = sender.FcmAvailable }));

        // Registers this device (or changes where it is woken). A new device is given its wake token here, once; the app keeps it and shares it with its rooms.
        push.MapPut("", async (HttpContext http, IAccounts accounts, IPushRegistry registry, IPushSender sender) =>
        {
            if (accounts.UserOfToken(Bearer(http)) is not { } user) return Problem(StatusCodes.Status401Unauthorized, "This device is not signed in. Sign in again.");
            PushRegisterRequest? body;
            try { body = await http.Request.ReadFromJsonAsync<PushRegisterRequest>(); }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException) { body = null; }
            if (!WakeTokens.GoodDeviceId(body?.DeviceId)) return Problem(StatusCodes.Status400BadRequest, "The device needs an id of letters, digits and - _ . (up to 64).");
            PushKind kind;
            if (string.Equals(body!.Kind, "unifiedpush", StringComparison.OrdinalIgnoreCase)) kind = PushKind.UnifiedPush;
            else if (string.Equals(body.Kind, "fcm", StringComparison.OrdinalIgnoreCase)) kind = PushKind.Fcm;
            else return Problem(StatusCodes.Status400BadRequest, "The kind is unifiedpush or fcm.");

            if (kind == PushKind.UnifiedPush)
            {
                if (PushEndpoints.Problem(body.Endpoint) is { } bad) return Problem(StatusCodes.Status400BadRequest, bad);
            }
            else
            {
                if (!sender.FcmAvailable) return Problem(StatusCodes.Status400BadRequest, "This service does not send through Firebase.");
                if (string.IsNullOrWhiteSpace(body.Endpoint) || body.Endpoint.Length > 4096) return Problem(StatusCodes.Status400BadRequest, "The device token is missing or too long.");
            }

            var result = registry.Register(user, body.DeviceId!, kind, body.Endpoint!.Trim(), options.PushMaxPerAccount);
            return result.Outcome switch
            {
                PushRegisterOutcome.Created => Results.Json(new { token = result.Token }, statusCode: StatusCodes.Status201Created),
                PushRegisterOutcome.Updated => Results.Json(new { token = (string?)null }),
                _ => Problem(StatusCodes.Status409Conflict, $"This account already has {options.PushMaxPerAccount} devices that get push notifications. Remove one first."),
            };
        });

        push.MapGet("", (HttpContext http, IAccounts accounts, IPushRegistry registry) =>
            accounts.UserOfToken(Bearer(http)) is { } user
                ? Results.Json(new { devices = registry.List(user).Select(d => new { deviceId = d.DeviceId, kind = d.Kind == PushKind.Fcm ? "fcm" : "unifiedpush", created = d.Created }) })
                : Problem(StatusCodes.Status401Unauthorized, "This device is not signed in. Sign in again."));

        // A new wake token, when the old one has been given to someone who should not have it (the old one stops working at once).
        push.MapPost("/{deviceId}/rotate", (HttpContext http, IAccounts accounts, IPushRegistry registry, string deviceId) =>
        {
            if (accounts.UserOfToken(Bearer(http)) is not { } user) return Problem(StatusCodes.Status401Unauthorized, "This device is not signed in. Sign in again.");
            return registry.Rotate(user, deviceId) is { } token ? Results.Json(new { token }) : Problem(StatusCodes.Status404NotFound, "This device is not registered.");
        });

        push.MapDelete("/{deviceId}", (HttpContext http, IAccounts accounts, IPushRegistry registry, string deviceId) =>
        {
            if (accounts.UserOfToken(Bearer(http)) is not { } user) return Problem(StatusCodes.Status401Unauthorized, "This device is not signed in. Sign in again.");
            return registry.Remove(user, deviceId) ? Results.NoContent() : Problem(StatusCodes.Status404NotFound, "This device is not registered.");
        });

        // Asking for a wake-up. No account: the token is the permission. It answers the same for a token that does not exist as for one that does not lead anywhere.
        push.MapPost("/wake/{token}", async (IAccounts accounts, IPushRegistry registry, IPushSender sender, WakeLimiter limiter, string token, CancellationToken cancel) =>
        {
            if (registry.Find(token) is not { } target) return Problem(StatusCodes.Status404NotFound, "Nobody to wake.");
            if (!accounts.Exists(target.Account))
            {
                registry.RemoveAccount(target.Account);
                return Problem(StatusCodes.Status404NotFound, "Nobody to wake.");
            }
            if (!limiter.TryTake(token, options.PushWakesPerMinute, options.PushWakesPerDay))
                return Problem(StatusCodes.Status429TooManyRequests, "That device has been woken a lot lately. Try again later.");

            switch (await sender.SendAsync(target, cancel))
            {
                case PushResult.Sent: return Results.StatusCode(StatusCodes.Status202Accepted);
                case PushResult.Gone:
                    registry.RemoveByToken(token);
                    return Problem(StatusCodes.Status410Gone, "That device does not get push notifications any more.");
                default: return Problem(StatusCodes.Status502BadGateway, "The push could not be sent.");
            }
        });
    }

    internal static string? Bearer(HttpContext http)
    {
        var header = http.Request.Headers.Authorization.ToString();
        return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..].Trim() : null;
    }

    internal static IResult Problem(int status, string message) => Results.Json(new { error = message }, statusCode: status);
}
