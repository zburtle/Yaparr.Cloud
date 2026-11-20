using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;

namespace Yaparr.Cloud;

/// <summary>Who is using the gateway and how much, so that one account cannot take it over: connections for each account and altogether, and bytes in a day for each account.</summary>
public sealed class GatewayRegistry
{
    private readonly ConcurrentDictionary<string, int> _connections = new();
    private readonly ConcurrentDictionary<string, (long Day, long Bytes)> _days = new();
    private readonly Func<DateTimeOffset> _now;
    private int _total;

    public GatewayRegistry() : this(() => DateTimeOffset.UtcNow) { }
    internal GatewayRegistry(Func<DateTimeOffset> now) => _now = now;

    public int Total => Volatile.Read(ref _total);

    /// <summary>Takes a place for one more connection, if the account and the service both have one.</summary>
    public bool TryEnter(string account, int perAccount, int altogether)
    {
        lock (_connections)
        {
            if (_total >= altogether || _connections.GetValueOrDefault(account) >= perAccount) return false;
            _connections[account] = _connections.GetValueOrDefault(account) + 1;
            _total++;
            return true;
        }
    }

    public void Leave(string account)
    {
        lock (_connections)
        {
            if (!_connections.TryGetValue(account, out var n)) return;
            if (n <= 1) _connections.TryRemove(account, out _); else _connections[account] = n - 1;
            _total--;
        }
    }

    /// <summary>Counts bytes against the account's day (UTC). False, and counts nothing, if that would pass the limit.</summary>
    public bool TryAddBytes(string account, long bytes, long perDay)
    {
        var day = _now().ToUnixTimeSeconds() / 86400;
        lock (_days)
        {
            var held = _days.TryGetValue(account, out var d) && d.Day == day ? d.Bytes : 0;
            if (held + bytes > perDay) return false;
            _days[account] = (day, held + bytes);
            return true;
        }
    }
}

/// <summary>
/// The UDP gateway (see the README, "UDP gateway"): for a device on a network that blocks UDP. It opens one WebSocket on port 443, and the service sends the datagrams it is given from a
/// UDP socket of its own (the address the world then sees for the device) and hands back what arrives there. For messages and voice, not screens, so it is <b>capped</b>: a rate and a burst
/// for each direction, a size for a datagram, a day's bytes for the account, a number of different places a minute, public addresses only, and a few connections for each account.
/// The chat protocol and voice fit in those limits; screen sharing, which needs several times the speed, does not (the apps also turn the share and watch buttons off while the gateway is in use). Everything the devices send through it is already locked between them; the service sees where it goes
/// and how much, and never what it says. Off unless <see cref="ServiceOptions.GatewayEnabled"/>.
/// </summary>
internal static class GatewayApi
{
    public static void MapGateway(this WebApplication app, ServiceOptions options)
    {
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });
        app.Map("/v1/gateway", async (HttpContext http, IAccounts accounts, GatewayRegistry registry, ILoggerFactory logs) =>
        {
            if (!options.GatewayEnabled) { http.Response.StatusCode = StatusCodes.Status404NotFound; return; }
            if (!http.WebSockets.IsWebSocketRequest) { http.Response.StatusCode = StatusCodes.Status400BadRequest; return; }
            if (accounts.UserOfToken(PushApi.Bearer(http)) is not { } user) { http.Response.StatusCode = StatusCodes.Status401Unauthorized; return; }
            if (!registry.TryEnter(user, options.GatewayConnectionsPerAccount, options.GatewayMaxConnections)) { http.Response.StatusCode = StatusCodes.Status429TooManyRequests; return; }
            try
            {
                using var socket = await http.WebSockets.AcceptWebSocketAsync();
                await new GatewaySession(options, registry, user, socket, PublicHint(http, options), logs.CreateLogger("Gateway")).RunAsync(http.RequestAborted);
            }
            finally { registry.Leave(user); }
        });
    }

    // The address the world is likely to see for this connection's UDP socket: the one set for the service, or else the one the device connected to (right unless a proxy sits in front).
    private static IPAddress PublicHint(HttpContext http, ServiceOptions options) =>
        IPAddress.TryParse(options.GatewayPublicAddress, out var configured) ? configured : http.Connection.LocalIpAddress ?? IPAddress.Any;
}

internal sealed class GatewaySession(ServiceOptions options, GatewayRegistry registry, string account, WebSocket ws, IPAddress publicHint, ILogger log)
{
    private readonly SemaphoreSlim _send = new(1, 1);
    private readonly ByteBucket _up = new(options.GatewayBytesPerSecond, options.GatewayBurstBytes);
    private readonly ByteBucket _down = new(options.GatewayBytesPerSecond, options.GatewayBurstBytes);
    private readonly Dictionary<IPEndPoint, long> _destinations = new();
    private long _lastActivity = Environment.TickCount64;
    private long _notedLimit;

    public async Task RunAsync(CancellationToken cancel)
    {
        using var udp = OpenSocket(options);
        var local = (IPEndPoint)udp.LocalEndPoint!;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        try
        {
            await SendAsync(GatewayRules.Encode(GatewayRules.Hello, new IPEndPoint(publicHint, local.Port), ReadOnlySpan<byte>.Empty), cts.Token);
            var tasks = new[] { UpAsync(udp, cts.Token), DownAsync(udp, cts.Token), IdleAsync(cts.Token) };
            await Task.WhenAny(tasks);
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ObjectDisposedException or SocketException) { }
        finally
        {
            cts.Cancel();
            if (ws.State == WebSocketState.Open)
                try { await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); } catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException) { }
        }
    }

    /// <summary>One socket for both families when the system has them, on a port inside the range set for the service (so a firewall can be told which), or any port.</summary>
    private static Socket OpenSocket(ServiceOptions options)
    {
        Socket socket;
        bool dual;
        try { socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp) { DualMode = true }; dual = true; }
        catch (Exception ex) when (ex is SocketException or NotSupportedException) { socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp); dual = false; }
        var any = dual ? IPAddress.IPv6Any : IPAddress.Any;
        if (options.GatewayPortMin > 0 && options.GatewayPortMax >= options.GatewayPortMin)
        {
            int span = options.GatewayPortMax - options.GatewayPortMin + 1, start = Random.Shared.Next(span);
            for (int i = 0; i < span; i++)
            {
                try { socket.Bind(new IPEndPoint(any, options.GatewayPortMin + (start + i) % span)); return socket; }
                catch (SocketException) { }
            }
            socket.Dispose();
            throw new SocketException((int)SocketError.AddressAlreadyInUse);
        }
        socket.Bind(new IPEndPoint(any, 0));
        return socket;
    }

    private async Task SendAsync(byte[] frame, CancellationToken cancel)
    {
        await _send.WaitAsync(cancel);
        try { await ws.SendAsync(frame, WebSocketMessageType.Binary, endOfMessage: true, cancel); }
        finally { _send.Release(); }
    }

    private Task NoteLimitAsync(byte reason, CancellationToken cancel)
    {
        // One notice a second at most, so a device that keeps going over is not answered with a flood of its own.
        var now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _notedLimit) < 1000) return Task.CompletedTask;
        Interlocked.Exchange(ref _notedLimit, now);
        return SendAsync(new[] { GatewayRules.Limit, reason }, cancel);
    }

    private async Task UpAsync(Socket udp, CancellationToken cancel)
    {
        var buffer = new byte[options.GatewayMaxPacket + 64];
        while (!cancel.IsCancellationRequested)
        {
            var result = await ws.ReceiveAsync(buffer, cancel);
            if (result.MessageType == WebSocketMessageType.Close) return;
            if (!result.EndOfMessage)
            {
                await ws.CloseOutputAsync(WebSocketCloseStatus.MessageTooBig, "A datagram is at most " + options.GatewayMaxPacket + " bytes.", CancellationToken.None);
                return;
            }
            if (result.MessageType != WebSocketMessageType.Binary) continue;
            Interlocked.Exchange(ref _lastActivity, Environment.TickCount64);
            if (!GatewayRules.TryDecode(buffer.AsSpan(0, result.Count), out var type, out var to, out var payload) || type != GatewayRules.Data) continue;
            if (payload.Length == 0) continue;
            if (payload.Length > options.GatewayMaxPacket) { await NoteLimitAsync(GatewayRules.ReasonSize, cancel); continue; }
            if (!GatewayRules.IsAllowedDestination(to, options.GatewayAllowPrivate) || !MayContact(to)) { await NoteLimitAsync(GatewayRules.ReasonDestination, cancel); continue; }
            if (!_up.TryTake(payload.Length)) { await NoteLimitAsync(GatewayRules.ReasonRate, cancel); continue; }
            if (!registry.TryAddBytes(account, payload.Length, options.GatewayBytesPerDay)) { await NoteLimitAsync(GatewayRules.ReasonDay, cancel); continue; }
            try { udp.SendTo(payload, SocketFlags.None, to); }
            catch (SocketException ex) { log.LogDebug("gateway send to {To} failed: {Error}", to, ex.SocketErrorCode); }
        }
    }

    // How many different places one connection may send to in a minute: enough for the lookups that find people, not for a scan.
    private bool MayContact(IPEndPoint to)
    {
        var now = Environment.TickCount64;
        if (_destinations.Count > 0 && _destinations.Count >= options.GatewayDestinationsPerMinute)
            foreach (var old in _destinations.Where(d => now - d.Value > 60_000).Select(d => d.Key).ToList()) _destinations.Remove(old);
        if (!_destinations.ContainsKey(to) && _destinations.Count >= options.GatewayDestinationsPerMinute) return false;
        _destinations[to] = now;
        return true;
    }

    private async Task DownAsync(Socket udp, CancellationToken cancel)
    {
        var buffer = new byte[65536];
        EndPoint any = new IPEndPoint(udp.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);
        while (!cancel.IsCancellationRequested)
        {
            var got = await udp.ReceiveFromAsync(buffer, SocketFlags.None, any, cancel);
            var from = (IPEndPoint)got.RemoteEndPoint;
            if (got.ReceivedBytes == 0 || got.ReceivedBytes > options.GatewayMaxPacket) continue;
            // What comes in is held to the same speed and day as what goes out: the service's bandwidth is what is being shared.
            if (!_down.TryTake(got.ReceivedBytes) || !registry.TryAddBytes(account, got.ReceivedBytes, options.GatewayBytesPerDay)) continue;
            Interlocked.Exchange(ref _lastActivity, Environment.TickCount64);
            await SendAsync(GatewayRules.Encode(GatewayRules.Data, from, buffer.AsSpan(0, got.ReceivedBytes)), cancel);
        }
    }

    private async Task IdleAsync(CancellationToken cancel)
    {
        var limit = TimeSpan.FromSeconds(Math.Max(10, options.GatewayIdleSeconds)).TotalMilliseconds;
        while (!cancel.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), cancel);
            if (Environment.TickCount64 - Interlocked.Read(ref _lastActivity) > limit) return;
        }
    }
}
