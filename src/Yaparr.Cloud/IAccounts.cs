namespace Yaparr.Cloud;

/// <summary>A device signed in as an account. <see cref="Id"/> is a short public name for it (the start of its token's hash), enough to sign it out and of no use for signing in.</summary>
public sealed record DeviceInfo(string Id, string Name, DateTimeOffset Created);

/// <summary>
/// Who may use the service: accounts named by an email address, the devices signed in as them, passwords (kept as hashes), pending sign-ups and the admin
/// flag. Two implementations: <see cref="AccountDirectory"/> keeps them in a file, <c>PostgresAccounts</c> in a database. Both are held to the same
/// behaviour by the same tests.
/// </summary>
public interface IAccounts
{
    bool Exists(string name);

    /// <summary>True if the account exists and has chosen a password, so it is for signing in and not for signing up.</summary>
    bool HasPassword(string name);

    /// <summary>The account's pass (<see cref="AccountPlan.Lifetime"/> or <see cref="AccountPlan.None"/>), or null if there is no such account. Only matters once billing is on.</summary>
    string? PassOf(string name);

    /// <summary>Gives an account a pass (<see cref="AccountPlan.IsPass"/>). False if there is no such account or it is not a pass.</summary>
    bool SetPass(string name, string pass);

    IReadOnlyList<AccountEntry> List();

    /// <summary>Adds an account with a password. False if the name is taken.</summary>
    bool Add(string name, string password);

    /// <summary>
    /// Begins an account made by signing up: it exists, with a lifetime pass, but cannot sign in until the person has chosen a password with the one-use
    /// token returned here (kept only as a hash, good for 30 minutes). Someone who comes back for the same address before choosing a password is given
    /// a new token, and the old one stops working. Null if the address already has an account with a password.
    /// </summary>
    string? StartSignup(string name, string? signupCodeId);

    /// <summary>The account a setup token is for, or null if it is not one, has been used, or has run out. Does not use it.</summary>
    string? SetupFor(string? token);

    /// <summary>Gives the account a setup token is for its password, once. Returns the account, or null if the token is not good.</summary>
    string? FinishSetup(string? token, string password);

    /// <summary>Gives an account a new password and signs every device out. False if there is no such account. Backups are untouched.</summary>
    bool SetPassword(string name, string password);

    /// <summary>True if this is the account's password. Takes as long for a name that does not exist, so it cannot be used to find accounts.</summary>
    bool CheckPassword(string name, string password);

    /// <summary>Removes an account and signs its devices out. False if there was none.</summary>
    bool Remove(string name);

    /// <summary>
    /// Signs a device in: makes its token (shown once, kept only as a hash); the oldest device is signed out when there are a great many. Null if there is no such account.
    /// <paramref name="deviceKey"/> is a random id the app made once for the device and keeps (a good one is 16 to 64 letters, digits, - and _; anything else is ignored): the same
    /// device signing in again, which is a new token, replaces its own earlier sign-in instead of adding to the list. Only a hash of it is kept.
    /// <paramref name="machineKey"/> is a second id, derived from the machine itself (its install of the system, never sent as it is): it catches the same machine signing in again after
    /// the app lost its own id (a profile wiped, the app reinstalled), but only together with the same device name, because machines cloned from one image share it.
    /// </summary>
    string? IssueToken(string name, string deviceName, string? deviceKey = null, string? machineKey = null);

    /// <summary>The account a token signs in as, or null if it is not (or no longer) a token of anyone.</summary>
    string? UserOfToken(string? token);

    /// <summary>Signs one device out. False if the token was not one.</summary>
    bool RevokeToken(string token);

    /// <summary>The devices signed in as an account, newest first.</summary>
    IReadOnlyList<DeviceInfo> DevicesOf(string name);

    /// <summary>Signs out one device of this account, by its <see cref="DeviceInfo.Id"/>. False if the account has no such device.</summary>
    bool RevokeDevice(string name, string deviceId);

    /// <summary>Lets an account sign in to the admin portal and returns the secret for its authenticator app (new each time). Null if there is no such account or it has no password yet.</summary>
    string? MakeAdmin(string name);

    /// <summary>Takes the portal away from an account. False if there is no such account or it was not an admin.</summary>
    bool RemoveAdmin(string name);

    bool IsAdmin(string name);

    /// <summary>Whether anyone may use the admin portal.</summary>
    bool AnyAdmin();

    /// <summary>The authenticator secret of an admin, or null for anyone else.</summary>
    string? TotpSecretOf(string name);

    /// <summary>Where the accounts (and signup codes) are kept and whether that can be reached now. Never throws: a database that is down says so.</summary>
    Task<StoreStatus> StatusAsync(CancellationToken cancel = default);
}
