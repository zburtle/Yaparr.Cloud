using System.Security.Cryptography;
using System.Text;

namespace Yaparr.Cloud;

/// <summary>
/// One-time codes from an authenticator app (RFC 6238: HMAC-SHA1, 30 seconds, 6 digits), the second thing the admin portal asks for after the
/// password. A code is accepted for the step before and after the current one (the phone's clock is a little off), and each step only once
/// (<see cref="AdminSessions.TryAcceptStep"/>), so a code that was seen over somebody's shoulder cannot be used again.
/// </summary>
public static class Totp
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
    public const int StepSeconds = 30;

    /// <summary>A new secret: 160 random bits, as the letters and digits (base 32) an authenticator app takes.</summary>
    public static string NewSecret()
    {
        var bytes = RandomNumberGenerator.GetBytes(20);
        var text = new StringBuilder();
        int buffer = 0, bits = 0;
        foreach (var b in bytes)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5) { text.Append(Alphabet[(buffer >> (bits - 5)) & 31]); bits -= 5; }
        }
        return text.ToString();
    }

    private static byte[]? Decode(string secret)
    {
        var bytes = new List<byte>();
        int buffer = 0, bits = 0;
        foreach (var c in secret.ToUpperInvariant())
        {
            var value = Alphabet.IndexOf(c);
            if (value < 0) return null;
            buffer = (buffer << 5) | value;
            bits += 5;
            if (bits >= 8) { bytes.Add((byte)((buffer >> (bits - 8)) & 255)); bits -= 8; }
        }
        return bytes.ToArray();
    }

    public static long StepOf(DateTimeOffset time) => time.ToUnixTimeSeconds() / StepSeconds;

    /// <summary>The six digits for a step.</summary>
    public static string Code(string secret, long step)
    {
        var key = Decode(secret) ?? throw new ArgumentException("Not a valid secret.", nameof(secret));
        var counter = new byte[8];
        for (int i = 7, n = 0; i >= 0; i--, n += 8) counter[i] = (byte)(step >> n);
        var hash = HMACSHA1.HashData(key, counter);
        int offset = hash[^1] & 0xF;
        int number = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (number % 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The step a typed code belongs to (this one, the one before or the one after), or null if it matches none. Spaces are ignored. Every
    /// candidate is compared, so how long it takes does not say which digit was wrong.
    /// </summary>
    public static long? Verify(string secret, string? typed, DateTimeOffset now)
    {
        var digits = new string((typed ?? "").Where(c => !char.IsWhiteSpace(c)).ToArray());
        if (digits.Length != 6 || !digits.All(char.IsAsciiDigit) || Decode(secret) == null) return null;
        long? found = null;
        for (long step = StepOf(now) - 1; step <= StepOf(now) + 1; step++)
            if (CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Code(secret, step)), Encoding.ASCII.GetBytes(digits))) found = step;
        return found;
    }

    /// <summary>What an authenticator app reads from a QR code, or is given to add the account by hand.</summary>
    public static string Uri(string account, string issuer, string secret) =>
        $"otpauth://totp/{System.Uri.EscapeDataString(issuer)}:{System.Uri.EscapeDataString(account)}?secret={secret}&issuer={System.Uri.EscapeDataString(issuer)}&algorithm=SHA1&digits=6&period={StepSeconds}";
}
