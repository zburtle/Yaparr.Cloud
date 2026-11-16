using System.Security.Cryptography;
using System.Text;

namespace Yaparr.Cloud;

/// <summary>Where a wake-up goes: through a UnifiedPush distributor (an https address the phone's distributor app gave it) or through Firebase Cloud Messaging (a device token).</summary>
public enum PushKind { UnifiedPush, Fcm }

/// <summary>A device's registration as its owner may see it. The wake token is never shown again after it is made.</summary>
public sealed record PushRegistration(string DeviceId, PushKind Kind, DateTimeOffset Created);

/// <summary>Where a wake token leads.</summary>
public sealed record PushTarget(string Account, string DeviceId, PushKind Kind, string Endpoint);

public enum PushRegisterOutcome { Created, Updated, TooMany }

/// <summary>The result of registering: a token only when one was made (a device that was already registered keeps the token it has).</summary>
public sealed record PushRegisterResult(PushRegisterOutcome Outcome, string? Token);

/// <summary>
/// The phones that can be woken, and the wake tokens that lead to them. A token is long and random and is given to the people the phone's owner talks to (inside
/// their rooms, where this service never looks); whoever holds one may ask for a wake-up and nothing more, so it leads to nothing but a push with no content in it.
/// Only a hash of a token is kept. Two implementations: <see cref="PushRegistry"/> in a file and <c>PostgresPushRegistry</c> in the database, held to the same
/// behaviour by the same tests.
/// </summary>
public interface IPushRegistry
{
    /// <summary>
    /// Registers a device (or updates where it is woken). A device that is new gets a wake token, returned once; one that is already registered keeps its token and
    /// gets none back. At most <paramref name="maxPerAccount"/> devices an account.
    /// </summary>
    PushRegisterResult Register(string account, string deviceId, PushKind kind, string endpoint, int maxPerAccount);

    /// <summary>Gives a registered device a new wake token (the old one stops working at once), returned once. Null if the device is not registered.</summary>
    string? Rotate(string account, string deviceId);

    /// <summary>Forgets a device. False if it was not registered.</summary>
    bool Remove(string account, string deviceId);

    IReadOnlyList<PushRegistration> List(string account);

    /// <summary>Where a wake token leads, or null if it is not one.</summary>
    PushTarget? Find(string? token);

    /// <summary>Forgets whatever a token leads to (the push service said the address is gone). False if it was not one.</summary>
    bool RemoveByToken(string token);

    /// <summary>Forgets every device of an account.</summary>
    void RemoveAccount(string account);
}

internal static class WakeTokens
{
    public static string Make() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string HashOf(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes("yaparr-wake:" + token)));

    /// <summary>Whether a string can be a token at all (43 characters of the URL-safe alphabet), before it is looked up.</summary>
    public static bool Looks(string? token) => token is { Length: 43 } && token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    public static bool GoodDeviceId(string? id) => id is { Length: > 0 and <= 64 } && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
}
