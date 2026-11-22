using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Yaparr.Cloud;

namespace Yaparr.Cloud.Tests;

/// <summary>
/// The one plug-in in this assembly that the loader finds (a public class: the loader ignores the others, which tests make by hand). It adds an address, a guard against deleting one account, and a card, a column and
/// a button to the admin portal, so a test can see each of the places a plug-in reaches the server.
/// </summary>
public sealed class ExamplePlugin : IYaparrPlugin
{
    public const string Protected = "keep@example.com";

    public string Name => "Example";
    public int ApiVersion => PluginApi.Version;

    public void ConfigureServices(IServiceCollection services, ServiceOptions options, Microsoft.Extensions.Configuration.IConfiguration configuration)
    {
        services.AddSingleton<IAccountDeletionGuard, Guard>();
        services.AddSingleton<IAdminExtension, Extension>();
    }

    public void MapEndpoints(WebApplication app, ServiceOptions options)
    {
        app.MapGet("/plugin-test/hello", () => Results.Text("hello from " + options.PluginsDirectory.Length));
        app.MapPost("/admin/plugin-test/press", (HttpContext http, IAccounts accounts) => AdminGuard.Post(http, (session, form) =>
            Task.FromResult(Results.Text("pressed by " + session.User + " for " + form["name"]))));
    }

    private sealed class Guard : IAccountDeletionGuard
    {
        public Task<string?> BeforeDeleteAsync(string account, CancellationToken cancel) =>
            Task.FromResult<string?>(account == Protected ? "This account is looked after by the example plug-in, so it was not deleted." : null);
    }

    private sealed class Extension : IAdminExtension
    {
        public IAdminView Begin(IReadOnlyList<AccountEntry> accounts, DateTimeOffset now) => new View(accounts.Count);

        private sealed class View(int count) : IAdminView
        {
            public string? ColumnTitle => "Example";
            public string Cell(AccountEntry account) => "<span class=\"example-cell\">cell of " + account.Name + "</span>";
            public string Actions(AccountEntry account, string csrf) =>
                $"<form method=\"post\" action=\"/admin/plugin-test/press\"><input type=\"hidden\" name=\"csrf\" value=\"{csrf}\"><input type=\"hidden\" name=\"name\" value=\"{account.Name}\"><button>Press</button></form>";
            public string? Card() => $"<section class=\"card\"><h2>Example card</h2><p>{count} accounts seen</p></section>";
        }
    }
}

internal sealed class OldPlugin : IYaparrPlugin
{
    public string Name => "Old";
    public int ApiVersion => PluginApi.Version - 1;
    public void ConfigureServices(IServiceCollection services, ServiceOptions options, Microsoft.Extensions.Configuration.IConfiguration configuration) { }
    public void MapEndpoints(WebApplication app, ServiceOptions options) { }
}

public sealed class PluginLoaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "yaparr-plugins-" + Guid.NewGuid().ToString("N"));

    public PluginLoaderTests() => Directory.CreateDirectory(_dir);

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static string ThisAssembly => typeof(ExamplePlugin).Assembly.Location;

    [Fact]
    public void With_nothing_named_nothing_is_loaded()
    {
        Assert.Empty(PluginRegistry.Load(null, Array.Empty<string>()).Plugins);
        Assert.Empty(PluginRegistry.Load("", Array.Empty<string>()).Plugins);
        Assert.Empty(PluginRegistry.Load(_dir, Array.Empty<string>()).Plugins);   // an empty folder
    }

    [Fact]
    public void A_plug_in_is_found_in_an_assembly_by_path_and_in_a_folder_and_only_public_classes_count()
    {
        var byPath = PluginRegistry.Load(null, new[] { ThisAssembly });
        Assert.Equal("Example", Assert.Single(byPath.Plugins).Name);   // OldPlugin is internal and is not one

        File.Copy(ThisAssembly, Path.Combine(_dir, "example.dll"));
        Assert.Equal("Example", Assert.Single(PluginRegistry.Load(_dir, Array.Empty<string>()).Plugins).Name);
    }

    [Fact]
    public void A_file_that_is_not_an_assembly_is_skipped_and_one_that_is_named_and_missing_is_an_error()
    {
        File.WriteAllText(Path.Combine(_dir, "notes.dll"), "this is not an assembly");
        Assert.Empty(PluginRegistry.Load(_dir, Array.Empty<string>()).Plugins);

        var missing = Assert.Throws<InvalidOperationException>(() => PluginRegistry.Load(null, new[] { Path.Combine(_dir, "nothing.dll") }));
        Assert.Contains("could not be found", missing.Message);
        var noFolder = Assert.Throws<InvalidOperationException>(() => PluginRegistry.Load(Path.Combine(_dir, "nowhere"), Array.Empty<string>()));
        Assert.Contains("Service__PluginsDirectory", noFolder.Message);
    }

    [Fact]
    public void A_plug_in_built_for_another_api_is_refused_with_both_versions_named()
    {
        var failure = Assert.Throws<InvalidOperationException>(() => PluginRegistry.From(new IYaparrPlugin[] { new OldPlugin() }));
        Assert.Contains("Old", failure.Message);
        Assert.Contains($"API {PluginApi.Version - 1}", failure.Message);
        Assert.Contains($"API {PluginApi.Version}", failure.Message);
        Assert.Single(PluginRegistry.From(new IYaparrPlugin[] { new ExamplePlugin() }).Plugins);
    }

    [Fact]
    public async Task The_deletion_guards_refuse_with_the_first_sentence_given_and_allow_when_none_does()
    {
        var yes = new Allow();
        var no = new Refuse("first");
        Assert.Null(await AccountDeletionGuards.RefusalAsync(new IAccountDeletionGuard[] { yes, yes }, "a@example.com", default));
        Assert.Equal("first", await AccountDeletionGuards.RefusalAsync(new IAccountDeletionGuard[] { yes, no, new Refuse("second") }, "a@example.com", default));
        Assert.Null(await AccountDeletionGuards.RefusalAsync(Array.Empty<IAccountDeletionGuard>(), "a@example.com", default));
    }

    private sealed class Allow : IAccountDeletionGuard { public Task<string?> BeforeDeleteAsync(string account, CancellationToken cancel) => Task.FromResult<string?>(null); }
    private sealed class Refuse(string why) : IAccountDeletionGuard { public Task<string?> BeforeDeleteAsync(string account, CancellationToken cancel) => Task.FromResult<string?>(why); }
}

public sealed class PluginServiceTests : ServiceTest
{
    private HttpClient WithPlugin() => Start(new() { ["Plugins:0"] = typeof(ExamplePlugin).Assembly.Location });

    [Fact]
    public async Task Without_a_plug_in_the_service_is_what_it_is_and_with_one_its_address_exists()
    {
        var plain = Start();
        Assert.Equal(HttpStatusCode.OK, (await plain.GetAsync("/healthz")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await plain.GetAsync("/plugin-test/hello")).StatusCode);

        var plugged = WithPlugin();
        var answer = await plugged.GetAsync("/plugin-test/hello");
        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        Assert.StartsWith("hello from", await answer.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_deletion_guard_stops_an_account_being_deleted_by_its_owner_with_the_guards_sentence_and_nothing_is_removed()
    {
        var client = WithPlugin();
        Register(ExamplePlugin.Protected);
        Register("free@example.com");

        var refused = await client.PostAsync("/account/delete", new FormUrlEncodedContent(new Dictionary<string, string> { ["username"] = ExamplePlugin.Protected, ["current"] = Password, ["confirm"] = "delete my account" }));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
        Assert.Contains("looked after by the example plug-in", await refused.Content.ReadAsStringAsync());
        Assert.True(new AccountDirectory(Options).Exists(ExamplePlugin.Protected));

        var allowed = await client.PostAsync("/account/delete", new FormUrlEncodedContent(new Dictionary<string, string> { ["username"] = "free@example.com", ["current"] = Password, ["confirm"] = "delete my account" }));
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.False(new AccountDirectory(Options).Exists("free@example.com"));
    }
}
