using Microsoft.Extensions.Logging;

namespace StatementDownloader.Core;

public sealed record AccountStatus(
    AccountConfig Account,
    VendorAuthState Auth,
    CycleEstimate? Estimate,
    ScheduleDecision Decision,
    DocumentRecord? LastDocument,
    int DocumentCount,
    IReadOnlyList<Issue> OpenIssues);

/// <summary>The hourly heartbeat: decide what's due for each account and run it.</summary>
public sealed class TickRunner(
    AppConfig config,
    AppPaths paths,
    AccountRunner runner,
    IDocumentStore documents,
    IScheduleStateStore schedule,
    IAuthStateStore auth,
    NotificationCenter notify,
    UiLinks links,
    TimeProvider clock,
    TimeZoneInfo zone,
    ILogger<TickRunner> log)
{
    public static string OverdueIssueKey(string id) => $"overdue:{id}";
    public static string StaleIssueKey(string id) => $"stale:{id}";

    public async Task<IReadOnlyList<RunReport>> TickAsync(CancellationToken ct = default)
    {
        using var runLock = TryLock();
        if (runLock is null)
        {
            log.LogInformation("Another run is in progress; skipping this tick.");
            return [];
        }

        var reports = new List<RunReport>();
        foreach (var account in config.Accounts.Where(a => a.Enabled))
        {
            try
            {
                var (estimate, _) = await EstimateAsync(account, ct);
                var state = await schedule.GetAsync(account.Id, ct);
                var decision = SchedulePlanner.Decide(account, config.Schedule, estimate, state, clock.GetUtcNow(), zone);
                await HandleNoticeAsync(account, decision, state, ct);

                if (decision.Run == PlannedRun.None) continue;
                log.LogInformation("{Account}: {Run} ({Reason})", account.Id, decision.Run, decision.Reason);
                reports.Add(await runner.RunAsync(account, decision.Run == PlannedRun.SignInOnly ? RunMode.SignInOnly : RunMode.Full, ct: ct));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "{Account}: tick failed", account.Id);
                await notify.RaiseAsync($"run:{account.Id}", new Notification
                {
                    Level = NotificationLevel.ActionNeeded,
                    AccountId = account.Id,
                    Title = $"{account.DisplayName}: check failed",
                    Message = $"Unexpected error: {ex.Message}. See the app log.",
                    Link = links.Account(account.Id),
                }, ct);
            }
        }
        await notify.SendDueAsync(ct);
        return reports;
    }

    /// <summary>Runs one account now regardless of schedule (Check now / Sign in now).</summary>
    public async Task<RunReport> RunNowAsync(AccountConfig account, bool interactive, CancellationToken ct = default, bool capture = false)
    {
        using var runLock = TryLock(wait: TimeSpan.FromMinutes(15)) ?? throw new InvalidOperationException("Another run is in progress; try again shortly.");
        var report = await runner.RunAsync(account, RunMode.Full, interactive, ct, capture);
        // Accounts on the same login are fetched in the same go: the sign-in just made covers them.
        if (report.Auth is AuthStatus.Healthy or AuthStatus.ExpiringSoon)
            foreach (var sibling in Logins.Siblings(config, account))
            {
                var r = await runner.RunAsync(sibling, RunMode.Full, interactive && runner.NeedsPerson(sibling), ct, capture);
                report = report with
                {
                    NewDocuments = report.NewDocuments + r.NewDocuments,
                    ActionsFailed = report.ActionsFailed + r.ActionsFailed,
                    Summary = $"{report.Summary} {sibling.DisplayName}: {r.Summary}",
                };
            }
        await notify.SendDueAsync(ct);
        return report;
    }

    /// <summary>
    /// Approves or rejects a document waiting for review. Approval runs the account's actions
    /// under the same run lock as scheduled and manual runs, so an action never runs twice at once.
    /// Returns null when no such document is waiting, otherwise the number of failed actions.
    /// </summary>
    public async Task<int?> ReviewAsync(string docId, bool approve, CancellationToken ct = default, TimeSpan? lockWait = null)
    {
        using var runLock = TryLock(wait: lockWait ?? TimeSpan.FromMinutes(1)) ?? throw new InvalidOperationException("A check is running right now; approve again in a few minutes.");
        // Re-read inside the lock: another run may have changed the document meanwhile.
        var doc = (await documents.GetAsync(null, ct)).FirstOrDefault(d => d.Id == docId);
        if (doc is null || doc.Review != ReviewState.Pending) return null;
        if (config.Accounts.FirstOrDefault(a => a.Id == doc.AccountId) is not { } account) return null;

        doc.Review = approve ? ReviewState.Approved : ReviewState.Rejected;
        await documents.UpdateAsync(doc, ct);
        var failed = approve ? await runner.Actions.RunForDocumentAsync(account, doc, ct) : 0;
        await notify.SendDueAsync(ct);
        return failed;
    }

    public async Task<(CycleEstimate? Estimate, IReadOnlyList<DocumentRecord> Documents)> EstimateAsync(AccountConfig account, CancellationToken ct = default)
    {
        var docs = await documents.GetAsync(account.Id, ct);
        var delays = docs.Where(d => d.FirstSeenUtc is not null && !d.Imported)
            .Select(d => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(d.FirstSeenUtc!.Value, zone).DateTime).DayNumber - d.DocumentDate.DayNumber);
        return (CycleEstimator.Estimate(docs.Select(d => d.DocumentDate), delays, config.Schedule.MinSpreadDays, config.Schedule.MaxSpreadDays), docs);
    }

    public async Task<IReadOnlyList<AccountStatus>> GetStatusAsync(CancellationToken ct = default)
    {
        var issues = await notify.OpenIssuesAsync(ct);
        var list = new List<AccountStatus>();
        foreach (var a in config.Accounts)
        {
            var (estimate, docs) = await EstimateAsync(a, ct);
            var state = await schedule.GetAsync(a.Id, ct);
            var decision = a.Enabled
                ? SchedulePlanner.Decide(a, config.Schedule, estimate, state, clock.GetUtcNow(), zone)
                : new ScheduleDecision(PlannedRun.None, SchedulePhase.Quiet, "disabled");
            list.Add(new(a, await auth.GetAsync(a.Id, ct), estimate, decision, docs.MaxBy(d => d.DocumentDate), docs.Count, issues.Where(i => i.AccountId == a.Id).ToList()));
        }
        return list;
    }

    private async Task HandleNoticeAsync(AccountConfig account, ScheduleDecision decision, ScheduleState state, CancellationToken ct)
    {
        if (decision.Notice == ScheduleNotice.None || decision.Expected is not { } expected) return;
        var docs = await documents.GetAsync(account.Id, ct);
        var last = docs.MaxBy(d => d.DocumentDate)?.DocumentDate;
        if (decision.Notice == ScheduleNotice.Overdue)
        {
            await notify.RaiseAsync(OverdueIssueKey(account.Id), new Notification
            {
                Level = NotificationLevel.HeadsUp,
                AccountId = account.Id,
                Title = $"{account.DisplayName}: {account.KindWord} is late",
                Message = $"The next {account.KindWord} was expected around {expected:yyyy-MM-dd} (last one dated {last:yyyy-MM-dd}). Still checking daily for {config.Schedule.OverdueGraceDays} more days.",
                Link = links.Account(account.Id),
            }, ct);
            state.OverdueNoticeFor = expected;
        }
        else
        {
            await notify.RaiseAsync(StaleIssueKey(account.Id), new Notification
            {
                Level = NotificationLevel.ActionNeeded,
                AccountId = account.Id,
                Title = $"{account.DisplayName}: no new {account.KindWord}",
                Message = $"Nothing new since the {account.KindWord} dated {last:yyyy-MM-dd} (expected around {expected:yyyy-MM-dd}). Check the account on the site, or adjust the schedule. Now checking weekly.",
                Link = links.Account(account.Id),
            }, ct);
            state.StaleNoticeFor = expected;
        }
        await schedule.SaveAsync(state, ct);
    }

    /// <summary>
    /// Runs <paramref name="work"/> under the run lock, so it never overlaps a check (scheduled,
    /// Check now, Sign in now, or one in another process).
    /// </summary>
    public async Task<T> UnderRunLockAsync<T>(Func<Task<T>> work, TimeSpan wait)
    {
        using var runLock = TryLock(wait) ?? throw new InvalidOperationException("A check is running right now; try again in a few minutes.");
        return await work();
    }

    private FileStream? TryLock(TimeSpan? wait = null)
    {
        Directory.CreateDirectory(paths.DataDir);
        var deadline = DateTime.UtcNow + (wait ?? TimeSpan.Zero);
        while (true)
        {
            try { return new FileStream(paths.File("run.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose); }
            catch (IOException) when (DateTime.UtcNow < deadline) { Thread.Sleep(2000); }
            catch (IOException) { return null; }
        }
    }
}
