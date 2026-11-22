using System.Reflection;
using System.Runtime.Loader;

namespace Yaparr.Cloud;

// How the server is extended without being changed: a plug-in is an assembly in a folder (Service__PluginsDirectory) that adds services, endpoints, background work and admin cards.
// With no folder named nothing is loaded and the server is exactly what it is: everything in this repository works on its own. Paying for the hosted service is done by a plug-in
// (kept apart from this repository); what this repository keeps, for anyone to build their own, is what a service has to do about it: who is covered (ISubscriptions, AccountPlan,
// PlanGate), the 402 that a lapsed account gets from every service, GET /v1/plan, and the hooks below.

/// <summary>What the plug-in API is. A plug-in says which one it was built against, and one that was built against another is refused at the start, with a message, not half-loaded.</summary>
public static class PluginApi
{
    public const int Version = 1;
}

/// <summary>A plug-in. One public class with a parameterless constructor in the assembly.</summary>
public interface IYaparrPlugin
{
    string Name { get; }

    /// <summary>The <see cref="PluginApi.Version"/> it was built for.</summary>
    int ApiVersion { get; }

    /// <summary>Adds what it needs to the service (its own stores, jobs, the hooks below). <paramref name="configuration"/> is the whole configuration, for the settings of its own.</summary>
    void ConfigureServices(IServiceCollection services, ServiceOptions options, IConfiguration configuration);

    /// <summary>Adds its endpoints, once the service is built.</summary>
    void MapEndpoints(WebApplication app, ServiceOptions options);
}

/// <summary>
/// Says which names of <c>Service__Billing__Provider</c> a part of the service answers to. With a provider named the service gates (PlanGate), and it refuses to start unless something
/// like this answers to the name, so a typo is said at the start and not at the first payment. A plug-in that takes payments registers one.
/// </summary>
public interface IBillingBackend
{
    /// <summary>What <c>Service__Billing__Provider</c> may say (not case sensitive).</summary>
    IReadOnlyList<string> Names { get; }
}

/// <summary>
/// Runs before an account is removed (by its owner, or from the admin portal). It returns null when the account may go, or a sentence for the person when it may not (nothing is
/// removed then): the account pays for something that could not be ended, say.
/// </summary>
public interface IAccountDeletionGuard
{
    Task<string?> BeforeDeleteAsync(string account, CancellationToken cancel);
}

/// <summary>A card, a column and some buttons that a plug-in adds to the admin portal's overview. The portal does the signing in and checks the form token; the plug-in adds its own addresses with <see cref="AdminGuard"/>.</summary>
public interface IAdminExtension
{
    /// <summary>Called once for each time the overview is drawn, with all the accounts, so that what is the same for every row is worked out once.</summary>
    IAdminView Begin(IReadOnlyList<AccountEntry> accounts, DateTimeOffset now);
}

/// <summary>What an <see cref="IAdminExtension"/> adds to one drawing of the overview. Everything returned is HTML that is already encoded.</summary>
public interface IAdminView
{
    /// <summary>The heading of the column it adds to the table of accounts, or null for none.</summary>
    string? ColumnTitle { get; }

    /// <summary>The cell of an account in that column.</summary>
    string Cell(AccountEntry account);

    /// <summary>Buttons for an account's row (forms carry <paramref name="csrf"/>, the session's form token).</summary>
    string Actions(AccountEntry account, string csrf);

    /// <summary>A card above the storage card, or null.</summary>
    string? Card();
}

public static class AccountDeletionGuards
{
    /// <summary>Null when every guard lets the account go, else the first sentence a guard gave.</summary>
    public static async Task<string?> RefusalAsync(IEnumerable<IAccountDeletionGuard> guards, string account, CancellationToken cancel)
    {
        foreach (var guard in guards)
            if (await guard.BeforeDeleteAsync(account, cancel) is { } problem) return problem;
        return null;
    }
}

/// <summary>The plug-ins that were loaded.</summary>
public sealed class PluginRegistry
{
    private readonly List<IYaparrPlugin> _plugins = new();

    public IReadOnlyList<IYaparrPlugin> Plugins => _plugins;

    /// <summary>Loads every plug-in in a folder, and each assembly named in <paramref name="assemblies"/> (for tests and for a plug-in that is built beside the service).</summary>
    public static PluginRegistry Load(string? directory, IEnumerable<string> assemblies, Action<string>? log = null)
    {
        var registry = new PluginRegistry();
        var paths = new List<string>();
        if (!string.IsNullOrWhiteSpace(directory))
        {
            if (!Directory.Exists(directory)) throw new InvalidOperationException($"Service__PluginsDirectory is \"{directory}\", but there is no such folder.");
            paths.AddRange(Directory.EnumerateFiles(directory, "*.dll").OrderBy(p => p, StringComparer.Ordinal));
        }
        paths.AddRange(assemblies.Where(a => !string.IsNullOrWhiteSpace(a)));

        foreach (var path in paths)
        {
            Assembly assembly;
            try { assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(path)); }
            catch (Exception ex) when (ex is BadImageFormatException or FileLoadException or FileNotFoundException)
            {
                // A library that a plug-in brought along is not a plug-in: only an assembly that cannot be read at all is a mistake worth stopping for.
                if (ex is FileNotFoundException) throw new InvalidOperationException($"The plug-in \"{path}\" could not be found.");
                continue;
            }
            // One public class with a parameterless constructor is a plug-in; anything else in the assembly is not.
            foreach (var type in SafeTypes(assembly).Where(t => t is { IsPublic: true, IsAbstract: false, IsInterface: false } && typeof(IYaparrPlugin).IsAssignableFrom(t) && t.GetConstructor(Type.EmptyTypes) != null))
            {
                var plugin = (IYaparrPlugin)(Activator.CreateInstance(type) ?? throw new InvalidOperationException($"The plug-in {type.FullName} could not be made."));
                registry.Accept(plugin);
                log?.Invoke($"plug-in: {plugin.Name}");
            }
        }
        return registry;
    }

    /// <summary>Takes plug-ins that are already made (the same check of the API version as for those that are loaded).</summary>
    internal static PluginRegistry From(IEnumerable<IYaparrPlugin> plugins)
    {
        var registry = new PluginRegistry();
        foreach (var plugin in plugins) registry.Accept(plugin);
        return registry;
    }

    private void Accept(IYaparrPlugin plugin)
    {
        if (plugin.ApiVersion != PluginApi.Version)
            throw new InvalidOperationException($"The plug-in {plugin.Name} was built for plug-in API {plugin.ApiVersion}, and this server has API {PluginApi.Version}. Use a build of the plug-in made for this server.");
        _plugins.Add(plugin);
    }

    private static IEnumerable<Type> SafeTypes(Assembly assembly)
    {
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { return ex.Types.OfType<Type>(); }
    }
}

/// <summary>For a plug-in's own admin addresses: the same sign-in and form-token check as the portal's, so an address a plug-in adds is no easier to reach than one of the portal's.</summary>
public static class AdminGuard
{
    /// <summary>The signed-in admin of this request, or null.</summary>
    public static AdminSession? Current(HttpContext http) => AdminPortal.CurrentOf(http);

    /// <summary>A form post of a signed-in admin that carries the token of its session; anything else is not done.</summary>
    public static Task<IResult> Post(HttpContext http, Func<AdminSession, IFormCollection, Task<IResult>> action) => AdminPortal.PostOf(http, action);
}
