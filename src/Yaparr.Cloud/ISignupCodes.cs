namespace Yaparr.Cloud;

/// <summary>
/// The codes that let a friend make an account. Two implementations: <see cref="SignupCodes"/> keeps them in a file, <c>PostgresSignupCodes</c> in a
/// database, held to the same behaviour by the same tests. Only a hash of a code is ever kept.
/// </summary>
public interface ISignupCodes
{
    /// <summary>Makes a code (16 letters and digits, about 80 bits) and returns it with how it will be listed. It is not kept anywhere but in what is returned.</summary>
    (string Code, SignupCodeInfo Info) Create(string note, int? maxUses, TimeSpan? lifetime);

    IReadOnlyList<SignupCodeInfo> List();

    /// <summary>Stops a code from working, by its id. People who already signed up keep their accounts. False if there is no such code.</summary>
    bool Revoke(string id);

    /// <summary>Takes a code out altogether, with the record of who used it. False if there is no such code.</summary>
    bool Delete(string id);

    /// <summary>
    /// Whether this code works now, and which one it is (its id), without using it. Null for a code that does not exist, was revoked, has expired or is
    /// used up (the caller says the same for all of them). A used-up code still lets back in someone who already used it, as long as it was not taken back or ended.
    /// </summary>
    string? Check(string? typed, string? email = null);

    /// <summary>Counts a person against a code, if it still works, and returns its id; null if it does not. A person who already used it is not counted twice.</summary>
    string? Use(string? typed, string email);
}
