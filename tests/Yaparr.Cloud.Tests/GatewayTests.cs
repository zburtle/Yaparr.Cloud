using System.Net.Http.Json;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using Yaparr.Cloud;

namespace Yaparr.Cloud.Tests;

public sealed class GatewayRulesTests
{
    [Fact]
    public void A_frame_is_written_and_read_back_for_both_families()
    {
        var v4 = new IPEndPoint(IPAddress.Parse("203.0.113.9"), 6881);
        var v6 = new IPEndPoint(IPAddress.Parse("2001:db8::7"), 51820);
        foreach (var to in new[] { v4, v6 })
        {
            var frame = GatewayRules.Encode(GatewayRules.Data, to, new byte[] { 1, 2, 3 });
            Assert.True(GatewayRules.TryDecode(frame, out var type, out var read, out var payload));
            Assert.Equal(GatewayRules.Data, type);
            Assert.Equal(to, read);
            Assert.Equal(new byte[] { 1, 2, 3 }, payload.ToArray());
        }
        // The bytes the apps' own test (GatewayFramesTests) expects: DATA, IPv4, port 6881 (0x1AE1), 203.0.113.9, a datagram of 1 2 3.
        Assert.Equal(new byte[] { 0x01, 0x04, 0x1A, 0xE1, 203, 0, 113, 9, 1, 2, 3 }, GatewayRules.Encode(GatewayRules.Data, v4, new byte[] { 1, 2, 3 }));
        // An address that came through the dual-stack socket as ::ffff:a.b.c.d goes out as the plain IPv4 address.
        var mapped = GatewayRules.Encode(GatewayRules.Data, new IPEndPoint(IPAddress.Parse("::ffff:203.0.113.9"), 1), new byte[] { 9 });
        Assert.Equal(4, mapped[1]);
        Assert.False(GatewayRules.TryDecode(new byte[] { 0x01, 4, 0, 1 }, out _, out _, out _));                  // too short
        Assert.False(GatewayRules.TryDecode(new byte[] { 0x42, 4, 0, 1, 1, 2, 3, 4 }, out _, out _, out _));      // not a kind that carries an address
        Assert.False(GatewayRules.TryDecode(new byte[] { 0x01, 5, 0, 1, 1, 2, 3, 4 }, out _, out _, out _));      // not a family
    }

    [Theory]
    [InlineData("203.0.113.9", true)]
    [InlineData("8.8.8.8", true)]
    [InlineData("2001:db8::1", true)]
    [InlineData("127.0.0.1", false)]
    [InlineData("::1", false)]
    [InlineData("10.0.0.5", false)]
    [InlineData("172.16.0.1", false)]
    [InlineData("172.32.0.1", true)]
    [InlineData("192.168.1.1", false)]
    [InlineData("169.254.169.254", false)]       // the cloud metadata address
    [InlineData("100.64.0.1", false)]            // carrier-grade NAT
    [InlineData("224.0.0.251", false)]           // multicast
    [InlineData("255.255.255.255", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("fe80::1", false)]
    [InlineData("fd00::1", false)]
    [InlineData("::ffff:10.0.0.1", false)]       // a private address however it is written
    public void Only_public_unicast_addresses_may_be_sent_to(string address, bool allowed)
    {
        Assert.Equal(allowed, GatewayRules.IsAllowedDestination(new IPEndPoint(IPAddress.Parse(address), 443), allowPrivate: false));
    }

    [Fact]
    public void Port_zero_is_never_allowed_and_a_service_run_for_one_network_may_allow_private_addresses_but_not_multicast()
    {
        Assert.False(GatewayRules.IsAllowedDestination(new IPEndPoint(IPAddress.Parse("203.0.113.9"), 0), allowPrivate: false));
        Assert.True(GatewayRules.IsAllowedDestination(new IPEndPoint(IPAddress.Loopback, 5), allowPrivate: true));
        Assert.False(GatewayRules.IsAllowedDestination(new IPEndPoint(IPAddress.Parse("224.0.0.1"), 5), allowPrivate: true));
    }

    [Fact]
    public void A_bucket_lets_a_burst_through_then_the_steady_rate_and_fills_again_with_time()
    {
        long now = 0;
        var bucket = new ByteBucket(100, 300, () => now);
        Assert.True(bucket.TryTake(200));
        Assert.False(bucket.TryTake(200));      // 100 left
        Assert.True(bucket.TryTake(100));
        Assert.False(bucket.TryTake(1));
        now += 1000;                            // a second: 100 bytes
        Assert.True(bucket.TryTake(100));
        Assert.False(bucket.TryTake(1));
        now += 60_000;                          // a long time: no more than the burst
        Assert.True(bucket.TryTake(300));
        Assert.False(bucket.TryTake(1));
    }

    [Fact]
    public void The_registry_holds_a_few_connections_for_each_account_and_a_days_bytes_that_start_over_at_midnight_utc()
    {
        var now = new DateTimeOffset(2026, 11, 19, 23, 0, 0, TimeSpan.Zero);
        var registry = new GatewayRegistry(() => now);
        Assert.True(registry.TryEnter("sam", 2, 3));
        Assert.True(registry.TryEnter("sam", 2, 3));
        Assert.False(registry.TryEnter("sam", 2, 3));         // two for an account
        Assert.True(registry.TryEnter("alex", 2, 3));
        Assert.False(registry.TryEnter("kim", 2, 3));         // three altogether
        registry.Leave("sam");
        Assert.True(registry.TryEnter("kim", 2, 3));
        Assert.Equal(3, registry.Total);

        Assert.True(registry.TryAddBytes("sam", 600, 1000));
        Assert.False(registry.TryAddBytes("sam", 500, 1000));
        Assert.True(registry.TryAddBytes("sam", 400, 1000));
        now = now.AddHours(2);                                // past midnight UTC
        Assert.True(registry.TryAddBytes("sam", 1000, 1000));
    }
}

public sealed class GatewayApiTests : ServiceTest
{
    private static readonly Dictionary<string, string> On = new() { ["GatewayEnabled"] = "true", ["GatewayAllowPrivate"] = "true" };

    private async Task<WebSocket> Connect(string? token)
    {
        var client = LastFactory!.Server.CreateWebSocketClient();
        client.ConfigureRequest = r => { if (token != null) r.Headers["Authorization"] = "Bearer " + token; };
        return await client.ConnectAsync(new Uri("http://localhost/v1/gateway"), CancellationToken.None);
    }

    private static async Task<byte[]> Receive(WebSocket socket)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var buffer = new byte[4096];
        var result = await socket.ReceiveAsync(buffer, timeout.Token);
        return buffer.AsSpan(0, result.Count).ToArray();
    }

    private static Task Send(WebSocket socket, IPEndPoint to, byte[] payload) =>
        socket.SendAsync(GatewayRules.Encode(GatewayRules.Data, to, payload), WebSocketMessageType.Binary, true, CancellationToken.None);

    private static (UdpClient Echo, Task Loop) StartEcho()
    {
        var echo = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var loop = Task.Run(async () =>
        {
            try { while (true) { var r = await echo.ReceiveAsync(); await echo.SendAsync(r.Buffer, r.Buffer.Length, r.RemoteEndPoint); } }
            catch (ObjectDisposedException) { }
            catch (SocketException) { }
        });
        return (echo, loop);
    }

    [Fact]
    public async Task A_datagram_goes_out_from_the_service_and_what_comes_back_is_handed_on_with_where_it_came_from()
    {
        Start(On);
        var token = Register("sam@example.com");
        var (echo, _) = StartEcho();
        using var _e = echo;
        using var socket = await Connect(token);

        Assert.True(GatewayRules.TryDecode(await Receive(socket), out var type, out var hello, out _));
        Assert.Equal(GatewayRules.Hello, type);
        Assert.True(hello.Port > 0);

        var echoAddress = (IPEndPoint)echo.Client.LocalEndPoint!;
        await Send(socket, echoAddress, new byte[] { 7, 7, 7 });
        Assert.True(GatewayRules.TryDecode(await Receive(socket), out type, out var from, out var payload));
        Assert.Equal(GatewayRules.Data, type);
        Assert.Equal(echoAddress, from);
        Assert.Equal(new byte[] { 7, 7, 7 }, payload.ToArray());
    }

    [Fact]
    public async Task The_gateway_is_off_by_default_and_needs_a_sign_in()
    {
        Start();
        var token = Register("sam@example.com");
        await Assert.ThrowsAnyAsync<Exception>(() => Connect(token));       // off: nothing is there
        Start(On);
        await Assert.ThrowsAnyAsync<Exception>(() => Connect(null));        // no sign-in
        await Assert.ThrowsAnyAsync<Exception>(() => Connect("not a token"));
    }

    [Fact]
    public async Task Private_addresses_are_refused_and_so_is_a_datagram_that_is_too_big()
    {
        Start(new() { ["GatewayEnabled"] = "true", ["GatewayMaxPacket"] = "100" });      // private addresses are not allowed
        var token = Register("sam@example.com");
        using var socket = await Connect(token);
        await Receive(socket);                                                           // the hello

        await Send(socket, new IPEndPoint(IPAddress.Loopback, 9), new byte[] { 1 });
        Assert.Equal(new[] { GatewayRules.Limit, GatewayRules.ReasonDestination }, await Receive(socket));

        await Task.Delay(1100);                                                          // one notice a second
        await Send(socket, new IPEndPoint(IPAddress.Parse("203.0.113.9"), 9), new byte[120]);   // a little over the limit is answered; much more closes the connection
        Assert.Equal(new[] { GatewayRules.Limit, GatewayRules.ReasonSize }, await Receive(socket));
    }

    [Fact]
    public async Task What_goes_over_the_speed_is_not_sent_and_the_device_is_told()
    {
        Start(new(On) { ["GatewayBytesPerSecond"] = "100", ["GatewayBurstBytes"] = "200" });
        var token = Register("sam@example.com");
        var (echo, _) = StartEcho();
        using var _e = echo;
        using var socket = await Connect(token);
        await Receive(socket);
        var to = (IPEndPoint)echo.Client.LocalEndPoint!;

        await Send(socket, to, new byte[150]);
        Assert.True(GatewayRules.TryDecode(await Receive(socket), out var type, out _, out _));   // the first goes out and comes back
        Assert.Equal(GatewayRules.Data, type);
        await Send(socket, to, new byte[150]);                                                    // 50 left in the bucket
        Assert.Equal(new[] { GatewayRules.Limit, GatewayRules.ReasonRate }, await Receive(socket));
    }

    [Fact]
    public async Task An_account_may_have_only_so_many_connections()
    {
        Start(new(On) { ["GatewayConnectionsPerAccount"] = "1" });
        var token = Register("sam@example.com");
        using var first = await Connect(token);
        await Assert.ThrowsAnyAsync<Exception>(() => Connect(token));
        first.Abort();
    }
}

public sealed class GatewayLimitsTests
{
    [Fact]
    public void The_default_speed_fits_voice_with_a_couple_of_people_and_is_far_below_a_screen_share()
    {
        var options = new ServiceOptions();
        const double voiceToOnePersonBytesPerSecond = 90_000 / 8.0;      // about 90 kbit/s on the wire for each person a voice goes to
        const double smallestScreenShareBytesPerSecond = 1_000_000 / 8.0; // a screen share of 1 Mbit/s, below the lowest steps the apps use
        Assert.True(options.GatewayBytesPerSecond >= 2 * voiceToOnePersonBytesPerSecond);
        Assert.True(options.GatewayBytesPerSecond * 4 < smallestScreenShareBytesPerSecond);
        // A day's bytes allow about 8 hours of one-to-one voice in both directions.
        Assert.True(options.GatewayBytesPerDay >= 2 * voiceToOnePersonBytesPerSecond * 8 * 3600);
    }
}

public sealed class AccountApiTests : ServiceTest
{
    [Fact]
    public async Task A_device_sees_the_devices_of_its_account_marks_itself_and_signs_another_out()
    {
        var client = Start();
        var accounts = new AccountDirectory(Options);
        accounts.Add("sam@example.com", Password);
        var phone = accounts.IssueToken("sam@example.com", "phone")!;
        var laptop = accounts.IssueToken("sam@example.com", "laptop")!;

        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(client, null, HttpMethod.Get, "/v1/devices")).StatusCode);
        var listing = await (await Send(client, phone, HttpMethod.Get, "/v1/devices")).Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var devices = listing.GetProperty("devices").EnumerateArray().ToList();
        Assert.Equal(2, devices.Count);
        var current = Assert.Single(devices, d => d.GetProperty("current").GetBoolean());
        Assert.Equal("phone", current.GetProperty("name").GetString());
        var other = Assert.Single(devices, d => !d.GetProperty("current").GetBoolean());

        Assert.Equal(HttpStatusCode.NoContent, (await Send(client, phone, HttpMethod.Delete, "/v1/devices/" + other.GetProperty("id").GetString())).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(client, laptop, HttpMethod.Get, "/v1/devices")).StatusCode);       // signed out
        Assert.Equal(HttpStatusCode.NotFound, (await Send(client, phone, HttpMethod.Delete, "/v1/devices/" + other.GetProperty("id").GetString())).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(client, null, HttpMethod.Delete, "/v1/devices/0123456789abcdef")).StatusCode);
    }

    [Fact]
    public async Task What_an_account_holds_is_reported_against_its_limits()
    {
        var client = Start(new() { ["GatewayEnabled"] = "true" });
        var token = Register("sam@example.com");
        var pieceData = System.Security.Cryptography.RandomNumberGenerator.GetBytes(1000);
        var pictureData = System.Security.Cryptography.RandomNumberGenerator.GetBytes(500);
        Assert.Equal(HttpStatusCode.OK, (await Send(client, token, HttpMethod.Put, "/v1/keep/" + BlobRules.IdOf(pieceData), pieceData)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(client, token, HttpMethod.Put, "/v1/blob/" + BlobRules.IdOf(pictureData), pictureData)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Send(client, token, HttpMethod.Put, "/v1/backup", Locked(2000), ifMatch: 0)).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(client, null, HttpMethod.Get, "/v1/usage")).StatusCode);
        var response = await Send(client, token, HttpMethod.Get, "/v1/usage");
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var usage = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal(1000, usage.GetProperty("kept").GetProperty("usedBytes").GetInt64());
        Assert.Equal(new ServiceOptions().KeepQuotaBytes, usage.GetProperty("kept").GetProperty("quotaBytes").GetInt64());
        Assert.Equal(500, usage.GetProperty("pictures").GetProperty("usedBytes").GetInt64());
        Assert.Equal(1, usage.GetProperty("backups").GetProperty("count").GetInt32());
        Assert.Equal(2000, usage.GetProperty("backups").GetProperty("bytes").GetInt64());
        Assert.True(usage.GetProperty("gateway").GetProperty("enabled").GetBoolean());
        Assert.Equal(0, usage.GetProperty("gateway").GetProperty("usedTodayBytes").GetInt64());
    }
}

public sealed class GatewayLimitsKeptTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "yaparr-gwusage-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    [Fact]
    public void The_days_bytes_survive_a_restart_and_start_over_the_next_day()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "gateway-usage.json");
        var now = new DateTimeOffset(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);
        var first = new GatewayRegistry(() => now, path);
        Assert.True(first.TryAddBytes("sam@example.com", 700, 1000));
        first.Flush();
        Assert.DoesNotContain("sam@example.com", File.ReadAllText(path));      // kept by a short hash, not by name

        var restarted = new GatewayRegistry(() => now, path);
        Assert.Equal(700, restarted.UsedToday("sam@example.com"));
        Assert.False(restarted.TryAddBytes("sam@example.com", 400, 1000));      // the day's limit still counts what was used before the restart
        Assert.Equal(0, restarted.UsedToday("alex@example.com"));

        now = now.AddDays(1);
        Assert.Equal(0, restarted.UsedToday("sam@example.com"));
        Assert.Equal(0, new GatewayRegistry(() => now, path).UsedToday("sam@example.com"));      // and a restart the next day starts clean
        Assert.True(restarted.TryAddBytes("sam@example.com", 1000, 1000));
    }

    [Fact]
    public void An_address_has_only_so_many_connections_at_once()
    {
        var registry = new GatewayRegistry();
        Assert.True(registry.TryEnterAddress("203.0.113.9", 2));
        Assert.True(registry.TryEnterAddress("203.0.113.9", 2));
        Assert.False(registry.TryEnterAddress("203.0.113.9", 2));
        Assert.True(registry.TryEnterAddress("203.0.113.10", 2));
        registry.LeaveAddress("203.0.113.9");
        Assert.True(registry.TryEnterAddress("203.0.113.9", 2));
        Assert.True(registry.TryEnterAddress(null, 1));
        Assert.False(registry.TryEnterAddress(null, 1));
    }
}
