using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;

namespace Yaparr.Cloud;

/// <summary>
/// The login. A person types a username and a password; this is what turns them into the key that signs their requests. It runs on their device
/// (and, for the person running the service, when an account is made), so the password is never sent anywhere: the server holds a public key and
/// checks signatures. That is what keeps the server from being able to read a backup, which is locked with the same password.
/// The username is the salt, so two people with the same password get different keys, and the key can be made again on any device.
/// </summary>
public static class LoginKeys
{
    // 64 MiB and three passes, as for locking a backup. Made smaller by the tests only, so they do not each take a quarter of a second.
    private const int DefaultMemoryKiB = 64 * 1024, DefaultPasses = 3, Lanes = 2;
    public const int MinPasswordLength = 12;

    public static string? CheckPassword(string? password) =>
        (password ?? "").Length < MinPasswordLength ? $"The password needs at least {MinPasswordLength} characters." : null;

    /// <summary>The Ed25519 key (its 32-byte seed) that signs for this username and password.</summary>
    public static byte[] SigningSeed(string username, string password) => SigningSeed(username, password, DefaultMemoryKiB, DefaultPasses);

    internal static byte[] SigningSeed(string username, string password, int memoryKiB, int passes)
    {
        var name = AccountDirectory.Normalize(username) ?? throw new ArgumentException("Not a usable username.", nameof(username));
        if (CheckPassword(password) is { } why) throw new ArgumentException(why, nameof(password));

        var salt = SHA256.HashData(Encoding.UTF8.GetBytes("yaparr-login-v1:" + name));
        var parameters = new Argon2Parameters.Builder(Argon2Parameters.Argon2id)
            .WithVersion(Argon2Parameters.Version13).WithMemoryAsKB(memoryKiB).WithIterations(passes).WithParallelism(Lanes).WithSalt(salt).Build();
        var argon = new Argon2BytesGenerator();
        argon.Init(parameters);
        var master = new byte[32];
        argon.GenerateBytes(Encoding.UTF8.GetBytes(password.Normalize(NormalizationForm.FormKC)), master);
        try { return HKDF.DeriveKey(HashAlgorithmName.SHA256, master, 32, salt: null, info: "yaparr-signing-key-v1"u8.ToArray()); }
        finally { CryptographicOperations.ZeroMemory(master); }
    }

    public static byte[] PublicKeyOf(byte[] signingSeed) => new Ed25519PrivateKeyParameters(signingSeed, 0).GeneratePublicKey().GetEncoded();

    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    /// <summary>A password with about 100 bits of randomness, in groups of four to read out loud: <c>K7QD-M3XF-9PLR-2WTE-HV8N</c>.</summary>
    public static string GeneratePassword()
    {
        var groups = Enumerable.Range(0, 5).Select(_ => new string(Enumerable.Range(0, 4).Select(_ => Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)]).ToArray()));
        return string.Join('-', groups);
    }
}
