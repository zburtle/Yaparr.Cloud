using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Yaparr.Cloud;

namespace Yaparr.Cloud.Tests;

/// <summary>Records the wake-ups the service sends, and answers as a test says.</summary>
internal sealed class FakePushSender : IPushSender
{
    public List<PushTarget> Sent { get; } = new();
    public PushResult Answer { get; set; } = PushResult.Sent;
    public bool FcmAvailable { get; set; } = true;

    public Task<PushResult> SendAsync(PushTarget target, CancellationToken cancel)
    {
        Sent.Add(target);
        return Task.FromResult(Answer);
    }
}

/// <summary>Keeps what is logged, so a test can look at it.</summary>
internal sealed class LogCapture : ILoggerProvider
{
    public List<string> Lines { get; } = new();
    public ILogger CreateLogger(string categoryName) => new Capture(this);
    public void Dispose() { }

    private sealed class Capture(LogCapture owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (owner.Lines) owner.Lines.Add(formatter(state, exception));
        }
    }
}

/// <summary>A handler that answers every request as a test says and keeps what it was asked.</summary>
internal sealed class FakeHandler : HttpMessageHandler
{
    public List<(HttpRequestMessage Request, string Body)> Requests { get; } = new();
    public Func<HttpRequestMessage, HttpResponseMessage> Answer { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.OK);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
    {
        Requests.Add((request, request.Content == null ? "" : await request.Content.ReadAsStringAsync(cancel)));
        return Answer(request);
    }
}

public sealed class PushApiTests : ServiceTest
{
    private readonly FakePushSender _sender = new();

    private HttpClient Run(Dictionary<string, string>? settings = null) =>
        Start(settings, services =>
        {
            foreach (var d in services.Where(d => d.ServiceType == typeof(IPushSender)).ToList()) services.Remove(d);
            services.AddSingleton<IPushSender>(_sender);
        });

    private static StringContent Body(object body) => new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    private async Task<string> RegisterDevice(HttpClient client, string token, string deviceId = "phone", string kind = "unifiedpush", string endpoint = "https://ntfy.example.org/upabc")
    {
        var request = new HttpRequestMessage(HttpMethod.Put, "/v1/push") { Content = Body(new { deviceId, kind, endpoint }) };
        request.Headers.Authorization = new("Bearer", token);
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
    }

    [Fact]
    public async Task A_signed_in_device_registers_and_anyone_with_its_token_can_wake_it()
    {
        var client = Run();
        var token = Register("sam@example.com");
        var wake = await RegisterDevice(client, token);

        // No account, no sign in: the token is the permission.
        var response = await client.PostAsync("/v1/push/wake/" + wake, null);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var sent = Assert.Single(_sender.Sent);
        Assert.Equal(("sam@example.com", PushKind.UnifiedPush, "https://ntfy.example.org/upabc"), (sent.Account, sent.Kind, sent.Endpoint));
    }

    [Fact]
    public async Task A_wake_is_logged_by_its_outcome_alone_and_nothing_about_anyone()
    {
        var logs = new LogCapture();
        var client = Start(null, services =>
        {
            foreach (var d in services.Where(d => d.ServiceType == typeof(IPushSender)).ToList()) services.Remove(d);
            services.AddSingleton<IPushSender>(_sender);
            services.AddSingleton<ILoggerProvider>(logs);
        });
        var token = Register("sam@example.com");
        var wake = await RegisterDevice(client, token, deviceId: "sams-phone", endpoint: "https://ntfy.example.org/upabc");
        await client.PostAsync("/v1/push/wake/" + wake, null);
        await client.PostAsync("/v1/push/wake/" + WakeTokens.Make(), null);

        string all;
        lock (logs.Lines) all = string.Join(" | ", logs.Lines);
        Assert.Contains("wake: sent (unifiedpush)", all);
        Assert.Contains("wake: no such token", all);
        foreach (var private_ in new[] { wake, token, "sam@example.com", "sams-phone", "ntfy.example.org", "upabc" })
            Assert.DoesNotContain(private_, all);
    }

    [Fact]
    public async Task Registering_needs_a_signed_in_device_and_a_good_address()
    {
        var client = Run();
        var token = Register("sam@example.com");
        async Task<HttpStatusCode> Put(string? bearer, object body)
        {
            var request = new HttpRequestMessage(HttpMethod.Put, "/v1/push") { Content = Body(body) };
            if (bearer != null) request.Headers.Authorization = new("Bearer", bearer);
            return (await client.SendAsync(request)).StatusCode;
        }
        Assert.Equal(HttpStatusCode.Unauthorized, await Put(null, new { deviceId = "p", kind = "unifiedpush", endpoint = "https://ntfy.example.org/x" }));
        Assert.Equal(HttpStatusCode.Unauthorized, await Put("not-a-token", new { deviceId = "p", kind = "unifiedpush", endpoint = "https://ntfy.example.org/x" }));
        foreach (var bad in new[] { "http://ntfy.example.org/x", "https://127.0.0.1/x", "https://localhost/x", "https://10.0.0.5/x", "https://user:pw@ntfy.example.org/x", "https://intranet/x", "https://ntfy.example.org:22/x", "ftp://ntfy.example.org/x", "", "not a url" })
            Assert.Equal(HttpStatusCode.BadRequest, await Put(token, new { deviceId = "p", kind = "unifiedpush", endpoint = bad }));
        Assert.Equal(HttpStatusCode.BadRequest, await Put(token, new { deviceId = "has space", kind = "unifiedpush", endpoint = "https://ntfy.example.org/x" }));
        Assert.Equal(HttpStatusCode.BadRequest, await Put(token, new { deviceId = "p", kind = "carrier-pigeon", endpoint = "https://ntfy.example.org/x" }));
        Assert.Equal(HttpStatusCode.Created, await Put(token, new { deviceId = "p", kind = "unifiedpush", endpoint = "https://ntfy.example.org/x" }));
    }

    [Fact]
    public async Task Registering_again_keeps_the_token_and_says_so()
    {
        var client = Run();
        var token = Register("sam@example.com");
        var wake = await RegisterDevice(client, token);
        var request = new HttpRequestMessage(HttpMethod.Put, "/v1/push") { Content = Body(new { deviceId = "phone", kind = "unifiedpush", endpoint = "https://ntfy.example.org/other" }) };
        request.Headers.Authorization = new("Bearer", token);
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(JsonValueKind.Null, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").ValueKind);

        await client.PostAsync("/v1/push/wake/" + wake, null);
        Assert.Equal("https://ntfy.example.org/other", _sender.Sent.Single().Endpoint);
    }

    [Fact]
    public async Task Firebase_is_only_taken_when_the_service_has_it()
    {
        var client = Run();
        var token = Register("sam@example.com");
        _sender.FcmAvailable = false;
        var request = new HttpRequestMessage(HttpMethod.Put, "/v1/push") { Content = Body(new { deviceId = "p", kind = "fcm", endpoint = "device-token" }) };
        request.Headers.Authorization = new("Bearer", token);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(request)).StatusCode);
        Assert.False((await client.GetFromJsonAsync<JsonElement>("/v1/push/options")).GetProperty("fcm").GetBoolean());

        _sender.FcmAvailable = true;
        request = new HttpRequestMessage(HttpMethod.Put, "/v1/push") { Content = Body(new { deviceId = "p", kind = "fcm", endpoint = "device-token" }) };
        request.Headers.Authorization = new("Bearer", token);
        Assert.Equal(HttpStatusCode.Created, (await client.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task A_token_that_does_not_exist_leads_nowhere_and_nothing_is_sent()
    {
        var client = Run();
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/v1/push/wake/" + WakeTokens.Make(), null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/v1/push/wake/short", null)).StatusCode);
        Assert.Empty(_sender.Sent);
    }

    [Fact]
    public async Task A_token_can_only_wake_so_often()
    {
        var client = Run(new() { ["PushWakesPerMinute"] = "3", ["PushWakesPerDay"] = "100" });
        var wake = await RegisterDevice(client, Register("sam@example.com"));
        for (int i = 0; i < 3; i++) Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsync("/v1/push/wake/" + wake, null)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.PostAsync("/v1/push/wake/" + wake, null)).StatusCode);
        Assert.Equal(3, _sender.Sent.Count);
    }

    [Fact]
    public async Task A_new_token_cuts_off_whoever_had_the_old_one()
    {
        var client = Run();
        var token = Register("sam@example.com");
        var old = await RegisterDevice(client, token);
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/push/phone/rotate");
        request.Headers.Authorization = new("Bearer", token);
        var fresh = (await (await client.SendAsync(request)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
        Assert.NotEqual(old, fresh);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/v1/push/wake/" + old, null)).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsync("/v1/push/wake/" + fresh, null)).StatusCode);
    }

    [Fact]
    public async Task Signing_a_device_out_of_push_stops_the_wake_ups()
    {
        var client = Run();
        var token = Register("sam@example.com");
        var wake = await RegisterDevice(client, token);
        var delete = new HttpRequestMessage(HttpMethod.Delete, "/v1/push/phone");
        delete.Headers.Authorization = new("Bearer", token);
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(delete)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/v1/push/wake/" + wake, null)).StatusCode);
    }

    [Fact]
    public async Task A_push_service_that_says_the_address_is_gone_removes_the_registration()
    {
        var client = Run();
        var wake = await RegisterDevice(client, Register("sam@example.com"));
        _sender.Answer = PushResult.Gone;
        Assert.Equal(HttpStatusCode.Gone, (await client.PostAsync("/v1/push/wake/" + wake, null)).StatusCode);
        _sender.Answer = PushResult.Sent;
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/v1/push/wake/" + wake, null)).StatusCode);

        var again = await RegisterDevice(client, Register("pat@example.com"));
        _sender.Answer = PushResult.Failed;
        Assert.Equal(HttpStatusCode.BadGateway, (await client.PostAsync("/v1/push/wake/" + again, null)).StatusCode);
        _sender.Answer = PushResult.Sent;
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsync("/v1/push/wake/" + again, null)).StatusCode);   // a failure does not forget the device
    }

    [Fact]
    public async Task An_account_that_was_removed_cannot_be_woken_and_one_account_has_a_limit_of_devices()
    {
        var client = Run(new() { ["PushMaxPerAccount"] = "2" });
        var token = Register("sam@example.com");
        var wake = await RegisterDevice(client, token, "a");
        await RegisterDevice(client, token, "b");
        var third = new HttpRequestMessage(HttpMethod.Put, "/v1/push") { Content = Body(new { deviceId = "c", kind = "unifiedpush", endpoint = "https://ntfy.example.org/c" }) };
        third.Headers.Authorization = new("Bearer", token);
        Assert.Equal(HttpStatusCode.Conflict, (await client.SendAsync(third)).StatusCode);

        Assert.True(new AccountDirectory(Options).Remove("sam@example.com"));
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/v1/push/wake/" + wake, null)).StatusCode);
        Assert.Empty(_sender.Sent);
    }

    [Fact]
    public async Task The_list_of_devices_shows_no_tokens_or_addresses()
    {
        var client = Run();
        var token = Register("sam@example.com");
        var wake = await RegisterDevice(client, token, endpoint: "https://ntfy.example.org/very-secret-topic");
        var request = new HttpRequestMessage(HttpMethod.Get, "/v1/push");
        request.Headers.Authorization = new("Bearer", token);
        var text = await (await client.SendAsync(request)).Content.ReadAsStringAsync();
        Assert.Contains("phone", text);
        Assert.DoesNotContain(wake, text);
        Assert.DoesNotContain("secret-topic", text);
    }
}

public sealed class PushSenderTests
{
    private static PushTarget Up(string endpoint = "https://ntfy.example.org/upabc") => new("sam@example.com", "phone", PushKind.UnifiedPush, endpoint);

    [Fact]
    public async Task A_wake_up_is_a_post_with_nothing_in_it_about_who_or_what()
    {
        var handler = new FakeHandler();
        var sender = new PushSender(new HttpClient(handler), null);
        Assert.Equal(PushResult.Sent, await sender.SendAsync(Up(), default));
        var (request, body) = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://ntfy.example.org/upabc", request.RequestUri!.ToString());
        Assert.Equal("{\"t\":\"wake\"}", body);
        Assert.DoesNotContain("sam", body);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, PushResult.Gone)]
    [InlineData(HttpStatusCode.Gone, PushResult.Gone)]
    [InlineData(HttpStatusCode.InternalServerError, PushResult.Failed)]
    [InlineData(HttpStatusCode.TooManyRequests, PushResult.Failed)]
    [InlineData(HttpStatusCode.Redirect, PushResult.Failed)]
    public async Task What_the_push_service_answers_is_told_apart(HttpStatusCode answer, PushResult expected)
    {
        var sender = new PushSender(new HttpClient(new FakeHandler { Answer = _ => new HttpResponseMessage(answer) }), null);
        Assert.Equal(expected, await sender.SendAsync(Up(), default));
    }

    [Fact]
    public async Task A_push_service_that_cannot_be_reached_is_a_failure_not_a_crash()
    {
        var sender = new PushSender(new HttpClient(new FakeHandler { Answer = _ => throw new HttpRequestException("down") }), null);
        Assert.Equal(PushResult.Failed, await sender.SendAsync(Up(), default));
    }

    [Fact]
    public async Task An_address_that_would_not_be_accepted_now_is_never_called()
    {
        var handler = new FakeHandler();
        var sender = new PushSender(new HttpClient(handler), null);
        Assert.Equal(PushResult.Gone, await sender.SendAsync(Up("http://169.254.169.254/latest/meta-data"), default));
        Assert.Empty(handler.Requests);
        Assert.Equal(PushResult.Failed, await sender.SendAsync(new PushTarget("a", "b", PushKind.Fcm, "token"), default));   // no Firebase here
    }

    [Theory]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.1.2.3", false)]
    [InlineData("172.16.0.1", false)]
    [InlineData("172.32.0.1", true)]
    [InlineData("192.168.1.1", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("fd00::1", false)]
    [InlineData("::ffff:10.0.0.1", false)]
    [InlineData("93.184.216.34", true)]
    [InlineData("2606:2800:220:1:248:1893:25c8:1946", true)]
    public void Only_addresses_on_the_internet_are_public(string address, bool expected) => Assert.Equal(expected, PushEndpoints.IsPublic(IPAddress.Parse(address)));

    [Fact]
    public async Task Firebase_is_asked_with_a_signed_request_and_the_token_it_gives_is_kept()
    {
        var dir = Path.Combine(Path.GetTempPath(), "yaparr-fcm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var rsa = RSA.Create(2048);
            var file = Path.Combine(dir, "account.json");
            File.WriteAllText(file, JsonSerializer.Serialize(new { client_email = "push@proj.iam.gserviceaccount.com", private_key = rsa.ExportPkcs8PrivateKeyPem(), project_id = "proj", token_uri = "https://oauth.example.org/token" }));
            var handler = new FakeHandler
            {
                Answer = request => request.RequestUri!.Host == "oauth.example.org"
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"access_token\":\"ya29.test\",\"expires_in\":3600}") }
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") },
            };
            var now = new DateTimeOffset(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);
            var fcm = FcmClient.FromFile(file, new HttpClient(handler), () => now)!;
            var sender = new PushSender(new HttpClient(new FakeHandler()), fcm);
            var target = new PushTarget("sam@example.com", "phone", PushKind.Fcm, "device-token-123");

            Assert.Equal(PushResult.Sent, await sender.SendAsync(target, default));
            Assert.Equal(PushResult.Sent, await sender.SendAsync(target, default));

            Assert.Equal(3, handler.Requests.Count);   // one trade for an access token, then two messages
            var exchange = handler.Requests[0];
            Assert.Contains("grant_type=urn%3Aietf%3Aparams%3Aoauth%3Agrant-type%3Ajwt-bearer", exchange.Body);
            var jwt = Uri.UnescapeDataString(exchange.Body.Split("assertion=")[1]).Split('.');
            Assert.Equal(3, jwt.Length);
            var claims = JsonDocument.Parse(Convert.FromBase64String(jwt[1].Replace('-', '+').Replace('_', '/').PadRight((jwt[1].Length + 3) / 4 * 4, '='))).RootElement;
            Assert.Equal("push@proj.iam.gserviceaccount.com", claims.GetProperty("iss").GetString());
            Assert.Equal("https://www.googleapis.com/auth/firebase.messaging", claims.GetProperty("scope").GetString());
            Assert.True(rsa.VerifyData(Encoding.ASCII.GetBytes(jwt[0] + "." + jwt[1]), Convert.FromBase64String(jwt[2].Replace('-', '+').Replace('_', '/').PadRight((jwt[2].Length + 3) / 4 * 4, '=')), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));

            var message = handler.Requests[1];
            Assert.Equal("https://fcm.googleapis.com/v1/projects/proj/messages:send", message.Request.RequestUri!.ToString());
            Assert.Equal("Bearer", message.Request.Headers.Authorization!.Scheme);
            Assert.Equal("ya29.test", message.Request.Headers.Authorization.Parameter);
            var sent = JsonDocument.Parse(message.Body).RootElement.GetProperty("message");
            Assert.Equal("device-token-123", sent.GetProperty("token").GetString());
            Assert.Equal("wake", sent.GetProperty("data").GetProperty("t").GetString());
            Assert.Equal("HIGH", sent.GetProperty("android").GetProperty("priority").GetString());
            Assert.False(sent.TryGetProperty("notification", out _));   // data only: nothing for Firebase to show or to read
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Firebase_saying_a_token_is_not_registered_means_the_device_is_gone()
    {
        var dir = Path.Combine(Path.GetTempPath(), "yaparr-fcm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var rsa = RSA.Create(2048);
            var file = Path.Combine(dir, "account.json");
            File.WriteAllText(file, JsonSerializer.Serialize(new { client_email = "a@b.c", private_key = rsa.ExportPkcs8PrivateKeyPem(), project_id = "proj" }));
            var handler = new FakeHandler
            {
                Answer = request => request.RequestUri!.Host == "oauth2.googleapis.com"
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"access_token\":\"t\",\"expires_in\":3600}") }
                    : new HttpResponseMessage(HttpStatusCode.NotFound),
            };
            var sender = new PushSender(new HttpClient(new FakeHandler()), FcmClient.FromFile(file, new HttpClient(handler))!);
            Assert.Equal(PushResult.Gone, await sender.SendAsync(new PushTarget("a", "b", PushKind.Fcm, "t"), default));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_missing_or_wrong_service_account_file_is_noticed_when_the_service_starts()
    {
        Assert.Null(FcmClient.FromFile("", new HttpClient()));
        Assert.ThrowsAny<IOException>(() => FcmClient.FromFile(Path.Combine(Path.GetTempPath(), "no-such-file-" + Guid.NewGuid().ToString("N") + ".json"), new HttpClient()));
    }

    [Fact]
    public void The_wake_limiter_counts_a_minute_and_a_day()
    {
        var now = new DateTimeOffset(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);
        var limiter = new WakeLimiter(() => now);
        for (int i = 0; i < 2; i++) Assert.True(limiter.TryTake("t", 2, 3));
        Assert.False(limiter.TryTake("t", 2, 3));
        Assert.True(limiter.TryTake("other", 2, 3));
        now += TimeSpan.FromMinutes(2);
        Assert.True(limiter.TryTake("t", 2, 3));       // a minute on: the third of the day
        now += TimeSpan.FromMinutes(2);
        Assert.False(limiter.TryTake("t", 2, 3));      // the day's three are used
        now += TimeSpan.FromHours(24);
        Assert.True(limiter.TryTake("t", 2, 3));
    }
}
