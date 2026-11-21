using StatementDownloader.Core;

namespace StatementDownloader.Tests;

public class CycleEstimatorTests
{
    // A real monthly utility pattern: billed between the 17th and 21st.
    private static readonly DateOnly[] Monthly =
    [
        new(2026, 4, 17), new(2026, 5, 19), new(2026, 6, 17), new(2026, 7, 21),
        new(2026, 8, 19), new(2026, 9, 18), new(2026, 10, 19), new(2026, 11, 18),
    ];

    [Fact]
    public void Learns_monthly_cycle_and_window()
    {
        var e = CycleEstimator.Estimate(Monthly, [])!;
        Assert.Equal(30, e.IntervalDays);
        Assert.Equal(4, e.SpreadDays);           // worst interval 34 vs median 30
        Assert.Equal(1, e.PostingDelayDays);     // default until observed
        Assert.Equal(new DateOnly(2026, 12, 19), e.NextExpected);
        Assert.Equal(new DateOnly(2026, 12, 15), e.WindowStart);
        Assert.Equal(new DateOnly(2026, 12, 23), e.WindowEnd);
    }

    [Fact]
    public void Needs_three_documents() =>
        Assert.Null(CycleEstimator.Estimate(Monthly.Take(2), []));

    [Fact]
    public void Spread_is_clamped()
    {
        var steady = Enumerable.Range(0, 6).Select(i => new DateOnly(2026, 1, 5).AddDays(30 * i));
        Assert.Equal(2, CycleEstimator.Estimate(steady, [])!.SpreadDays);
        DateOnly[] erratic = [new(2026, 1, 1), new(2026, 1, 20), new(2026, 3, 15), new(2026, 4, 14)];
        Assert.Equal(7, CycleEstimator.Estimate(erratic, [])!.SpreadDays);
    }

    [Fact]
    public void Uses_observed_posting_delay() =>
        Assert.Equal(new DateOnly(2026, 12, 21), CycleEstimator.Estimate(Monthly, [3, 3, 2])!.NextExpected);
}

public class SchedulePlannerTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    private static readonly ScheduleDefaults Defaults = new();
    private static readonly AccountConfig Auto = new() { Id = "water", FileName = "{date:yyyy-MM-dd}.pdf" };
    private static readonly CycleEstimate Est = new(30, 4, 1, new(2026, 11, 18), new(2026, 12, 19), new(2026, 12, 15), new(2026, 12, 23));

    private static DateTimeOffset At(int month, int day, int hour = 9) => new(2026, month, day, hour, 0, 0, TimeSpan.Zero);
    private static ScheduleDecision Decide(DateTimeOffset now, ScheduleState? s = null, AccountConfig? a = null, CycleEstimate? e = null) =>
        SchedulePlanner.Decide(a ?? Auto, Defaults, e ?? Est, s ?? new ScheduleState { AccountId = "water" }, now, Utc);

    [Fact]
    public void Quiet_before_preflight() => Assert.Equal(PlannedRun.None, Decide(At(12, 5)).Run);

    [Fact]
    public void Preflight_sign_in_once()
    {
        var d = Decide(At(12, 13));
        Assert.Equal((PlannedRun.SignInOnly, SchedulePhase.Preflight), (d.Run, d.Phase));
        var done = new ScheduleState { AccountId = "water", LastSignInAttemptUtc = At(12, 12, 8) };
        Assert.Equal(PlannedRun.None, Decide(At(12, 14), done).Run);
    }

    [Fact]
    public void In_window_checks_twice_a_day()
    {
        Assert.Equal(PlannedRun.FullCheck, Decide(At(12, 16)).Run);
        var recent = new ScheduleState { AccountId = "water", LastFullCheckUtc = At(12, 16, 6) };
        Assert.Equal(PlannedRun.None, Decide(At(12, 16, 12), recent).Run);
        Assert.Equal(PlannedRun.FullCheck, Decide(At(12, 16, 18), recent).Run);
    }

    [Fact]
    public void Overdue_notice_once_then_daily()
    {
        var d = Decide(At(12, 25));
        Assert.Equal((PlannedRun.FullCheck, SchedulePhase.Overdue, ScheduleNotice.Overdue), (d.Run, d.Phase, d.Notice));
        var noticed = new ScheduleState { AccountId = "water", OverdueNoticeFor = Est.NextExpected, LastFullCheckUtc = At(12, 25, 8) };
        var again = Decide(At(12, 25, 20), noticed);
        Assert.Equal((PlannedRun.None, ScheduleNotice.None), (again.Run, again.Notice));
    }

    [Fact]
    public void Stale_after_grace()
    {
        var d = Decide(At(12, 31));
        Assert.Equal((SchedulePhase.Stale, ScheduleNotice.Stale), (d.Phase, d.Notice));
    }

    [Fact]
    public void Learning_checks_daily_without_estimate()
    {
        var d = SchedulePlanner.Decide(Auto, Defaults, null, new ScheduleState { AccountId = "water" }, At(12, 1), Utc);
        Assert.Equal((PlannedRun.FullCheck, SchedulePhase.Learning), (d.Run, d.Phase));
    }

    [Fact]
    public void Interval_mode()
    {
        var a = new AccountConfig { Id = "bank", FileName = "{date:yyyy-MM-dd}.pdf", Schedule = new ScheduleConfig { Every = "7d" } };
        var s = new ScheduleState { AccountId = "bank", LastFullCheckUtc = At(12, 1) };
        Assert.Equal(PlannedRun.None, Decide(At(12, 5), s, a).Run);
        Assert.Equal(PlannedRun.FullCheck, Decide(At(12, 8), s, a).Run);
    }

    [Theory]
    [InlineData("15-22", 2026, 12, 10, "2026-12-15", "2026-12-22")]
    [InlineData("15-22", 2026, 12, 23, "2027-01-15", "2027-01-22")]
    [InlineData("28-4", 2026, 12, 2, "2026-11-28", "2026-12-04")]
    [InlineData("28-31", 2026, 2, 10, "2026-02-28", "2026-02-28")]
    public void Fixed_window_dates(string range, int y, int m, int d, string start, string end)
    {
        var cfg = new ScheduleConfig { DayOfMonth = range };
        var (s, e) = SchedulePlanner.FixedWindow(cfg.DayRange, new DateOnly(y, m, d));
        Assert.Equal((DateOnly.Parse(start), DateOnly.Parse(end)), (s, e));
    }
}

public class FileNameTemplateTests
{
    private static readonly AccountConfig Account = new() { Id = "water", Kind = DocumentKind.Bill };

    [Fact]
    public void Renders_tokens() =>
        Assert.Equal("Water/2026/2026-11-18-Water-Bill-123.pdf",
            FileNameTemplate.Render("Water/{year}/{date:yyyy-MM-dd}-Water-Bill-{statement}.pdf", Account, new(2026, 11, 18), "123"));

    [Theory]
    [InlineData("{date:yyyy-MM-dd}-Dekalb-Water-Bill.pdf", "2025-02-25-Dekalb-Water-Bill.pdf", "2025-02-25")]
    [InlineData("{date:yyyy-MM-dd}-Dekalb-Water-Bill.pdf", "2025-02-25-Dekalb-Water-Bill (2).pdf", null)]
    [InlineData("{date:yyyyMMdd} statement {statement}.pdf", "20261118 statement 99.pdf", "2026-11-18")]
    [InlineData("{kind}-{date:MMM yyyy}.pdf", "bill-Nov 2026.pdf", "2026-11-01")]
    [InlineData("{date:yyyy-MM-dd}.pdf", "notes.txt", null)]
    public void Reads_dates_back(string template, string file, string? expected) =>
        Assert.Equal(expected is null ? null : DateOnly.Parse(expected), FileNameTemplate.TryParseDate(template, file));
}

public class AuthTransitionsTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Warn = TimeSpan.FromDays(5);
    private static VendorAuthState New() => new() { AccountId = "water" };

    [Theory]
    [InlineData(AuthFailureReason.VerificationCode, AuthStatus.NeedsAssist)]
    [InlineData(AuthFailureReason.Captcha, AuthStatus.NeedsAssist)]
    [InlineData(AuthFailureReason.SessionExpired, AuthStatus.NeedsAssist)]
    [InlineData(AuthFailureReason.PasswordRejected, AuthStatus.Broken)]
    [InlineData(AuthFailureReason.SiteChanged, AuthStatus.Broken)]
    public void Failure_reason_decides_status(AuthFailureReason reason, AuthStatus expected) =>
        Assert.Equal(expected, AuthTransitions.Failed(New(), Now, reason, null).Status);

    [Fact]
    public void Success_sets_healthy_or_expiring()
    {
        Assert.Equal(AuthStatus.Healthy, AuthTransitions.Succeeded(New(), Now, Now.AddDays(30), Warn).Status);
        Assert.Equal(AuthStatus.ExpiringSoon, AuthTransitions.Succeeded(New(), Now, Now.AddDays(2), Warn).Status);
    }

    [Fact]
    public void Expired_remembered_sign_in_needs_assist()
    {
        var s = AuthTransitions.Succeeded(New(), Now.AddDays(-40), Now.AddDays(-1), Warn);
        s = AuthTransitions.CheckExpiry(s, Now, Warn);
        Assert.Equal((AuthStatus.NeedsAssist, AuthFailureReason.SessionExpired), (s.Status, s.LastFailure));
    }
}

public class ConfigTests
{
    private const string Yaml = """
        documentsRoot: "C:/Docs"
        notifications:
          channels:
            - { type: ntfy, name: phone, topic: abc }
          routes:
            actionNeeded: [phone]
          quietHours: "22:00-07:00"
        accounts:
          - id: water
            site: dekalb-water
            credential: env:WATER_LOGIN
            folder: "Water/{year}"
            fileName: "{date:yyyy-MM-dd}-Water.pdf"
            schedule: auto
            actions:
              - { type: webhook, url: "http://localhost:9/hook" }
          - id: bank
            site: some-bank
            kind: statement
            credential: env:BANK
            folder: Bank
            fileName: "{date:yyyy-MM}.pdf"
            schedule: { every: 7d }
          - id: power
            site: power
            credential: env:P
            folder: Power
            fileName: "{date:yyyy-MM-dd}.pdf"
            schedule: { dayOfMonth: "3-9" }
        """;

    [Fact]
    public void Parses_sample()
    {
        var c = ConfigLoader.Parse(Yaml);
        Assert.Equal(3, c.Accounts.Count);
        Assert.Equal(ScheduleMode.Auto, c.Accounts[0].Schedule.Mode);
        Assert.Equal(ScheduleMode.Interval, c.Accounts[1].Schedule.Mode);
        Assert.Equal(DocumentKind.Statement, c.Accounts[1].Kind);
        Assert.Equal((3, 9), c.Accounts[2].Schedule.DayRange);
        Assert.Equal("webhook", c.Accounts[0].Actions[0]["type"]);
        Assert.Equal(["phone"], c.Notifications.Routes["actionNeeded"]);
    }

    [Fact]
    public void Repo_sample_config_is_valid()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "config.sample.yaml"))) dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        var c = ConfigLoader.Load(Path.Combine(dir!, "config.sample.yaml"));
        Assert.NotEmpty(c.Accounts);
    }

    [Fact]
    public void Rejects_bad_accounts()
    {
        var ex = Assert.Throws<InvalidDataException>(() => ConfigLoader.Parse("""
            documentsRoot: x
            accounts:
              - { id: "Bad Id", site: s, credential: c, folder: f, fileName: "no-date.pdf" }
            """));
        Assert.Contains("lowercase", ex.Message);
        Assert.Contains("date token", ex.Message);
    }
}
