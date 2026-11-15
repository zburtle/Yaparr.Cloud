namespace Yaparr.Cloud;

/// <summary>
/// What the person running the service types to look after accounts: <c>Yaparr.Cloud accounts add|password|remove|list</c>. It works on the same
/// data folder as the running service, which notices the change by itself.
/// </summary>
public static class AdminCommands
{
    public const string Usage =
        "Usage:\n" +
        "  accounts add <email> [--password <password>]           make an account (named by the person's email address); without a password one is made up and shown once\n" +
        "  accounts password <email> [--password <password>]      give an account a new password and sign its devices out\n" +
        "  accounts remove <email> [--keep-backup]                remove an account and, unless told not to, its backup\n" +
        "  accounts list                                          who has an account, their devices, and the space each uses\n" +
        "  accounts admin <email> [--remove]                      let an account sign in to the admin portal (/admin): shows the secret for its authenticator app once; --remove takes it away\n";

    public const string CodesUsage =
        "Usage:\n" +
        "  codes add [--note <who it is for>] [--uses <how many people>] [--days <how long it works>]\n" +
        "                                  make a signup code and the link to send; one code can be used by many people (any number unless --uses)\n" +
        "  codes list                      the codes, who used each, and whether it still works\n" +
        "  codes revoke <id>               stop a code working (people who signed up with it keep their accounts)\n" +
        "  codes delete <id>               take a code out altogether, with the record of who used it (they keep their accounts)\n";

    public const string BackupsUsage =
        "Usage:\n" +
        "  backups copy-to-s3 [--dry-run]  copy the backup files in the data folder into the bucket (the S3 settings have to be set); it can be run again, a version already\n" +
        "                                  there is left alone, and nothing is removed from the data folder\n";

    /// <summary><c>backups copy-to-s3</c>: moves a service that kept its backups as files over to an S3-compatible store (see <see cref="BackupCopy"/>).</summary>
    public static async Task<int> RunBackupsAsync(string[] args, ServiceOptions options, TextWriter output)
    {
        if (args.Length == 0 || !args[0].Equals("copy-to-s3", StringComparison.OrdinalIgnoreCase)) { output.Write(BackupsUsage); return 2; }
        if (options.S3Bucket.Length == 0 || options.S3Endpoint.Length == 0)
        {
            output.WriteLine("Set Service__S3Endpoint, Service__S3Bucket, Service__S3AccessKey and Service__S3SecretKey first: that is where the backups are copied to.");
            return 2;
        }
        bool dryRun = args.Contains("--dry-run");
        using var s3 = S3BackupStore.CreateClient(options);
        var result = await BackupCopy.CopyAsync(options, s3, output, dryRun, CancellationToken.None);
        output.WriteLine($"{(dryRun ? "Would copy" : "Copied")} {result.Copied} version{(result.Copied == 1 ? "" : "s")} ({result.Bytes / 1024.0 / 1024.0:0.0} MiB); {result.AlreadyThere} already in the bucket" +
                         (result.Different > 0 ? $"; {result.Different} in the bucket with another size (left alone, look at them)" : "") + ".");
        if (!dryRun && result.Different == 0) output.WriteLine("The files in the data folder are still there. When you have checked the service on the bucket you can remove them.");
        return result.Different > 0 ? 1 : 0;
    }

    public static Task<int> RunCodesAsync(string[] args, ServiceOptions options, TextWriter output) => RunCodesAsync(args, options, output, DateTimeOffset.UtcNow);

    internal static Task<int> RunCodesAsync(string[] args, ServiceOptions options, TextWriter output, DateTimeOffset now)
    {
        var codes = Stores.Codes(options, () => now);
        var command = args.FirstOrDefault()?.ToLowerInvariant();
        var rest = args.Skip(1).ToList();
        switch (command)
        {
            case "add":
            {
                int? uses = null;
                TimeSpan? lifetime = null;
                if (Option(rest, "--uses") is { } usesText)
                {
                    if (!int.TryParse(usesText, out var n) || n < 1) { output.WriteLine("--uses has to be a number of people, 1 or more."); return Task.FromResult(2); }
                    uses = n;
                }
                if (Option(rest, "--days") is { } daysText)
                {
                    if (!int.TryParse(daysText, out var d) || d < 1 || d > 3650) { output.WriteLine("--days has to be a number of days, 1 to 3650."); return Task.FromResult(2); }
                    lifetime = TimeSpan.FromDays(d);
                }
                var (code, info) = codes.Create(Option(rest, "--note") ?? "", uses, lifetime);
                output.WriteLine($"Made the code {code}  (id {info.Id})");
                output.WriteLine(uses == null ? "Any number of people can sign up with it, until you revoke it." : $"Up to {uses} people can sign up with it.");
                if (info.Expires is { } end) output.WriteLine($"It works until {end:yyyy-MM-dd}.");
                var server = options.PublicUrl.Trim().TrimEnd('/');
                output.WriteLine();
                output.WriteLine("Send this link (the code is not kept anywhere else, so this is the only time it is shown):");
                output.WriteLine($"  {SignupLink(options, code)}");
                if (server.Length == 0)
                    output.WriteLine("Set Service__PublicUrl to this service's address (like https://backup.example.org) to have it filled in; until then replace <your server address> with it.");
                return Task.FromResult(0);
            }
            case "list":
            {
                var all = codes.List();
                if (all.Count == 0) output.WriteLine("No codes yet.");
                foreach (var info in all)
                {
                    var state = info.Problem(now) ?? "works";
                    output.WriteLine($"{info.Id}  {state,-8} made {info.Created:yyyy-MM-dd}  {info.Uses.Count}{(info.MaxUses is { } max ? $" of {max}" : "")} signed up" +
                        (info.Expires is { } end ? $"  until {end:yyyy-MM-dd}" : "") + (info.Note.Length > 0 ? $"  \"{info.Note}\"" : ""));
                    foreach (var use in info.Uses) output.WriteLine($"    {use.Email}  {use.At:yyyy-MM-dd HH:mm}");
                }
                return Task.FromResult(0);
            }
            case "revoke":
            {
                if (rest.Count == 0) { output.Write(CodesUsage); return Task.FromResult(2); }
                if (!codes.Revoke(rest[0])) { output.WriteLine("There is no code with that id (codes list shows them)."); return Task.FromResult(1); }
                output.WriteLine("Revoked. People who already signed up with it keep their accounts.");
                return Task.FromResult(0);
            }
            case "delete":
            {
                if (rest.Count == 0) { output.Write(CodesUsage); return Task.FromResult(2); }
                if (!codes.Delete(rest[0])) { output.WriteLine("There is no code with that id (codes list shows them)."); return Task.FromResult(1); }
                output.WriteLine("Deleted. People who signed up with it keep their accounts.");
                return Task.FromResult(0);
            }
            default:
                output.Write(CodesUsage);
                return Task.FromResult(2);
        }
    }

    /// <summary>The link to send for a code: the sign-up page on the website, with this server and the code after the # (so neither goes to the website's host).</summary>
    public static string SignupLink(ServiceOptions options, string code)
    {
        var server = options.PublicUrl.Trim().TrimEnd('/');
        var page = options.SignupPageUrl.Trim().TrimEnd('/');
        return $"{page}#server={(server.Length == 0 ? "<your server address>" : Uri.EscapeDataString(server))}&code={code}";
    }

    public static async Task<int> RunAsync(string[] args, ServiceOptions options, TextWriter output)
    {
        var accounts = Stores.Accounts(options);
        var store = Stores.Backups(options);
        var command = args.FirstOrDefault()?.ToLowerInvariant();
        var rest = args.Skip(1).ToList();

        switch (command)
        {
            case "add":
            case "password":
            {
                if (rest.Count == 0) { output.Write(Usage); return 2; }
                var name = AccountDirectory.Normalize(rest[0]);
                if (name == null) { output.WriteLine("That is not a usable email address. An account is named by the email address of its owner, like sam@example.com."); return 2; }
                var password = Option(rest, "--password");
                var made = password == null;
                password ??= Passwords.Generate();
                if (Passwords.Check(password) is { } why) { output.WriteLine(why); return 2; }

                // Only a hash of the password is kept.
                if (command == "add")
                {
                    if (!accounts.Add(name, password)) { output.WriteLine($"There is already an account called {name}."); return 1; }
                    output.WriteLine($"Made the account {name}.");
                }
                else
                {
                    if (!accounts.SetPassword(name, password)) { output.WriteLine("There is no such account."); return 1; }
                    output.WriteLine($"Changed the password of {name} and signed its devices out. Its backups are untouched.");
                }
                if (made)
                {
                    output.WriteLine($"Its password is:  {password}");
                    output.WriteLine("It is not kept anywhere, so this is the only time it is shown. Give it to them; they can change it on the sign-in page.");
                }
                return 0;
            }
            case "remove":
            {
                if (rest.Count == 0) { output.Write(Usage); return 2; }
                var name = AccountDirectory.Normalize(rest[0]);
                if (name == null || !accounts.Remove(name)) { output.WriteLine("There is no such account."); return 1; }
                output.WriteLine($"Removed the account {name}.");
                if (rest.Contains("--keep-backup")) output.WriteLine("Its backup was kept. Making an account with the same name again reaches it.");
                else output.WriteLine(await store.DeleteAsync(BackupStore.FolderOf(name), CancellationToken.None) ? "Its backup was deleted." : "It had no backup.");
                return 0;
            }
            case "admin":
            {
                if (rest.Count == 0) { output.Write(Usage); return 2; }
                var name = AccountDirectory.Normalize(rest[0]);
                if (name == null) { output.WriteLine("That is not a usable email address."); return 2; }
                if (rest.Contains("--remove"))
                {
                    if (!accounts.RemoveAdmin(name)) { output.WriteLine($"{name} is not an admin."); return 1; }
                    output.WriteLine($"{name} can no longer sign in to the admin portal.");
                    return 0;
                }
                if (accounts.MakeAdmin(name) is not { } secret) { output.WriteLine("There is no such account, or it has not chosen a password yet."); return 1; }
                output.WriteLine($"{name} can now sign in to the admin portal, at /admin, with the password and a code from an authenticator app.");
                output.WriteLine($"Add this to the app (it asks for a key or a setup link): the key is  {secret}");
                output.WriteLine($"or the link  {Totp.Uri(name, "Yaparr cloud", secret)}");
                output.WriteLine("It is shown once. Running this again makes a new key (for a lost phone) and the old one stops working.");
                return 0;
            }
            case "list":
            {
                var all = accounts.List();
                if (all.Count == 0) output.WriteLine("No accounts yet.");
                foreach (var account in all)
                {
                    if (AccountDirectory.Normalize(account.Name) != account.Name)
                        output.WriteLine($"{account.Name}  is not an email address, so it cannot sign in any more: remove it and add the person again with their address.");
                    var versions = await store.VersionsAsync(BackupStore.FolderOf(account.Name));
                    var size = versions.Sum(v => v.Size);
                    output.WriteLine($"{account.Name,-32} made {account.Created:yyyy-MM-dd}  " +
                        (account.HasPassword ? $"{account.Devices} device{(account.Devices == 1 ? "" : "s")} signed in"
                            : account.SignupCode != null ? "signed up, has not chosen a password yet"
                            : "NO PASSWORD (run: accounts password " + account.Name + ")") + $"  {account.Pass} pass" + (account.SignupCode != null ? $" (code {account.SignupCode})" : "") + (account.IsAdmin ? "  ADMIN" : "") + "  " +
                        (versions.Count == 0 ? "no backup" : $"{versions.Count} version{(versions.Count == 1 ? "" : "s")}, {size / 1024.0 / 1024.0:0.0} MiB, last {versions[0].At:yyyy-MM-dd HH:mm}"));
                }
                return 0;
            }
            default:
                output.Write(Usage);
                return 2;
        }
    }

    private static string? Option(List<string> args, string name)
    {
        var at = args.IndexOf(name);
        return at >= 0 && at + 1 < args.Count ? args[at + 1] : null;
    }
}
