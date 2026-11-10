namespace Yaparr.Cloud;

/// <summary>
/// What the person running the service types to look after accounts: <c>Yaparr.Cloud accounts add|password|remove|list</c>. It works on the same
/// data folder as the running service, which notices the change by itself.
/// </summary>
public static class AdminCommands
{
    public const string Usage =
        "Usage:\n" +
        "  accounts add <username> [--password <password>]        make an account; without a password one is made up and shown once\n" +
        "  accounts password <username> [--password <password>]   give an account a new password and sign its devices out\n" +
        "  accounts remove <username> [--keep-backup]             remove an account and, unless told not to, its backup\n" +
        "  accounts list                                          who has an account, their devices, and the space each uses\n";

    public static async Task<int> RunAsync(string[] args, ServiceOptions options, TextWriter output)
    {
        var accounts = new AccountDirectory(options);
        var store = new BackupStore(options);
        var command = args.FirstOrDefault()?.ToLowerInvariant();
        var rest = args.Skip(1).ToList();

        switch (command)
        {
            case "add":
            case "password":
            {
                if (rest.Count == 0) { output.Write(Usage); return 2; }
                var name = AccountDirectory.Normalize(rest[0]);
                if (name == null) { output.WriteLine("That is not a usable username: 3 to 32 letters, digits, dots, dashes or underscores."); return 2; }
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
            case "list":
            {
                var all = accounts.List();
                if (all.Count == 0) output.WriteLine("No accounts yet.");
                foreach (var account in all)
                {
                    var versions = store.Versions(BackupStore.FolderOf(account.Name));
                    var size = versions.Sum(v => v.Size);
                    output.WriteLine($"{account.Name,-32} made {account.Created:yyyy-MM-dd}  " +
                        (account.HasPassword ? $"{account.Devices} device{(account.Devices == 1 ? "" : "s")} signed in" : "NO PASSWORD (run: accounts password " + account.Name + ")") + "  " +
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
