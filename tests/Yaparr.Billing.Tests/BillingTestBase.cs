using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Yaparr.Cloud;

namespace Yaparr.Cloud.Tests;

/// <summary>Starts the service in memory on a data folder of its own, with the billing plug-in loaded (a copy of the server's own test harness, which this repository's tests cannot share).</summary>
public abstract class BillingTestBase : IDisposable
{
    protected readonly string Dir = Path.Combine(Path.GetTempPath(), "yaparr-cloud-" + Guid.NewGuid().ToString("N"));
    private readonly List<WebApplicationFactory<Program>> _factories = new();
    protected const string Password = "correct horse battery";

    protected ServiceOptions Options => new() { DataDirectory = Dir };

    /// <summary>The service started by the last <see cref="Start"/>, for a test that needs more than an HTTP client (a WebSocket).</summary>
    protected WebApplicationFactory<Program>? LastFactory { get; private set; }

    protected HttpClient Start(Dictionary<string, string>? settings = null, Action<Microsoft.Extensions.DependencyInjection.IServiceCollection>? services = null)
    {
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Service:DataDirectory", Dir);
            b.UseSetting("Service:Plugins:0", typeof(BillingPlugin).Assembly.Location);   // the plug-in under test
            foreach (var (key, value) in settings ?? new()) b.UseSetting("Service:" + key, value);
            if (services != null) b.ConfigureServices(services);   // what a test puts in place of a part of the service
        });
        _factories.Add(factory);
        LastFactory = factory;
        // A browser is sent from page to page; the tests look at each step, so they do not follow. Cookies are sent by hand, so a test says when one is.
        return factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
    }

    public void Dispose()
    {
        foreach (var f in _factories) f.Dispose();
        try { Directory.Delete(Dir, true); } catch (IOException) { }
    }

    /// <summary>An account, and a device that is signed in as it (its token).</summary>
    protected string Register(string name, string password = Password)
    {
        var accounts = new AccountDirectory(Options);
        Assert.True(accounts.Add(name, password));
        return accounts.IssueToken(name, "A test")!;
    }

    // What the service accepts as a backup: it starts as a locked one does.
    protected static byte[] Locked(int length = 1000)
    {
        var body = RandomNumberGenerator.GetBytes(length);
        BackupStore.Magic.CopyTo(body, 0);
        return body;
    }

    protected static async Task<HttpResponseMessage> Send(HttpClient client, string? token, HttpMethod method, string path, byte[]? body = null, int? ifMatch = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (token != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (ifMatch != null) request.Headers.Add("If-Match", "\"" + ifMatch.Value + "\"");
        if (body != null) request.Content = new ByteArrayContent(body);
        return await client.SendAsync(request);
    }

    protected static async Task<JsonElement[]> Versions(HttpClient client, string token) =>
        (await (await Send(client, token, HttpMethod.Get, "/v1/backup/versions")).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("versions").EnumerateArray().ToArray();

    // The files of backups, not the list of accounts that sits beside them.
    protected IEnumerable<string> BackupFiles() =>
        Directory.Exists(Dir) ? Directory.EnumerateDirectories(Dir).SelectMany(d => Directory.EnumerateFiles(d)) : Enumerable.Empty<string>();
}

/// <summary>Keeps what is logged, so a test can look at it.</summary>
internal sealed class LogCapture : Microsoft.Extensions.Logging.ILoggerProvider
{
    public List<string> Lines { get; } = new();
    public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => new Capture(this);
    public void Dispose() { }

    private sealed class Capture(LogCapture owner) : Microsoft.Extensions.Logging.ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (owner.Lines) owner.Lines.Add(formatter(state, exception));
        }
    }
}
