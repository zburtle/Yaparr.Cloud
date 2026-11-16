using System.Globalization;
using System.Text.Json;

namespace Yaparr.Cloud;

/// <summary>
/// When the last daily dump of the database was made. The dump is made on the machine, outside the service (<c>pg-backup.sh</c> from cron), which writes
/// <c>last-dump.json</c> into the data folder when one succeeds; the admin portal reads that to say whether the database is being backed up.
/// </summary>
public static class DumpStatus
{
    public const string FileName = "last-dump.json";

    /// <summary>A dump older than this is a problem.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(2);

    private sealed record Dump(DateTimeOffset At, long Bytes, string? File, bool? Uploaded = null);

    public static StoreStatus Read(ServiceOptions options, DateTimeOffset now)
    {
        const string kind = "Daily dump";
        const string where = "on the machine";
        var path = Path.Combine(Path.GetFullPath(options.DataDirectory), FileName);
        if (!File.Exists(path)) return new StoreStatus(kind, where, false, "none recorded yet (is pg-backup.sh in cron?)");
        try
        {
            var dump = JsonSerializer.Deserialize<Dump>(File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (dump == null) return new StoreStatus(kind, where, false, "the record of it cannot be read");
            var size = dump.Bytes >= 1024 * 1024 ? (dump.Bytes / 1024.0 / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " MiB" : Math.Max(1, dump.Bytes / 1024) + " KiB";
            var age = now - dump.At;
            var text = $"last {dump.At.UtcDateTime:yyyy-MM-dd HH:mm} UTC · {size}";
            // With a bucket set, a dump that did not get there is a problem too: it is what survives losing the machine.
            var inBucket = options.S3Bucket.Length > 0;
            if (inBucket) text += dump.Uploaded == true ? " · also in the bucket" : " · NOT copied to the bucket";
            if (age > MaxAge) return new StoreStatus(kind, where, false, text + $" · {(int)age.TotalDays} days ago");
            return new StoreStatus(kind, where, !inBucket || dump.Uploaded == true, text);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new StoreStatus(kind, where, false, "the record of it cannot be read");
        }
    }
}
