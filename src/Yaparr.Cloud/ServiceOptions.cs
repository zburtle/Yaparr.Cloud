namespace Yaparr.Cloud;

/// <summary>What an operator can set in <c>appsettings.json</c> (section <c>Service</c>) or with environment variables (<c>Service__MaxBlobBytes</c>).</summary>
public sealed class ServiceOptions
{
    /// <summary>Where the backups are kept.</summary>
    public string DataDirectory { get; set; } = "data";

    /// <summary>The largest backup accepted, in bytes (512 MiB).</summary>
    public long MaxBlobBytes { get; set; } = 512L * 1024 * 1024;

    /// <summary>How many versions of a backup are kept; an older one is removed when a newer one is saved.</summary>
    public int KeepVersions { get; set; } = 5;

    /// <summary>Requests per minute from one account (or one address, for a request that is not signed).</summary>
    public int RequestsPerMinute { get; set; } = 120;

    /// <summary>Backups saved per hour by one account (or address).</summary>
    public int WritesPerHour { get; set; } = 12;

    /// <summary>The address people reach this service at, like <c>https://backup.example.org</c>. It goes into the sign-up links the admin makes, and into what a new account is told to type into Yaparr. Without it, the address of the request is used for the second, and the first is left for the admin to fill in.</summary>
    public string PublicUrl { get; set; } = "";

    /// <summary>The page on the website where people sign up with a code. The link the admin sends is this address with the server and the code after a <c>#</c>.</summary>
    public string SignupPageUrl { get; set; } = "https://yaparr.com/signup";

    /// <summary>True when a reverse proxy sits in front (which is how it gets https), so the caller's address is read from <c>X-Forwarded-For</c>. Leave it off when the service is reachable directly, or anyone could claim any address.</summary>
    public bool BehindProxy { get; set; }

}
