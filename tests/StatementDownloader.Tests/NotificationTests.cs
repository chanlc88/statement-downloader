using Microsoft.Extensions.Logging.Abstractions;
using StatementDownloader.Core;

namespace StatementDownloader.Tests;

public sealed class NotificationTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sd-notify-").FullName;
    public void Dispose() => Directory.Delete(_dir, true);

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Phone : INotificationChannel
    {
        public List<Notification> Sent { get; } = [];
        public string Name => "phone";
        public Task SendAsync(Notification n, CancellationToken ct = default) { Sent.Add(n); return Task.CompletedTask; }
    }

    private (NotificationCenter, Phone, Clock) Center(DateTimeOffset now)
    {
        var phone = new Phone();
        var clock = new Clock(now);
        var config = new NotificationConfig
        {
            QuietHours = "22:00-07:00",
            Routes = new() { ["actionNeeded"] = ["phone"], ["info"] = ["phone"] },
        };
        var center = new NotificationCenter([phone], config, new ScheduleDefaults(), new AppPaths(Path.Combine(_dir, "config.yaml"), _dir),
            clock, TimeZoneInfo.Utc, NullLogger<NotificationCenter>.Instance);
        return (center, phone, clock);
    }

    private static Notification Problem => new() { Level = NotificationLevel.ActionNeeded, Title = "couldn't get bills", Message = "failed" };

    [Fact]
    public async Task An_issue_resolved_during_quiet_hours_is_never_sent()
    {
        var (center, phone, clock) = Center(new DateTimeOffset(2026, 1, 5, 6, 30, 0, TimeSpan.Zero));
        await center.RaiseAsync("documents:a", Problem);
        await center.ResolveAsync("documents:a", "downloading again");
        clock.Now = clock.Now.AddHours(1);
        await center.SendDueAsync();
        Assert.Empty(phone.Sent);
        Assert.Empty((await center.ReadAsync()).Held);
    }

    [Fact]
    public async Task An_issue_still_open_after_quiet_hours_is_sent()
    {
        var (center, phone, clock) = Center(new DateTimeOffset(2026, 1, 5, 6, 30, 0, TimeSpan.Zero));
        await center.RaiseAsync("documents:a", Problem);
        await center.SendDueAsync();
        Assert.Empty(phone.Sent);
        clock.Now = clock.Now.AddHours(1);
        await center.SendDueAsync();
        Assert.Equal(["couldn't get bills"], phone.Sent.Select(n => n.Title));
    }
}
