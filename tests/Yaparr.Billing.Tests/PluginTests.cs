using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Yaparr.Cloud;

namespace Yaparr.Cloud.Tests;

/// <summary>The billing plug-in as the server loads it.</summary>
public sealed class BillingPluginTests : BillingTestBase
{
    [Fact]
    public void The_plug_in_says_which_plug_in_api_it_was_built_for()
    {
        var plugin = new BillingPlugin();
        Assert.Equal(PluginApi.Version, plugin.ApiVersion);
        Assert.False(string.IsNullOrWhiteSpace(plugin.Name));
    }

    [Fact]
    public async Task With_no_provider_named_the_plug_in_adds_nothing_the_service_would_gate_on()
    {
        var client = Start();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/healthz")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/billing/done")).StatusCode);   // the addresses exist, and answer 404 while billing is off
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/billing/webhook/polar", new StringContent("{}"))).StatusCode);
    }

    [Fact]
    public void Polar_named_without_its_settings_stops_the_service_and_says_what_is_missing()
    {
        var failure = Record.Exception(() => Start(new() { ["Billing:Provider"] = "polar", ["Billing:Polar:AccessToken"] = "x" }).GetAsync("/healthz").GetAwaiter().GetResult());
        Assert.NotNull(failure);
        var all = new List<string>();
        for (var e = failure; e != null; e = e.InnerException) all.Add(e.Message);
        Assert.Contains("Service__Billing__Polar__WebhookSecret", string.Join(" | ", all));
    }
}
