using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using Xunit;
using Yaparr.Cloud;

namespace Yaparr.Cloud.Tests;

/// <summary>
/// The service running on a database (skipped unless <c>YAPARR_TEST_POSTGRES</c> is set): signing up, signing in and keeping a backup, with the accounts
/// and codes in Postgres and nothing about them in the data folder.
/// </summary>
public sealed class PostgresServiceTests : ServiceTest, IDisposable
{
    private readonly PostgresTestDatabase _database = new();

    private ServiceOptions DbOptions => new() { DataDirectory = Dir, Database = _database.ConnectionString };

    public new void Dispose()
    {
        base.Dispose();
        _database.Dispose();
    }

    [SkippableFact]
    public async Task A_friend_signs_up_gets_a_token_and_keeps_a_backup_with_everything_in_the_database()
    {
        var client = Start(new() { ["Database"] = _database.ConnectionString });
        var (code, _) = Stores.Codes(DbOptions).Create("Sam", null, null);

        var signUp = await client.PostAsync("/signup", new FormUrlEncodedContent(new Dictionary<string, string> { ["code"] = code, ["email"] = "sam@example.com" }));
        Assert.Equal(HttpStatusCode.OK, signUp.StatusCode);
        var token = Regex.Match(await signUp.Content.ReadAsStringAsync(), "name=\"token\" value=\"(set_[A-Za-z0-9_-]+)\"").Groups[1].Value;
        Assert.NotEmpty(token);
        var setup = await client.PostAsync("/account/setup", new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = token, ["password"] = Password, ["again"] = Password }));
        Assert.Equal(HttpStatusCode.OK, setup.StatusCode);

        var accounts = Stores.Accounts(DbOptions);
        Assert.True(accounts.CheckPassword("sam@example.com", Password));
        Assert.Equal("sam@example.com", Assert.Single(Stores.Codes(DbOptions).List().Single().Uses).Email);

        var bearer = accounts.IssueToken("sam@example.com", "A test")!;
        var body = Locked(4000);
        var put = new HttpRequestMessage(HttpMethod.Put, "/v1/backup") { Content = new ByteArrayContent(body) };
        put.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        put.Headers.TryAddWithoutValidation("If-Match", "\"0\"");
        Assert.Equal(HttpStatusCode.Created, (await client.SendAsync(put)).StatusCode);

        var get = new HttpRequestMessage(HttpMethod.Get, "/v1/backup");
        get.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        var back = await client.SendAsync(get);
        Assert.Equal(HttpStatusCode.OK, back.StatusCode);
        Assert.Equal(body, await back.Content.ReadAsByteArrayAsync());

        // Nobody is signed in without a token, and nothing about accounts was written as a file.
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/v1/backup")).StatusCode);
        Assert.False(File.Exists(Path.Combine(Dir, "accounts.json")));
        Assert.False(File.Exists(Path.Combine(Dir, "codes.json")));
    }

    [SkippableFact]
    public async Task The_accounts_and_codes_of_a_service_that_used_files_move_into_an_empty_database_once()
    {
        // As the service has been kept so far: in files.
        var files = new ServiceOptions { DataDirectory = Dir };
        var oldAccounts = new AccountDirectory(files);
        oldAccounts.Add("sam@example.com", Password);
        oldAccounts.Add("boss@example.com", Password);
        var phone = oldAccounts.IssueToken("sam@example.com", "phone")!;
        var laptop = oldAccounts.IssueToken("sam@example.com", "laptop")!;
        var secret = oldAccounts.MakeAdmin("boss@example.com")!;
        var pending = oldAccounts.StartSignup("new@example.com", "abcd1234")!;
        var oldCodes = new SignupCodes(files);
        var (code, info) = oldCodes.Create("friends", 5, TimeSpan.FromDays(30));
        oldCodes.Use(code, "sam@example.com");
        var (revoked, revokedInfo) = oldCodes.Create("old", null, null);
        oldCodes.Revoke(revokedInfo.Id);

        // The first start with a database copies them over.
        var accounts = Stores.Accounts(DbOptions);
        var codes = Stores.Codes(DbOptions);
        Assert.Equal(new[] { "boss@example.com", "new@example.com", "sam@example.com" }, accounts.List().Select(a => a.Name));
        Assert.True(accounts.CheckPassword("sam@example.com", Password));
        Assert.Equal("sam@example.com", accounts.UserOfToken(phone));
        Assert.Equal("sam@example.com", accounts.UserOfToken(laptop));
        Assert.Equal(2, accounts.List().Single(a => a.Name == "sam@example.com").Devices);
        Assert.True(accounts.IsAdmin("boss@example.com"));
        Assert.Equal(secret, accounts.TotpSecretOf("boss@example.com"));
        Assert.False(accounts.HasPassword("new@example.com"));
        Assert.Equal("new@example.com", accounts.SetupFor(pending));
        Assert.Equal("abcd1234", accounts.List().Single(a => a.Name == "new@example.com").SignupCode);

        var friends = codes.List().Single(c => c.Id == info.Id);
        Assert.Equal("friends", friends.Note);
        Assert.Equal(5, friends.MaxUses);
        Assert.Equal("sam@example.com", Assert.Single(friends.Uses).Email);
        Assert.Equal(info.Id, codes.Check(code));
        Assert.True(codes.List().Single(c => c.Id == revokedInfo.Id).Revoked);
        Assert.Null(codes.Check(revoked));

        // The files are no longer the record: they were renamed, and a second start does not copy again.
        Assert.False(File.Exists(Path.Combine(Dir, "accounts.json")));
        Assert.False(File.Exists(Path.Combine(Dir, "codes.json")));
        Assert.True(File.Exists(Path.Combine(Dir, "accounts.json.imported")));
        Assert.True(File.Exists(Path.Combine(Dir, "codes.json.imported")));
        accounts.Remove("boss@example.com");
        File.Copy(Path.Combine(Dir, "accounts.json.imported"), Path.Combine(Dir, "accounts.json"));
        Stores.Forget(_database.ConnectionString);
        Assert.DoesNotContain(Stores.Accounts(DbOptions).List(), a => a.Name == "boss@example.com");   // a database that has accounts is left as it is
        await Task.CompletedTask;
    }

    [SkippableFact]
    public void A_database_made_by_a_newer_version_is_refused_and_not_damaged()
    {
        using (var source = Npgsql.NpgsqlDataSource.Create(_database.ConnectionString))
        {
            PostgresDatabase.Migrate(source);
            using var bump = source.CreateCommand($"insert into schema_version (version) values ({PostgresDatabase.SchemaVersion + 1})");
            bump.ExecuteNonQuery();
        }
        var error = Assert.Throws<InvalidOperationException>(() => Stores.Accounts(DbOptions));
        Assert.Contains("newer version", error.Message);
    }

    [SkippableFact]
    public void Two_copies_of_the_service_starting_at_once_make_each_schema_version_once()
    {
        var sources = Enumerable.Range(0, 6).Select(_ => Npgsql.NpgsqlDataSource.Create(_database.ConnectionString)).ToList();
        try
        {
            Parallel.ForEach(sources, PostgresDatabase.Migrate);
            using var count = sources[0].CreateCommand("select count(*) from schema_version");
            Assert.Equal((long)PostgresDatabase.SchemaVersion, Convert.ToInt64(count.ExecuteScalar()));
        }
        finally { foreach (var s in sources) s.Dispose(); }
    }
}
