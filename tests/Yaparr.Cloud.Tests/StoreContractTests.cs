using Npgsql;
using Xunit;
using Yaparr.Cloud;

namespace Yaparr.Cloud.Tests;

/// <summary>
/// A database of its own for a test (a schema in the Postgres that <c>YAPARR_TEST_POSTGRES</c> names, a connection string like
/// <c>Host=127.0.0.1;Port=54329;Username=yaparr;Password=test;Database=yaparr</c>), dropped afterwards. Tests that need one are skipped when the
/// variable is not set; in CI it always is.
/// </summary>
internal sealed class PostgresTestDatabase : IDisposable
{
    public const string Variable = "YAPARR_TEST_POSTGRES";
    private readonly string _baseConnection;
    private readonly string _schema = "t_" + Guid.NewGuid().ToString("N");

    public PostgresTestDatabase()
    {
        Skip.If(string.IsNullOrEmpty(Environment.GetEnvironmentVariable(Variable)), $"Set {Variable} to a Postgres connection string to run these.");
        _baseConnection = Environment.GetEnvironmentVariable(Variable)!;
        Run($"create schema {_schema}");
        ConnectionString = _baseConnection + ";Search Path=" + _schema;
    }

    public string ConnectionString { get; } = "";

    private void Run(string sql)
    {
        using var source = NpgsqlDataSource.Create(_baseConnection);
        using var command = source.CreateCommand(sql);
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        if (ConnectionString.Length == 0) return;
        Stores.Forget(ConnectionString);
        Run($"drop schema if exists {_schema} cascade");
    }
}

/// <summary>What every store of accounts and signup codes does, whichever way it keeps them (see <see cref="FileStoreTests"/> and <see cref="PostgresStoreTests"/>).</summary>
public abstract class StoreContractTests : IDisposable
{
    protected const string Password = "correct horse battery";

    protected abstract IAccounts NewAccounts();

    protected abstract ISignupCodes NewCodes(Func<DateTimeOffset>? now = null);

    protected abstract IPushRegistry NewPush();

    protected abstract ISubscriptions NewSubscriptions();

    public virtual void Dispose() { }

    // ---- accounts ----

    [SkippableFact]
    public void An_account_is_added_with_a_password_and_a_name_cannot_be_added_twice()
    {
        var accounts = NewAccounts();
        Assert.False(accounts.Exists("sam@example.com"));
        Assert.True(accounts.Add("sam@example.com", Password));
        Assert.False(accounts.Add("sam@example.com", "another password entirely"));
        Assert.True(accounts.Exists("sam@example.com"));
        Assert.True(accounts.HasPassword("sam@example.com"));
        Assert.True(accounts.CheckPassword("sam@example.com", Password));
        Assert.False(accounts.CheckPassword("sam@example.com", "another password entirely"));
    }

    [SkippableFact]
    public void A_wrong_password_and_an_account_that_is_not_there_are_both_refused()
    {
        var accounts = NewAccounts();
        accounts.Add("sam@example.com", Password);
        Assert.False(accounts.CheckPassword("sam@example.com", ""));
        Assert.False(accounts.CheckPassword("nobody@example.com", Password));
        Assert.False(accounts.Exists("nobody@example.com"));
        Assert.False(accounts.HasPassword("nobody@example.com"));
    }

    [SkippableFact]
    public void A_new_password_replaces_the_old_and_signs_every_device_out()
    {
        var accounts = NewAccounts();
        accounts.Add("sam@example.com", Password);
        var token = accounts.IssueToken("sam@example.com", "phone")!;
        Assert.Equal("sam@example.com", accounts.UserOfToken(token));

        Assert.True(accounts.SetPassword("sam@example.com", "a brand new password"));
        Assert.False(accounts.CheckPassword("sam@example.com", Password));
        Assert.True(accounts.CheckPassword("sam@example.com", "a brand new password"));
        Assert.Null(accounts.UserOfToken(token));
        Assert.False(accounts.SetPassword("nobody@example.com", "a brand new password"));
    }

    [SkippableFact]
    public void A_device_signs_in_with_a_token_that_names_its_account_and_can_be_signed_out()
    {
        var accounts = NewAccounts();
        accounts.Add("sam@example.com", Password);
        accounts.Add("kim@example.com", Password);
        var sam = accounts.IssueToken("sam@example.com", "phone")!;
        var kim = accounts.IssueToken("kim@example.com", "laptop")!;
        Assert.StartsWith("ycl_", sam);
        Assert.NotEqual(sam, kim);
        Assert.Equal("sam@example.com", accounts.UserOfToken(sam));
        Assert.Equal("kim@example.com", accounts.UserOfToken(kim));
        Assert.Null(accounts.UserOfToken("ycl_made-up"));
        Assert.Null(accounts.UserOfToken("not-a-token"));
        Assert.Null(accounts.UserOfToken(null));
        Assert.Null(accounts.UserOfToken("ycl_" + new string('x', 200)));

        Assert.True(accounts.RevokeToken(sam));
        Assert.Null(accounts.UserOfToken(sam));
        Assert.Equal("kim@example.com", accounts.UserOfToken(kim));
        Assert.False(accounts.RevokeToken(sam));
    }

    [SkippableFact]
    public void An_account_lists_its_devices_newest_first_and_signs_one_out_by_its_id_and_only_its_own()
    {
        var accounts = NewAccounts();
        accounts.Add("sam@example.com", Password);
        accounts.Add("kim@example.com", Password);
        var phone = accounts.IssueToken("sam@example.com", "phone")!;
        System.Threading.Thread.Sleep(20);
        var laptop = accounts.IssueToken("sam@example.com", "laptop")!;
        var kim = accounts.IssueToken("kim@example.com", "tablet")!;

        var devices = accounts.DevicesOf("sam@example.com");
        Assert.Equal(new[] { "laptop", "phone" }, devices.Select(d => d.Name));
        Assert.All(devices, d => Assert.Equal(16, d.Id.Length));
        Assert.Equal(AccountDirectory.DeviceIdOf(AccountDirectory.HashOf(laptop)), devices[0].Id);
        Assert.Empty(accounts.DevicesOf("nobody@example.com"));

        Assert.False(accounts.RevokeDevice("kim@example.com", devices[1].Id));         // another account's device
        Assert.False(accounts.RevokeDevice("sam@example.com", "0123456789abcdef"));      // no such device
        Assert.False(accounts.RevokeDevice("sam@example.com", "short"));
        Assert.Equal("sam@example.com", accounts.UserOfToken(phone));
        Assert.True(accounts.RevokeDevice("sam@example.com", devices[1].Id));
        Assert.Null(accounts.UserOfToken(phone));
        Assert.Equal("sam@example.com", accounts.UserOfToken(laptop));
        Assert.Equal("kim@example.com", accounts.UserOfToken(kim));
        Assert.Equal(new[] { "laptop" }, accounts.DevicesOf("sam@example.com").Select(d => d.Name));
    }

    [SkippableFact]
    public void The_same_device_signing_in_again_takes_the_place_of_its_earlier_sign_in_and_other_devices_stay()
    {
        var accounts = NewAccounts();
        accounts.Add("sam@example.com", Password);
        accounts.Add("kim@example.com", Password);
        const string laptop = "0123456789abcdef0123456789abcdef", phone = "fedcba9876543210fedcba9876543210";
        var first = accounts.IssueToken("sam@example.com", "laptop", laptop)!;
        var phoneToken = accounts.IssueToken("sam@example.com", "phone", phone)!;
        var other = accounts.IssueToken("sam@example.com", "no id")!;
        var kim = accounts.IssueToken("kim@example.com", "laptop", laptop)!;           // the same id on another account is another device

        var again = accounts.IssueToken("sam@example.com", "laptop (renamed)", laptop)!;
        Assert.Null(accounts.UserOfToken(first));                                       // the earlier sign-in of that device is gone
        Assert.Equal("sam@example.com", accounts.UserOfToken(again));
        Assert.Equal("sam@example.com", accounts.UserOfToken(phoneToken));
        Assert.Equal("sam@example.com", accounts.UserOfToken(other));
        Assert.Equal("kim@example.com", accounts.UserOfToken(kim));
        Assert.Equal(3, accounts.DevicesOf("sam@example.com").Count);
        Assert.Contains(accounts.DevicesOf("sam@example.com"), d => d.Name == "laptop (renamed)");

        // Devices that give no id (or a bad one) are never taken for each other: each sign-in is its own.
        accounts.IssueToken("sam@example.com", "no id");
        accounts.IssueToken("sam@example.com", "bad", "short");
        accounts.IssueToken("sam@example.com", "bad", "short");
        Assert.Equal(6, accounts.DevicesOf("sam@example.com").Count);
        Assert.Equal("", AccountDirectory.KeyHashOf("short"));
        Assert.Equal("", AccountDirectory.KeyHashOf("has spaces in it which is not allowed"));
        Assert.NotEqual("", AccountDirectory.KeyHashOf(laptop));
        Assert.DoesNotContain(laptop, AccountDirectory.KeyHashOf(laptop));
    }

    [SkippableFact]
    public void The_same_machine_signing_in_again_without_its_own_id_replaces_its_entry_only_with_the_same_name()
    {
        var accounts = NewAccounts();
        accounts.Add("sam@example.com", Password);
        accounts.Add("kim@example.com", Password);
        const string machine = "a1b2c3d4e5f60718293a4b5c6d7e8f90a1b2c3d4e5f60718293a4b5c6d7e8f90";
        var first = accounts.IssueToken("sam@example.com", "ZACHDESKTOP", deviceKey: null, machineKey: machine)!;
        var clone = accounts.IssueToken("sam@example.com", "A CLONE", deviceKey: null, machineKey: machine)!;      // the same machine id, another name: a cloned image, another device
        var kim = accounts.IssueToken("kim@example.com", "ZACHDESKTOP", deviceKey: null, machineKey: machine)!;     // another account

        var again = accounts.IssueToken("sam@example.com", "ZACHDESKTOP", deviceKey: "0123456789abcdef0123456789abcdef", machineKey: machine)!;
        Assert.Null(accounts.UserOfToken(first));                                       // the machine signing in again replaced its earlier entry ...
        Assert.Equal("sam@example.com", accounts.UserOfToken(clone));                   // ... but not the clone
        Assert.Equal("kim@example.com", accounts.UserOfToken(kim));
        Assert.Equal("sam@example.com", accounts.UserOfToken(again));
        Assert.Equal(new[] { "A CLONE", "ZACHDESKTOP" }, accounts.DevicesOf("sam@example.com").Select(d => d.Name).Order());

        // No machine id (or a bad one) never matches anything.
        accounts.IssueToken("sam@example.com", "ZACHDESKTOP");
        accounts.IssueToken("sam@example.com", "ZACHDESKTOP", machineKey: "short");
        accounts.IssueToken("sam@example.com", "ZACHDESKTOP", machineKey: "short");
        Assert.Equal(5, accounts.DevicesOf("sam@example.com").Count);
        Assert.NotEqual(AccountDirectory.KeyHashOf(machine), AccountDirectory.KeyHashOf(machine, "machine-key:"));   // the two kinds of id are kept apart
    }

    [SkippableFact]
    public void No_token_is_made_for_an_account_that_is_not_there_and_only_a_hash_of_one_is_kept()
    {
        var accounts = NewAccounts();
        Assert.Null(accounts.IssueToken("nobody@example.com", "phone"));
    }

    [SkippableFact]
    public void Only_the_newest_twenty_devices_stay_signed_in()
    {
        var accounts = NewAccounts();
        accounts.Add("sam@example.com", Password);
        var tokens = Enumerable.Range(0, 23).Select(i => accounts.IssueToken("sam@example.com", "device " + i)!).ToList();
        Assert.All(tokens.Take(3), t => Assert.Null(accounts.UserOfToken(t)));
        Assert.All(tokens.Skip(3), t => Assert.Equal("sam@example.com", accounts.UserOfToken(t)));
        Assert.Equal(20, accounts.List().Single().Devices);
    }

    [SkippableFact]
    public void Removing_an_account_signs_its_devices_out_and_frees_the_name()
    {
        var accounts = NewAccounts();
        accounts.Add("sam@example.com", Password);
        var token = accounts.IssueToken("sam@example.com", "phone")!;
        Assert.True(accounts.Remove("sam@example.com"));
        Assert.False(accounts.Remove("sam@example.com"));
        Assert.False(accounts.Exists("sam@example.com"));
        Assert.Null(accounts.UserOfToken(token));
        Assert.True(accounts.Add("sam@example.com", Password));
    }

    [SkippableFact]
    public void The_list_shows_each_account_with_its_devices_in_order_of_name()
    {
        var accounts = NewAccounts();
        Assert.Empty(accounts.List());
        accounts.Add("zed@example.com", Password);
        accounts.Add("amy@example.com", Password);
        accounts.IssueToken("amy@example.com", "a");
        accounts.IssueToken("amy@example.com", "b");
        var list = accounts.List();
        Assert.Equal(new[] { "amy@example.com", "zed@example.com" }, list.Select(a => a.Name));
        Assert.Equal(2, list[0].Devices);
        Assert.Equal(0, list[1].Devices);
        Assert.All(list, a => { Assert.True(a.HasPassword); Assert.Equal(AccountDirectory.LifetimePass, a.Pass); Assert.False(a.IsAdmin); Assert.Null(a.SignupCode); });
        Assert.InRange(list[0].Created, DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(1));
    }

    [SkippableFact]
    public async Task The_store_says_where_it_is_and_that_it_answers()
    {
        var accounts = NewAccounts();
        accounts.Add("sam@example.com", Password);
        var status = await accounts.StatusAsync();
        Assert.True(status.Ok, status.Detail);
        Assert.NotEmpty(status.Kind);
        Assert.NotEmpty(status.Where);
        Assert.Contains("1 account", status.Detail);
    }

    // ---- signing up ----

    [SkippableFact]
    public void A_sign_up_makes_an_account_that_cannot_sign_in_until_a_password_is_chosen_with_the_token()
    {
        var accounts = NewAccounts();
        var token = accounts.StartSignup("new@example.com", "abcd1234")!;
        Assert.StartsWith("set_", token);
        Assert.True(accounts.Exists("new@example.com"));
        Assert.False(accounts.HasPassword("new@example.com"));
        Assert.False(accounts.CheckPassword("new@example.com", Password));
        var entry = accounts.List().Single();
        Assert.Equal("abcd1234", entry.SignupCode);
        Assert.False(entry.HasPassword);

        Assert.Equal("new@example.com", accounts.SetupFor(token));
        Assert.Equal("new@example.com", accounts.SetupFor(token));          // looking does not use it
        Assert.Equal("new@example.com", accounts.FinishSetup(token, Password));
        Assert.True(accounts.HasPassword("new@example.com"));
        Assert.True(accounts.CheckPassword("new@example.com", Password));
        Assert.Null(accounts.SetupFor(token));
        Assert.Null(accounts.FinishSetup(token, "a second password!"));      // once
        Assert.True(accounts.CheckPassword("new@example.com", Password));
    }

    [SkippableFact]
    public void Coming_back_before_choosing_a_password_gives_a_new_token_and_the_old_one_stops()
    {
        var accounts = NewAccounts();
        var first = accounts.StartSignup("new@example.com", "abcd1234")!;
        var second = accounts.StartSignup("new@example.com", "abcd1234")!;
        Assert.NotEqual(first, second);
        Assert.Null(accounts.SetupFor(first));
        Assert.Equal("new@example.com", accounts.SetupFor(second));
        Assert.Single(accounts.List());
    }

    [SkippableFact]
    public void A_sign_up_for_an_account_that_has_a_password_is_refused()
    {
        var accounts = NewAccounts();
        accounts.Add("sam@example.com", Password);
        Assert.Null(accounts.StartSignup("sam@example.com", "abcd1234"));
        Assert.True(accounts.CheckPassword("sam@example.com", Password));
        Assert.Null(accounts.List().Single().SignupCode);
    }

    [SkippableFact]
    public void A_setup_token_that_is_not_one_is_refused()
    {
        var accounts = NewAccounts();
        accounts.StartSignup("new@example.com", null);
        Assert.Null(accounts.SetupFor(null));
        Assert.Null(accounts.SetupFor(""));
        Assert.Null(accounts.SetupFor("set_made-up"));
        Assert.Null(accounts.SetupFor("ycl_made-up"));
        Assert.Null(accounts.SetupFor("set_" + new string('x', 200)));
        Assert.Null(accounts.FinishSetup("set_made-up", Password));
        Assert.False(accounts.HasPassword("new@example.com"));
    }

    [SkippableFact]
    public void Finishing_a_sign_up_leaves_no_device_signed_in()
    {
        var accounts = NewAccounts();
        var token = accounts.StartSignup("new@example.com", null)!;
        accounts.FinishSetup(token, Password);
        Assert.Equal(0, accounts.List().Single().Devices);
    }

    // ---- the admin flag ----

    [SkippableFact]
    public void Only_an_account_with_a_password_can_be_made_an_admin_and_the_secret_changes_each_time()
    {
        var accounts = NewAccounts();
        Assert.False(accounts.AnyAdmin());
        Assert.Null(accounts.MakeAdmin("nobody@example.com"));
        var token = accounts.StartSignup("new@example.com", null)!;
        Assert.Null(accounts.MakeAdmin("new@example.com"));           // no password yet
        accounts.FinishSetup(token, Password);
        accounts.Add("boss@example.com", Password);

        var secret = accounts.MakeAdmin("boss@example.com")!;
        Assert.Matches("^[A-Z2-7]{32}$", secret);
        Assert.True(accounts.IsAdmin("boss@example.com"));
        Assert.True(accounts.AnyAdmin());
        Assert.Equal(secret, accounts.TotpSecretOf("boss@example.com"));
        Assert.True(accounts.List().Single(a => a.Name == "boss@example.com").IsAdmin);
        Assert.False(accounts.IsAdmin("new@example.com"));
        Assert.Null(accounts.TotpSecretOf("new@example.com"));

        var again = accounts.MakeAdmin("boss@example.com")!;
        Assert.NotEqual(secret, again);
        Assert.Equal(again, accounts.TotpSecretOf("boss@example.com"));
    }

    [SkippableFact]
    public void Taking_the_portal_away_removes_the_flag_and_the_secret()
    {
        var accounts = NewAccounts();
        accounts.Add("boss@example.com", Password);
        accounts.MakeAdmin("boss@example.com");
        Assert.True(accounts.RemoveAdmin("boss@example.com"));
        Assert.False(accounts.IsAdmin("boss@example.com"));
        Assert.False(accounts.AnyAdmin());
        Assert.Null(accounts.TotpSecretOf("boss@example.com"));
        Assert.False(accounts.RemoveAdmin("boss@example.com"));
        Assert.False(accounts.RemoveAdmin("nobody@example.com"));
    }

    // ---- signup codes ----

    [SkippableFact]
    public void A_code_is_made_listed_and_found_however_it_is_typed()
    {
        var codes = NewCodes();
        var (code, info) = codes.Create("book club", 5, TimeSpan.FromDays(10));
        Assert.Matches("^[A-HJ-NP-Z2-9]{4}(-[A-HJ-NP-Z2-9]{4}){3}$", code);
        Assert.Equal(8, info.Id.Length);
        var listed = Assert.Single(codes.List());
        Assert.Equal(info.Id, listed.Id);
        Assert.Equal("book club", listed.Note);
        Assert.Equal(5, listed.MaxUses);
        Assert.NotNull(listed.Expires);
        Assert.False(listed.Revoked);
        Assert.Empty(listed.Uses);

        Assert.Equal(info.Id, codes.Check(code));
        Assert.Equal(info.Id, codes.Check(code.ToLowerInvariant().Replace("-", " ")));
        Assert.Null(codes.Check("AAAA-AAAA-AAAA-AAAA"));
        Assert.Null(codes.Check(""));
        Assert.Null(codes.Check(null));
        Assert.NotEqual(code, codes.Create("", null, null).Code);
        Assert.Equal(2, codes.List().Count);
    }

    [SkippableFact]
    public void A_note_that_is_too_long_is_cut()
    {
        var codes = NewCodes();
        codes.Create(new string('n', 300), null, null);
        Assert.Equal(100, codes.List().Single().Note.Length);
    }

    [SkippableFact]
    public void Using_a_code_counts_each_person_once_and_a_limit_is_a_limit()
    {
        var codes = NewCodes();
        var (code, info) = codes.Create("", 2, null);
        Assert.Equal(info.Id, codes.Use(code, "a@example.com"));
        Assert.Equal(info.Id, codes.Use(code, "a@example.com"));        // the same person coming back to finish: not counted again
        Assert.Equal(info.Id, codes.Use(code, "b@example.com"));
        Assert.Null(codes.Use(code, "c@example.com"));                  // used up
        Assert.Null(codes.Check(code));                                 // used up for anyone new
        Assert.Equal(info.Id, codes.Check(code, "a@example.com"));      // but not for someone who already used it
        Assert.Null(codes.Check(code, "c@example.com"));
        var uses = codes.List().Single().Uses;
        Assert.Equal(new[] { "a@example.com", "b@example.com" }, uses.Select(u => u.Email).OrderBy(e => e));
        Assert.Equal("used up", codes.List().Single().Problem(DateTimeOffset.UtcNow));
    }

    [SkippableFact]
    public void A_code_without_a_limit_can_be_used_by_any_number_of_people()
    {
        var codes = NewCodes();
        var (code, _) = codes.Create("", null, null);
        for (int i = 0; i < 30; i++) Assert.NotNull(codes.Use(code, $"p{i}@example.com"));
        Assert.Equal(30, codes.List().Single().Uses.Count);
        Assert.NotNull(codes.Check(code));
    }

    [SkippableFact]
    public void A_code_stops_working_when_it_ends_and_a_person_who_used_it_is_not_let_back_after()
    {
        var now = new DateTimeOffset(2026, 11, 15, 9, 0, 0, TimeSpan.Zero);
        var codes = NewCodes(() => now);
        var (code, _) = codes.Create("", null, TimeSpan.FromDays(2));
        Assert.NotNull(codes.Use(code, "a@example.com"));
        now += TimeSpan.FromDays(1);
        Assert.NotNull(codes.Check(code));
        now += TimeSpan.FromDays(2);
        Assert.Null(codes.Check(code));
        Assert.Null(codes.Check(code, "a@example.com"));
        Assert.Null(codes.Use(code, "a@example.com"));
        Assert.Null(codes.Use(code, "b@example.com"));
        Assert.Equal("expired", codes.List().Single().Problem(now));
    }

    [SkippableFact]
    public void A_revoked_code_stops_for_everyone_and_keeps_its_record()
    {
        var codes = NewCodes();
        var (code, info) = codes.Create("", null, null);
        codes.Use(code, "a@example.com");
        Assert.True(codes.Revoke(info.Id));
        Assert.Null(codes.Check(code));
        Assert.Null(codes.Check(code, "a@example.com"));
        Assert.Null(codes.Use(code, "a@example.com"));
        Assert.Null(codes.Use(code, "b@example.com"));
        var listed = codes.List().Single();
        Assert.True(listed.Revoked);
        Assert.Single(listed.Uses);
        Assert.True(codes.Revoke(info.Id.ToUpperInvariant()));         // an id is understood in either case
        Assert.False(codes.Revoke("nothing"));
    }

    [SkippableFact]
    public void A_deleted_code_is_gone_with_who_used_it_and_the_others_are_left()
    {
        var codes = NewCodes();
        var (code, info) = codes.Create("one", null, null);
        var (other, otherInfo) = codes.Create("two", null, null);
        codes.Use(code, "a@example.com");
        Assert.True(codes.Delete(info.Id));
        Assert.False(codes.Delete(info.Id));
        Assert.False(codes.Delete("nothing"));
        Assert.Null(codes.Check(code));
        Assert.Null(codes.Use(code, "b@example.com"));
        Assert.Equal(otherInfo.Id, Assert.Single(codes.List()).Id);
        Assert.Equal(otherInfo.Id, codes.Check(other));
    }

    [SkippableFact]
    public void The_codes_are_listed_oldest_first_with_the_time_each_person_used_one()
    {
        var now = new DateTimeOffset(2026, 11, 15, 9, 0, 0, TimeSpan.Zero);
        var codes = NewCodes(() => now);
        var (first, _) = codes.Create("first", null, null);
        now += TimeSpan.FromHours(1);
        codes.Create("second", null, null);
        now += TimeSpan.FromHours(1);
        codes.Use(first, "a@example.com");
        var list = codes.List();
        Assert.Equal(new[] { "first", "second" }, list.Select(c => c.Note));
        Assert.Equal(now, list[0].Uses.Single().At);
        Assert.Equal(new DateTimeOffset(2026, 11, 15, 9, 0, 0, TimeSpan.Zero), list[0].Created);
    }

    // ---- push registrations ----

    [SkippableFact]
    public void A_device_is_registered_once_and_keeps_its_token_when_it_registers_again()
    {
        var push = NewPush();
        var first = push.Register("sam@example.com", "phone", PushKind.UnifiedPush, "https://push.example.org/up/abc", 5);
        Assert.Equal(PushRegisterOutcome.Created, first.Outcome);
        Assert.True(WakeTokens.Looks(first.Token));

        var again = push.Register("sam@example.com", "phone", PushKind.Fcm, "device-token", 5);
        Assert.Equal(PushRegisterOutcome.Updated, again.Outcome);
        Assert.Null(again.Token);
        var found = push.Find(first.Token)!;
        Assert.Equal(("sam@example.com", "phone", PushKind.Fcm, "device-token"), (found.Account, found.DeviceId, found.Kind, found.Endpoint));
        Assert.Single(push.List("sam@example.com"));
    }

    [SkippableFact]
    public void Only_a_hash_of_the_token_is_kept_and_an_unknown_or_odd_token_leads_nowhere()
    {
        var push = NewPush();
        var token = push.Register("sam@example.com", "phone", PushKind.UnifiedPush, "https://push.example.org/up/abc", 5).Token!;
        Assert.NotNull(push.Find(token));
        Assert.Null(push.Find(WakeTokens.Make()));
        Assert.Null(push.Find(null));
        Assert.Null(push.Find(""));
        Assert.Null(push.Find(token + "x"));
        Assert.Null(push.Find("' or 1=1 --"));
    }

    [SkippableFact]
    public void An_account_may_register_only_so_many_devices_and_one_account_does_not_use_up_anothers()
    {
        var push = NewPush();
        for (int i = 0; i < 3; i++) Assert.Equal(PushRegisterOutcome.Created, push.Register("sam@example.com", "d" + i, PushKind.UnifiedPush, "https://push.example.org/" + i, 3).Outcome);
        Assert.Equal(PushRegisterOutcome.TooMany, push.Register("sam@example.com", "d3", PushKind.UnifiedPush, "https://push.example.org/3", 3).Outcome);
        Assert.Equal(PushRegisterOutcome.Updated, push.Register("sam@example.com", "d1", PushKind.UnifiedPush, "https://push.example.org/new", 3).Outcome);
        Assert.Equal(PushRegisterOutcome.Created, push.Register("pat@example.com", "d0", PushKind.UnifiedPush, "https://push.example.org/p", 3).Outcome);
        Assert.Equal(3, push.List("sam@example.com").Count);
    }

    [SkippableFact]
    public void A_new_token_makes_the_old_one_stop_and_a_removed_device_cannot_be_woken()
    {
        var push = NewPush();
        var old = push.Register("sam@example.com", "phone", PushKind.UnifiedPush, "https://push.example.org/up/abc", 5).Token!;
        var fresh = push.Rotate("sam@example.com", "phone")!;
        Assert.NotEqual(old, fresh);
        Assert.Null(push.Find(old));
        Assert.NotNull(push.Find(fresh));
        Assert.Null(push.Rotate("sam@example.com", "nothing"));
        Assert.Null(push.Rotate("pat@example.com", "phone"));

        Assert.False(push.Remove("pat@example.com", "phone"));
        Assert.True(push.Remove("sam@example.com", "phone"));
        Assert.Null(push.Find(fresh));
        Assert.False(push.Remove("sam@example.com", "phone"));
    }

    [SkippableFact]
    public void A_token_can_be_forgotten_by_itself_and_an_account_forgotten_with_all_its_devices()
    {
        var push = NewPush();
        var a = push.Register("sam@example.com", "a", PushKind.UnifiedPush, "https://push.example.org/a", 5).Token!;
        push.Register("sam@example.com", "b", PushKind.UnifiedPush, "https://push.example.org/b", 5);
        var other = push.Register("pat@example.com", "a", PushKind.UnifiedPush, "https://push.example.org/c", 5).Token!;
        Assert.True(push.RemoveByToken(a));
        Assert.False(push.RemoveByToken(a));
        Assert.Single(push.List("sam@example.com"));
        push.RemoveAccount("sam@example.com");
        Assert.Empty(push.List("sam@example.com"));
        Assert.NotNull(push.Find(other));
    }

    [SkippableFact]
    public void The_list_shows_devices_and_what_they_use_but_never_a_token_or_an_address()
    {
        var push = NewPush();
        var now = new DateTimeOffset(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);
        push.Register("sam@example.com", "phone", PushKind.Fcm, "secret-device-token", 5);
        var listed = push.List("sam@example.com").Single();
        Assert.Equal(("phone", PushKind.Fcm), (listed.DeviceId, listed.Kind));
        Assert.DoesNotContain("secret", listed.ToString());
        Assert.True(listed.Created > now - TimeSpan.FromDays(3650));
    }

    // ---- passes and subscriptions (docs/billing.md) ----

    [SkippableFact]
    public void An_account_has_the_lifetime_pass_until_it_is_taken_away_and_only_a_pass_that_exists_can_be_given()
    {
        var accounts = NewAccounts();
        Assert.Null(accounts.PassOf("sam@example.com"));
        Assert.False(accounts.SetPass("sam@example.com", AccountPlan.None));   // no such account

        accounts.Add("sam@example.com", Password);
        Assert.Equal(AccountPlan.Lifetime, accounts.PassOf("sam@example.com"));
        Assert.True(accounts.SetPass("sam@example.com", AccountPlan.None));
        Assert.Equal("none", accounts.PassOf("sam@example.com"));
        Assert.False(accounts.SetPass("sam@example.com", "gold"));
        Assert.Equal("none", accounts.PassOf("sam@example.com"));
        Assert.True(accounts.SetPass("sam@example.com", AccountPlan.Lifetime));
        Assert.Equal(AccountPlan.Lifetime, accounts.PassOf("sam@example.com"));

        // Giving one is not a way to change anything else about the account.
        Assert.True(accounts.CheckPassword("sam@example.com", Password));
    }

    private static readonly DateTimeOffset Epoch = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

    private static Subscription Sub(string account, string id, SubscriptionStatus status = SubscriptionStatus.Active, int days = 30, string provider = "polar") =>
        new(account, provider, "cus_" + account, id, "cloud", status, Epoch.AddDays(days), Epoch);

    [SkippableFact]
    public void A_subscription_is_stored_again_in_place_of_the_same_one_and_listed_for_its_own_account_newest_first()
    {
        var store = NewSubscriptions();
        Assert.Empty(store.Of("sam@example.com"));

        store.Upsert(Sub("sam@example.com", "sub_1", days: 10));
        store.Upsert(Sub("sam@example.com", "sub_2", days: 40));
        store.Upsert(Sub("kim@example.com", "sub_3"));
        Assert.Equal(new[] { "sub_2", "sub_1" }, store.Of("sam@example.com").Select(s => s.ProviderSubscriptionId));
        Assert.Equal(new[] { "sub_3" }, store.Of("kim@example.com").Select(s => s.ProviderSubscriptionId));

        // The provider says it is now over: the same subscription, not a second one.
        store.Upsert(Sub("sam@example.com", "sub_2", SubscriptionStatus.Ended, days: 5));
        var again = store.Of("sam@example.com");
        Assert.Equal(2, again.Count);
        var changed = again.Single(s => s.ProviderSubscriptionId == "sub_2");
        Assert.Equal((SubscriptionStatus.Ended, Epoch.AddDays(5), "cloud", "cus_sam@example.com", "polar"), (changed.Status, changed.PeriodEnd, changed.Plan, changed.ProviderCustomerId, changed.Provider));

        // The same id at another provider is another subscription.
        store.Upsert(Sub("sam@example.com", "sub_2", provider: "stripe"));
        Assert.Equal(3, store.Of("sam@example.com").Count);
    }

    [SkippableFact]
    public void An_account_is_known_to_a_provider_by_a_random_id_that_stays_the_same_and_leads_back_to_it()
    {
        var store = NewSubscriptions();
        var sam = store.BillingIdOf("sam@example.com");
        var kim = store.BillingIdOf("kim@example.com");
        Assert.Equal(22, sam.Length);                       // 128 bits
        Assert.Matches("^[A-Za-z0-9_-]+$", sam);
        Assert.NotEqual(sam, kim);
        Assert.DoesNotContain("sam", sam, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(sam, store.BillingIdOf("sam@example.com"));
        Assert.Equal(sam, NewSubscriptions().BillingIdOf("sam@example.com"));   // kept, not only remembered

        Assert.Equal("sam@example.com", store.AccountOf(sam));
        Assert.Equal("kim@example.com", store.AccountOf(kim));
        Assert.Null(store.AccountOf("nobody"));
        Assert.Null(store.AccountOf(""));
        Assert.Null(store.AccountOf(null));
    }

    [SkippableFact]
    public void A_webhook_event_is_handled_once_and_can_be_forgotten_so_that_it_is_tried_again()
    {
        var store = NewSubscriptions();
        Assert.True(store.TryRecordEvent("polar", "evt_1", Epoch));
        Assert.False(store.TryRecordEvent("polar", "evt_1", Epoch.AddMinutes(1)));
        Assert.True(store.TryRecordEvent("stripe", "evt_1", Epoch));   // another provider's event with the same id

        store.ForgetEvent("polar", "evt_1");
        Assert.True(store.TryRecordEvent("polar", "evt_1", Epoch));
        store.ForgetEvent("polar", "evt_none");   // not an error
    }

    [SkippableFact]
    public void Old_events_are_purged_and_new_ones_stay()
    {
        var store = NewSubscriptions();
        store.TryRecordEvent("polar", "old", Epoch.AddDays(-40));
        store.TryRecordEvent("polar", "new", Epoch);
        Assert.Equal(1, store.PurgeEvents(Epoch.AddDays(-30)));
        Assert.True(store.TryRecordEvent("polar", "old", Epoch));     // it was forgotten
        Assert.False(store.TryRecordEvent("polar", "new", Epoch));    // it was not
        Assert.Equal(0, store.PurgeEvents(Epoch.AddDays(-100)));
    }

    [SkippableFact]
    public void Removing_an_account_forgets_what_it_paid_for_and_its_id_but_nothing_of_anyone_else()
    {
        var store = NewSubscriptions();
        store.Upsert(Sub("sam@example.com", "sub_1"));
        store.Upsert(Sub("kim@example.com", "sub_2"));
        var sam = store.BillingIdOf("sam@example.com");
        var kim = store.BillingIdOf("kim@example.com");

        store.RemoveAccount("sam@example.com");
        Assert.Empty(store.Of("sam@example.com"));
        Assert.Null(store.AccountOf(sam));
        Assert.Single(store.Of("kim@example.com"));
        Assert.Equal("kim@example.com", store.AccountOf(kim));
        store.RemoveAccount("nobody@example.com");   // not an error
        // A new account with the same name is a new person to the provider.
        Assert.NotEqual(sam, store.BillingIdOf("sam@example.com"));
    }
}

/// <summary>The stores that keep accounts and codes in files.</summary>
public sealed class FileStoreTests : StoreContractTests
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "yaparr-store-" + Guid.NewGuid().ToString("N"));

    private ServiceOptions Options => new() { DataDirectory = _dir };

    protected override IAccounts NewAccounts() => new AccountDirectory(Options);

    protected override ISignupCodes NewCodes(Func<DateTimeOffset>? now = null) => new SignupCodes(Options, now ?? (() => DateTimeOffset.UtcNow));

    protected override IPushRegistry NewPush() => new PushRegistry(Options);

    protected override ISubscriptions NewSubscriptions() => new SubscriptionStore(Options);

    public override void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }
}

/// <summary>The stores that keep accounts and codes in Postgres: the same cases as for files (skipped unless <c>YAPARR_TEST_POSTGRES</c> is set).</summary>
public sealed class PostgresStoreTests : StoreContractTests
{
    private readonly PostgresTestDatabase _database;

    public PostgresStoreTests()
    {
        _database = new PostgresTestDatabase();
    }

    private ServiceOptions Options => new() { DataDirectory = Path.Combine(Path.GetTempPath(), "yaparr-no-files-" + Guid.NewGuid().ToString("N")), Database = _database.ConnectionString };

    protected override IAccounts NewAccounts() => Stores.Accounts(Options);

    protected override ISignupCodes NewCodes(Func<DateTimeOffset>? now = null) => Stores.Codes(Options, now ?? (() => DateTimeOffset.UtcNow));

    protected override IPushRegistry NewPush() => Stores.Push(Options);

    protected override ISubscriptions NewSubscriptions() => Stores.Subscriptions(Options);

    public override void Dispose() => _database.Dispose();

    [SkippableFact]
    public async Task The_database_says_what_it_is()
    {
        var status = await NewAccounts().StatusAsync();
        Assert.Equal("PostgreSQL", status.Kind);
        Assert.StartsWith("database ", status.Where);
        Assert.Matches(@"version \d+", status.Detail);
        Assert.Contains("MiB", status.Detail);
    }

    [SkippableFact]
    public async Task A_database_that_is_down_says_so_quickly_and_does_not_throw()
    {
        using var source = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Username=x;Password=x;Database=x;Timeout=3");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var status = await new PostgresAccounts(source).StatusAsync();
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10));
        Assert.False(status.Ok);
        Assert.Equal("PostgreSQL", status.Kind);
        Assert.NotEmpty(status.Detail);
    }
}
