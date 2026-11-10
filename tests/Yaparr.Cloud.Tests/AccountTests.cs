using Yaparr.Cloud;

namespace Yaparr.Cloud.Tests;

public sealed class AccountTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "yaparr-accounts-" + Guid.NewGuid().ToString("N"));
    private ServiceOptions Options => new() { DataDirectory = _dir };

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    [Theory]
    [InlineData("sam", "sam")]
    [InlineData("  Sam  ", "sam")]
    [InlineData("Sam.Burtle_2", "sam.burtle_2")]
    public void A_username_is_kept_trimmed_and_in_lower_case(string typed, string kept) => Assert.Equal(kept, AccountDirectory.Normalize(typed));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ab")]
    [InlineData("-sam")]
    [InlineData("sam-")]
    [InlineData("sam smith")]
    [InlineData("sam/../../x")]
    [InlineData("a-name-that-goes-on-for-far-too-long-to-use")]
    public void A_name_that_cannot_be_used_is_refused(string? typed) => Assert.Null(AccountDirectory.Normalize(typed));

    [Fact]
    public void The_same_username_and_password_always_make_the_same_key_and_nothing_else_does()
    {
        const string password = "correct horse battery";
        byte[] Seed(string name, string pw) => LoginKeys.SigningSeed(name, pw, 8 * 1024, 1);

        Assert.Equal(Seed("sam", password), Seed("sam", password));
        Assert.Equal(Seed("sam", password), Seed("  SAM ", password));            // how the name is typed does not matter
        Assert.NotEqual(Seed("sam", password), Seed("alex", password));            // the same password for another person is another key
        Assert.NotEqual(Seed("sam", password), Seed("sam", password + "!"));
        Assert.Equal(32, Seed("sam", password).Length);
        Assert.Equal(Seed("sam", "café au lait please"), Seed("sam", "café au lait please"));   // however an accent is typed
    }

    [Fact]
    public void Signing_matches_the_apps_down_to_the_byte()
    {
        // The same fixed key, name and time as the test in the app (BackupServerClientTests): if one side changes how it signs, both tests fail.
        var seed = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
        var headers = RequestSigner.Sign(seed, "sam", "get", "/v1/backup/versions", DateTimeOffset.FromUnixTimeSeconds(1_700_000_000));
        Assert.Equal("aRyonQ8cTcTj5OsOnHVrdNM4o77gdFZsgazLs5vwzInhgp-_7pffD2G-XbHvg_jbZK0-YMxfPbgymje_4QopAA", headers[RequestSigner.SignatureHeader]);
    }

    [Fact]
    public void The_key_for_a_name_and_password_is_the_one_the_app_makes()
    {
        var key = LoginKeys.PublicKeyOf(LoginKeys.SigningSeed("sam", "correct horse battery"));
        Assert.Equal("6Kn/wTntFsynrBg8OrBKkovzDHVWJXOG/H1c1JugIFA=", Convert.ToBase64String(key));
    }

    [Fact]
    public void A_short_password_or_an_unusable_name_makes_no_key()
    {
        Assert.Throws<ArgumentException>(() => LoginKeys.SigningSeed("sam", "short", 8 * 1024, 1));
        Assert.Throws<ArgumentException>(() => LoginKeys.SigningSeed("no spaces allowed", "correct horse battery", 8 * 1024, 1));
    }

    [Fact]
    public void A_made_up_password_is_long_enough_and_different_each_time()
    {
        var a = LoginKeys.GeneratePassword();
        Assert.Null(LoginKeys.CheckPassword(a));
        Assert.NotEqual(a, LoginKeys.GeneratePassword());
        Assert.Matches("^[A-Z2-9]{4}(-[A-Z2-9]{4}){4}$", a);
    }

    [Fact]
    public void The_list_survives_a_restart_and_is_read_again_when_another_program_changes_it()
    {
        var running = new AccountDirectory(Options);
        Assert.Null(running.PublicKeyOf("sam"));

        // The admin command runs as a different process, with its own copy of the list.
        var admin = new AccountDirectory(Options);
        var key = new byte[32];
        key[0] = 7;
        Assert.True(admin.Add("sam", key));
        Assert.Equal(key, running.PublicKeyOf("sam"));                       // seen without a restart
        Assert.False(admin.Add("sam", key));                                 // a name is taken once
        Assert.Equal(key, new AccountDirectory(Options).PublicKeyOf("sam"));  // and it is in the file

        Assert.True(admin.Remove("sam"));
        Assert.Null(running.PublicKeyOf("sam"));
        Assert.False(admin.Remove("sam"));
    }

    [Fact]
    public async Task The_file_holds_a_public_key_and_never_a_password()
    {
        var output = new StringWriter();
        Assert.Equal(0, await AdminCommands.RunAsync(["add", "sam", "--password", "correct horse battery"], Options, output));
        var file = File.ReadAllText(Path.Combine(_dir, "accounts.json"));
        Assert.DoesNotContain("correct horse battery", file);
        Assert.Contains("sam", file);
    }

    [Fact]
    public async Task The_admin_adds_an_account_whose_password_then_signs_in()
    {
        var output = new StringWriter();
        Assert.Equal(0, await AdminCommands.RunAsync(["add", "Sam"], Options, output));
        var text = output.ToString();
        Assert.Contains("Made the account sam", text);
        var password = text.Split("Its password is:")[1].Split('\n')[0].Trim();

        // What the friend does on their device: make the key from the name and password they were given.
        var seed = LoginKeys.SigningSeed("sam", password);
        Assert.Equal(LoginKeys.PublicKeyOf(seed), new AccountDirectory(Options).PublicKeyOf("sam"));
    }

    [Fact]
    public async Task Adding_checks_the_name_the_password_and_duplicates()
    {
        Assert.Equal(2, await AdminCommands.RunAsync(["add", "no spaces"], Options, new StringWriter()));
        Assert.Equal(2, await AdminCommands.RunAsync(["add", "sam", "--password", "short"], Options, new StringWriter()));
        Assert.Equal(0, await AdminCommands.RunAsync(["add", "sam", "--password", "correct horse battery"], Options, new StringWriter()));
        Assert.Equal(1, await AdminCommands.RunAsync(["add", "SAM", "--password", "another long password"], Options, new StringWriter()));
        Assert.Equal(2, await AdminCommands.RunAsync(["frobnicate"], Options, new StringWriter()));
        Assert.Equal(2, await AdminCommands.RunAsync([], Options, new StringWriter()));
    }

    [Fact]
    public async Task Removing_an_account_deletes_its_backup_unless_told_to_keep_it()
    {
        var store = new BackupStore(Options);
        async Task<string> Give(string name)
        {
            await AdminCommands.RunAsync(["add", name, "--password", "correct horse battery"], Options, new StringWriter());
            var folder = RequestSigner.FolderOf(name);
            var body = new byte[100];
            BackupStore.Magic.CopyTo(body, 0);
            var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(body));
            Assert.Equal(SaveOutcome.Saved, (await store.SaveAsync(folder, 0, new MemoryStream(body), hash, default)).Outcome);
            return folder;
        }
        var sam = await Give("sam");
        var alex = await Give("alex");

        var output = new StringWriter();
        Assert.Equal(0, await AdminCommands.RunAsync(["remove", "sam"], Options, output));
        Assert.Contains("backup was deleted", output.ToString());
        Assert.Empty(store.Versions(sam));

        Assert.Equal(0, await AdminCommands.RunAsync(["remove", "alex", "--keep-backup"], Options, new StringWriter()));
        Assert.Single(store.Versions(alex));

        Assert.Equal(1, await AdminCommands.RunAsync(["remove", "nobody"], Options, new StringWriter()));
    }

    [Fact]
    public async Task The_list_shows_who_has_an_account_and_how_much_they_use()
    {
        var empty = new StringWriter();
        await AdminCommands.RunAsync(["list"], Options, empty);
        Assert.Contains("No accounts yet", empty.ToString());

        await AdminCommands.RunAsync(["add", "sam", "--password", "correct horse battery"], Options, new StringWriter());
        var output = new StringWriter();
        Assert.Equal(0, await AdminCommands.RunAsync(["list"], Options, output));
        Assert.Contains("sam", output.ToString());
        Assert.Contains("no backup", output.ToString());
    }
}
