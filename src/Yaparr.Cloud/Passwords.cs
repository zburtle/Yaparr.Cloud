using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;

namespace Yaparr.Cloud;

/// <summary>
/// The passwords of accounts. A password signs a person in (in their browser); it does not lock their backup, which has a recovery code of its
/// own that never reaches the server. So a password can be reset by the person running the service without anything being lost. Only a hash is
/// kept (Argon2id, with its settings beside it, so they can be raised later and old hashes still check).
/// </summary>
public static class Passwords
{
    public const int MinLength = 10;
    private const int MemoryKiB = 32 * 1024, Passes = 2, Lanes = 1, SaltLength = 16, HashLength = 32;

    public static string? Check(string? password) =>
        (password ?? "").Length < MinLength ? $"The password needs at least {MinLength} characters." : null;

    /// <summary>What is stored for a password: <c>argon2id$memory$passes$lanes$salt$hash</c>.</summary>
    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltLength);
        return $"argon2id${MemoryKiB}${Passes}${Lanes}${Convert.ToBase64String(salt)}${Convert.ToBase64String(Derive(password, salt, MemoryKiB, Passes, Lanes))}";
    }

    /// <summary>True if the password is the one that was hashed. A hash that cannot be read (or none) is never right, and takes as long to say so.</summary>
    public static bool Verify(string password, string? stored)
    {
        var parts = (stored ?? "").Split('$');
        if (parts.Length == 6 && parts[0] == "argon2id"
            && int.TryParse(parts[1], out var memory) && int.TryParse(parts[2], out var passes) && int.TryParse(parts[3], out var lanes)
            && memory is >= 8 * 1024 and <= 1024 * 1024 && passes is >= 1 and <= 16 && lanes is >= 1 and <= 8)
        {
            try
            {
                var salt = Convert.FromBase64String(parts[4]);
                var expected = Convert.FromBase64String(parts[5]);
                return CryptographicOperations.FixedTimeEquals(Derive(password, salt, memory, passes, lanes), expected);
            }
            catch (FormatException) { }
        }
        // The same work for a name with no password, so how long the answer takes does not say whether the account is there.
        Derive(password, new byte[SaltLength], MemoryKiB, Passes, Lanes);
        return false;
    }

    private static byte[] Derive(string password, byte[] salt, int memoryKiB, int passes, int lanes)
    {
        var parameters = new Argon2Parameters.Builder(Argon2Parameters.Argon2id)
            .WithVersion(Argon2Parameters.Version13).WithMemoryAsKB(memoryKiB).WithIterations(passes).WithParallelism(lanes).WithSalt(salt).Build();
        var argon = new Argon2BytesGenerator();
        argon.Init(parameters);
        var hash = new byte[HashLength];
        argon.GenerateBytes(Encoding.UTF8.GetBytes((password ?? "").Normalize(NormalizationForm.FormKC)), hash);
        return hash;
    }

    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    /// <summary>A password with about 100 bits of randomness, in groups of four to read out loud: <c>K7QD-M3XF-9PLR-2WTE-HV8N</c>.</summary>
    public static string Generate()
    {
        var groups = Enumerable.Range(0, 5).Select(_ => new string(Enumerable.Range(0, 4).Select(_ => Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)]).ToArray()));
        return string.Join('-', groups);
    }
}
