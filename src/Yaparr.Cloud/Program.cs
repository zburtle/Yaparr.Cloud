using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Http.Features;
using Yaparr.Cloud;

// "accounts add|remove|list" looks after who may use the service and then exits; anything else runs the service.
if (args.Length > 0 && args[0].Equals("accounts", StringComparison.OrdinalIgnoreCase))
{
    var config = new ConfigurationBuilder().AddJsonFile("appsettings.json", optional: true).AddEnvironmentVariables().Build();
    return await AdminCommands.RunAsync(args[1..], config.GetSection("Service").Get<ServiceOptions>() ?? new ServiceOptions(), Console.Out);
}

var builder = WebApplication.CreateBuilder(args);
var options = builder.Configuration.GetSection("Service").Get<ServiceOptions>() ?? new ServiceOptions();
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<BackupStore>();
builder.Services.AddSingleton<AccountDirectory>();
builder.Services.AddSingleton<WriteLimiter>();
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = options.MaxBlobBytes + 1024);

// Behind a reverse proxy (the usual way to give it https) the address of the caller is in a header, not on the connection.
if (options.BehindProxy)
    builder.Services.Configure<ForwardedHeadersOptions>(o =>
    {
        o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        o.KnownIPNetworks.Clear();
        o.KnownProxies.Clear();
    });

// A limit on requests from one address, whether or not they are signed correctly: the cheap defence against being hammered.
builder.Services.AddRateLimiter(r =>
{
    r.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    r.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = Math.Max(1, options.RequestsPerMinute),
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        }));
});

var app = builder.Build();
if (options.BehindProxy) app.UseForwardedHeaders();
app.UseRateLimiter();

app.MapGet("/healthz", () => Results.Ok(new { ok = true }));

// Everything under /v1 is signed (see RequestSigner). The account is the username that signed, and only an account that was made by hand exists.
var backup = app.MapGroup("/v1/backup");

backup.MapPut("", async (HttpContext http, BackupStore store, WriteLimiter writes, AccountDirectory accounts, CancellationToken cancel) =>
{
    if (Authenticate(http, options, accounts, out var user, out var contentHash) is { } refused) return refused;
    var account = RequestSigner.FolderOf(user);

    if (!http.Request.Headers.TryGetValue("If-Match", out var ifMatch) || !int.TryParse(ifMatch.ToString().Trim('"'), NumberStyles.None, CultureInfo.InvariantCulture, out var basedOn))
        return Problem(StatusCodes.Status428PreconditionRequired, "Say which version this is based on (If-Match: the newest version you know, 0 if there is none).");
    if (http.Request.ContentLength is { } length && length > options.MaxBlobBytes)
        return Problem(StatusCodes.Status413PayloadTooLarge, "This backup is too big.");
    if (!writes.TryTake(account, options.WritesPerHour))
        return Problem(StatusCodes.Status429TooManyRequests, "Too many backups saved in the last hour. Try again later.");
    if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit) limit.MaxRequestBodySize = options.MaxBlobBytes;

    var result = await store.SaveAsync(account, basedOn, http.Request.Body, contentHash, cancel);
    return result.Outcome switch
    {
        SaveOutcome.Saved => Results.Json(new { version = result.Version, size = result.Size }, statusCode: StatusCodes.Status201Created),
        SaveOutcome.Conflict => Results.Json(new { error = "A newer backup exists. Fetch it first.", version = result.Version }, statusCode: StatusCodes.Status409Conflict),
        SaveOutcome.TooLarge => Problem(StatusCodes.Status413PayloadTooLarge, "This backup is too big."),
        SaveOutcome.HashMismatch => Problem(StatusCodes.Status400BadRequest, "What arrived is not what was signed."),
        SaveOutcome.NotALockedBackup => Problem(StatusCodes.Status400BadRequest, "Only a backup locked with a passphrase is accepted."),
        _ => Problem(StatusCodes.Status500InternalServerError, "Not saved."),
    };
});

backup.MapGet("", (HttpContext http, BackupStore store, AccountDirectory accounts, int? version) =>
{
    if (Authenticate(http, options, accounts, out var user, out _) is { } refused) return refused;
    var account = RequestSigner.FolderOf(user);
    var found = store.Open(account, version);
    if (found == null) return Problem(StatusCodes.Status404NotFound, "There is no backup.");
    http.Response.Headers["X-Yaparr-Version"] = found.Value.Info.Version.ToString(CultureInfo.InvariantCulture);
    return Results.Stream(found.Value.Content, "application/octet-stream");
});

backup.MapGet("/versions", (HttpContext http, BackupStore store, AccountDirectory accounts) =>
{
    if (Authenticate(http, options, accounts, out var user, out _) is { } refused) return refused;
    return Results.Json(store.Versions(RequestSigner.FolderOf(user)).Select(v => new { version = v.Version, size = v.Size, at = v.At }));
});

backup.MapDelete("", async (HttpContext http, BackupStore store, AccountDirectory accounts, CancellationToken cancel) =>
{
    if (Authenticate(http, options, accounts, out var user, out _) is { } refused) return refused;
    return await store.DeleteAsync(RequestSigner.FolderOf(user), cancel) ? Results.NoContent() : Problem(StatusCodes.Status404NotFound, "There is no backup.");
});

// A changed password: the body is {"publicKey": "<base64url of the new key>"}, signed with the key that is current. The app locks the backup again
// with the new password and saves it afterwards. (Only the key is replaced here, so the username, and with it the backup's folder, stay the same.)
app.MapPut("/v1/account/key", async (HttpContext http, AccountDirectory accounts, WriteLimiter writes, CancellationToken cancel) =>
{
    if (Authenticate(http, options, accounts, out var user, out var contentHash) is { } refused) return refused;
    if (!writes.TryTake("key:" + user, 5)) return Problem(StatusCodes.Status429TooManyRequests, "Too many changes. Try again later.");

    var body = new MemoryStream();
    var buffer = new byte[1024];
    int n;
    while ((n = await http.Request.Body.ReadAsync(buffer, cancel)) > 0)
    {
        if (body.Length + n > 1024) return Problem(StatusCodes.Status413PayloadTooLarge, "That is too big.");
        body.Write(buffer, 0, n);
    }
    if (Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(body.ToArray())) != contentHash)
        return Problem(StatusCodes.Status400BadRequest, "What arrived is not what was signed.");

    byte[]? key = null;
    try
    {
        var text = System.Text.Json.JsonDocument.Parse(body.ToArray()).RootElement.GetProperty("publicKey").GetString();
        key = text == null ? null : System.Buffers.Text.Base64Url.DecodeFromChars(text);
    }
    catch (Exception ex) when (ex is System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException or FormatException) { }
    if (key is not { Length: 32 }) return Problem(StatusCodes.Status400BadRequest, "That is not a key.");

    return accounts.SetKey(user, key) ? Results.NoContent() : Problem(StatusCodes.Status404NotFound, "There is no such account.");
});

app.Run();
return 0;

static IResult Problem(int status, string message) => Results.Json(new { error = message }, statusCode: status);

// Null if the request is signed correctly (with the account and the body hash it was signed with); otherwise the answer to send.
static IResult? Authenticate(HttpContext http, ServiceOptions options, AccountDirectory accounts, out string username, out string contentHash)
{
    var request = http.Request;
    // Signed as sent, so the path and query are taken from the raw request and not rebuilt from a parsed form.
    var pathAndQuery = request.Path.Value + request.QueryString.Value;
    if (RequestSigner.TryVerify(request.Method, pathAndQuery, name => request.Headers.TryGetValue(name, out var v) ? v.ToString() : null,
            accounts.PublicKeyOf, DateTimeOffset.UtcNow, options.ClockSkewSeconds, out username, out contentHash, out var error))
        return null;
    return Problem(StatusCodes.Status401Unauthorized, error);
}

/// <summary>For the tests, which start the service in memory.</summary>
public partial class Program;
