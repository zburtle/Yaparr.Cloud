using System.Net;
using System.Net.Http.Headers;
using Amazon.S3;
using Amazon.S3.Model;
using Xunit;
using Yaparr.Cloud;

namespace Yaparr.Cloud.Tests;

/// <summary>
/// The service keeping its backups in a bucket, and the command that moves a service's files there (skipped unless <c>YAPARR_TEST_S3</c> names an S3-compatible
/// store, see <see cref="S3BackupStoreTests"/>).
/// </summary>
public sealed class S3ServiceTests : ServiceTest, IDisposable
{
    private readonly string _prefix = "svc-" + Guid.NewGuid().ToString("N") + "/";
    private readonly string[] _parts;

    public S3ServiceTests()
    {
        Skip.If(string.IsNullOrEmpty(Environment.GetEnvironmentVariable(S3BackupStoreTests.Variable)), "Set YAPARR_TEST_S3 to run these.");
        _parts = Environment.GetEnvironmentVariable(S3BackupStoreTests.Variable)!.Split(';');
        using var client = S3BackupStore.CreateClient(S3Options());
        try { client.PutBucketAsync(_parts[1]).GetAwaiter().GetResult(); }
        catch (AmazonS3Exception) { }
    }

    private ServiceOptions S3Options() => new()
    {
        DataDirectory = Dir, S3Endpoint = _parts[0], S3Bucket = _parts[1], S3AccessKey = _parts[2], S3SecretKey = _parts[3],
        S3Region = _parts.Length > 4 ? _parts[4] : "auto", S3Prefix = _prefix, S3ConditionalWrites = Environment.GetEnvironmentVariable("YAPARR_TEST_S3_CONDITIONAL") != "0",
    };

    private Dictionary<string, string> Settings() => new()
    {
        ["S3Endpoint"] = _parts[0], ["S3Bucket"] = _parts[1], ["S3AccessKey"] = _parts[2], ["S3SecretKey"] = _parts[3],
        ["S3Region"] = _parts.Length > 4 ? _parts[4] : "auto", ["S3Prefix"] = _prefix,
        ["S3ConditionalWrites"] = (Environment.GetEnvironmentVariable("YAPARR_TEST_S3_CONDITIONAL") != "0").ToString(),
    };

    private async Task<List<string>> KeysAsync()
    {
        using var client = S3BackupStore.CreateClient(S3Options());
        var listed = await client.ListObjectsV2Async(new ListObjectsV2Request { BucketName = _parts[1], Prefix = _prefix });
        return (listed.S3Objects ?? new List<S3Object>()).Select(o => o.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
    }

    public new void Dispose()
    {
        base.Dispose();
        using var client = S3BackupStore.CreateClient(S3Options());
        var listed = client.ListObjectsV2Async(new ListObjectsV2Request { BucketName = _parts[1], Prefix = _prefix }).GetAwaiter().GetResult();
        foreach (var o in listed.S3Objects ?? new List<S3Object>()) client.DeleteObjectAsync(_parts[1], o.Key).GetAwaiter().GetResult();
    }

    private static HttpRequestMessage Authorized(HttpMethod method, string url, string token, byte[]? body = null, int? basedOn = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body != null) request.Content = new ByteArrayContent(body);
        if (basedOn != null) request.Headers.TryAddWithoutValidation("If-Match", $"\"{basedOn}\"");
        return request;
    }

    [SkippableFact]
    public async Task The_service_keeps_a_friends_backup_in_the_bucket_and_not_in_the_data_folder()
    {
        var client = Start(Settings());
        var token = Register("sam@example.com");

        var first = Locked(5000);
        var put = await client.SendAsync(Authorized(HttpMethod.Put, "/v1/backup", token, first, 0));
        Assert.Equal(HttpStatusCode.Created, put.StatusCode);
        var second = Locked(6000);
        Assert.Equal(HttpStatusCode.Created, (await client.SendAsync(Authorized(HttpMethod.Put, "/v1/backup", token, second, 1))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.SendAsync(Authorized(HttpMethod.Put, "/v1/backup", token, Locked(), 1))).StatusCode);

        var get = await client.SendAsync(Authorized(HttpMethod.Get, "/v1/backup", token));
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal("2", get.Headers.GetValues("X-Yaparr-Version").Single());
        Assert.Equal(second, await get.Content.ReadAsByteArrayAsync());
        var older = await client.SendAsync(Authorized(HttpMethod.Get, "/v1/backup?version=1", token));
        Assert.Equal(first, await older.Content.ReadAsByteArrayAsync());

        var versions = await (await client.SendAsync(Authorized(HttpMethod.Get, "/v1/backup/versions", token))).Content.ReadAsStringAsync();
        Assert.Contains("\"version\":2", versions);
        Assert.Contains("\"version\":1", versions);

        var keys = await KeysAsync();
        Assert.Equal(2, keys.Count);
        Assert.All(keys, k => Assert.StartsWith(_prefix + BackupStore.FolderOf("sam@example.com") + "/v0000000", k));
        Assert.Empty(Directory.EnumerateFiles(Dir, "v*.bin", SearchOption.AllDirectories));      // nothing was written as a file

        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(Authorized(HttpMethod.Delete, "/v1/backup", token))).StatusCode);
        Assert.Empty(await KeysAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(Authorized(HttpMethod.Get, "/v1/backup", token))).StatusCode);
    }

    [SkippableFact]
    public async Task Moving_the_files_to_the_bucket_copies_each_version_once_and_leaves_what_differs_alone()
    {
        // A service that has kept its backups as files so far.
        var files = new BackupStore(new ServiceOptions { DataDirectory = Dir, KeepVersions = 5, KeepDays = 0 });
        var sam = BackupStore.FolderOf("sam@example.com");
        var kim = BackupStore.FolderOf("kim@example.com");
        var contents = new Dictionary<(string, int), byte[]>
        {
            [(sam, 1)] = Locked(1500), [(sam, 2)] = Locked(2500), [(kim, 1)] = Locked(900),
        };
        foreach (var ((account, version), body) in contents)
            Assert.Equal(SaveOutcome.Saved, (await files.SaveAsync(account, version - 1, new MemoryStream(body), CancellationToken.None)).Outcome);
        File.WriteAllText(Path.Combine(Dir, "accounts.json"), "{}");         // other files in the data folder are not backups and are not touched

        var options = S3Options();
        var output = new StringWriter();
        Assert.Equal(0, await AdminCommands.RunBackupsAsync(new[] { "copy-to-s3", "--dry-run" }, options, output));
        Assert.Contains("Would copy 3 versions", output.ToString());
        Assert.Empty(await KeysAsync());

        output = new StringWriter();
        Assert.Equal(0, await AdminCommands.RunBackupsAsync(new[] { "copy-to-s3" }, options, output));
        Assert.Contains("Copied 3 versions", output.ToString());
        var bucket = Stores.Backups(options);
        foreach (var ((account, version), body) in contents)
        {
            var found = await bucket.OpenAsync(account, version);
            await using var content = found!.Value.Content;
            using var copy = new MemoryStream();
            await content.CopyToAsync(copy);
            Assert.Equal(body, copy.ToArray());
        }
        Assert.Equal(3, (await KeysAsync()).Count);
        Assert.True(File.Exists(Path.Combine(Dir, "accounts.json")));        // the files stay

        // Again: nothing new.
        output = new StringWriter();
        Assert.Equal(0, await AdminCommands.RunBackupsAsync(new[] { "copy-to-s3" }, options, output));
        Assert.Contains("Copied 0 versions", output.ToString());
        Assert.Contains("3 already in the bucket", output.ToString());

        // A version that is in the bucket with another size is reported, not overwritten.
        using (var client = S3BackupStore.CreateClient(options))
            await client.PutObjectAsync(new PutObjectRequest { BucketName = options.S3Bucket, Key = _prefix + kim + "/v00000001.bin", ContentBody = "something else", UseChunkEncoding = false });
        output = new StringWriter();
        Assert.Equal(1, await AdminCommands.RunBackupsAsync(new[] { "copy-to-s3" }, options, output));
        Assert.Contains("Left alone", output.ToString());
    }

    [SkippableFact]
    public async Task Moving_the_files_needs_the_store_to_be_set_up()
    {
        var output = new StringWriter();
        Assert.Equal(2, await AdminCommands.RunBackupsAsync(new[] { "copy-to-s3" }, new ServiceOptions { DataDirectory = Dir }, output));
        Assert.Contains("Service__S3Bucket", output.ToString());
        Assert.Equal(2, await AdminCommands.RunBackupsAsync(Array.Empty<string>(), new ServiceOptions { DataDirectory = Dir }, new StringWriter()));
    }
}
