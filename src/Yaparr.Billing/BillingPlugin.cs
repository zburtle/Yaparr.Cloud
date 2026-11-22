using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Yaparr.Cloud;

/// <summary>
/// Takes payment for Yaparr Cloud: the Polar adapter, the webhook, the pay-first flow, the account page, and the payment controls in the admin portal. What a payment decides (who is covered, the 402, GET /v1/plan) is in
/// the server (PlanGate), which works without this; this is the part that finds out who has paid.
/// </summary>
public sealed class BillingPlugin : IYaparrPlugin
{
    public string Name => "Yaparr billing";

    public int ApiVersion => PluginApi.Version;

    public void ConfigureServices(IServiceCollection services, ServiceOptions options, IConfiguration configuration)
    {
        // A provider is only made when it is the one named, and only when it is set up: a half-set Polar is said at the start, with the name of what is missing.
        if (options.Billing.Provider.Equals(PolarBilling.ProviderName, StringComparison.OrdinalIgnoreCase))
        {
            var polar = configuration.GetSection("Service:Billing:Polar").Get<PolarOptions>() ?? new PolarOptions();
            if (PolarBilling.Missing(polar) is { } missing)
                throw new InvalidOperationException($"Service__Billing__Provider is \"polar\", but {missing} is not set.");
            services.AddSingleton<IBillingProvider>(_ => new PolarBilling(polar, PolarBilling.CreateClient(polar)));
        }
        services.AddSingleton<IBillingBackend, BillingProviders>();
        services.AddSingleton<BillingService>();
        services.AddSingleton<BillingTickets>();
        services.AddHostedService<BillingJobs>();
        services.AddSingleton<IAccountDeletionGuard, BillingDeletionGuard>();
        services.AddSingleton<IAdminExtension, BillingAdminExtension>();
    }

    public void MapEndpoints(WebApplication app, ServiceOptions options)
    {
        app.MapBilling(options);
        BillingAdminRoutes.Map(app, options);
    }
}

/// <summary>The names the providers that are in place answer to (what <c>Service__Billing__Provider</c> may say).</summary>
internal sealed class BillingProviders(IEnumerable<IBillingProvider> providers) : IBillingBackend
{
    public IReadOnlyList<string> Names => providers.Select(p => p.Name).ToList();
}

/// <summary>An account is not removed while it is still being charged: what it pays for is ended first, and if that cannot be done nothing is deleted.</summary>
internal sealed class BillingDeletionGuard(BillingService billing) : IAccountDeletionGuard
{
    public async Task<string?> BeforeDeleteAsync(string account, CancellationToken cancel) =>
        await billing.CancelAllAsync(account, cancel)
            ? null
            : "The payment provider could not be reached to end the subscription, so nothing was deleted. Try again in a minute.";
}
