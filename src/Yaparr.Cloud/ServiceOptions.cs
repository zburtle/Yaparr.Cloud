namespace Yaparr.Cloud;

/// <summary>What an operator can set in <c>appsettings.json</c> (section <c>Service</c>) or with environment variables (<c>Service__MaxBlobBytes</c>).</summary>
public sealed class ServiceOptions
{
    /// <summary>Where the backups are kept.</summary>
    public string DataDirectory { get; set; } = "data";

    /// <summary>The largest backup accepted, in bytes (512 MiB).</summary>
    public long MaxBlobBytes { get; set; } = 512L * 1024 * 1024;

    /// <summary>How many of the newest versions of a backup are always kept (3).</summary>
    public int KeepVersions { get; set; } = 3;

    /// <summary>
    /// And the newest version of each of this many days, counting today (7): one a day for a week, so saving often does not push the older copies out. 0 keeps
    /// only the newest <see cref="KeepVersions"/>. See <see cref="Retention"/>.
    /// </summary>
    public int KeepDays { get; set; } = 7;

    /// <summary>
    /// A Postgres connection string (<c>Host=postgres;Database=yaparr;Username=yaparr;Password=...</c>). With one, accounts and signup codes are kept in the
    /// database, which is made and brought up to date when the service starts, and the files from before (<c>accounts.json</c>, <c>codes.json</c>) are
    /// imported once. Without one they are kept in those files. Backups are files either way.
    /// </summary>
    public string Database { get; set; } = "";

    /// <summary>
    /// With a bucket name, backups are kept as objects in an S3-compatible store (Cloudflare R2, DigitalOcean Spaces, MinIO) at <see cref="S3Endpoint"/>
    /// instead of as files in the data folder. Use a key that can only read and write that one bucket. Without one, they are files.
    /// </summary>
    public string S3Bucket { get; set; } = "";

    /// <summary>The store's address, for R2 <c>https://&lt;account id&gt;.r2.cloudflarestorage.com</c> (without the bucket name).</summary>
    public string S3Endpoint { get; set; } = "";

    public string S3AccessKey { get; set; } = "";

    public string S3SecretKey { get; set; } = "";

    /// <summary>The region name the store signs for: <c>auto</c> for R2, the region (<c>nyc3</c>) for Spaces.</summary>
    public string S3Region { get; set; } = "auto";

    /// <summary>A prefix for every key, so a bucket can be shared (<c>backups/</c> by default).</summary>
    public string S3Prefix { get; set; } = "backups/";

    /// <summary>
    /// The folder in the bucket for the daily dumps of the database (<c>db-dumps/</c> by default), apart from the backups and not inside their folder, so pruning one can
    /// never reach the other. See <see cref="DumpStore"/>.
    /// </summary>
    public string S3DumpPrefix { get; set; } = "db-dumps/";

    /// <summary>
    /// Whether a save is put with <c>If-None-Match: *</c>, so that it only succeeds if nobody saved that version first (R2 and MinIO take it). Turn it off for
    /// a store that does not; the check made before the upload still stops two devices, only two copies of the service at once could then both save.
    /// </summary>
    public bool S3ConditionalWrites { get; set; } = true;

    /// <summary>Requests per minute from one account (or one address, for a request that is not signed).</summary>
    public int RequestsPerMinute { get; set; } = 120;

    /// <summary>Backups saved per hour by one account (or address).</summary>
    public int WritesPerHour { get; set; } = 12;

    /// <summary>The address people reach this service at, like <c>https://backup.example.org</c>. It goes into the sign-up links the admin makes, and into what a new account is told to type into Yaparr. Without it, the address of the request is used for the second, and the first is left for the admin to fill in.</summary>
    public string PublicUrl { get; set; } = "";

    /// <summary>The page on the website where people sign up with a code. The link the admin sends is this address with the server and the code after a <c>#</c>.</summary>
    public string SignupPageUrl { get; set; } = "https://yaparr.com/signup";

    /// <summary>
    /// The Firebase service account key (the JSON file downloaded from the Firebase console) that lets this service wake phones that use Firebase Cloud Messaging.
    /// Without one only UnifiedPush works. Keep the file out of the repository and readable only by the service.
    /// </summary>
    public string PushFcmServiceAccountFile { get; set; } = "";

    /// <summary>How many times one wake token may be used in a minute (6).</summary>
    public int PushWakesPerMinute { get; set; } = 6;

    /// <summary>And in a day (200).</summary>
    public int PushWakesPerDay { get; set; } = 200;

    /// <summary>How many devices of one account may be registered for push notifications (5).</summary>
    public int PushMaxPerAccount { get; set; } = 5;

    /// <summary>How many synced records one account may hold (2000). Deleted records are kept as markers and count too.</summary>
    public int SyncMaxRecords { get; set; } = 2000;

    /// <summary>The longest body of one synced record, in base64 characters (65536, about 48 KB locked).</summary>
    public int SyncMaxRecordBytes { get; set; } = 65536;

    /// <summary>How many times one account may send synced records in a minute (30).</summary>
    public int SyncWritesPerMinute { get; set; } = 30;

    /// <summary>And in a day (5000).</summary>
    public int SyncWritesPerDay { get; set; } = 5000;

    /// <summary>True when a reverse proxy sits in front (which is how it gets https), so the caller's address is read from <c>X-Forwarded-For</c>. Leave it off when the service is reachable directly, or anyone could claim any address.</summary>
    public bool BehindProxy { get; set; }

}
