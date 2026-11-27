namespace StatementDownloader.Core;

/// <summary>When the next document is expected, learned from past document dates.</summary>
public sealed record CycleEstimate(
    int IntervalDays,
    int SpreadDays,
    int PostingDelayDays,
    DateOnly LastDocumentDate,
    DateOnly NextExpected,
    DateOnly WindowStart,
    DateOnly WindowEnd);

public static class CycleEstimator
{
    public const int MinimumDocuments = 3;

    /// <param name="documentDates">Dates printed on past documents.</param>
    /// <param name="postingDelays">Days between a document's date and the day it was first found online.</param>
    public static CycleEstimate? Estimate(IEnumerable<DateOnly> documentDates, IEnumerable<int> postingDelays, int minSpread = 2, int maxSpread = 7)
    {
        var dates = documentDates.Distinct().Order().ToList();
        if (dates.Count < MinimumDocuments) return null;

        // Recent behaviour matters most: use at most the last 12 intervals (a year of monthly bills).
        var intervals = dates.Zip(dates.Skip(1), (a, b) => b.DayNumber - a.DayNumber).TakeLast(12).ToList();
        var interval = (int)Math.Round(Median(intervals), MidpointRounding.AwayFromZero);
        if (interval <= 0) return null;

        var deviation = intervals.Max(i => Math.Abs(i - interval));
        var spread = Math.Clamp(deviation, minSpread, maxSpread);

        var delays = postingDelays.Where(d => d >= 0).ToList();
        var delay = delays.Count > 0 ? (int)Math.Round(Median(delays), MidpointRounding.AwayFromZero) : 1;

        var last = dates[^1];
        var next = last.AddDays(interval + delay);
        return new(interval, spread, delay, last, next, next.AddDays(-spread), next.AddDays(spread));
    }

    /// <summary>
    /// How long each document took to appear online, from when it was found. Only documents found
    /// as they came out count: not the backlog fetched on the account's first day of downloads,
    /// and not older documents fetched together with a newer one (catching up after a gap).
    /// </summary>
    public static IEnumerable<int> PostingDelays(IEnumerable<(DateOnly Document, DateOnly Found)> found)
    {
        var list = found.ToList();
        if (list.Count == 0) return [];
        var firstDay = list.Min(f => f.Found);
        return list.Where(f => f.Found != firstDay)
            .GroupBy(f => f.Found)
            .Select(day => day.Max(f => f.Document))
            .Select(newest => list.First(f => f.Document == newest).Found.DayNumber - newest.DayNumber)
            .ToList();
    }

    private static double Median(IReadOnlyList<int> values)
    {
        var sorted = values.Order().ToList();
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }
}

/// <summary>Per-account scheduling memory.</summary>
public sealed class ScheduleState
{
    public string AccountId { get; set; } = "";
    public DateTimeOffset? LastFullCheckUtc { get; set; }
    public DateTimeOffset? LastSignInAttemptUtc { get; set; }
    /// <summary>Expected date the overdue notice was sent for (so it's sent once per cycle).</summary>
    public DateOnly? OverdueNoticeFor { get; set; }
    public DateOnly? StaleNoticeFor { get; set; }
    /// <summary>
    /// When each configured action (by <see cref="ActionKeys"/>) was first seen. Actions only run on
    /// documents found after that, so adding one doesn't replay it over the whole history.
    /// Null until the first run with stable action keys.
    /// </summary>
    public Dictionary<string, DateTimeOffset>? ActionsSince { get; set; }
    /// <summary>The person says documents start here: months before the oldest on record count as missing.</summary>
    public DateOnly? ExpectFrom { get; set; }
    /// <summary>What's known about missing documents, by <see cref="GapSlot.Key"/>.</summary>
    public Dictionary<string, GapMark>? GapMarks { get; set; }
    /// <summary>What the last gap fill found, in words.</summary>
    public string? LastFill { get; set; }
    public DateTimeOffset? LastFillUtc { get; set; }
    /// <summary>The oldest document the site listed in the last fill.</summary>
    public DateOnly? OldestListed { get; set; }
}

public enum PlannedRun { None, SignInOnly, FullCheck }

public enum ScheduleNotice { None, Overdue, Stale }

public enum SchedulePhase { Learning, Quiet, Preflight, InWindow, Overdue, Stale, Interval }

public sealed record ScheduleDecision(PlannedRun Run, SchedulePhase Phase, string Reason, ScheduleNotice Notice = ScheduleNotice.None, DateOnly? Expected = null);

/// <summary>Pure decision logic: what, if anything, to do for an account right now.</summary>
public static class SchedulePlanner
{
    public static ScheduleDecision Decide(
        AccountConfig account,
        ScheduleDefaults defaults,
        CycleEstimate? estimate,
        ScheduleState state,
        DateTimeOffset now,
        TimeZoneInfo zone)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        var checkGap = TimeSpan.FromHours(24.0 / Math.Max(1, defaults.ChecksPerDay));

        bool FullDue(TimeSpan gap) => state.LastFullCheckUtc is not { } last || now - last >= gap - TimeSpan.FromMinutes(5);
        DateTimeOffset StartOf(DateOnly d) => new(d.ToDateTime(TimeOnly.MinValue), zone.GetUtcOffset(d.ToDateTime(TimeOnly.MinValue)));

        switch (account.Schedule.Mode)
        {
            case ScheduleMode.Interval:
                return FullDue(account.Schedule.Interval)
                    ? new(PlannedRun.FullCheck, SchedulePhase.Interval, $"every {account.Schedule.Every}")
                    : new(PlannedRun.None, SchedulePhase.Interval, "not due yet");

            case ScheduleMode.Window:
                {
                    var (start, end) = FixedWindow(account.Schedule.DayRange, today);
                    return InOrBeforeWindow(start, end, null);
                }

            default:
                if (estimate is null)
                    return FullDue(TimeSpan.FromDays(1))
                        ? new(PlannedRun.FullCheck, SchedulePhase.Learning, $"learning the cycle (needs {CycleEstimator.MinimumDocuments} documents)")
                        : new(PlannedRun.None, SchedulePhase.Learning, "learning the cycle; checked today");

                if (today > estimate.WindowEnd.AddDays(defaults.OverdueGraceDays))
                    return new(FullDue(TimeSpan.FromDays(7)) ? PlannedRun.FullCheck : PlannedRun.None, SchedulePhase.Stale,
                        $"no new {account.KindWord} since {estimate.LastDocumentDate:yyyy-MM-dd}; weekly check",
                        state.StaleNoticeFor == estimate.NextExpected ? ScheduleNotice.None : ScheduleNotice.Stale, estimate.NextExpected);

                if (today > estimate.WindowEnd)
                    return new(FullDue(TimeSpan.FromDays(1)) ? PlannedRun.FullCheck : PlannedRun.None, SchedulePhase.Overdue,
                        $"expected by {estimate.WindowEnd:yyyy-MM-dd}; daily check",
                        state.OverdueNoticeFor == estimate.NextExpected ? ScheduleNotice.None : ScheduleNotice.Overdue, estimate.NextExpected);

                return InOrBeforeWindow(estimate.WindowStart, estimate.WindowEnd, estimate.NextExpected);
        }

        ScheduleDecision InOrBeforeWindow(DateOnly start, DateOnly end, DateOnly? expected)
        {
            if (today >= start && today <= end)
                return FullDue(checkGap)
                    ? new(PlannedRun.FullCheck, SchedulePhase.InWindow, $"in window {start:MM-dd}–{end:MM-dd}", Expected: expected)
                    : new(PlannedRun.None, SchedulePhase.InWindow, "in window; checked recently", Expected: expected);

            var preflight = start.AddDays(-defaults.PreflightDays);
            if (today >= preflight && today < start)
                return state.LastSignInAttemptUtc is { } s && s >= StartOf(preflight)
                    ? new(PlannedRun.None, SchedulePhase.Preflight, "pre-flight sign-in done", Expected: expected)
                    : new(PlannedRun.SignInOnly, SchedulePhase.Preflight, $"pre-flight sign-in before window opens {start:yyyy-MM-dd}", Expected: expected);

            if (defaults.KeepAliveDays > 0 && (state.LastSignInAttemptUtc is not { } k || now - k >= TimeSpan.FromDays(defaults.KeepAliveDays)))
                return new(PlannedRun.SignInOnly, SchedulePhase.Quiet, "keep-alive sign-in", Expected: expected);

            return new(PlannedRun.None, SchedulePhase.Quiet, $"next window {start:yyyy-MM-dd}–{end:yyyy-MM-dd}", Expected: expected);
        }
    }

    /// <summary>The fixed day-of-month window containing <paramref name="today"/>, or the next one.</summary>
    public static (DateOnly Start, DateOnly End) FixedWindow((int From, int To) days, DateOnly today)
    {
        static DateOnly Day(int y, int m, int d) => new(y, m, Math.Min(d, DateTime.DaysInMonth(y, m)));
        var thisMonth = new DateOnly(today.Year, today.Month, 1);
        foreach (var monthStart in new[] { thisMonth.AddMonths(-1), thisMonth, thisMonth.AddMonths(1) })
        {
            var start = Day(monthStart.Year, monthStart.Month, days.From);
            var endMonth = days.To >= days.From ? monthStart : monthStart.AddMonths(1);
            var end = Day(endMonth.Year, endMonth.Month, days.To);
            if (today <= end) return (start, end);
        }
        throw new InvalidOperationException("unreachable");
    }
}

public interface IScheduleStateStore
{
    Task<ScheduleState> GetAsync(string accountId, CancellationToken ct = default);
    Task SaveAsync(ScheduleState state, CancellationToken ct = default);
}

public sealed class JsonScheduleStateStore(AppPaths paths) : IScheduleStateStore
{
    private string FilePath => paths.File("schedule-state.json");

    public async Task<ScheduleState> GetAsync(string accountId, CancellationToken ct = default) =>
        (await JsonFile.ReadAsync<List<ScheduleState>>(FilePath, ct))?.FirstOrDefault(s => s.AccountId == accountId)
        ?? new ScheduleState { AccountId = accountId };

    public Task SaveAsync(ScheduleState state, CancellationToken ct = default) =>
        JsonFile.UpdateAsync<List<ScheduleState>>(FilePath, all =>
        {
            all.RemoveAll(s => s.AccountId == state.AccountId);
            all.Add(state);
        }, ct);
}
