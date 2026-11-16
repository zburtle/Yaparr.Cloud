using Xunit;
using Yaparr.Cloud;

namespace Yaparr.Cloud.Tests;

/// <summary>What every store of synced records does, whichever way it keeps them (see <see cref="FileSyncStoreTests"/> and <see cref="PostgresSyncStoreTests"/>).</summary>
public abstract class SyncStoreContractTests : IDisposable
{
    protected abstract ISyncStore NewSync();

    public virtual void Dispose() { }

    private static SyncRecord Rec(string id, string clock, string body = "AAAA", bool deleted = false, string kind = "room", string device = "phone") =>
        new(kind, id, clock, device, deleted, deleted ? "" : body);

    [SkippableFact]
    public void Records_are_stored_with_the_accounts_next_cursor_and_pulled_in_the_order_they_changed()
    {
        var sync = NewSync();
        var put = sync.Put("sam@example.com", new[] { Rec("a", "0001"), Rec("b", "0001") }, 100);
        Assert.Equal((SyncPutOutcome.Stored, 2L), (put.Outcome, put.Cursor));
        Assert.Empty(put.Stale);

        var all = sync.Pull("sam@example.com", 0, 10);
        Assert.Equal(new[] { "a", "b" }, all.Records.Select(r => r.Id));
        Assert.Equal(new long[] { 1, 2 }, all.Records.Select(r => r.Cursor));
        Assert.Equal((2L, false), (all.Cursor, all.More));

        sync.Put("sam@example.com", new[] { Rec("a", "0002", "BBBB") }, 100);
        var after = sync.Pull("sam@example.com", 2, 10);
        var changed = Assert.Single(after.Records);
        Assert.Equal(("a", "BBBB", 3L), (changed.Id, changed.Body, changed.Cursor));
        Assert.Empty(sync.Pull("sam@example.com", 3, 10).Records);
    }

    [SkippableFact]
    public void A_pull_is_paged_and_says_where_the_next_one_starts()
    {
        var sync = NewSync();
        sync.Put("sam@example.com", Enumerable.Range(0, 5).Select(i => Rec("r" + i, "0001")).ToList(), 100);
        var first = sync.Pull("sam@example.com", 0, 2);
        Assert.Equal((2, 2L, true), (first.Records.Count, first.Cursor, first.More));
        var second = sync.Pull("sam@example.com", first.Cursor, 2);
        Assert.Equal((2, 4L, true), (second.Records.Count, second.Cursor, second.More));
        var third = sync.Pull("sam@example.com", second.Cursor, 2);
        Assert.Equal((1, 5L, false), (third.Records.Count, third.Cursor, third.More));
    }

    [SkippableFact]
    public void A_write_that_is_not_newer_is_not_taken_and_the_same_write_again_is_ignored()
    {
        var sync = NewSync();
        sync.Put("sam@example.com", new[] { Rec("a", "0005", "NEW1") }, 100);

        var old = sync.Put("sam@example.com", new[] { Rec("a", "0004", "OLD1") }, 100);
        Assert.Equal(new[] { "room/a" }, old.Stale);
        Assert.Equal(1L, old.Cursor);

        var again = sync.Put("sam@example.com", new[] { Rec("a", "0005", "NEW1") }, 100);
        Assert.Empty(again.Stale);
        Assert.Equal(1L, again.Cursor);   // nothing changed, so no new cursor

        var held = Assert.Single(sync.Pull("sam@example.com", 0, 10).Records);
        Assert.Equal(("NEW1", 1L), (held.Body, held.Cursor));
    }

    [SkippableFact]
    public void Clocks_are_compared_as_plain_text_whatever_the_language_of_the_database()
    {
        var sync = NewSync();
        sync.Put("sam@example.com", new[] { Rec("a", "Zz-1", "AAAA") }, 100);
        // As plain text (ordinal) "Zz-1" sorts before "aa-1"; a database's own ordering of text may put the lower case first.
        var put = sync.Put("sam@example.com", new[] { Rec("a", "aa-1", "BBBB") }, 100);
        Assert.Empty(put.Stale);
        Assert.Equal("BBBB", Assert.Single(sync.Pull("sam@example.com", 0, 10).Records).Body);
        Assert.Equal(new[] { "room/a" }, sync.Put("sam@example.com", new[] { Rec("a", "Zz-2") }, 100).Stale);
    }

    [SkippableFact]
    public void A_deleted_record_stays_as_a_marker_that_a_later_write_can_replace()
    {
        var sync = NewSync();
        sync.Put("sam@example.com", new[] { Rec("a", "0001", "KEY1") }, 100);
        sync.Put("sam@example.com", new[] { Rec("a", "0002", deleted: true) }, 100);
        var marker = Assert.Single(sync.Pull("sam@example.com", 0, 10).Records);
        Assert.Equal((true, "", 2L), (marker.Deleted, marker.Body, marker.Cursor));

        // A write older than the marker (a device that was away and did not know) does not bring the record back.
        Assert.Equal(new[] { "room/a" }, sync.Put("sam@example.com", new[] { Rec("a", "0001", "KEY1") }, 100).Stale);
        // Joining again later is newer than the marker, and replaces it.
        sync.Put("sam@example.com", new[] { Rec("a", "0003", "KEY2") }, 100);
        var back = Assert.Single(sync.Pull("sam@example.com", 0, 10).Records);
        Assert.Equal((false, "KEY2"), (back.Deleted, back.Body));
    }

    [SkippableFact]
    public void Kinds_and_ids_and_accounts_do_not_mix()
    {
        var sync = NewSync();
        sync.Put("sam@example.com", new[] { Rec("a", "0001", "AAAA", kind: "room"), Rec("a", "0001", "BBBB", kind: "block") }, 100);
        sync.Put("pat@example.com", new[] { Rec("a", "0001", "CCCC") }, 100);
        Assert.Equal(new[] { "AAAA", "BBBB" }, sync.Pull("sam@example.com", 0, 10).Records.Select(r => r.Body));
        Assert.Equal(new[] { "CCCC" }, sync.Pull("pat@example.com", 0, 10).Records.Select(r => r.Body));
        Assert.Equal(1L, sync.Pull("pat@example.com", 0, 10).Cursor);   // each account counts its own
    }

    [SkippableFact]
    public void A_call_takes_all_of_its_new_records_or_none_when_the_account_would_pass_its_limit()
    {
        var sync = NewSync();
        sync.Put("sam@example.com", new[] { Rec("a", "0001"), Rec("b", "0001") }, 3);
        var refused = sync.Put("sam@example.com", new[] { Rec("c", "0001"), Rec("d", "0001") }, 3);
        Assert.Equal(SyncPutOutcome.TooMany, refused.Outcome);
        Assert.Equal(2, sync.Pull("sam@example.com", 0, 10).Records.Count);

        // A new version of a record it already holds is not a new record, so it is taken at the limit.
        sync.Put("sam@example.com", new[] { Rec("c", "0001") }, 3);
        var update = sync.Put("sam@example.com", new[] { Rec("a", "0002", "BBBB") }, 3);
        Assert.Equal(SyncPutOutcome.Stored, update.Outcome);
    }

    [SkippableFact]
    public void Two_writes_of_one_record_in_one_call_are_taken_in_order_and_an_older_one_after_a_newer_is_stale()
    {
        var sync = NewSync();
        var put = sync.Put("sam@example.com", new[] { Rec("a", "0002", "NEW1"), Rec("a", "0001", "OLD1") }, 100);
        Assert.Equal(new[] { "room/a" }, put.Stale);
        Assert.Equal("NEW1", Assert.Single(sync.Pull("sam@example.com", 0, 10).Records).Body);
    }

    [SkippableFact]
    public void Removing_an_account_forgets_its_records_and_starts_its_cursor_again()
    {
        var sync = NewSync();
        sync.Put("sam@example.com", new[] { Rec("a", "0001") }, 100);
        sync.Put("pat@example.com", new[] { Rec("a", "0001") }, 100);
        sync.RemoveAccount("sam@example.com");
        Assert.Empty(sync.Pull("sam@example.com", 0, 10).Records);
        Assert.Single(sync.Pull("pat@example.com", 0, 10).Records);
        Assert.Equal(1L, sync.Put("sam@example.com", new[] { Rec("a", "0001") }, 100).Cursor);
    }
}

/// <summary>Synced records in files.</summary>
public sealed class FileSyncStoreTests : SyncStoreContractTests
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "yaparr-sync-" + Guid.NewGuid().ToString("N"));

    protected override ISyncStore NewSync() => new SyncStore(new ServiceOptions { DataDirectory = _dir });

    public override void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }
}

/// <summary>Synced records in Postgres: the same cases as for files (skipped unless <c>YAPARR_TEST_POSTGRES</c> is set).</summary>
public sealed class PostgresSyncStoreTests : SyncStoreContractTests
{
    private readonly PostgresTestDatabase _database = new();

    protected override ISyncStore NewSync() =>
        Stores.Sync(new ServiceOptions { DataDirectory = Path.Combine(Path.GetTempPath(), "yaparr-no-files-" + Guid.NewGuid().ToString("N")), Database = _database.ConnectionString });

    public override void Dispose() => _database.Dispose();
}
