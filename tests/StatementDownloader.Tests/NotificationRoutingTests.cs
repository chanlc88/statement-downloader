using Microsoft.Extensions.Logging.Abstractions;
using StatementDownloader.Actions;
using StatementDownloader.Core;

namespace StatementDownloader.Tests;

/// <summary>Notifications routed to each account group's email addresses, and the on/off switches.</summary>
public sealed class NotificationRoutingTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sd-route-").FullName;
    public void Dispose() => Directory.Delete(_dir, true);

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Mail(bool hasDefault = true) : IEmailChannel
    {
        public List<Notification> Sent { get; } = [];
        public string Name => "email";
        public bool HasDefaultRecipient => hasDefault;
        public Task SendAsync(Notification n, CancellationToken ct = default) { Sent.Add(n); return Task.CompletedTask; }
    }

    private sealed class Phone : INotificationChannel
    {
        public List<Notification> Sent { get; } = [];
        public string Name => "phone";
        public Task SendAsync(Notification n, CancellationToken ct = default) { Sent.Add(n); return Task.CompletedTask; }
    }

    private readonly List<AccountConfig> _accounts =
    [
        new() { Id = "water", Group = "Maple Rentals" },
        new() { Id = "card", Group = "Acme Holdings" },
        new() { Id = "checking", Group = "Personal" },
        new() { Id = "loose" },
    ];

    private NotificationConfig Config() => new()
    {
        Routes = new() { ["actionNeeded"] = ["phone", "email"], ["headsUp"] = ["email"], ["info"] = ["email"] },
        Groups = new()
        {
            ["maple rentals"] = new() { Email = ["maple@example.com"] },
            ["Acme Holdings"] = new() { Email = ["acme@example.com", "books@example.com"] },
            ["Personal"] = new() { Email = ["me@example.com"] },
        },
    };

    private (NotificationCenter, Mail, Phone, Clock) Center(NotificationConfig config, bool mailHasDefault = true)
    {
        var mail = new Mail(mailHasDefault);
        var phone = new Phone();
        var clock = new Clock(new DateTimeOffset(2026, 1, 5, 12, 0, 0, TimeSpan.Zero));
        var center = new NotificationCenter([mail, phone], config, new ScheduleDefaults(), new AppPaths(Path.Combine(_dir, "config.yaml"), _dir),
            clock, TimeZoneInfo.Utc, NullLogger<NotificationCenter>.Instance, () => _accounts);
        return (center, mail, phone, clock);
    }

    private static Notification For(string account, NotificationLevel level = NotificationLevel.ActionNeeded) =>
        new() { Level = level, AccountId = account, Title = $"{account}: problem", Message = "details" };

    [Fact]
    public async Task Each_account_is_emailed_to_its_groups_addresses()
    {
        var (center, mail, phone, _) = Center(Config());
        await center.RaiseAsync("documents:water", For("water"));
        await center.RaiseAsync("documents:card", For("card"));
        await center.NotifyAsync(For("checking", NotificationLevel.Info));   // e.g. "waiting for your review"
        await center.NotifyAsync(For("loose", NotificationLevel.HeadsUp));

        Assert.Equal(["maple@example.com"], mail.Sent[0].EmailTo);            // group names compare ignoring case
        Assert.Equal(["acme@example.com", "books@example.com"], mail.Sent[1].EmailTo);
        Assert.Equal(["me@example.com"], mail.Sent[2].EmailTo);
        Assert.Null(mail.Sent[3].EmailTo);                                    // no group: the channel's own 'to'
        Assert.Equal(2, phone.Sent.Count);                                    // other channels still follow routes
    }

    [Fact]
    public async Task Off_sends_nothing_but_keeps_the_issue_and_says_why()
    {
        var config = Config();
        config.Enabled = false;
        var (center, mail, phone, _) = Center(config);
        await center.RaiseAsync("documents:water", For("water"));

        Assert.Empty(mail.Sent);
        Assert.Empty(phone.Sent);
        Assert.Single(await center.OpenIssuesAsync());
        Assert.Equal("notifications are off", (await center.ReadAsync()).Log.Single().Skipped);
    }

    [Fact]
    public async Task A_group_or_an_account_can_be_turned_off()
    {
        var config = Config();
        config.Groups["Acme Holdings"].Enabled = false;
        _accounts.Single(a => a.Id == "water").Notify = false;
        var (center, mail, phone, _) = Center(config);

        await center.RaiseAsync("documents:water", For("water"));
        await center.RaiseAsync("documents:card", For("card"));
        await center.RaiseAsync("documents:checking", For("checking"));

        Assert.Equal(["checking: problem"], mail.Sent.Select(n => n.Title));
        Assert.Single(phone.Sent);
        var skipped = (await center.ReadAsync()).Log.Select(l => l.Skipped).OfType<string>().ToList();
        Assert.Contains("notifications are off for water", skipped);
        Assert.Contains("notifications are off for Acme Holdings", skipped);
    }

    [Fact]
    public async Task Reminders_follow_the_switches_at_the_time_they_are_due()
    {
        var config = Config();
        var (center, mail, _, clock) = Center(config);
        await center.RaiseAsync("documents:card", For("card"));
        config.Groups["Acme Holdings"].Enabled = false;   // turned off in Settings later
        clock.Now = clock.Now.AddDays(2);
        await center.SendDueAsync();
        Assert.Single(mail.Sent);                          // only the first, no reminder
    }

    [Fact]
    public async Task The_daily_summary_is_one_email_per_group()
    {
        var config = Config();
        config.Digest = true;
        var (center, mail, _, clock) = Center(config);
        await center.NotifyAsync(For("water", NotificationLevel.Info));
        await center.NotifyAsync(For("card", NotificationLevel.Info));
        await center.NotifyAsync(For("loose", NotificationLevel.Info));
        Assert.Empty(mail.Sent);

        clock.Now = clock.Now.AddDays(1);
        await center.SendDueAsync();
        Assert.Equal(3, mail.Sent.Count);
        var maple = mail.Sent.Single(n => n.Group == "Maple Rentals");
        Assert.Equal(["maple@example.com"], maple.EmailTo);
        Assert.Contains("water: problem", maple.Message);
        Assert.DoesNotContain("card: problem", maple.Message);
        Assert.Null(mail.Sent.Single(n => n.Group is null).EmailTo);
    }

    [Fact]
    public async Task Test_sends_one_email_per_group_with_addresses()
    {
        var (center, mail, phone, _) = Center(Config(), mailHasDefault: false);
        var results = await center.SendTestAsync();
        Assert.Single(phone.Sent);
        Assert.Equal(3, mail.Sent.Count);   // no default 'to', so none without a group
        Assert.Contains(results, r => r.Channel == "email → Personal" && r.Error is null);
    }

    [Fact]
    public void Email_channel_sends_to_group_addresses_else_its_own()
    {
        var channel = new EmailChannel("email", new Dictionary<string, string> { ["to"] = "a@example.com, b@example.com" }, null!);
        var n = For("water");
        Assert.Equal(["a@example.com", "b@example.com"], channel.RecipientsFor(n));
        Assert.Equal(["g@example.com"], channel.RecipientsFor(n with { EmailTo = ["g@example.com"] }));
        Assert.True(channel.HasDefaultRecipient);
        Assert.False(new EmailChannel("email", new Dictionary<string, string>(), null!).HasDefaultRecipient);
        Assert.StartsWith("[Action needed] ", EmailChannel.Compose(n).Subject);
    }

    [Fact]
    public void Config_reads_groups_switches_and_account_notify()
    {
        var config = ConfigLoader.Parse("""
            documentsRoot: /docs
            notifications:
              enabled: false
              groups:
                "Maple Rentals":
                  email: [maple@example.com, " maple@example.com "]
                Personal:
                  enabled: false
            accounts:
              - id: water
                group: maple rentals
                notify: false
                site: s
                credential: env:X
                folder: f
                fileName: "{date:yyyy-MM-dd}.pdf"
            """);
        Assert.False(config.Notifications.Enabled);
        Assert.Equal(["maple@example.com"], config.Notifications.Group("MAPLE RENTALS")!.Email);
        Assert.False(config.Notifications.Group("personal")!.Enabled);
        Assert.Empty(config.Notifications.Group("Personal")!.Email);
        Assert.False(config.Accounts[0].Notify);
        Assert.True(ConfigLoader.Parse("documentsRoot: /docs").Notifications.Enabled);
    }

    [Fact]
    public void Config_rejects_a_bad_address()
    {
        var ex = Assert.Throws<InvalidDataException>(() => ConfigLoader.Parse("""
            documentsRoot: /docs
            notifications:
              groups:
                Personal: { email: [not-an-address] }
            """));
        Assert.Contains("isn't an email address", ex.Message);
    }
}
