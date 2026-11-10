using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace Yaparr.Cloud;

/// <summary>
/// How a request proves who sent it, without a password ever reaching the server. The client makes an Ed25519 key from the username and password
/// (<see cref="LoginKeys"/>) and signs each request with it. The server holds only the public key of each account (<see cref="AccountDirectory"/>),
/// so it can check a signature but cannot sign, and cannot learn the password. The same code signs (clients, tests) and checks (server).
/// <code>
/// X-Yaparr-User            the username, as <see cref="AccountDirectory.Normalize"/> makes it
/// X-Yaparr-Time            when the request was made, seconds since 1970
/// X-Yaparr-Content-Sha256  hex SHA-256 of the body (of nothing, for a request without one)
/// X-Yaparr-Sig             the signature of the text below, base64url
///
/// YAPSVC2 \n username \n METHOD \n path and query \n time \n content hash
/// </code>
/// The body hash is in the signed text, so the server can refuse a request before it reads a large body, and then checks the body against it.
/// </summary>
public static class RequestSigner
{
    public const string UserHeader = "X-Yaparr-User", TimeHeader = "X-Yaparr-Time", HashHeader = "X-Yaparr-Content-Sha256", SignatureHeader = "X-Yaparr-Sig";

    /// <summary>The hash of an empty body, which is what a request without one carries.</summary>
    public static readonly string EmptyHash = Convert.ToHexStringLower(SHA256.HashData(Array.Empty<byte>()));

    // A real key to check against when the name is not an account, so the work done is the same either way.
    private static readonly byte[] Unused = new Ed25519PrivateKeyParameters(new byte[Ed25519PrivateKeyParameters.KeySize], 0).GeneratePublicKey().GetEncoded();

    public static string TextToSign(string username, string method, string pathAndQuery, long time, string contentHash) =>
        $"YAPSVC2\n{username}\n{method.ToUpperInvariant()}\n{pathAndQuery}\n{time}\n{contentHash}";

    /// <summary>The name of an account's folder: the hex SHA-256 of its username, so nothing from a request ever becomes part of a path.</summary>
    public static string FolderOf(string username) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("yaparr-account:" + username)));

    /// <summary>Makes the headers for a request.</summary>
    public static IReadOnlyDictionary<string, string> Sign(byte[] signingSeed, string username, string method, string pathAndQuery, DateTimeOffset now, string? contentHash = null)
    {
        var signer = new Ed25519Signer();
        signer.Init(true, new Ed25519PrivateKeyParameters(signingSeed, 0));
        var time = now.ToUnixTimeSeconds();
        var hash = contentHash ?? EmptyHash;
        var text = Encoding.UTF8.GetBytes(TextToSign(username, method, pathAndQuery, time, hash));
        signer.BlockUpdate(text, 0, text.Length);
        return new Dictionary<string, string>
        {
            [UserHeader] = username,
            [TimeHeader] = time.ToString(),
            [HashHeader] = hash,
            [SignatureHeader] = Base64Url.EncodeToString(signer.GenerateSignature()),
        };
    }

    /// <summary>
    /// Checks the headers of a request. True, with the username and the body hash it was signed with, when the account exists, the time is close enough
    /// to now and the signature is good. Otherwise false, with a reason that is safe to show the caller: it is the same whether the name does not exist
    /// or the password was wrong, so it cannot be used to find out who has an account.
    /// </summary>
    public static bool TryVerify(string method, string pathAndQuery, Func<string, string?> header, Func<string, byte[]?> publicKeyOf, DateTimeOffset now,
        int clockSkewSeconds, out string username, out string contentHash, out string error)
    {
        username = contentHash = "";
        error = "The request is not signed.";
        var userText = header(UserHeader);
        var timeText = header(TimeHeader);
        var sigText = header(SignatureHeader);
        if (userText == null || timeText == null || sigText == null) return false;

        error = "The username or password is wrong.";
        if (AccountDirectory.Normalize(userText) is not { } name || name != userText || !long.TryParse(timeText, out var time)) return false;
        var hash = (header(HashHeader) ?? EmptyHash).ToLowerInvariant();
        if (hash.Length != 64 || !hash.All(Uri.IsHexDigit)) return false;

        byte[] signature;
        try { signature = Base64Url.DecodeFromChars(sigText); }
        catch (FormatException) { return false; }
        if (signature.Length != Ed25519PrivateKeyParameters.SignatureSize) return false;

        if (Math.Abs(now.ToUnixTimeSeconds() - time) > clockSkewSeconds)
        {
            error = "The clock of this device is too far from the server's. Check the date and time.";
            return false;
        }

        // Checked even for a name that does not exist, so how long the answer takes does not say whether the account is there.
        var publicKey = publicKeyOf(name);
        var verifier = new Ed25519Signer();
        verifier.Init(false, new Ed25519PublicKeyParameters(publicKey ?? Unused, 0));
        var text = Encoding.UTF8.GetBytes(TextToSign(name, method, pathAndQuery, time, hash));
        verifier.BlockUpdate(text, 0, text.Length);
        if (!verifier.VerifySignature(signature) || publicKey == null) return false;

        username = name;
        contentHash = hash;
        error = "";
        return true;
    }
}
