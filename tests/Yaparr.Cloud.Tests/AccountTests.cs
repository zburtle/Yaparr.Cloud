using Yaparr.Cloud;

namespace Yaparr.Cloud.Tests;

public sealed class AccountTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "yaparr-accounts-" + Guid.NewGuid().ToString("N"));
    private ServiceOptions Options => new() { DataDirectory = _dir };
    private const string Password = "correct horse battery";

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    [Theory]
    [InlineData("sam@example.com", "sam@example.com")]
    [InlineData("  Sam@Example.COM  ", "sam@example.com")]
    [InlineData("sam.burtle+yaparr@mail.example.co.uk", "sam.burtle+yaparr@mail.example.co.uk")]
    [InlineData("s_m-1@example.org", "s_m-1@example.org")]
    public void An_account_is_named_by_an_email_address_kept_trimmed_and_in_lower_case(string typed, string kept) => Assert.Equal(kept, AccountDirectory.Normalize(typed));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sam")]                                              // a name that is not an address
    [InlineData("sam@")]
    [InlineData("@example.com")]
    [InlineData("sam@example")]                          // no dot in the domain
    [InlineData("sam@example.c")]                        // a one-letter ending
    [InlineData("sam@@example.com")]
    [InlineData("sam smith@example.com")]
    [InlineData("sam..smith@example.com")]
    [InlineData(".sam@example.com")]
    [InlineData("sam.@example.com")]
    [InlineData("sam@-example.com")]
    [InlineData("sam@example..com")]
    [InlineData("sam@example.com ; rm -rf /")]
    [InlineData("<script>@example.com")]
    [InlineData("sam/../../x@example.com")]
    [InlineData("sam@example.com\nBcc: x@example.com")]
    [InlineData("sam@127.0.0.1")]
    public void Something_that_is_not_an_email_address_is_refused(string? typed) => Assert.Null(AccountDirectory.Normalize(typed));

    [Fact]
    public void An_address_that_is_too_long_is_refused()
    {
        Assert.NotNull(AccountDirectory.Normalize(new string('a', 64) + "@" + new string('b', 60) + ".example.com"));
        Assert.Null(AccountDirectory.Normalize(new string('a', 65) + "@example.com"));                            // the part before the @ is at most 64
        Assert.Null(AccountDirectory.Normalize("a@" + string.Join('.', Enumerable.Repeat(new string('b', 60), 5)) + ".com"));   // 254 in all
    }

    [Fact]
    public void A_password_is_kept_as_a_hash_that_only_it_matches()
    {
        var hash = Passwords.Hash(Password);
        Assert.StartsWith("argon2id$", hash);
        Assert.DoesNotContain(Password, hash);
        Assert.NotEqual(hash, Passwords.Hash(Password));                       // a salt of its own each time
        Assert.True(Passwords.Verify(Password, hash));
        Assert.False(Passwords.Verify(Password + "!", hash));
        Assert.False(Passwords.Verify("", hash));
        Assert.True(Passwords.Verify("café au lait please", Passwords.Hash("café au lait please")));   // however an accent is typed
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a hash")]
    [InlineData("argon2id$999999999$2$1$AAAA$AAAA")]   // asks for more memory than is allowed
    [InlineData("argon2id$32768$2$1$not base64!$AAAA")]
    public void A_hash_that_cannot_be_read_matches_nothing(string? stored) => Assert.False(Passwords.Verify(Password, stored));

    [Fact]
    public void A_made_up_password_is_long_enough_and_different_each_time()
    {
        var a = Passwords.Generate();
        Assert.Null(Passwords.Check(a));
        Assert.NotNull(Passwords.Check("too short"));
        Assert.NotEqual(a, Passwords.Generate());
        Assert.Matches("^[A-Z2-9]{4}(-[A-Z2-9]{4}){4}$", a);
    }

    [Fact]
    public void The_list_survives_a_restart_and_is_read_again_when_another_program_changes_it()
    {
        var running = new AccountDirectory(Options);
        Assert.False(running.Exists("sam@example.com"));

        // The admin command runs as a different process, with its own copy of the list.
        var admin = new AccountDirectory(Options);
        Assert.True(admin.Add("sam@example.com", Password));
        Assert.True(running.CheckPassword("sam@example.com", Password));                  // seen without a restart
        Assert.False(running.CheckPassword("sam@example.com", "something else"));
        Assert.False(running.CheckPassword("alex@example.com", Password));
        Assert.False(admin.Add("sam@example.com", Password));                             // a name is taken once
        Assert.True(new AccountDirectory(Options).Exists("sam@example.com"));             // and it is in the file

        Assert.True(admin.Remove("sam@example.com"));
        Assert.False(running.CheckPassword("sam@example.com", Password));
        Assert.False(admin.Remove("sam@example.com"));
    }

    [Fact]
    public void A_device_is_signed_in_by_a_token_that_is_kept_only_as_a_hash()
    {
        var accounts = new AccountDirectory(Options);
        accounts.Add("sam@example.com", Password);
        var token = accounts.IssueToken("sam@example.com", "Sam's laptop")!;
        Assert.StartsWith("ycl_", token);
        Assert.Null(accounts.IssueToken("nobody@example.com", "x"));

        Assert.Equal("sam@example.com", accounts.UserOfToken(token));
        Assert.Equal("sam@example.com", new AccountDirectory(Options).UserOfToken(token));   // after a restart too
        Assert.Null(accounts.UserOfToken(token + "x"));
        Assert.Null(accounts.UserOfToken(null));
        Assert.Null(accounts.UserOfToken("ycl_"));
        Assert.DoesNotContain(token, File.ReadAllText(Path.Combine(_dir, "accounts.json")));
        Assert.DoesNotContain(Password, File.ReadAllText(Path.Combine(_dir, "accounts.json")));

        Assert.True(accounts.RevokeToken(token));
        Assert.Null(accounts.UserOfToken(token));
        Assert.False(accounts.RevokeToken(token));
    }

    [Fact]
    public void A_new_password_signs_every_device_out_and_an_account_keeps_only_so_many_devices()
    {
        var accounts = new AccountDirectory(Options);
        accounts.Add("sam@example.com", Password);
        var tokens = Enumerable.Range(0, 22).Select(i => accounts.IssueToken("sam@example.com", "Device " + i)!).ToList();
        Assert.Null(accounts.UserOfToken(tokens[0]));         // the oldest made room
        Assert.Null(accounts.UserOfToken(tokens[1]));
        Assert.Equal("sam@example.com", accounts.UserOfToken(tokens[2]));
        Assert.Equal(20, accounts.List().Single().Devices);

        Assert.True(accounts.SetPassword("sam@example.com", "a brand new password"));
        Assert.All(tokens, t => Assert.Null(accounts.UserOfToken(t)));
        Assert.True(accounts.CheckPassword("sam@example.com", "a brand new password"));
        Assert.False(accounts.CheckPassword("sam@example.com", Password));
        Assert.False(accounts.SetPassword("nobody@example.com", "a brand new password"));
    }

    [Fact]
    public void An_account_from_before_passwords_were_kept_cannot_sign_in_until_it_is_given_one()
    {
        // What the first version of the service wrote: a public key and no password.
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "accounts.json"), """{ "users": { "sam@example.com": { "publicKey": "6Kn/wTntFsynrBg8OrBKkovzDHVWJXOG/H1c1JugIFA=", "created": "2026-11-10T20:49:55+00:00" } } }""");
        var accounts = new AccountDirectory(Options);
        Assert.True(accounts.Exists("sam@example.com"));
        Assert.False(accounts.List().Single().HasPassword);
        Assert.False(accounts.CheckPassword("sam@example.com", Password));
        Assert.False(accounts.CheckPassword("sam@example.com", ""));

        Assert.True(accounts.SetPassword("sam@example.com", Password));
        Assert.True(accounts.CheckPassword("sam@example.com", Password));
    }

    [Fact]
    public async Task The_admin_adds_an_account_whose_made_up_password_then_signs_in()
    {
        var output = new StringWriter();
        Assert.Equal(0, await AdminCommands.RunAsync(["add", "Sam@Example.com"], Options, output));
        var text = output.ToString();
        Assert.Contains("Made the account sam@example.com", text);
        var password = text.Split("Its password is:")[1].Split('\n')[0].Trim();
        Assert.True(new AccountDirectory(Options).CheckPassword("sam@example.com", password));
        Assert.DoesNotContain(password, File.ReadAllText(Path.Combine(_dir, "accounts.json")));
    }

    [Fact]
    public async Task The_admin_can_reset_a_password_which_signs_devices_out()
    {
        await AdminCommands.RunAsync(["add", "sam@example.com", "--password", Password], Options, new StringWriter());
        var token = new AccountDirectory(Options).IssueToken("sam@example.com", "x")!;

        var output = new StringWriter();
        Assert.Equal(0, await AdminCommands.RunAsync(["password", "sam@example.com"], Options, output));
        var password = output.ToString().Split("Its password is:")[1].Split('\n')[0].Trim();
        var accounts = new AccountDirectory(Options);
        Assert.True(accounts.CheckPassword("sam@example.com", password));
        Assert.False(accounts.CheckPassword("sam@example.com", Password));
        Assert.Null(accounts.UserOfToken(token));
        Assert.Equal(1, await AdminCommands.RunAsync(["password", "nobody@example.com"], Options, new StringWriter()));
    }

    [Fact]
    public async Task Adding_checks_the_name_the_password_and_duplicates()
    {
        Assert.Equal(2, await AdminCommands.RunAsync(["add", "no spaces"], Options, new StringWriter()));
        Assert.Equal(2, await AdminCommands.RunAsync(["add", "sam@example.com", "--password", "short"], Options, new StringWriter()));
        Assert.Equal(0, await AdminCommands.RunAsync(["add", "sam@example.com", "--password", Password], Options, new StringWriter()));
        Assert.Equal(1, await AdminCommands.RunAsync(["add", "SAM@EXAMPLE.COM", "--password", "another long password"], Options, new StringWriter()));
        Assert.Equal(2, await AdminCommands.RunAsync(["frobnicate"], Options, new StringWriter()));
        Assert.Equal(2, await AdminCommands.RunAsync([], Options, new StringWriter()));
    }

    [Fact]
    public async Task Removing_an_account_deletes_its_backup_unless_told_to_keep_it()
    {
        var store = new BackupStore(Options);
        async Task<string> Give(string name)
        {
            await AdminCommands.RunAsync(["add", name, "--password", Password], Options, new StringWriter());
            var folder = BackupStore.FolderOf(name);
            var body = new byte[100];
            BackupStore.Magic.CopyTo(body, 0);
            Assert.Equal(SaveOutcome.Saved, (await store.SaveAsync(folder, 0, new MemoryStream(body), default)).Outcome);
            return folder;
        }
        var sam = await Give("sam@example.com");
        var alex = await Give("alex@example.com");

        var output = new StringWriter();
        Assert.Equal(0, await AdminCommands.RunAsync(["remove", "sam@example.com"], Options, output));
        Assert.Contains("backup was deleted", output.ToString());
        Assert.Empty(store.Versions(sam));

        Assert.Equal(0, await AdminCommands.RunAsync(["remove", "alex@example.com", "--keep-backup"], Options, new StringWriter()));
        Assert.Single(store.Versions(alex));
        Assert.Equal(1, await AdminCommands.RunAsync(["remove", "nobody@example.com"], Options, new StringWriter()));
    }

    [Fact]
    public async Task An_account_that_is_not_an_email_address_is_flagged_in_the_list_and_cannot_sign_in()
    {
        // From before accounts were named by an email address: it is still in the file, but nothing can sign in to a name that is not one.
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "accounts.json"), """{ "users": { "sam": { "passwordHash": null, "created": "2026-11-10T20:49:55+00:00" } } }""");
        var output = new StringWriter();
        Assert.Equal(0, await AdminCommands.RunAsync(["list"], Options, output));
        Assert.Contains("sam  is not an email address", output.ToString());
        Assert.Null(AccountDirectory.Normalize("sam"));

        // Removing it by name is not possible either (the name is not an address), so the admin edits the file or starts again; adding the address works.
        Assert.Equal(0, await AdminCommands.RunAsync(["add", "sam@example.com", "--password", Password], Options, new StringWriter()));
    }

    [Fact]
    public async Task The_list_shows_who_has_an_account_their_devices_and_how_much_they_use()
    {
        var empty = new StringWriter();
        await AdminCommands.RunAsync(["list"], Options, empty);
        Assert.Contains("No accounts yet", empty.ToString());

        await AdminCommands.RunAsync(["add", "sam@example.com", "--password", Password], Options, new StringWriter());
        new AccountDirectory(Options).IssueToken("sam@example.com", "x");
        var output = new StringWriter();
        Assert.Equal(0, await AdminCommands.RunAsync(["list"], Options, output));
        Assert.Contains("sam@example.com", output.ToString());
        Assert.Contains("1 device signed in", output.ToString());
        Assert.Contains("no backup", output.ToString());
    }
}
