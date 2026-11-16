using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Yaparr.Cloud;

public enum PushResult { Sent, Gone, Failed }

/// <summary>Sends a wake-up. What is sent has no content: it only tells the phone to connect and look.</summary>
public interface IPushSender
{
    /// <summary>Whether wake-ups can go through Firebase on this service (it has been given a service account).</summary>
    bool FcmAvailable { get; }

    Task<PushResult> SendAsync(PushTarget target, CancellationToken cancel);
}

/// <summary>
/// What a UnifiedPush address may be. The service sends to an address a signed-in person gave it, so the address is held to what a push service's is: https,
/// a name (not a number), a normal port, no password in it. Whether the name leads somewhere public is checked again when it is connected to (see <see cref="PushHttp"/>).
/// </summary>
public static class PushEndpoints
{
    /// <summary>Why an address cannot be used, or null if it can.</summary>
    public static string? Problem(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint) || endpoint.Length > 500) return "The push address is missing or too long.";
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return "The push address must be an https address.";
        if (uri.UserInfo.Length > 0) return "The push address must not hold a name or password.";
        if (uri.HostNameType != UriHostNameType.Dns) return "The push address must use a name, not a number.";
        if (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || !uri.Host.Contains('.') || uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase)) return "The push address must be a public name.";
        if (uri.Port is not (443 or >= 1024)) return "The push address uses a port that is not allowed.";
        return null;
    }

    /// <summary>Whether an address leads somewhere on the internet and not to this machine or its network.</summary>
    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.None)) return false;
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast) return false;
            var bytes = address.GetAddressBytes();
            return (bytes[0] & 0xFE) != 0xFC;   // fc00::/7, the private range
        }
        var b = address.GetAddressBytes();
        return !(b[0] == 10 || b[0] == 127 || b[0] == 0 || b[0] >= 224
                 || b[0] == 172 && b[1] is >= 16 and <= 31
                 || b[0] == 192 && b[1] == 168
                 || b[0] == 169 && b[1] == 254
                 || b[0] == 100 && b[1] is >= 64 and <= 127);   // carrier-grade NAT
    }
}

/// <summary>The HTTP client that talks to push services: no redirects, a short time, and it only connects to public addresses.</summary>
public static class PushHttp
{
    public static HttpClient CreateSafeClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            ConnectCallback = async (context, cancel) =>
            {
                // The name is resolved here, and the connection is made to an address that was checked, so a name cannot be made to answer with a private address after the check.
                var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancel);
                var chosen = addresses.FirstOrDefault(PushEndpoints.IsPublic) ?? throw new HttpRequestException("The push address does not lead to a public address.");
                var socket = new Socket(chosen.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(chosen, context.DnsEndPoint.Port), cancel);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10), MaxResponseContentBufferSize = 64 * 1024 };
    }
}

/// <summary>Sends wake-ups through UnifiedPush (a POST to the phone's address) and, when the service has been given a Firebase service account, through Firebase.</summary>
public sealed class PushSender : IPushSender
{
    /// <summary>What the push carries: nothing about who or what, only that something is waiting.</summary>
    internal static readonly byte[] WakeBody = Encoding.ASCII.GetBytes("{\"t\":\"wake\"}");

    private readonly HttpClient _unifiedPush;
    private readonly FcmClient? _fcm;

    public PushSender(HttpClient unifiedPush, FcmClient? fcm)
    {
        _unifiedPush = unifiedPush;
        _fcm = fcm;
    }

    public bool FcmAvailable => _fcm != null;

    public async Task<PushResult> SendAsync(PushTarget target, CancellationToken cancel)
    {
        try
        {
            return target.Kind == PushKind.Fcm
                ? _fcm == null ? PushResult.Failed : await _fcm.SendAsync(target.Endpoint, cancel)
                : await SendUnifiedPushAsync(target.Endpoint, cancel);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or JsonException or InvalidOperationException or CryptographicException)
        {
            return PushResult.Failed;
        }
    }

    private async Task<PushResult> SendUnifiedPushAsync(string endpoint, CancellationToken cancel)
    {
        if (PushEndpoints.Problem(endpoint) != null) return PushResult.Gone;
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new ByteArrayContent(WakeBody) };
        request.Content.Headers.ContentType = new("application/json");
        request.Headers.TryAddWithoutValidation("TTL", "60");
        request.Headers.TryAddWithoutValidation("Urgency", "high");
        using var response = await _unifiedPush.SendAsync(request, cancel);
        if (response.IsSuccessStatusCode) return PushResult.Sent;
        // The distributor says the address is no longer one (the app was removed, or the person signed out of the push service).
        return response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone ? PushResult.Gone : PushResult.Failed;
    }
}

/// <summary>Firebase Cloud Messaging (the HTTP v1 API) with a service account: a signed token is traded for an access token, which is kept until it runs out.</summary>
public sealed class FcmClient
{
    private sealed record Account(string ClientEmail, string PrivateKey, string ProjectId, string TokenUri);

    private readonly HttpClient _http;
    private readonly Account _account;
    private readonly Func<DateTimeOffset> _now;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _access;
    private DateTimeOffset _accessUntil;

    private FcmClient(HttpClient http, Account account, Func<DateTimeOffset> now)
    {
        _http = http;
        _account = account;
        _now = now;
    }

    /// <summary>Reads a service account file (the JSON key downloaded from Firebase). Null if the path is empty; throws if the file is not one.</summary>
    public static FcmClient? FromFile(string path, HttpClient http, Func<DateTimeOffset>? now = null)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var json = JsonDocument.Parse(File.ReadAllText(path)).RootElement;
        string Get(string name) => json.TryGetProperty(name, out var value) && value.GetString() is { Length: > 0 } text ? text : throw new InvalidOperationException($"The Firebase service account file has no {name}.");
        var account = new Account(Get("client_email"), Get("private_key"), Get("project_id"), json.TryGetProperty("token_uri", out var uri) ? uri.GetString() ?? "https://oauth2.googleapis.com/token" : "https://oauth2.googleapis.com/token");
        return new FcmClient(http, account, now ?? (() => DateTimeOffset.UtcNow));
    }

    private static string B64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>The signed request for an access token (a JWT, RS256).</summary>
    internal string SignedRequest()
    {
        var issued = _now().ToUnixTimeSeconds();
        var header = B64(Encoding.UTF8.GetBytes("{\"alg\":\"RS256\",\"typ\":\"JWT\"}"));
        var claims = B64(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["iss"] = _account.ClientEmail,
            ["scope"] = "https://www.googleapis.com/auth/firebase.messaging",
            ["aud"] = _account.TokenUri,
            ["iat"] = issued,
            ["exp"] = issued + 3600,
        }));
        using var rsa = RSA.Create();
        rsa.ImportFromPem(_account.PrivateKey);
        var signature = rsa.SignData(Encoding.ASCII.GetBytes(header + "." + claims), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return header + "." + claims + "." + B64(signature);
    }

    private async Task<string> AccessTokenAsync(CancellationToken cancel)
    {
        await _gate.WaitAsync(cancel);
        try
        {
            if (_access != null && _now() < _accessUntil - TimeSpan.FromMinutes(2)) return _access;
            using var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer",
                ["assertion"] = SignedRequest(),
            });
            using var response = await _http.PostAsync(_account.TokenUri, form, cancel);
            response.EnsureSuccessStatusCode();
            var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancel)).RootElement;
            _access = body.GetProperty("access_token").GetString() ?? throw new InvalidOperationException("Google gave no access token.");
            _accessUntil = _now() + TimeSpan.FromSeconds(body.TryGetProperty("expires_in", out var seconds) ? seconds.GetInt32() : 3000);
            return _access;
        }
        finally { _gate.Release(); }
    }

    /// <summary>A high priority message with data and no notification, which Android lets start the app for a moment.</summary>
    internal static byte[] MessageFor(string deviceToken) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        message = new
        {
            token = deviceToken,
            data = new { t = "wake" },
            android = new { priority = "HIGH", ttl = "60s" },
        },
    });

    public async Task<PushResult> SendAsync(string deviceToken, CancellationToken cancel)
    {
        var access = await AccessTokenAsync(cancel);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://fcm.googleapis.com/v1/projects/{Uri.EscapeDataString(_account.ProjectId)}/messages:send")
        {
            Content = new ByteArrayContent(MessageFor(deviceToken)),
        };
        request.Content.Headers.ContentType = new("application/json");
        request.Headers.Authorization = new("Bearer", access);
        using var response = await _http.SendAsync(request, cancel);
        if (response.IsSuccessStatusCode) return PushResult.Sent;
        // 404 is Firebase saying the token is not registered any more (the app was removed or its data cleared).
        if (response.StatusCode == HttpStatusCode.Unauthorized) { _access = null; }
        return response.StatusCode == HttpStatusCode.NotFound ? PushResult.Gone : PushResult.Failed;
    }
}
