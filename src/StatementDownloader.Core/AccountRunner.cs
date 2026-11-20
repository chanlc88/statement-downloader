using System.Globalization;
using Microsoft.Extensions.Logging;

namespace StatementDownloader.Core;

public enum RunMode { SignInOnly, Full }

public sealed record RunReport(string AccountId, AuthStatus Auth, int NewDocuments, int ActionsFailed, string Summary);

/// <summary>
/// Runs one account: sign in, download, file, record history, run actions, and keep the
/// sign-in state and issues up to date.
/// </summary>
public sealed class AccountRunner(
    AppConfig config,
    AppPaths paths,
    IReadOnlyDictionary<string, ISiteFetcher> sites,
    ISecretProvider secrets,
    IAuthStateStore auth,
    IDocumentStore documents,
    IScheduleStateStore schedule,
    ActionRunner actions,
    NotificationCenter notify,
    UiLinks links,
    TimeProvider clock,
    ILoggerFactory loggers)
{
    private readonly ILogger _log = loggers.CreateLogger<AccountRunner>();
    private TimeSpan ExpiryWarning => TimeSpan.FromDays(config.Schedule.ExpiryWarningDays);

    public static string AuthIssueKey(string id) => $"auth:{id}";
    public static string ExpiringIssueKey(string id) => $"expiring:{id}";
    public static string DocumentIssueKey(string id) => $"documents:{id}";

    public async Task<RunReport> RunAsync(AccountConfig account, RunMode mode, bool interactive = false, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var state = AuthTransitions.CheckExpiry(await auth.GetAsync(account.Id, ct), now, ExpiryWarning);

        if (!interactive && state.Status == AuthStatus.NeedsAssist)
        {
            await auth.SaveAsync(state, ct);
            await RaiseAuthIssueAsync(account, state, ct);
            return new(account.Id, state.Status, 0, 0, "Waiting for an assisted sign-in.");
        }

        if (!sites.TryGetValue(account.Site, out var site))
            return new(account.Id, state.Status, 0, 0, $"Unknown site '{account.Site}'.");

        var sched = await schedule.GetAsync(account.Id, ct);
        sched.LastSignInAttemptUtc = now;
        if (mode == RunMode.Full) sched.LastFullCheckUtc = now;
        await schedule.SaveAsync(sched, ct);

        if (interactive) await auth.SaveAsync(state with { Status = AuthStatus.Assisting }, ct);

        var workDir = Path.Combine(paths.WorkDir, account.Id + "-" + now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(workDir);
        try
        {
            var known = (await documents.GetAsync(account.Id, ct)).Select(d => new KnownDocument(d.DocumentDate, d.StatementNumber)).ToList();
            SiteResult result;
            SiteCredential credential;
            try { credential = await secrets.GetCredentialAsync(account.Credential, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return await SignInFailedAsync(account, state, SiteResult.AuthFailed(AuthFailureReason.CredentialUnavailable, ex.Message), interactive, clock.GetUtcNow(), ct);
            }
            try
            {
                result = await site.RunAsync(new SiteContext
                {
                    Account = account,
                    Credential = credential,
                    ProfileDir = paths.ProfileDir(account.Id),
                    WorkDir = workDir,
                    DiagnosticsDir = paths.DiagnosticsDir(account.Id),
                    Known = known,
                    SignInOnly = mode == RunMode.SignInOnly,
                    Interactive = interactive,
                    Log = loggers.CreateLogger($"Site.{account.Site}"),
                }, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogError(ex, "{Account}: run failed", account.Id);
                result = SiteResult.AuthFailed(AuthFailureReason.SiteUnavailable, ex.Message);
            }

            now = clock.GetUtcNow();
            if (!result.SignedIn) return await SignInFailedAsync(account, state, result, interactive, now, ct);

            state = AuthTransitions.Succeeded(state, now, result.RememberedUntilUtc, ExpiryWarning);
            await auth.SaveAsync(state, ct);
            await auth.AppendEventAsync(new AuthEvent { AccountId = account.Id, AtUtc = now, Succeeded = true, Assisted = interactive, RememberedUntilUtc = state.RememberedUntilUtc }, ct);
            await notify.ResolveAsync(AuthIssueKey(account.Id), $"{account.DisplayName} is signed in again.", ct);
            await UpdateExpiryIssueAsync(account, state, ct);

            if (result.DocumentError is not null)
            {
                await notify.RaiseAsync(DocumentIssueKey(account.Id), new Notification
                {
                    Level = NotificationLevel.ActionNeeded,
                    AccountId = account.Id,
                    Title = $"{account.DisplayName}: couldn't get {account.KindWord}s",
                    Message = $"Signed in, but listing or downloading failed: {result.DocumentError}" +
                              (result.DiagnosticsPath is null ? "" : $" A screenshot was saved to {result.DiagnosticsPath}."),
                    Link = links.Account(account.Id),
                }, ct);
                return new(account.Id, state.Status, 0, 0, result.DocumentError);
            }
            if (mode == RunMode.Full) await notify.ResolveAsync(DocumentIssueKey(account.Id), $"{account.DisplayName}: {account.KindWord}s are downloading again.", ct);

            var added = 0;
            foreach (var found in result.Documents.OrderBy(d => d.DocumentDate))
                if (await FileDocumentAsync(account, found, known, now, ct) is not null) added++;

            if (added > 0)
            {
                await notify.ResolveAsync(TickRunner.OverdueIssueKey(account.Id), null, ct);
                await notify.ResolveAsync(TickRunner.StaleIssueKey(account.Id), null, ct);
            }

            var failed = await actions.RunPendingAsync(account, ct);
            var summary = mode == RunMode.SignInOnly ? "Signed in." : added == 0 ? $"Signed in; no new {account.KindWord}s." : $"Signed in; {added} new {account.KindWord}(s).";
            return new(account.Id, state.Status, added, failed, summary);
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); } catch (IOException) { }
        }
    }

    private async Task<DocumentRecord?> FileDocumentAsync(AccountConfig account, FoundDocument found, List<KnownDocument> known, DateTimeOffset now, CancellationToken ct)
    {
        if (found.StatementNumber is { } sn && known.Any(k => k.StatementNumber == sn))
        {
            File.Delete(found.TempPath);
            return null;
        }

        var filed = await new DocumentFiler(config.DocumentsRoot).FileAsync(account, found, ct);
        var existing = (await documents.GetAsync(account.Id, ct)).FirstOrDefault(d => d.Sha256 == filed.Sha256);
        if (existing is not null) return null;

        var record = new DocumentRecord
        {
            AccountId = account.Id,
            Kind = account.Kind,
            DocumentDate = found.DocumentDate,
            StatementNumber = found.StatementNumber,
            Amount = found.Amount,
            VendorAccountNumber = found.VendorAccountNumber,
            Path = filed.Path,
            Sha256 = filed.Sha256,
            FirstSeenUtc = now,
            Review = account.Review && account.Actions.Count > 0 ? ReviewState.Pending : ReviewState.None,
        };
        await documents.AddAsync(record, ct);
        known.Add(new KnownDocument(record.DocumentDate, record.StatementNumber));

        var amount = record.Amount is { } a ? $" for {a.ToString("C", CultureInfo.GetCultureInfo("en-US"))}" : "";
        var where = filed.Outcome == FileOutcome.AlreadyOnDisk ? "It was already in the folder" : $"Saved to {filed.Path}";
        var review = record.Review == ReviewState.Pending ? " It's waiting for your review before any actions run." : "";
        await notify.NotifyAsync(new Notification
        {
            Level = NotificationLevel.Info,
            AccountId = account.Id,
            Title = $"New {account.KindWord} from {account.DisplayName}",
            Message = $"Dated {record.DocumentDate:yyyy-MM-dd}{amount}. {where}.{review}",
            Link = record.Review == ReviewState.Pending ? links.Review : links.Account(account.Id),
        }, ct);

        if (filed.Outcome == FileOutcome.NameClash)
            await notify.RaiseAsync($"clash:{record.Id}", new Notification
            {
                Level = NotificationLevel.ActionNeeded,
                AccountId = account.Id,
                Title = $"{account.DisplayName}: file name already used",
                Message = $"A different file already had this {account.KindWord}'s name, so it was saved as {Path.GetFileName(filed.Path)}. Check which one is right.",
                Link = links.Account(account.Id),
            }, ct);
        return record;
    }

    private async Task<RunReport> SignInFailedAsync(AccountConfig account, VendorAuthState state, SiteResult result, bool interactive, DateTimeOffset now, CancellationToken ct)
    {
        state = AuthTransitions.Failed(state, now, result.Failure, result.FailureDetail);
        await auth.SaveAsync(state, ct);
        await auth.AppendEventAsync(new AuthEvent { AccountId = account.Id, AtUtc = now, Succeeded = false, Assisted = interactive, Reason = result.Failure, Detail = result.FailureDetail }, ct);
        await RaiseAuthIssueAsync(account, state, ct, result.DiagnosticsPath);
        return new(account.Id, state.Status, 0, 0, $"Sign-in failed: {AuthTransitions.Explain(result.Failure).What}.");
    }

    private Task RaiseAuthIssueAsync(AccountConfig account, VendorAuthState state, CancellationToken ct, string? diagnostics = null)
    {
        var (what, todo) = AuthTransitions.Explain(state.LastFailure);
        var needsYou = state.Status == AuthStatus.NeedsAssist;
        var waiting = needsYou ? $" Any new {account.KindWord}s are fetched right after you sign in." : "";
        var diag = diagnostics is null ? "" : $" Screenshot: {diagnostics}.";
        var detail = !needsYou && !string.IsNullOrWhiteSpace(state.LastFailureDetail) ? $" Details: {state.LastFailureDetail}" : "";
        return notify.RaiseAsync(AuthIssueKey(account.Id), new Notification
        {
            Level = NotificationLevel.ActionNeeded,
            AccountId = account.Id,
            Title = needsYou ? $"{account.DisplayName} needs you to sign in" : $"{account.DisplayName} can't sign in",
            Message = $"{Capitalize(what)}. {todo}{waiting}{detail}{diag}",
            Link = needsYou ? links.Assist(account.Id) : links.Account(account.Id),
        }, ct);
    }

    private Task UpdateExpiryIssueAsync(AccountConfig account, VendorAuthState state, CancellationToken ct)
    {
        if (state.Status != AuthStatus.ExpiringSoon || state.RememberedUntilUtc is not { } until)
            return notify.ResolveAsync(ExpiringIssueKey(account.Id), null, ct);
        return notify.RaiseAsync(ExpiringIssueKey(account.Id), new Notification
        {
            Level = NotificationLevel.HeadsUp,
            AccountId = account.Id,
            Title = $"{account.DisplayName}: sign-in expires soon",
            Message = $"The remembered sign-in lapses on {until.ToLocalTime():yyyy-MM-dd}. Sign in now (and tick \"remember me\") to avoid being asked when a {account.KindWord} is due.",
            Link = links.Assist(account.Id),
        }, ct);
    }

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}

/// <summary>Runs each account's configured actions on documents that haven't had them yet.</summary>
public sealed class ActionRunner(
    IReadOnlyDictionary<string, IDocumentAction> registry,
    ISecretProvider secrets,
    IDocumentStore documents,
    NotificationCenter notify,
    UiLinks links,
    TimeProvider clock,
    ILoggerFactory loggers)
{
    /// <summary>Returns the number of action runs that failed.</summary>
    public async Task<int> RunPendingAsync(AccountConfig account, CancellationToken ct = default)
    {
        if (account.Actions.Count == 0) return 0;
        var failed = 0;
        foreach (var doc in await documents.GetAsync(account.Id, ct))
        {
            if (doc.Imported || doc.Review is ReviewState.Pending or ReviewState.Rejected) continue;
            failed += await RunForDocumentAsync(account, doc, ct);
        }
        return failed;
    }

    public async Task<int> RunForDocumentAsync(AccountConfig account, DocumentRecord doc, CancellationToken ct = default)
    {
        var failed = 0;
        for (var i = 0; i < account.Actions.Count; i++)
        {
            var settings = account.Actions[i];
            var type = settings.GetValueOrDefault("type") ?? "";
            if (doc.Actions.Any(a => a.Index == i && a.Succeeded)) continue;

            var rec = doc.Actions.FirstOrDefault(a => a.Index == i) ?? new ActionRecord { Index = i, Type = type };
            if (!doc.Actions.Contains(rec)) doc.Actions.Add(rec);
            ActionResult result;
            if (!registry.TryGetValue(type, out var action)) result = ActionResult.Fail($"Unknown action type '{type}'.");
            else
            {
                try
                {
                    result = await action.RunAsync(new ActionContext { Account = account, Document = doc, Settings = settings, Secrets = secrets, Log = loggers.CreateLogger($"Action.{type}") }, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    result = ActionResult.Fail(ex.Message);
                }
            }

            rec.Attempts++;
            rec.LastAttemptUtc = clock.GetUtcNow();
            rec.Succeeded = result.Succeeded;
            rec.Summary = result.Summary;
            await documents.UpdateAsync(doc, ct);

            var key = $"action:{doc.Id}:{i}";
            var label = $"{account.DisplayName} {account.KindWord} {doc.DocumentDate:yyyy-MM-dd}";
            if (result.Succeeded)
            {
                await notify.ResolveAsync(key, null, ct);
                await notify.NotifyAsync(new Notification { Level = NotificationLevel.Info, AccountId = account.Id, Title = $"{label}: {type} done", Message = result.Summary, Link = links.Account(account.Id) }, ct);
            }
            else
            {
                failed++;
                await notify.RaiseAsync(key, new Notification
                {
                    Level = NotificationLevel.ActionNeeded,
                    AccountId = account.Id,
                    Title = $"{label}: {type} failed",
                    Message = $"{result.Summary} It will be retried on the next check (attempt {rec.Attempts}).",
                    Link = links.Account(account.Id),
                }, ct);
            }
        }
        return failed;
    }
}
