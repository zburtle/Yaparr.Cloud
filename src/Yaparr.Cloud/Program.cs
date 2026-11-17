using System.Globalization;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Http.Features;
using Yaparr.Cloud;

// "accounts ...", "codes ..." and "backups ..." look after who may use the service and where its backups are kept, and then exit; anything else runs the service.
if (args.Length > 0 && (args[0].Equals("accounts", StringComparison.OrdinalIgnoreCase) || args[0].Equals("codes", StringComparison.OrdinalIgnoreCase)
                        || args[0].Equals("backups", StringComparison.OrdinalIgnoreCase) || args[0].Equals("dumps", StringComparison.OrdinalIgnoreCase)))
{
    var config = new ConfigurationBuilder().SetBasePath(AppContext.BaseDirectory).AddJsonFile("appsettings.json", optional: true).AddEnvironmentVariables().Build();
    var settings = config.GetSection("Service").Get<ServiceOptions>() ?? new ServiceOptions();
    if (args[0].Equals("backups", StringComparison.OrdinalIgnoreCase)) return await AdminCommands.RunBackupsAsync(args[1..], settings, Console.Out);
    if (args[0].Equals("dumps", StringComparison.OrdinalIgnoreCase)) return await AdminCommands.RunDumpsAsync(args[1..], settings, Console.Out, Console.OpenStandardInput(), Console.OpenStandardOutput());
    return args[0].Equals("codes", StringComparison.OrdinalIgnoreCase)
        ? await AdminCommands.RunCodesAsync(args[1..], settings, Console.Out)
        : await AdminCommands.RunAsync(args[1..], settings, Console.Out);
}

var builder = WebApplication.CreateBuilder(args);
var options = builder.Configuration.GetSection("Service").Get<ServiceOptions>() ?? new ServiceOptions();
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<IBackupStore>(_ => Stores.Backups(options));
builder.Services.AddSingleton<IAccounts>(_ => Stores.Accounts(options));
builder.Services.AddSingleton<ISignupCodes>(_ => Stores.Codes(options));
builder.Services.AddSingleton<IPushRegistry>(_ => Stores.Push(options));
builder.Services.AddSingleton<ISyncStore>(_ => Stores.Sync(options));
builder.Services.AddSingleton<IPushSender>(_ =>
{
    var http = PushHttp.CreateSafeClient();
    return new PushSender(http, FcmClient.FromFile(options.PushFcmServiceAccountFile, new HttpClient { Timeout = TimeSpan.FromSeconds(10) }));
});
builder.Services.AddSingleton<WakeLimiter>();
builder.Services.AddSingleton<WriteLimiter>();
builder.Services.AddSingleton<LoginFlow>();
builder.Services.AddSingleton<SignInHandoff>();
builder.Services.AddSingleton<LoginLimiter>();
builder.Services.AddSingleton<AdminSessions>();
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

app.MapPost("/login", async (HttpContext http, IAccounts accounts, LoginFlow flow, LoginLimiter limiter) =>
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
app.MapPost("/v1/token", async (HttpContext http, IAccounts accounts, LoginFlow flow) =>
{
    TokenRequest? body;
    try { body = await http.Request.ReadFromJsonAsync<TokenRequest>(); }
    catch (Exception ex) when (ex is JsonException or InvalidOperationException) { body = null; }
    if (flow.Redeem(body?.Code, body?.Verifier) is not { } signedIn || accounts.IssueToken(signedIn.User, signedIn.Device) is not { } token)
        return Problem(StatusCodes.Status400BadRequest, "That sign-in has expired or was already used. Sign in again.");
    return Results.Json(new { token, username = signedIn.User });
});

// The app opened from the "You're in" page trades the code in its button for a token (see SignInHandoff).
app.MapPost("/v1/token/handoff", async (HttpContext http, IAccounts accounts, SignInHandoff handoff) =>
{
    HandoffRequest? body;
    try { body = await http.Request.ReadFromJsonAsync<HandoffRequest>(); }
    catch (Exception ex) when (ex is JsonException or InvalidOperationException) { body = null; }
    if (handoff.Redeem(body?.Code) is not { } user || accounts.IssueToken(user, "Yaparr, opened from the sign-up page") is not { } token)
        return Problem(StatusCodes.Status400BadRequest, "That sign-in has expired or was already used. Sign in from Yaparr's settings instead.");
    return Results.Json(new { token, username = user });
});

// Signing a device out.
app.MapDelete("/v1/token", (HttpContext http, IAccounts accounts) =>
    BearerOf(http) is { } token && accounts.RevokeToken(token) ? Results.NoContent() : Problem(StatusCodes.Status401Unauthorized, NotSignedIn));

app.MapGet("/account/password", (HttpContext http) => Html(http, Pages.ChangePassword("", null)));

app.MapPost("/account/password", async (HttpContext http, IAccounts accounts, LoginLimiter limiter) =>
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

// Deleting an account, by its owner: the email address, the password and the words typed. It removes the account and everything held for it (AccountErasure).
app.MapGet("/account/delete", (HttpContext http) => Html(http, Pages.DeleteAccount("", null)));

app.MapPost("/account/delete", async (HttpContext http, IAccounts accounts, LoginLimiter limiter, IBackupStore backups, IPushRegistry push, ISyncStore sync, ILoggerFactory logs) =>
{
    var form = await http.Request.ReadFormAsync();
    var typed = form["username"].ToString();
    var current = form["current"].ToString();
    IResult Again(string problem, int status) => Html(http, Pages.DeleteAccount(typed, problem), status);

    if (current.Length > 1000) return Again("That password is too long.", StatusCodes.Status400BadRequest);
    if (AccountDirectory.Normalize(typed) is not { } name) return Again("The username or password is wrong.", StatusCodes.Status401Unauthorized);
    if (limiter.IsBlocked(name)) return Again("Too many wrong tries. Wait a quarter of an hour and try again.", StatusCodes.Status429TooManyRequests);
    if (!await Task.Run(() => accounts.CheckPassword(name, current)))
    {
        limiter.Failed(name);
        return Again("The username or password is wrong.", StatusCodes.Status401Unauthorized);
    }
    limiter.Succeeded(name);
    if (!string.Equals(form["confirm"].ToString().Trim(), Pages.DeleteWords, StringComparison.OrdinalIgnoreCase))
        return Again($"Type \"{Pages.DeleteWords}\" in the last box to confirm. Nothing was deleted.", StatusCodes.Status400BadRequest);
    // An admin has to be taken off the portal first, on the server, so that the last one is not deleted by mistake.
    if (accounts.IsAdmin(name))
        return Again("This account can sign in to the admin portal. Ask whoever runs this server to take that away first.", StatusCodes.Status409Conflict);
    await AccountErasure.EraseAsync(name, accounts, backups, push, sync);
    // Logged without the name: only that it happened.
    logs.CreateLogger("Yaparr.Cloud.Accounts").LogInformation("An account was deleted by its owner");
    return Html(http, Pages.Message("Your account is deleted", "Your backup, synced settings and wake-up registrations are gone from this server, and every device is signed out."));
}).DisableAntiforgery();

// ---- signing up, with a code from a link (the page on the website posts here) ----

// The website's page collects an email address and the code from the link and posts them here, so the code goes to this server and not to the
// website, and the password is chosen on this server's own page below and never passes through the website.
app.MapPost("/signup", async (HttpContext http, IAccounts accounts, ISignupCodes codes, LoginLimiter limiter) =>
{
    var form = await http.Request.ReadFormAsync();
    var who = "ip:" + (http.Connection.RemoteIpAddress?.ToString() ?? "unknown");
    if (limiter.IsBlocked(who))
        return Html(http, Pages.Message("Too many tries", "Wait a quarter of an hour and try again."), StatusCodes.Status429TooManyRequests);

    const string badCode = "This sign-up link does not work. It may have been used up, expired or taken back. Ask the person who sent it for a new one.";
    if (AccountDirectory.Normalize(form["email"].ToString()) is not { } email)
        return Html(http, Pages.Message("That email address does not look right", "Go back and check it."), StatusCodes.Status400BadRequest);
    if (codes.Check(form["code"].ToString(), email) is null)
    {
        limiter.Failed(who);
        return Html(http, Pages.Message("This link does not work", badCode), StatusCodes.Status403Forbidden);
    }
    if (accounts.HasPassword(email))
        return Html(http, Pages.Message("You already have an account", "That email address already has an account here. Open Yaparr and sign in to the backup server with it."), StatusCodes.Status409Conflict);

    // Counted against the code once for a person, even if they come back to finish.
    if (codes.Use(form["code"].ToString(), email) is not { } codeId || accounts.StartSignup(email, codeId) is not { } token)
    {
        limiter.Failed(who);
        return Html(http, Pages.Message("This link does not work", badCode), StatusCodes.Status403Forbidden);
    }
    return Html(http, Pages.SetPassword(token, email, null));
}).DisableAntiforgery();

app.MapPost("/account/setup", async (HttpContext http, IAccounts accounts, SignInHandoff handoff) =>
{
    var form = await http.Request.ReadFormAsync();
    var token = form["token"].ToString();
    string password = form["password"].ToString(), again = form["again"].ToString();
    if (accounts.SetupFor(token) is not { } email)
        return Html(http, Pages.Message("This page has run out", "Go back to your sign-up link and start again."), StatusCodes.Status400BadRequest);

    IResult Retry(string problem) => Html(http, Pages.SetPassword(token, email, problem), StatusCodes.Status400BadRequest);
    if (Passwords.Check(password) is { } weak) return Retry(weak);
    if (password.Length > 1000) return Retry("That password is too long.");
    if (password != again) return Retry("The two passwords are not the same.");
    if (await Task.Run(() => accounts.FinishSetup(token, password)) is not { } done)
        return Html(http, Pages.Message("This page has run out", "Go back to your sign-up link and start again."), StatusCodes.Status400BadRequest);

    var server = options.PublicUrl.Length > 0 ? options.PublicUrl.TrimEnd('/') : $"{http.Request.Scheme}://{http.Request.Host}";
    // A button that opens the app already signed in (see SignInHandoff). Not on a phone: the Android app has no backup server yet.
    var phone = System.Text.RegularExpressions.Regex.IsMatch(http.Request.Headers.UserAgent.ToString(), "Android|iPhone|iPad|iPod|Mobile", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    return Html(http, Pages.Welcome(done, server, phone ? null : SignInHandoff.AppLink(handoff.Create(done), server)));
}).DisableAntiforgery();

// ---- the admin portal, for the person running the service (see AdminPortal) ----

app.MapAdminPortal(options);

// ---- waking phones for push notifications (see PushApi) ----

app.MapPush(options);
app.MapSync(options);

// ---- the backup, for an app that is signed in (Authorization: Bearer <token>) ----

var backup = app.MapGroup("/v1/backup");

backup.MapPut("", async (HttpContext http, IBackupStore store, WriteLimiter writes, IAccounts accounts, CancellationToken cancel) =>
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

backup.MapGet("", async (HttpContext http, IBackupStore store, IAccounts accounts, int? version, CancellationToken cancel) =>
{
    if (accounts.UserOfToken(BearerOf(http)) is not { } user) return Problem(StatusCodes.Status401Unauthorized, NotSignedIn);
    var found = await store.OpenAsync(BackupStore.FolderOf(user), version, cancel);
    if (found == null) return Problem(StatusCodes.Status404NotFound, "There is no backup.");
    http.Response.Headers["X-Yaparr-Version"] = found.Value.Info.Version.ToString(CultureInfo.InvariantCulture);
    http.Response.ContentLength = found.Value.Info.Size;
    return Results.Stream(found.Value.Content, "application/octet-stream");
});

backup.MapGet("/versions", async (HttpContext http, IBackupStore store, IAccounts accounts, CancellationToken cancel) =>
    accounts.UserOfToken(BearerOf(http)) is { } user
        ? Results.Json(new { username = user, versions = (await store.VersionsAsync(BackupStore.FolderOf(user), cancel)).Select(v => new { version = v.Version, size = v.Size, at = v.At }) })
        : Problem(StatusCodes.Status401Unauthorized, NotSignedIn));

backup.MapDelete("", async (HttpContext http, IBackupStore store, IAccounts accounts, CancellationToken cancel) =>
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
static IResult Html(HttpContext http, string page, int status = StatusCodes.Status200OK) => Responses.Html(http, page, status);

internal sealed record TokenRequest(string? Code, string? Verifier);

internal sealed record HandoffRequest(string? Code);

/// <summary>For the tests, which start the service in memory.</summary>
public partial class Program
{
    private const string NotSignedIn = "This device is not signed in. Sign in again.";
}
