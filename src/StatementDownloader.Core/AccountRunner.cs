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
    public ActionRunner Actions => actions;
    private TimeSpan ExpiryWarning => TimeSpan.FromDays(config.Schedule.ExpiryWarningDays);

    public static string AuthIssueKey(string id) => $"auth:{id}";
    public static string ExpiringIssueKey(string id) => $"expiring:{id}";
    public static string DocumentIssueKey(string id) => $"documents:{id}";

    /// <summary>Accounts on one login share their sign-in notifications.</summary>
    private string SignInIssueId(AccountConfig account) =>
        Logins.Siblings(config, account).Count == 0 ? account.Id : Logins.Id(account);

    /// <summary>The account's site is only visited with the person present.</summary>
    public bool NeedsPerson(AccountConfig account) => sites.TryGetValue(account.Site, out var s) && s.NeedsPerson;

    public async Task<RunReport> RunAsync(AccountConfig account, RunMode mode, bool interactive = false, CancellationToken ct = default, bool capture = false)
    {
        var now = clock.GetUtcNow();
        if (!sites.TryGetValue(account.Site, out var site))
            return new(account.Id, AuthStatus.Unknown, 0, 0, $"Unknown site '{account.Site}'.");

        var state = AuthTransitions.CheckExpiry(await auth.GetAsync(account.Id, ct), now, ExpiryWarning);

        if (!interactive && state.Status == AuthStatus.NeedsAssist)
        {
            await auth.SaveAsync(state, ct);
            await RaiseAuthIssueAsync(account, state, ct);
            return new(account.Id, state.Status, 0, 0, "Waiting for an assisted sign-in.");
        }

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
                    ProfileDir = Logins.ProfileDir(paths, config, account),
                    WorkDir = workDir,
                    DiagnosticsDir = paths.DiagnosticsDir(account.Id),
                    Known = known,
                    SignInOnly = mode == RunMode.SignInOnly,
                    Interactive = interactive,
                    SharedLogin = Logins.Siblings(config, account).Count > 0,
                    Mailbox = config.Mailbox is { } mailbox ? new Mailbox(mailbox, secrets) : null,
                    Capture = capture,
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
            await notify.ResolveAsync(AuthIssueKey(SignInIssueId(account)), $"{Logins.Label(config, account)}: signed in again.", ct);
            if (SignInIssueId(account) != account.Id) await notify.ResolveAsync(AuthIssueKey(account.Id), null, ct);
            await UpdateExpiryIssueAsync(account, state, ct);
            // The other accounts on this login share the browser profile, so they're signed in too.
            foreach (var sibling in Logins.Siblings(config, account))
            {
                await auth.SaveAsync(AuthTransitions.Succeeded(await auth.GetAsync(sibling.Id, ct), now, result.RememberedUntilUtc, ExpiryWarning), ct);
                await notify.ResolveAsync(AuthIssueKey(sibling.Id), null, ct);
            }

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
        // Check against the store as it is now, not only as it was when the run started: a history
        // import running at the same time may have added this document since.
        var current = await documents.GetAsync(account.Id, ct);
        var alreadyKnown = found.StatementNumber is { } sn
            ? known.Any(k => k.StatementNumber == sn) || current.Any(d => d.StatementNumber == sn)
            // Without a number the date identifies it; documents added earlier in this run don't
            // count, as two can share a date.
            : current.Any(d => d.StatementNumber is null && d.DocumentDate == found.DocumentDate && d.FirstSeenUtc != now);
        if (alreadyKnown)
        {
            File.Delete(found.TempPath);
            return null;
        }

        var filed = await new DocumentFiler(config.DocumentsRoot).FileAsync(account, found, ct,
            sites.TryGetValue(account.Site, out var site) ? site.Identify : null);
        var existing = (await documents.GetAsync(account.Id, ct)).FirstOrDefault(d => d.Sha256 == filed.Sha256);
        if (existing is not null) return null;

        var record = await AddAndAnnounceAsync(account, found, filed.Path, filed.Sha256, now,
            filed.Outcome == FileOutcome.AlreadyOnDisk ? "It was already in the folder" : $"Saved to {filed.Path}", ct);
        known.Add(new KnownDocument(record.DocumentDate, record.StatementNumber));

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

    /// <summary>Records a new document (waiting for review when the account asks) and tells the person.</summary>
    private async Task<DocumentRecord> AddAndAnnounceAsync(AccountConfig account, FoundDocument found, string path, string sha256, DateTimeOffset now, string where, CancellationToken ct)
    {
        var record = new DocumentRecord
        {
            AccountId = account.Id,
            Kind = account.Kind,
            DocumentDate = found.DocumentDate,
            StatementNumber = found.StatementNumber,
            Amount = found.Amount,
            VendorAccountNumber = found.VendorAccountNumber,
            Path = path,
            Sha256 = sha256,
            FirstSeenUtc = now,
            Review = account.Review && account.Actions.Count > 0 ? ReviewState.Pending : ReviewState.None,
        };
        await documents.AddAsync(record, ct);

        var amount = record.Amount is { } a ? $" for {a.ToString("C", CultureInfo.GetCultureInfo("en-US"))}" : "";
        var review = record.Review == ReviewState.Pending ? " It's waiting for your review before any actions run." : "";
        await notify.NotifyAsync(new Notification
        {
            Level = NotificationLevel.Info,
            AccountId = account.Id,
            Title = $"New {account.KindWord} from {account.DisplayName}",
            Message = $"Dated {record.DocumentDate:yyyy-MM-dd}{amount}. {where}.{review}",
            Link = record.Review == ReviewState.Pending ? links.Review : links.Account(account.Id),
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

    private async Task RaiseAuthIssueAsync(AccountConfig account, VendorAuthState state, CancellationToken ct, string? diagnostics = null)
    {
        // Issues raised per account before logins were shared are replaced by the login's one.
        if (SignInIssueId(account) != account.Id)
            foreach (var a in Logins.Siblings(config, account).Append(account))
                await notify.ResolveAsync(AuthIssueKey(a.Id), null, ct);
        var (what, todo) = AuthTransitions.Explain(state.LastFailure);
        var needsYou = state.Status == AuthStatus.NeedsAssist;
        var waiting = needsYou ? $" Any new {account.KindWord}s are fetched right after you sign in." : "";
        var diag = diagnostics is null ? "" : $" Screenshot: {diagnostics}.";
        var detail = !needsYou && !string.IsNullOrWhiteSpace(state.LastFailureDetail) ? $" Details: {state.LastFailureDetail}" : "";
        // One notification per login: accounts sharing a sign-in don't each ask.
        var who = Logins.Label(config, account);
        await notify.RaiseAsync(AuthIssueKey(SignInIssueId(account)), new Notification
        {
            Level = NotificationLevel.ActionNeeded,
            AccountId = account.Id,
            Title = needsYou ? $"{who} needs you to sign in" : $"{who} can't sign in",
            Message = $"{Capitalize(what)}. {todo}{waiting}{detail}{diag}",
            Link = needsYou ? links.Assist(account.Id) : links.Account(account.Id),
        }, ct);
    }

    private Task UpdateExpiryIssueAsync(AccountConfig account, VendorAuthState state, CancellationToken ct)
    {
        if (state.Status != AuthStatus.ExpiringSoon || state.RememberedUntilUtc is not { } until)
            return notify.ResolveAsync(ExpiringIssueKey(SignInIssueId(account)), null, ct);
        return notify.RaiseAsync(ExpiringIssueKey(SignInIssueId(account)), new Notification
        {
            Level = NotificationLevel.HeadsUp,
            AccountId = account.Id,
            Title = $"{Logins.Label(config, account)}: sign-in expires soon",
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
    IScheduleStateStore schedule,
    NotificationCenter notify,
    UiLinks links,
    TimeProvider clock,
    ILoggerFactory loggers)
{
    /// <summary>
    /// Runs actions that haven't succeeded yet on the account's documents. An action added to
    /// the config later only applies to documents found after it was added, so adding one
    /// never replays it over the whole history. Returns the number of action runs that failed.
    /// </summary>
    public async Task<int> RunPendingAsync(AccountConfig account, CancellationToken ct = default)
    {
        var since = await ActionStartsAsync(account, ct);
        if (account.Actions.Count == 0) return 0;
        var failed = 0;
        foreach (var doc in await documents.GetAsync(account.Id, ct))
        {
            if (doc.Imported || doc.Review is ReviewState.Pending or ReviewState.Rejected) continue;
            failed += await RunForDocumentAsync(account, doc, ct, since);
        }
        return failed;
    }

    /// <summary>When each of the account's actions was first seen, recording new ones as of now.</summary>
    private async Task<IReadOnlyDictionary<string, DateTimeOffset>> ActionStartsAsync(AccountConfig account, CancellationToken ct)
    {
        var state = await schedule.GetAsync(account.Id, ct);
        var keys = ActionKeys.For(account);
        // First run with stable keys: the actions already configured keep applying to every
        // document, as they did before. From then on, a new key starts now.
        var changed = state.ActionsSince is null;
        var start = changed ? DateTimeOffset.MinValue : clock.GetUtcNow();
        state.ActionsSince ??= [];
        foreach (var key in keys)
            changed |= state.ActionsSince.TryAdd(key, start);
        if (changed) await schedule.SaveAsync(state, ct);
        return state.ActionsSince;
    }

    /// <param name="since">When each action started applying; documents found earlier are left alone. Null runs them all (approval).</param>
    public async Task<int> RunForDocumentAsync(AccountConfig account, DocumentRecord doc, CancellationToken ct = default, IReadOnlyDictionary<string, DateTimeOffset>? since = null)
    {
        var failed = 0;
        var keys = ActionKeys.For(account);
        for (var i = 0; i < account.Actions.Count; i++)
        {
            var settings = account.Actions[i];
            var type = settings.GetValueOrDefault("type") ?? "";
            var key = keys[i];
            if (since?.TryGetValue(key, out var from) == true && doc.FirstSeenUtc < from) continue;

            // Records from before stable keys only knew the position; adopt one if the type matches.
            var rec = doc.Actions.FirstOrDefault(a => a.Key == key)
                      ?? doc.Actions.FirstOrDefault(a => a.Key is null && a.Index == i && a.Type == type);
            if (rec is null) doc.Actions.Add(rec = new ActionRecord { Type = type });
            rec.Key = key;
            rec.Index = i;
            if (rec.Succeeded) continue;
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

            var issueKey = $"action:{doc.Id}:{key}";
            var label = $"{account.DisplayName} {account.KindWord} {doc.DocumentDate:yyyy-MM-dd}";
            if (result.Succeeded)
            {
                await notify.ResolveAsync(issueKey, null, ct);
                await notify.ResolveAsync($"action:{doc.Id}:{i}", null, ct);   // raised before stable keys
                await notify.NotifyAsync(new Notification { Level = NotificationLevel.Info, AccountId = account.Id, Title = $"{label}: {type} done", Message = result.Summary, Link = links.Account(account.Id) }, ct);
            }
            else
            {
                failed++;
                await notify.RaiseAsync(issueKey, new Notification
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
