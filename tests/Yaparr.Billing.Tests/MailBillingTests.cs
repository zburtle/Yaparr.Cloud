using System.Net;
using System.Net.Mail;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Yaparr.Cloud;

namespace Yaparr.Cloud.Tests;

/// <summary>A mailer for tests: it keeps what it was asked to send, and can fail as a mail server that is down does.</summary>
internal sealed class FakeMailer : IMailer
{
    public List<Yaparr.Cloud.MailMessage> Sent { get; } = new();
    public bool Down { get; set; }

    public Task SendAsync(Yaparr.Cloud.MailMessage message, CancellationToken cancel)
    {
        if (Down) throw new SmtpException("The mail server could not be reached.");
        lock (Sent) Sent.Add(message);
        return Task.CompletedTask;
    }
}

/// <summary>What email does for someone who paid: the link to choose the password goes to the address (the pay-first flow is the billing plug-in's; the mail itself is the server's).</summary>
public sealed class MailBillingTests : BillingTestBase
{
    private readonly FakeMailer _mailer = new();
    private readonly FakeBilling _billing = new();

    private HttpClient Run() =>
        Start(new() { ["Mail:Host"] = "smtp.fake.test", ["Mail:From"] = "noreply@example.org", ["Billing:Provider"] = "fake" }, services =>
        {
            services.AddSingleton<IMailer>(_mailer);
            services.AddSingleton<IBillingProvider>(_billing);
        });

    private static Task<HttpResponseMessage> Form(HttpClient client, string path, params (string, string)[] fields) =>
        client.PostAsync(path, new FormUrlEncodedContent(fields.ToDictionary(f => f.Item1, f => f.Item2)));

    private Task Idle() => LastFactory!.Services.GetRequiredService<MailService>().IdleAsync();

    private static string LinkIn(Yaparr.Cloud.MailMessage message) => Regex.Match(message.Body, @"https?://\S+").Value;

    private static string Path(string link) => new Uri(link).PathAndQuery;

    [Fact]
    public async Task Paying_first_with_email_on_sends_the_link_to_choose_the_password_to_the_address_and_the_page_does_not_show_the_form()
    {
        var client = Run();
        var start = await Form(client, "/billing/start", ("email", "new@example.com"), ("interval", "monthly"));
        Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);
        var request = _billing.Checkouts.Last();
        var paidToken = System.Web.HttpUtility.ParseQueryString(request.SuccessUrl.Query)["t"]!;
        _billing.States["sub_1"] = new SubscriptionState("cus_1", "sub_1", request.AccountRef, "cloud", SubscriptionStatus.Active, DateTimeOffset.UtcNow.AddDays(30), false);

        // Back from the payment page: nothing to choose here, and a mail is on its way.
        var done = await client.GetAsync("/billing/done?t=" + Uri.EscapeDataString(paidToken));
        await Idle();
        var html = await done.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, done.StatusCode);
        Assert.Contains("Check your email", html);
        Assert.DoesNotContain("name=\"password\"", html);
        Assert.DoesNotContain("set_", html);   // the way in is only in the mail
        var mail = Assert.Single(_mailer.Sent);
        Assert.Equal("new@example.com", mail.To);
        var link = LinkIn(mail);
        Assert.Contains("/account/setup?t=set_", link);

        // The link on the payment page has done its part; the mail's is the way on.
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/billing/done?t=" + Uri.EscapeDataString(paidToken))).StatusCode);
        var form = await (await client.GetAsync(Path(link))).Content.ReadAsStringAsync();
        Assert.Contains("name=\"password\"", form);
        Assert.Contains("Your payment has arrived", form);
        var token = Regex.Match(form, "name=\"token\" value=\"(set_[A-Za-z0-9_-]+)\"").Groups[1].Value;
        var finished = await Form(client, "/account/setup", ("token", token), ("password", "a brand new password"), ("again", "a brand new password"));
        Assert.Equal(HttpStatusCode.OK, finished.StatusCode);
        Assert.True(new AccountDirectory(Options).CheckPassword("new@example.com", "a brand new password"));
    }

    [Fact]
    public async Task Someone_who_paid_and_lost_the_link_asks_for_another_on_the_page_that_says_they_have_paid()
    {
        var client = Run();
        var accounts = new AccountDirectory(Options);
        accounts.StartPending("lost@example.com", TimeSpan.FromHours(24));
        new SubscriptionStore(Options).Upsert(new Subscription("lost@example.com", "fake", "cus_1", "sub_1", "cloud", SubscriptionStatus.Active, DateTimeOffset.UtcNow.AddDays(10), DateTimeOffset.UtcNow));

        var paid = await Form(client, "/billing/start", ("email", "lost@example.com"));
        Assert.Equal(HttpStatusCode.Conflict, paid.StatusCode);
        var html = await paid.Content.ReadAsStringAsync();
        Assert.Contains("action=\"/account/forgot\"", html);
        Assert.Contains("Email me a link", html);

        await Form(client, "/account/forgot", ("email", "lost@example.com"));
        await Idle();
        var mail = Assert.Single(_mailer.Sent);
        Assert.Contains("/account/setup?t=set_", LinkIn(mail));
        Assert.Contains("Choose your Yaparr Cloud password", mail.Subject);

        // An address that has paid for nothing is sent nothing.
        accounts.StartPending("unpaid@example.com", TimeSpan.FromHours(24));
        await Form(client, "/account/forgot", ("email", "unpaid@example.com"));
        await Idle();
        Assert.Single(_mailer.Sent);
    }
}
