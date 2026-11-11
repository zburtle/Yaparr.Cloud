namespace Yaparr.Cloud;

/// <summary>
/// Which of an account's saved backups to keep. A backup is saved when something that cannot be lost changes (a room, a key, a linked device) and once a day
/// for the history, so saving often must not push the older, known-good copies out: a bad save would then replace every good one. So what is kept is
/// <list type="bullet">
/// <item>the newest few versions, always (<see cref="ServiceOptions.KeepVersions"/>), and</item>
/// <item>the newest version of each of the last few days (<see cref="ServiceOptions.KeepDays"/>), counting today: a copy from each morning of the week.</item>
/// </list>
/// Days are UTC days. Everything else goes. At most <c>KeepVersions + KeepDays</c> versions are kept, and the newest is never removed.
/// </summary>
public static class Retention
{
    /// <summary>The versions to delete, out of all that exist (including the one just saved), as of <paramref name="now"/>.</summary>
    public static IReadOnlyList<int> ToDelete(IReadOnlyList<BackupVersion> versions, DateTimeOffset now, int keepNewest, int keepDays)
    {
        var newestFirst = versions.OrderByDescending(v => v.Version).ToList();
        var keep = new HashSet<int>(newestFirst.Take(Math.Max(1, keepNewest)).Select(v => v.Version));

        if (keepDays > 0)
        {
            var today = now.UtcDateTime.Date;
            // The first of a day's versions in this order is its newest (versions only go up with time).
            foreach (var day in newestFirst.GroupBy(v => v.At.UtcDateTime.Date))
                if ((today - day.Key).TotalDays < keepDays) keep.Add(day.First().Version);
        }

        return newestFirst.Where(v => !keep.Contains(v.Version)).Select(v => v.Version).ToList();
    }
}
