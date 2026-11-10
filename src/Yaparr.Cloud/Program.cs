using System.Globalization;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Http.Features;
using Yaparr.Cloud;

// "accounts add|password|remove|list" looks after who may use the service and then exits; anything else runs the service.
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
builder.Services.AddSingleton<LoginFlow>();
builder.Services.AddSingleton<LoginLimiter>();
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = options.MaxBlobBytes + 1024);

// Behind a reverse proxy (the usual way to give it https) the address of the caller is in a header, not on the connection.
if (options.BehindProxy)
    builder.Services.Configure<ForwardedHeadersOptions>(o =>
    {
        o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        o.KnownIPNetworks.Clear();
        o.KnownProxies.Clear();
    });

// A limit on requests from one address, whatever they are: the cheap defence against being hammered.
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

// ---- signing in, in the browser (see LoginFlow) ----

app.MapGet("/login", (HttpContext http, string? redirect_uri, string? code_challenge, string? state, string? device) =>
    LoginFlow.Read(redirect_uri, code_challenge, state, device, out var problem) is { } request
        ? Html(http, Pages.Login(request, "", null))
        : Html(http, Pages.Message("This link does not work", problem), StatusCodes.Status400BadRequest));

app.MapPost("/login", async (HttpContext http, AccountDirectory accounts, LoginFlow flow, LoginLimiter limiter) =>
{
    var form = await http.Request.ReadFormAsync();
    if (LoginFlow.Read(form["redirect_uri"], form["code_challenge"], form["state"], form["device"], out var problem) is not { } request)
        return Html(http, Pages.Message("This link does not work", problem), StatusCodes.Status400BadRequest);

    var typed = form["username"].ToString();
    var password = form["password"].ToString();
    // The same answer for a name that does not exist as for a wrong password, so the page cannot be used to find out who has an account.
    const string wrong = "The username or password is wrong.";
    if (AccountDirectory.Normalize(typed) is not { } name || password.Length > 1000)
        return Html(http, Pages.Login(request, typed, wrong), StatusCodes.Status401Unauthorized);
    if (limiter.IsBlocked(name))
        return Html(http, Pages.Login(request, typed, "Too many wrong tries. Wait a quarter of an hour and try again."), StatusCodes.Status429TooManyRequests);
    if (!await Task.Run(() => accounts.CheckPassword(name, password)))
    {
        limiter.Failed(name);
        return Html(http, Pages.Login(request, typed, wrong), StatusCodes.Status401Unauthorized);
    }
    limiter.Succeeded(name);
    return Results.Redirect(flow.Complete(request, name));
}).DisableAntiforgery();

// The app trades the code the browser brought back, and the verifier only it knows, for a token.
app.MapPost("/v1/token", async (HttpContext http, AccountDirectory accounts, LoginFlow flow) =>
{
    TokenRequest? body;
    try { body = await http.Request.ReadFromJsonAsync<TokenRequest>(); }
    catch (Exception ex) when (ex is JsonException or InvalidOperationException) { body = null; }
    if (flow.Redeem(body?.Code, body?.Verifier) is not { } signedIn || accounts.IssueToken(signedIn.User, signedIn.Device) is not { } token)
        return Problem(StatusCodes.Status400BadRequest, "That sign-in has expired or was already used. Sign in again.");
    return Results.Json(new { token, username = signedIn.User });
});

// Signing a device out.
app.MapDelete("/v1/token", (HttpContext http, AccountDirectory accounts) =>
    BearerOf(http) is { } token && accounts.RevokeToken(token) ? Results.NoContent() : Problem(StatusCodes.Status401Unauthorized, NotSignedIn));

app.MapGet("/account/password", (HttpContext http) => Html(http, Pages.ChangePassword("", null)));

app.MapPost("/account/password", async (HttpContext http, AccountDirectory accounts, LoginLimiter limiter) =>
{
    var form = await http.Request.ReadFormAsync();
    var typed = form["username"].ToString();
    string current = form["current"].ToString(), replacement = form["new"].ToString();
    IResult Again(string problem, int status) => Html(http, Pages.ChangePassword(typed, problem), status);

    if (Passwords.Check(replacement) is { } weak) return Again(weak, StatusCodes.Status400BadRequest);
    if (replacement != form["again"].ToString()) return Again("The two new passwords are not the same.", StatusCodes.Status400BadRequest);
    if (replacement.Length > 1000 || current.Length > 1000) return Again("That password is too long.", StatusCodes.Status400BadRequest);
    if (AccountDirectory.Normalize(typed) is not { } name) return Again("The username or current password is wrong.", StatusCodes.Status401Unauthorized);
    if (limiter.IsBlocked(name)) return Again("Too many wrong tries. Wait a quarter of an hour and try again.", StatusCodes.Status429TooManyRequests);
    if (!await Task.Run(() => accounts.CheckPassword(name, current)))
    {
        limiter.Failed(name);
        return Again("The username or current password is wrong.", StatusCodes.Status401Unauthorized);
    }
    limiter.Succeeded(name);
    await Task.Run(() => accounts.SetPassword(name, replacement));
    return Html(http, Pages.Message("Password changed", "Every device was signed out. Open Yaparr and sign in again with the new password. Your backups are untouched."));
}).DisableAntiforgery();

// ---- the backup, for an app that is signed in (Authorization: Bearer <token>) ----

var backup = app.MapGroup("/v1/backup");

backup.MapPut("", async (HttpContext http, BackupStore store, WriteLimiter writes, AccountDirectory accounts, CancellationToken cancel) =>
{
    if (accounts.UserOfToken(BearerOf(http)) is not { } user) return Problem(StatusCodes.Status401Unauthorized, NotSignedIn);

    if (!http.Request.Headers.TryGetValue("If-Match", out var ifMatch) || !int.TryParse(ifMatch.ToString().Trim('"'), NumberStyles.None, CultureInfo.InvariantCulture, out var basedOn))
        return Problem(StatusCodes.Status428PreconditionRequired, "Say which version this is based on (If-Match: the newest version you know, 0 if there is none).");
    if (http.Request.ContentLength is { } length && length > options.MaxBlobBytes)
        return Problem(StatusCodes.Status413PayloadTooLarge, "This backup is too big.");
    if (!writes.TryTake(user, options.WritesPerHour))
        return Problem(StatusCodes.Status429TooManyRequests, "Too many backups saved in the last hour. Try again later.");
    if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit) limit.MaxRequestBodySize = options.MaxBlobBytes;

    var result = await store.SaveAsync(BackupStore.FolderOf(user), basedOn, http.Request.Body, cancel);
    return result.Outcome switch
    {
        SaveOutcome.Saved => Results.Json(new { version = result.Version, size = result.Size }, statusCode: StatusCodes.Status201Created),
        SaveOutcome.Conflict => Results.Json(new { error = "A newer backup exists. Fetch it first.", version = result.Version }, statusCode: StatusCodes.Status409Conflict),
        SaveOutcome.TooLarge => Problem(StatusCodes.Status413PayloadTooLarge, "This backup is too big."),
        SaveOutcome.NotALockedBackup => Problem(StatusCodes.Status400BadRequest, "Only a locked backup is accepted."),
        _ => Problem(StatusCodes.Status500InternalServerError, "Not saved."),
    };
});

backup.MapGet("", (HttpContext http, BackupStore store, AccountDirectory accounts, int? version) =>
{
    if (accounts.UserOfToken(BearerOf(http)) is not { } user) return Problem(StatusCodes.Status401Unauthorized, NotSignedIn);
    var found = store.Open(BackupStore.FolderOf(user), version);
    if (found == null) return Problem(StatusCodes.Status404NotFound, "There is no backup.");
    http.Response.Headers["X-Yaparr-Version"] = found.Value.Info.Version.ToString(CultureInfo.InvariantCulture);
    return Results.Stream(found.Value.Content, "application/octet-stream");
});

backup.MapGet("/versions", (HttpContext http, BackupStore store, AccountDirectory accounts) =>
    accounts.UserOfToken(BearerOf(http)) is { } user
        ? Results.Json(new { username = user, versions = store.Versions(BackupStore.FolderOf(user)).Select(v => new { version = v.Version, size = v.Size, at = v.At }) })
        : Problem(StatusCodes.Status401Unauthorized, NotSignedIn));

backup.MapDelete("", async (HttpContext http, BackupStore store, AccountDirectory accounts, CancellationToken cancel) =>
{
    if (accounts.UserOfToken(BearerOf(http)) is not { } user) return Problem(StatusCodes.Status401Unauthorized, NotSignedIn);
    return await store.DeleteAsync(BackupStore.FolderOf(user), cancel) ? Results.NoContent() : Problem(StatusCodes.Status404NotFound, "There is no backup.");
});

app.Run();
return 0;

static IResult Problem(int status, string message) => Results.Json(new { error = message }, statusCode: status);

static string? BearerOf(HttpContext http)
{
    var header = http.Request.Headers.Authorization.ToString();
    return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..].Trim() : null;
}

// A page for a browser: never kept, never framed by another site, and with nothing it could load from anywhere.
static IResult Html(HttpContext http, string page, int status = StatusCodes.Status200OK)
{
    var headers = http.Response.Headers;
    headers.CacheControl = "no-store";
    headers.ContentSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'; frame-ancestors 'none'; base-uri 'none'";
    headers.XFrameOptions = "DENY";
    headers.XContentTypeOptions = "nosniff";
    headers["Referrer-Policy"] = "no-referrer";
    return Results.Content(page, "text/html; charset=utf-8", statusCode: status);
}

internal sealed record TokenRequest(string? Code, string? Verifier);

/// <summary>For the tests, which start the service in memory.</summary>
public partial class Program
{
    private const string NotSignedIn = "This device is not signed in. Sign in again.";
}
