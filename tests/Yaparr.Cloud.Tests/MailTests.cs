using System.Net;
using System.Net.Mail;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
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

public sealed class MailOptionsTests
{
    [Fact]
    public void Email_is_off_until_a_host_is_named_and_a_half_set_one_says_what_is_missing()
    {
        Assert.Null(new MailOptions().Problem());                                          // off: nothing to check
        Assert.Contains("From", new MailOptions { Host = "smtp.example.org" }.Problem());
        Assert.Contains("From", new MailOptions { Host = "smtp.example.org", From = "not an address" }.Problem());
        Assert.Contains("Password", new MailOptions { Host = "smtp.example.org", From = "noreply@example.org", User = "sam" }.Problem());
        Assert.Contains("Security", new MailOptions { Host = "smtp.example.org", From = "noreply@example.org", Security = "plain" }.Problem());
        Assert.Contains("Port", new MailOptions { Host = "smtp.example.org", From = "noreply@example.org", Port = 0 }.Problem());
        Assert.Null(new MailOptions { Host = "smtp.example.org", From = "noreply@example.org", User = "sam", Password = "x" }.Problem());
        Assert.Equal("starttls", new MailOptions().Security);                              // encrypted before the password is sent, unless said otherwise
    }

    [Fact]
    public async Task A_message_goes_through_a_real_smtp_conversation_with_the_text_in_it()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var received = new TaskCompletionSource<string>();
        _ = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII);
            await using var writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
            var data = new StringBuilder();
            await writer.WriteLineAsync("220 test ready");
            var inData = false;
            while (await reader.ReadLineAsync() is { } line)
            {
                if (inData)
                {
                    if (line == ".") { inData = false; await writer.WriteLineAsync("250 queued"); received.TrySetResult(data.ToString()); }
                    else data.AppendLine(line);
                    continue;
                }
                if (line.StartsWith("EHLO", StringComparison.OrdinalIgnoreCase) || line.StartsWith("HELO", StringComparison.OrdinalIgnoreCase)) await writer.WriteLineAsync("250 test");
                else if (line.StartsWith("MAIL FROM") || line.StartsWith("RCPT TO")) { data.AppendLine(line); await writer.WriteLineAsync("250 ok"); }
                else if (line == "DATA") { inData = true; await writer.WriteLineAsync("354 go on"); }
                else if (line == "QUIT") { await writer.WriteLineAsync("221 bye"); break; }
                else await writer.WriteLineAsync("250 ok");
            }
        });

        var mailer = new SmtpMailer(new MailOptions { Host = "127.0.0.1", Port = port, Security = "none", From = "noreply@example.org", FromName = "Yaparr Cloud" });
        await mailer.SendAsync(MailService.ResetLink("sam@example.org", "https://cloud.example/account/reset?t=rst_abc", TimeSpan.FromHours(1)), default);

        var text = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains("MAIL FROM:<noreply@example.org>", text);
        Assert.Contains("RCPT TO:<sam@example.org>", text);
        Assert.Contains("Subject: Reset your Yaparr Cloud password", text);
        Assert.Contains("https://cloud.example/account/reset?t=rst_abc", BodyOf(text));
    }

    // The text as the person's mail program shows it: what came after the headers, with the transfer encoding (base64 or quoted-printable) undone.
    private static string BodyOf(string data)
    {
        var split = data.IndexOf("\n\n", StringComparison.Ordinal);
        var headers = data[..split];
        var body = data[(split + 2)..].Trim();
        if (headers.Contains("Content-Transfer-Encoding: base64", StringComparison.OrdinalIgnoreCase))
            return Encoding.UTF8.GetString(Convert.FromBase64String(string.Concat(body.Split('\n').Select(l => l.Trim()))));
        if (headers.Contains("Content-Transfer-Encoding: quoted-printable", StringComparison.OrdinalIgnoreCase))
            return Regex.Replace(body.Replace("=\r\n", "").Replace("=\n", ""), "=([0-9A-F]{2})", m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());
        return body;
    }

    [Fact]
    public async Task A_mail_server_that_is_down_is_logged_as_that_and_never_breaks_the_page()
    {
        var mail = new MailService(new FakeMailer { Down = true }, NullLoggerFactory.Instance);
        mail.Send(MailService.ResetLink("sam@example.org", "https://x.example/", TimeSpan.FromHours(1)));
        await mail.IdleAsync();   // it finished, and nothing was thrown at the caller
        Assert.False(new MailService(null, NullLoggerFactory.Instance).Enabled);
    }

    [Fact]
    public void A_reset_link_works_once_for_an_hour_and_asking_again_replaces_it()
    {
        var now = new DateTimeOffset(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);
        var links = new ResetLinks(() => now);
        var first = links.Make("sam@example.org");
        Assert.Equal("sam@example.org", links.Peek(first));
        Assert.Equal("sam@example.org", links.Peek(first));       // looking does not use it
        var second = links.Make("sam@example.org");
        Assert.Null(links.Peek(first));                           // replaced
        Assert.Equal("sam@example.org", links.Take(second));
        Assert.Null(links.Take(second));                          // once
        var third = links.Make("kim@example.org");
        now = now.AddMinutes(61);
        Assert.Null(links.Peek(third));                           // an hour
        Assert.Null(links.Peek("rst_nothing"));
        Assert.Null(links.Peek(null));
        Assert.Null(links.Peek(new string('a', 500)));
    }
}

public sealed class MailFlowTests : ServiceTest
{
    private const string Sam = "sam@example.com";
    private readonly FakeMailer _mailer = new();
    private readonly FakeBilling _billing = new();

    private HttpClient Run(bool mail = true, bool billing = false)
    {
        var settings = new Dictionary<string, string>();
        if (mail) { settings["Mail:Host"] = "smtp.fake.test"; settings["Mail:From"] = "noreply@example.org"; }
        if (billing) settings["Billing:Provider"] = "fake";
        return Start(settings, services =>
        {
            if (mail) services.AddSingleton<IMailer>(_mailer);   // off means no mail server is named and nothing is put in its place
            if (billing) services.AddSingleton<IBillingProvider>(_billing);
        });
    }

    private static Task<HttpResponseMessage> Form(HttpClient client, string path, params (string, string)[] fields) =>
        client.PostAsync(path, new FormUrlEncodedContent(fields.ToDictionary(f => f.Item1, f => f.Item2)));

    // A sign-in link the service accepts (the phone's own address, a challenge of 43 characters, a state).
    private static readonly string LoginUrl = "/login?redirect_uri=" + Uri.EscapeDataString(LoginFlow.AppLink) + "&code_challenge=" + new string('a', 43) + "&state=" + new string('b', 22) + "&device=Laptop";

    private Task Idle() => LastFactory!.Services.GetRequiredService<MailService>().IdleAsync();

    private static string LinkIn(Yaparr.Cloud.MailMessage message) => Regex.Match(message.Body, @"https?://\S+").Value;

    private static string Path(string link) => new Uri(link).PathAndQuery;

    // ---- off ----

    [Fact]
    public async Task With_email_off_none_of_its_addresses_exist_and_no_page_offers_it()
    {
        var client = Run(mail: false);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/account/forgot")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Form(client, "/account/forgot", ("email", Sam))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/account/reset?t=rst_x")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Form(client, "/account/reset", ("token", "rst_x"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/account/setup?t=set_x")).StatusCode);
        var login = await (await client.GetAsync(LoginUrl)).Content.ReadAsStringAsync();
        Assert.Contains("name=\"password\"", login);   // it is the sign-in page ...
        Assert.DoesNotContain("/account/forgot", login);   // ... without the offer
        Assert.Empty(_mailer.Sent);
    }

    [Fact]
    public void A_half_set_mail_server_stops_the_service_and_says_what_is_missing()
    {
        var failure = Record.Exception(() => Start(new() { ["Mail:Host"] = "smtp.fake.test" }).GetAsync("/healthz").GetAwaiter().GetResult());
        Assert.NotNull(failure);
        var all = new List<string>();
        for (var e = failure; e != null; e = e.InnerException) all.Add(e.Message);
        Assert.Contains("Service__Mail__From", string.Join(" | ", all));
    }

    // ---- a forgotten password ----

    [Fact]
    public async Task Asking_for_a_link_gives_the_same_answer_for_an_address_with_an_account_and_one_without_and_only_the_first_is_mailed()
    {
        var client = Run();
        Register(Sam);
        var known = await Form(client, "/account/forgot", ("email", Sam));
        var unknown = await Form(client, "/account/forgot", ("email", "nobody@example.com"));
        var nonsense = await Form(client, "/account/forgot", ("email", "not an address"));
        await Idle();

        Assert.Equal(HttpStatusCode.OK, known.StatusCode);
        Assert.Equal(HttpStatusCode.OK, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.OK, nonsense.StatusCode);
        var body = await known.Content.ReadAsStringAsync();
        Assert.Equal(body, await unknown.Content.ReadAsStringAsync());
        Assert.Equal(body, await nonsense.Content.ReadAsStringAsync());
        Assert.DoesNotContain(Sam, body);

        var mail = Assert.Single(_mailer.Sent);
        Assert.Equal(Sam, mail.To);
        Assert.Contains("/account/reset?t=rst_", LinkIn(mail));
    }

    [Fact]
    public async Task The_reset_link_shows_a_form_a_typing_mistake_does_not_use_it_up_and_a_new_password_signs_every_device_out_and_works_once()
    {
        var client = Run();
        var token = Register(Sam);
        await Form(client, "/account/forgot", ("email", Sam));
        await Idle();
        var link = Path(LinkIn(_mailer.Sent[0]));

        var page = await client.GetAsync(link);   // a mail scanner opening it uses up nothing
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();
        var t = Regex.Match(html, "name=\"token\" value=\"(rst_[A-Za-z0-9_-]+)\"").Groups[1].Value;
        Assert.NotEmpty(t);

        Assert.Equal(HttpStatusCode.BadRequest, (await Form(client, "/account/reset", ("token", t), ("password", "short"), ("again", "short"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Form(client, "/account/reset", ("token", t), ("password", "a brand new password"), ("again", "a different one entirely"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(link)).StatusCode);   // still good

        var done = await Form(client, "/account/reset", ("token", t), ("password", "a brand new password"), ("again", "a brand new password"));
        Assert.Equal(HttpStatusCode.OK, done.StatusCode);
        var accounts = new AccountDirectory(Options);
        Assert.True(accounts.CheckPassword(Sam, "a brand new password"));
        Assert.False(accounts.CheckPassword(Sam, Password));
        Assert.Null(accounts.UserOfToken(token));   // every device was signed out

        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(link)).StatusCode);   // once
        Assert.Equal(HttpStatusCode.BadRequest, (await Form(client, "/account/reset", ("token", t), ("password", "yet another password"), ("again", "yet another password"))).StatusCode);
        Assert.False(accounts.CheckPassword(Sam, "yet another password"));
    }

    [Fact]
    public async Task Asking_again_replaces_the_link_before_and_a_made_up_link_gives_nothing()
    {
        var client = Run();
        Register(Sam);
        await Form(client, "/account/forgot", ("email", Sam));
        await Idle();
        await Form(client, "/account/forgot", ("email", Sam));
        await Idle();
        Assert.Equal(2, _mailer.Sent.Count);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(Path(LinkIn(_mailer.Sent[0])))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(Path(LinkIn(_mailer.Sent[1])))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/account/reset?t=rst_madeup")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/account/reset")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/account/setup?t=set_madeup")).StatusCode);
    }

    [Fact]
    public async Task The_sign_in_pages_offer_the_link_and_a_mail_server_that_is_down_still_answers_the_same()
    {
        var client = Run();
        var login = await (await client.GetAsync(LoginUrl)).Content.ReadAsStringAsync();
        Assert.Contains("/account/forgot", login);
        Assert.Contains("/account/forgot", await (await client.GetAsync("/account/forgot")).Content.ReadAsStringAsync());

        Register(Sam);
        _mailer.Down = true;
        var response = await Form(client, "/account/forgot", ("email", Sam));
        await Idle();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(_mailer.Sent);
    }

    private static Task<HttpResponseMessage> FormFrom(HttpClient client, string path, string from, params (string, string)[] fields)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new FormUrlEncodedContent(fields.ToDictionary(f => f.Item1, f => f.Item2)) };
        request.Headers.Add("X-Forwarded-For", from);
        return client.SendAsync(request);
    }

    [Fact]
    public async Task Asking_over_and_over_is_stopped_for_one_address_wherever_it_comes_from_and_from_one_place_for_any_address()
    {
        var client = Start(new() { ["Mail:Host"] = "smtp.fake.test", ["Mail:From"] = "noreply@example.org", ["BehindProxy"] = "true" }, services => services.AddSingleton<IMailer>(_mailer));
        Register(Sam);

        // One address, asked for from twelve different places: eight mails, and the same answer to all twelve.
        var answers = new List<string>();
        for (var i = 0; i < 12; i++) answers.Add(await (await FormFrom(client, "/account/forgot", "10.0.0." + (i + 1), ("email", Sam))).Content.ReadAsStringAsync());
        await Idle();
        Assert.Equal(8, _mailer.Sent.Count);
        Assert.All(answers, a => Assert.Equal(answers[0], a));

        // One place, asking for different addresses: stopped after eight tries, whoever they are for.
        for (var i = 0; i < 8; i++) Assert.Equal(HttpStatusCode.OK, (await FormFrom(client, "/account/forgot", "10.9.9.9", ("email", $"someone{i}@example.com"))).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await FormFrom(client, "/account/forgot", "10.9.9.9", ("email", "another@example.com"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await FormFrom(client, "/account/forgot", "10.8.8.8", ("email", "another@example.com"))).StatusCode);   // somewhere else is not stopped
    }

    // ---- an address proved before a password is chosen on it ----

    [Fact]
    public async Task Paying_first_with_email_on_sends_the_link_to_choose_the_password_to_the_address_and_the_page_does_not_show_the_form()
    {
        var client = Run(billing: true);
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
        var client = Run(billing: true);
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

    [Fact]
    public async Task A_sign_up_code_with_email_on_mails_the_link_to_choose_the_password_and_shows_no_form()
    {
        var client = Run();
        var code = new SignupCodes(Options).Create("", null, null).Code;

        var response = await Form(client, "/signup", ("code", code), ("email", "friend@example.com"));
        await Idle();
        var html = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Check your email", html);
        Assert.DoesNotContain("name=\"password\"", html);
        Assert.DoesNotContain("set_", html);

        var mail = Assert.Single(_mailer.Sent);
        Assert.Equal("friend@example.com", mail.To);
        var form = await (await client.GetAsync(Path(LinkIn(mail)))).Content.ReadAsStringAsync();
        var token = Regex.Match(form, "name=\"token\" value=\"(set_[A-Za-z0-9_-]+)\"").Groups[1].Value;
        Assert.Equal(HttpStatusCode.OK, (await Form(client, "/account/setup", ("token", token), ("password", "a brand new password"), ("again", "a brand new password"))).StatusCode);
        Assert.True(new AccountDirectory(Options).CheckPassword("friend@example.com", "a brand new password"));

        // Someone who signed up and never chose a password asks for another link on the same page.
        new AccountDirectory(Options).StartSignup("second@example.com", "code2");
        await Form(client, "/account/forgot", ("email", "second@example.com"));
        await Idle();
        Assert.Equal(2, _mailer.Sent.Count);
        Assert.Equal("second@example.com", _mailer.Sent[1].To);
    }

    [Fact]
    public async Task With_email_off_a_sign_up_code_still_shows_the_form_as_it_always_did()
    {
        var client = Run(mail: false);
        var code = new SignupCodes(Options).Create("", null, null).Code;
        var html = await (await Form(client, "/signup", ("code", code), ("email", "friend@example.com"))).Content.ReadAsStringAsync();
        Assert.Contains("name=\"password\"", html);
        Assert.Empty(_mailer.Sent);
    }
}
