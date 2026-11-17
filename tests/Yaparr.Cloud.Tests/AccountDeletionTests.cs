using System.Net;
using Yaparr.Cloud;

namespace Yaparr.Cloud.Tests;

public class AccountDeletionTests : ServiceTest
{
    private const string Sam = "sam@example.com";

    private static Task<HttpResponseMessage> Delete(HttpClient client, string username, string password, string confirm = "delete my account") =>
        client.PostAsync("/account/delete", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["username"] = username, ["current"] = password, ["confirm"] = confirm,
        }));

    // Everything an account can have on the server: a signed-in device, a backup, a device to wake, synced records.
    private async Task<(HttpClient Client, string Token)> AccountWithEverything(string name = Sam)
    {
        var client = Start();
        var token = Register(name);
        Assert.True((await Send(client, token, HttpMethod.Put, "/v1/backup", Locked(), ifMatch: 0)).IsSuccessStatusCode);
        Assert.Equal(PushRegisterOutcome.Created, Stores.Push(Options).Register(name, "phone", PushKind.UnifiedPush, "https://ntfy.example.org/abc", 5).Outcome);
        Assert.Equal(SyncPutOutcome.Stored, Stores.Sync(Options).Put(name, [new SyncRecord("account", "name", "00000000000100000000", "dev", false, "AAAA")], 100).Outcome);
        return (client, token);
    }

    private async Task AssertNothingLeft(string name)
    {
        Assert.False(new AccountDirectory(Options).Exists(name));
        Assert.Empty(Stores.Push(Options).List(name));
        Assert.Empty(Stores.Sync(Options).Pull(name, 0, 100).Records);
        Assert.Empty(await Stores.Backups(Options).VersionsAsync(BackupStore.FolderOf(name)));
    }

    [Fact]
    public async Task An_owner_who_types_their_password_and_the_words_loses_the_account_and_everything_held_for_it()
    {
        var (client, token) = await AccountWithEverything();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/account/delete")).StatusCode);

        var deleted = await Delete(client, Sam, Password);
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        Assert.Contains("Your account is deleted", await deleted.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(client, token, HttpMethod.Get, "/v1/backup")).StatusCode);
        await AssertNothingLeft(Sam);
        // The password no longer works, and the address can be signed up again from scratch.
        Assert.Equal(HttpStatusCode.Unauthorized, (await Delete(client, Sam, Password)).StatusCode);
    }

    [Fact]
    public async Task A_wrong_password_or_missing_words_delete_nothing()
    {
        var (client, token) = await AccountWithEverything();

        Assert.Equal(HttpStatusCode.Unauthorized, (await Delete(client, Sam, "not the password")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Delete(client, "nobody@example.com", Password)).StatusCode);
        var unconfirmed = await Delete(client, Sam, Password, confirm: "yes");
        Assert.Equal(HttpStatusCode.BadRequest, unconfirmed.StatusCode);
        Assert.Contains("Nothing was deleted", await unconfirmed.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, (await Send(client, token, HttpMethod.Get, "/v1/backup")).StatusCode);
        Assert.Single(Stores.Push(Options).List(Sam));
        Assert.NotEmpty(Stores.Sync(Options).Pull(Sam, 0, 100).Records);
    }

    [Fact]
    public async Task Someone_else_cannot_delete_an_account_by_guessing_and_the_tries_are_limited()
    {
        var (client, _) = await AccountWithEverything();
        for (int i = 0; i < 8; i++) await Delete(client, Sam, "wrong " + i);
        // Locked out after eight wrong tries: even the right password is turned away for a while.
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Delete(client, Sam, Password)).StatusCode);
        Assert.True(new AccountDirectory(Options).Exists(Sam));
    }

    [Fact]
    public async Task An_admin_account_is_not_deleted_this_way()
    {
        var (client, _) = await AccountWithEverything();
        Assert.NotNull(new AccountDirectory(Options).MakeAdmin(Sam));
        Assert.Equal(HttpStatusCode.Conflict, (await Delete(client, Sam, Password)).StatusCode);
        Assert.True(new AccountDirectory(Options).Exists(Sam));
    }

    [Fact]
    public async Task Removing_an_account_as_the_admin_leaves_nothing_behind_either()
    {
        await AccountWithEverything();
        var output = new StringWriter();
        Assert.Equal(0, await AdminCommands.RunAsync(["remove", Sam], Options, output));
        Assert.Contains("backup was deleted", output.ToString());
        await AssertNothingLeft(Sam);
    }

    [Fact]
    public async Task One_account_is_deleted_and_another_is_not_touched()
    {
        var (client, _) = await AccountWithEverything();
        var (_, otherToken) = await AccountWithEverything("alex@example.com");
        Assert.Equal(HttpStatusCode.OK, (await Delete(client, Sam, Password)).StatusCode);
        await AssertNothingLeft(Sam);
        Assert.Equal(HttpStatusCode.OK, (await Send(client, otherToken, HttpMethod.Get, "/v1/backup")).StatusCode);
        Assert.Single(Stores.Push(Options).List("alex@example.com"));
        Assert.Single(Stores.Sync(Options).Pull("alex@example.com", 0, 100).Records);
    }
}
