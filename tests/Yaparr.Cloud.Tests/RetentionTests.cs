using Yaparr.Cloud;

namespace Yaparr.Cloud.Tests;

public sealed class RetentionTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 15, 0, 0, TimeSpan.Zero);

    // Versions numbered from 1, each made at the given moment.
    private static List<BackupVersion> Made(params DateTimeOffset[] at) => at.Select((t, i) => new BackupVersion(i + 1, 1000, t)).ToList();

    private static int[] Deleted(IReadOnlyList<BackupVersion> versions, int keepNewest = 3, int keepDays = 7) =>
        Retention.ToDelete(versions, Now, keepNewest, keepDays).Order().ToArray();

    [Fact]
    public void A_few_backups_are_all_kept()
    {
        Assert.Empty(Deleted(Made(Now.AddHours(-30), Now.AddHours(-2), Now.AddMinutes(-5))));
        Assert.Empty(Deleted(Made(Now)));
        Assert.Empty(Deleted(new List<BackupVersion>()));
    }

    [Fact]
    public void Saving_often_in_one_day_keeps_the_newest_few_and_does_not_touch_other_days()
    {
        // Yesterday: one. Today: ten, a few minutes apart (a busy morning of changes).
        var versions = Made(new[] { Now.AddDays(-1) }.Concat(Enumerable.Range(0, 10).Select(i => Now.AddMinutes(-100 + i * 5))).ToArray());
        // Kept: the newest three (11, 10, 9), yesterday's only one (1), and today's newest (11, already kept).
        Assert.Equal(new[] { 2, 3, 4, 5, 6, 7, 8 }, Deleted(versions));
    }

    [Fact]
    public void One_a_day_is_kept_for_a_week_and_the_newest_of_each_day_is_the_one()
    {
        // Two a day for ten days, at 08:00 and 20:00 UTC, ending this morning.
        var times = new List<DateTimeOffset>();
        for (int day = 9; day >= 0; day--)
        {
            var date = Now.UtcDateTime.Date.AddDays(-day);
            times.Add(new DateTimeOffset(date.AddHours(8), TimeSpan.Zero));
            if (day > 0) times.Add(new DateTimeOffset(date.AddHours(20), TimeSpan.Zero));
        }
        var versions = Made(times.ToArray());
        var kept = versions.Select(v => v.Version).Except(Deleted(versions)).Order().ToArray();

        // The newest three are kept (the last three versions), and the newest of each of the last 7 days (today and six before).
        var newestOfDay = versions.Where(v => (Now.UtcDateTime.Date - v.At.UtcDateTime.Date).TotalDays < 7)
            .GroupBy(v => v.At.UtcDateTime.Date).Select(g => g.Max(v => v.Version));
        var expected = versions.OrderByDescending(v => v.Version).Take(3).Select(v => v.Version).Concat(newestOfDay).Distinct().Order().ToArray();
        Assert.Equal(expected, kept);
        Assert.True(kept.Length <= 3 + 7);
        Assert.Contains(versions[^1].Version, kept);                    // the newest is always kept
        Assert.DoesNotContain(1, kept);                                 // nothing from ten days ago

        // The one kept for a past day is that day's evening one, the newest.
        var sixDaysAgo = versions.Where(v => v.At.UtcDateTime.Date == Now.UtcDateTime.Date.AddDays(-6)).ToList();
        Assert.Equal(sixDaysAgo.Max(v => v.Version), kept.Intersect(sixDaysAgo.Select(v => v.Version)).Single());
    }

    [Fact]
    public void A_day_is_a_utc_day_and_a_week_is_seven_of_them_counting_today()
    {
        var today = Now.UtcDateTime.Date;
        var versions = Made(
            new DateTimeOffset(today.AddDays(-7).AddHours(23), TimeSpan.Zero),   // seven days back: out
            new DateTimeOffset(today.AddDays(-6).AddHours(1), TimeSpan.Zero),    // six back: in
            Now.AddMinutes(-3), Now.AddMinutes(-2), Now.AddMinutes(-1));         // the newest three
        Assert.Equal(new[] { 1 }, Deleted(versions));
    }

    [Fact]
    public void With_no_days_only_the_newest_are_kept_and_the_newest_is_never_removed()
    {
        var versions = Made(Now.AddDays(-5), Now.AddDays(-4), Now.AddDays(-3), Now.AddDays(-2), Now.AddDays(-1));
        Assert.Equal(new[] { 1, 2 }, Deleted(versions, keepNewest: 3, keepDays: 0));
        Assert.Equal(new[] { 1, 2, 3, 4 }, Deleted(versions, keepNewest: 0, keepDays: 0));   // at least one is always kept, even if told none
    }

    [Fact]
    public void A_date_in_the_future_is_not_a_reason_to_throw_a_backup_away()
    {
        var versions = Made(Now.AddDays(3), Now.AddDays(3).AddMinutes(1), Now.AddDays(3).AddMinutes(2), Now.AddDays(3).AddMinutes(3), Now.AddDays(3).AddMinutes(4));
        Assert.Equal(new[] { 1, 2 }, Deleted(versions));                     // the newest three, and the newest of that (future) day is among them
    }

    // ---- in the store, with real files ----

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "yaparr-retention-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static MemoryStream Locked() { var b = new byte[200]; BackupStore.Magic.CopyTo(b, 0); return new MemoryStream(b); }

    [Fact]
    public async Task The_store_thins_by_the_dates_of_the_files_after_each_save()
    {
        var clock = Now;
        var options = new ServiceOptions { DataDirectory = _dir, KeepVersions = 3, KeepDays = 7 };
        var store = new BackupStore(options, () => clock);
        var account = BackupStore.FolderOf("sam@example.com");

        // Twelve days of one save a day, then three more on the last day, each save made "at" its moment.
        var moments = Enumerable.Range(0, 12).Select(d => Now.AddDays(-11 + d).AddHours(-3)).Concat([Now.AddHours(-2), Now.AddHours(-1), Now.AddMinutes(-1)]).ToList();
        int based = 0;
        foreach (var moment in moments)
        {
            clock = moment;
            var saved = await store.SaveAsync(account, based, Locked(), default);
            Assert.Equal(SaveOutcome.Saved, saved.Outcome);
            based = saved.Version;
            // The file's own date is what thinning reads later, so it is set to the moment it stands for.
            File.SetLastWriteTimeUtc(Path.Combine(_dir, account, $"v{based:D8}.bin"), moment.UtcDateTime);
        }

        var kept = store.Versions(account).Select(v => v.Version).Order().ToArray();
        Assert.True(kept.Length <= 10, "at most KeepVersions + KeepDays are kept: " + string.Join(",", kept));
        Assert.Contains(15, kept);                                   // the newest
        Assert.Contains(14, kept);
        Assert.Contains(13, kept);                                   // the newest three
        Assert.DoesNotContain(1, kept);                              // eleven days old
        Assert.DoesNotContain(2, kept);
        Assert.DoesNotContain(3, kept);
        Assert.DoesNotContain(4, kept);                              // five more back than a week
        // The saves of days well inside the week are still there, one each (12 is today's first save of three... but today's newest is 15).
        Assert.Contains(11, kept);                                   // yesterday's
        Assert.Contains(10, kept);                                   // the day before
    }
}
