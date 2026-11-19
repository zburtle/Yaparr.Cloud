using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace Yaparr.Cloud;

/// <summary>
/// The wire format and the rules of the UDP gateway (see the README, "UDP gateway"). A device on a network that blocks UDP sends the datagrams it would have sent, inside a WebSocket
/// on port 443; the service sends them from a UDP socket of its own and hands back what arrives there. Every WebSocket message is one frame:
/// <code>
/// 0x01 DATA   | family (4 or 6) | port (2, big endian) | address (4 or 16) | the datagram     (to the service: where to send it; from it: who it came from)
/// 0x10 HELLO  | family | port | address                                                       (from the service, once: the address the world sees for this connection)
/// 0x11 LIMIT  | reason (1 = rate, 2 = day, 3 = destination, 4 = size)                         (from the service: a datagram was not sent, or not handed on)
/// </code>
/// </summary>
internal static class GatewayRules
{
    public const byte Data = 0x01, Hello = 0x10, Limit = 0x11;
    public const byte ReasonRate = 1, ReasonDay = 2, ReasonDestination = 3, ReasonSize = 4;

    /// <summary>A frame's header size for an address family: type, family, port, address.</summary>
    public static int HeaderLength(AddressFamily family) => 1 + 1 + 2 + (family == AddressFamily.InterNetworkV6 ? 16 : 4);

    public static byte[] Encode(byte type, IPEndPoint endpoint, ReadOnlySpan<byte> payload)
    {
        var address = endpoint.Address.IsIPv4MappedToIPv6 ? endpoint.Address.MapToIPv4() : endpoint.Address;
        var family = address.AddressFamily;
        var frame = new byte[HeaderLength(family) + payload.Length];
        frame[0] = type;
        frame[1] = family == AddressFamily.InterNetworkV6 ? (byte)6 : (byte)4;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2), (ushort)endpoint.Port);
        address.TryWriteBytes(frame.AsSpan(4), out _);
        payload.CopyTo(frame.AsSpan(HeaderLength(family)));
        return frame;
    }

    /// <summary>Reads a frame of the kinds that carry an address. False for anything that is not one.</summary>
    public static bool TryDecode(ReadOnlySpan<byte> frame, out byte type, out IPEndPoint endpoint, out ReadOnlySpan<byte> payload)
    {
        type = 0;
        endpoint = null!;
        payload = default;
        if (frame.Length < 8 || frame[0] is not (Data or Hello)) return false;
        int addressLength = frame[1] switch { 4 => 4, 6 => 16, _ => 0 };
        if (addressLength == 0 || frame.Length < 4 + addressLength) return false;
        type = frame[0];
        endpoint = new IPEndPoint(new IPAddress(frame.Slice(4, addressLength)), BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(2, 2)));
        payload = frame[(4 + addressLength)..];
        return true;
    }

    /// <summary>
    /// Whether the gateway may send to this address. A public unicast address only: the service must not be a way into anyone's private network, its own included, so loopback, private,
    /// link-local, shared (carrier-grade NAT), multicast, broadcast and unspecified addresses, and port 0, are refused, unless <paramref name="allowPrivate"/> is set (for tests,
    /// and for a service run for one network).
    /// </summary>
    public static bool IsAllowedDestination(IPEndPoint to, bool allowPrivate)
    {
        if (to.Port == 0) return false;
        var address = to.Address.IsIPv4MappedToIPv6 ? to.Address.MapToIPv4() : to.Address;
        if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.Broadcast)) return false;
        if (allowPrivate) return !IsMulticast(address);
        if (IPAddress.IsLoopback(address) || IsMulticast(address)) return false;
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return !(b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254)
                     || (b[0] == 100 && b[1] is >= 64 and <= 127) || b[0] == 0 || b[0] >= 240 || (b[0] == 192 && b[1] == 0 && b[2] == 0));
        }
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var b = address.GetAddressBytes();
            bool uniqueLocal = (b[0] & 0xFE) == 0xFC, linkLocal = b[0] == 0xFE && (b[1] & 0xC0) == 0x80;
            return !(uniqueLocal || linkLocal || address.IsIPv6SiteLocal || address.IsIPv6LinkLocal);
        }
        return false;
    }

    private static bool IsMulticast(IPAddress address) =>
        address.AddressFamily == AddressFamily.InterNetwork ? address.GetAddressBytes()[0] is >= 224 and <= 239 : address.IsIPv6Multicast;
}

/// <summary>A bucket of bytes that fills at a steady rate up to a burst, for the speed of one direction of one connection.</summary>
internal sealed class ByteBucket
{
    private readonly double _perSecond, _burst;
    private readonly Func<long> _ticks;
    private double _level;
    private long _last;

    public ByteBucket(double bytesPerSecond, double burstBytes, Func<long>? ticks = null)
    {
        _perSecond = Math.Max(1, bytesPerSecond);
        _burst = Math.Max(1, burstBytes);
        _ticks = ticks ?? (() => Environment.TickCount64);
        _level = _burst;
        _last = _ticks();
    }

    /// <summary>True, and takes the bytes, if the bucket holds that many.</summary>
    public bool TryTake(int bytes)
    {
        lock (this)
        {
            var now = _ticks();
            _level = Math.Min(_burst, _level + (now - _last) / 1000.0 * _perSecond);
            _last = now;
            if (_level < bytes) return false;
            _level -= bytes;
            return true;
        }
    }
}
