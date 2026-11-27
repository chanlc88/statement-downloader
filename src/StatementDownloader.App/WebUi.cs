using System.Collections.Concurrent;
using System.Reflection;
using StatementDownloader.Core;

namespace StatementDownloader.App;

/// <summary>Tracks runs started from the UI so the dashboard can show "Checking…".</summary>
public sealed class RunTracker
{
    public ConcurrentDictionary<string, (string Kind, DateTimeOffset Started)> Running { get; } = new(StringComparer.OrdinalIgnoreCase);
    public ConcurrentDictionary<string, RunReport> LastReports { get; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Hourly heartbeat inside <c>serve</c>.</summary>
public sealed class TickService(AppServices services, ILogger<TickService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            try { await services.Ticks.TickAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogError(ex, "Tick failed"); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

public static class WebUi
{
    public static WebApplication Build(AppServices services, string[] args)
    {
        var builder = WebApplication.CreateSlimBuilder(args);
        builder.WebHost.UseUrls($"http://127.0.0.1:{services.Config.Ui.Port}");
        builder.Services.AddSingleton(services);
        builder.Services.AddSingleton(new RunTracker());
        builder.Services.AddHostedService<TickService>();
        builder.Services.ConfigureHttpJsonOptions(o =>
        {
            foreach (var c in JsonFile.Options.Converters) o.SerializerOptions.Converters.Add(c);
            o.SerializerOptions.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
        });
        builder.Logging.AddSimpleConsole(o => o.SingleLine = true);
        // Every UI request is logged at Information otherwise, burying the app's own messages.
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);

        var app = builder.Build();
        var html = LoadIndex().Replace("__UI_KEY__", services.UiKey);

        // Pages: one client-side app for every route.
        foreach (var route in new[] { "/", "/accounts/{id}", "/review", "/issues", "/settings" })
            app.MapGet(route, () => Results.Content(html, "text/html; charset=utf-8"));

        var api = app.MapGroup("/api");
        api.MapGet("/status", async (AppServices s, RunTracker runs, CancellationToken ct) =>
        {
            var statuses = await s.Ticks.GetStatusAsync(ct);
            var events = await s.Auth.GetEventsAsync(null, ct);
            var docs = await s.Documents.GetAsync(null, ct);
            var views = new List<object>();
            foreach (var st in statuses)
                views.Add(AccountView(st, runs, s, events, docs, await s.Schedule.GetAsync(st.Account.Id, ct)));
            return Results.Ok(new
            {
                machine = Environment.MachineName,
                documentsRoot = s.Config.DocumentsRoot,
                accounts = views,
            });
        });

        api.MapGet("/accounts/{id}", async (string id, AppServices s, RunTracker runs, CancellationToken ct) =>
        {
            var st = (await s.Ticks.GetStatusAsync(ct)).FirstOrDefault(x => x.Account.Id == id);
            if (st is null) return Results.NotFound();
            var docs = await s.Documents.GetAsync(id, ct);
            var events = await s.Auth.GetEventsAsync(id, ct);
            var sched = await s.Schedule.GetAsync(id, ct);
            var gaps = GapFinder.Find(docs.Select(d => d.DocumentDate), sched.ExpectFrom, sched.GapMarks);
            return Results.Ok(new
            {
                account = AccountView(st, runs, s, events, docs, sched),
                documents = docs.OrderByDescending(d => d.DocumentDate).Select(d => new { d.Id, date = d.DocumentDate, d.StatementNumber, d.Amount, d.Path, d.Imported, d.Backfilled, d.ActionsRequested, d.FirstSeenUtc, review = d.Review, actions = d.Actions }),
                gaps = new
                {
                    known = gaps is not null,
                    needs = GapFinder.MinimumDocuments,
                    intervalDays = gaps?.IntervalDays,
                    monthly = gaps?.Monthly,
                    missing = gaps?.MissingCount ?? 0,
                    slots = gaps?.Slots.OrderByDescending(x => x.Expected).Select(x => new { x.Key, expected = x.Expected, mark = x.Mark }) ?? [],
                    hidden = gaps?.Slots.Count(x => x.Mark == GapMark.NoStatement) ?? 0,
                    expectFrom = sched.ExpectFrom,
                    lastFill = sched.LastFill,
                    sched.LastFillUtc,
                    sched.OldestListed,
                    hasActions = st.Account.Actions.Count > 0,
                },
                signIns = events.OrderByDescending(e => e.AtUtc).Take(50),
            });
        });

        api.MapGet("/issues", async (AppServices s, CancellationToken ct) =>
        {
            var data = await s.Notify.ReadAsync(ct);
            return Results.Ok(new
            {
                open = data.Issues.Where(i => i.IsOpen).OrderByDescending(i => i.Level).ThenBy(i => i.OpenedUtc),
                resolved = data.Issues.Where(i => !i.IsOpen).OrderByDescending(i => i.ResolvedUtc).Take(30),
                recent = data.Log.AsEnumerable().Reverse().Take(100),
                held = data.Held,
            });
        });

        api.MapGet("/review", async (AppServices s, CancellationToken ct) =>
            Results.Ok((await s.Documents.GetAsync(null, ct)).Where(d => d.Review == ReviewState.Pending)
                .Select(d => new { d.Id, d.AccountId, account = s.Config.Accounts.FirstOrDefault(a => a.Id == d.AccountId)?.DisplayName, date = d.DocumentDate, d.StatementNumber, d.Amount, d.Path,
                    actions = s.Config.Accounts.FirstOrDefault(a => a.Id == d.AccountId)?.Actions.Select(a => a.GetValueOrDefault("type")) })));

        // A saved document, shown in the browser. Opened in a new tab, which can't send the key
        // header, so the key comes in the query string instead.
        api.MapGet("/documents/{id}/file", async (string id, string? key, AppServices s, CancellationToken ct) =>
        {
            if (key != s.UiKey) return Results.StatusCode(403);
            if (await SavedFileAsync(s, id, ct) is not { } path) return Results.NotFound("The file isn't there any more.");
            var type = Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase) ? "application/pdf" : "application/octet-stream";
            return Results.File(path, type, enableRangeProcessing: true);
        });

        // Which accounts wait for review, and the latest decisions, for the Review page.
        api.MapGet("/review/recent", async (AppServices s, CancellationToken ct) =>
        {
            var names = s.Config.Accounts.ToDictionary(a => a.Id, a => a.DisplayName);
            return Results.Ok(new
            {
                accounts = s.Config.Accounts.Where(a => a.Review && a.Actions.Count > 0).Select(a => a.DisplayName),
                decided = (await s.Documents.GetAsync(null, ct)).Where(d => d.Review is ReviewState.Approved or ReviewState.Rejected)
                    .OrderByDescending(d => d.Actions.Select(x => (DateTimeOffset?)x.LastAttemptUtc).Max() ?? d.FirstSeenUtc)
                    .Take(10)
                    .Select(d => new { d.Id, account = names.GetValueOrDefault(d.AccountId, d.AccountId), date = d.DocumentDate, d.Amount, review = d.Review, actions = d.Actions.Select(x => new { x.Succeeded, x.Summary }) }),
            });
        });

        // State-changing endpoints need the per-install key (blocks cross-site requests).
        var write = api.MapGroup("").AddEndpointFilter(async (ctx, next) =>
            ctx.HttpContext.Request.Headers["X-SD-Key"] == services.UiKey ? await next(ctx) : Results.StatusCode(403));

        write.MapPost("/accounts/{id}/check", (string id, AppServices s, RunTracker runs) => StartRun(s, runs, id, interactive: false));
        write.MapPost("/accounts/{id}/assist", (string id, AppServices s, RunTracker runs) => StartRun(s, runs, id, interactive: true));
        // Fill gaps in the history: every missing document, or one year's (?year=2021).
        write.MapPost("/accounts/{id}/fill", (string id, int? year, AppServices s, RunTracker runs) => StartFill(s, runs, id, year));

        // What the person says about a missing document: myself (I'll get it), none (there was no
        // document), or clear.
        write.MapPost("/accounts/{id}/gaps/{key}/{mark}", async (string id, string key, string mark, AppServices s, CancellationToken ct) =>
        {
            GapMark? m = mark switch { "myself" => GapMark.GetMyself, "none" => GapMark.NoStatement, "clear" => GapMark.None, _ => null };
            if (m is null) return Results.BadRequest();
            await s.Ticks.MarkGapAsync(s.Account(id), key, m.Value, ct);
            return Results.Ok();
        });

        // Where the account's documents start (yyyy-MM); empty clears it.
        write.MapPost("/accounts/{id}/expect-from", async (string id, string? from, AppServices s, CancellationToken ct) =>
        {
            DateOnly? date = null;
            if (!string.IsNullOrWhiteSpace(from))
            {
                if (!DateOnly.TryParseExact(from.Trim() + (from.Trim().Length == 7 ? "-01" : ""), "yyyy-MM-dd", out var d) || d > DateOnly.FromDateTime(DateTime.Today))
                    return Results.BadRequest(new { error = "Give a month like 2019-01." });
                date = d;
            }
            await s.Ticks.SetExpectFromAsync(s.Account(id), date, ct);
            return Results.Ok();
        });

        // Runs the account's actions on a backfilled document (they skip those unless asked).
        write.MapPost("/documents/{id}/run-actions", async (string id, AppServices s, CancellationToken ct) =>
        {
            try
            {
                var failed = await s.Ticks.RunActionsOnAsync(id, ct);
                return failed is null ? Results.NotFound(new { error = "No such document." }) : Results.Ok(new { failed });
            }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
        });

        write.MapPost("/accounts/{id}/import-history", async (string id, AppServices s, CancellationToken ct) =>
        {
            var (added, skipped) = await HistoryImport.RunAsync(s, s.Account(id), ct);
            return Results.Ok(new { added, skipped });
        });

        write.MapPost("/review/{docId}/{decision}", async (string docId, string decision, AppServices s, CancellationToken ct) =>
        {
            if (decision is not ("approve" or "reject")) return Results.BadRequest();
            try
            {
                var failed = await s.Ticks.ReviewAsync(docId, decision == "approve", ct);
                return failed is null ? Results.Conflict(new { error = "This document was already approved or rejected." }) : Results.Ok(new { failed });
            }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
        });

        // Opens the document's folder on this computer with the file selected.
        write.MapPost("/documents/{id}/show", async (string id, AppServices s, CancellationToken ct) =>
        {
            if (await SavedFileAsync(s, id, ct) is not { } path) return Results.NotFound(new { error = "The file isn't there any more." });
            ShowInFolder(path);
            return Results.Ok();
        });

        write.MapPost("/notifications/test", async (AppServices s, CancellationToken ct) =>
            Results.Ok((await s.Notify.SendTestAsync(ct)).Select(r => new { channel = r.Channel, ok = r.Error is null, error = r.Error })));

        write.MapPost("/issues/dismiss-all", async (AppServices s, CancellationToken ct) =>
            Results.Ok(new { dismissed = await s.Notify.ResolveAllAsync(ct) }));

        write.MapPost("/notifications/clear", async (AppServices s, CancellationToken ct) =>
        {
            await s.Notify.ClearHistoryAsync(ct);
            return Results.Ok();
        });

        write.MapPost("/issues/{key}/dismiss", async (string key, AppServices s, CancellationToken ct) =>
        {
            await s.Notify.ResolveAsync(key, null, ct);
            return Results.Ok();
        });

        // Settings: where documents are saved, groups, tags and notifications. SettingsService edits
        // config.yaml in place (keeping comments) and applies the change to the running agent, so no
        // restart is needed. The command line sends its changes here too while the agent runs.
        api.MapGet("/settings", async (AppServices s, CancellationToken ct) => Results.Ok(await SettingsService.ViewAsync(s, ct)));

        api.MapGet("/settings/preview", async (string account, string? folder, string? fileName, string? root, AppServices s, CancellationToken ct) =>
            Results.Ok(await SettingsService.PreviewAsync(s, account, folder, fileName, root, ct)));

        // Existing subfolders of the base folder, for suggestions while typing a folder.
        api.MapGet("/settings/folders", (string? under, AppServices s) =>
        {
            try { return Results.Ok(new { folders = SettingsService.Folders(s, under) }); }
            catch (SettingsProblem) { return Results.BadRequest(); }
        });

        // The sites accounts can be added for, with what each needs.
        api.MapGet("/sites", (AppServices s) => Results.Ok(SettingsService.SitesView(s.Sites.Values)));

        // Adds an account to config.yaml, checks its login can be read, and imports the documents
        // already in its folder. The first sign-in is left to the person (Sign in now).
        write.MapPost("/settings/accounts", async (NewAccount n, AppServices s, CancellationToken ct) =>
        {
            try
            {
                var added = await SettingsService.AddAccountAsync(s, n, ct: ct);
                return Results.Ok(new { id = added.Id, imported = added.Imported });
            }
            catch (SettingsProblem ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        write.MapPost("/settings", async (SettingsChange change, AppServices s, CancellationToken ct) =>
        {
            try { return Results.Ok(await SettingsService.ApplyAsync(s, change, ct: ct)); }
            catch (SettingsProblem ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        return app;
    }

    private static IResult StartRun(AppServices s, RunTracker runs, string id, bool interactive)
    {
        var account = s.Account(id);
        // Accounts on one login share a browser profile, and a run on one also runs the others.
        if (Logins.Siblings(s.Config, account).Any(x => runs.Running.ContainsKey(x.Id)))
            return Results.Conflict(new { message = "Another account on the same sign-in is running; try again when it's done." });
        if (!runs.Running.TryAdd(account.Id, (interactive ? "assist" : "check", DateTimeOffset.UtcNow)))
            return Results.Conflict(new { message = "Already running." });
        _ = Task.Run(async () =>
        {
            try { runs.LastReports[account.Id] = await s.Ticks.RunNowAsync(account, interactive); }
            catch (Exception ex) { runs.LastReports[account.Id] = new RunReport(account.Id, AuthStatus.Unknown, 0, 0, "Failed: " + ex.Message); }
            finally { runs.Running.TryRemove(account.Id, out _); }
        });
        return Results.Accepted(value: new { started = true });
    }

    private static IResult StartFill(AppServices s, RunTracker runs, string id, int? year)
    {
        var account = s.Account(id);
        if (Logins.Siblings(s.Config, account).Any(x => runs.Running.ContainsKey(x.Id)))
            return Results.Conflict(new { message = "Another account on the same sign-in is running; try again when it's done." });
        if (!runs.Running.TryAdd(account.Id, ("fill", DateTimeOffset.UtcNow)))
            return Results.Conflict(new { message = "Already running." });
        _ = Task.Run(async () =>
        {
            try
            {
                // Like Check now, it opens the window for you when the site needs you there.
                var auth = await s.Auth.GetAsync(account.Id);
                var interactive = s.Sites.GetValueOrDefault(account.Site)?.NeedsPerson == true || auth.Status == AuthStatus.NeedsAssist;
                if (interactive) runs.Running[account.Id] = ("fill-assist", DateTimeOffset.UtcNow);
                var outcome = await s.Ticks.FillGapsAsync(account, year, interactive);
                runs.LastReports[account.Id] = new RunReport(account.Id, auth.Status, outcome.Found, 0, outcome.Message);
            }
            catch (Exception ex) { runs.LastReports[account.Id] = new RunReport(account.Id, AuthStatus.Unknown, 0, 0, "Failed: " + ex.Message); }
            finally { runs.Running.TryRemove(account.Id, out _); }
        });
        return Results.Accepted(value: new { started = true });
    }

    /// <summary>
    /// How the account signs in, for the banner on its card: on its own, on its own once you've signed
    /// in (while this PC is remembered), or with you every time.
    /// </summary>
    private static object SignInMode(AccountStatus st, AppServices s, IReadOnlyList<AuthEvent> events)
    {
        var a = st.Account;
        var site = s.Sites.GetValueOrDefault(a.Site);
        if (site?.NeedsPerson == true)
            return new { mode = "every-time", title = "Needs you every time",
                detail = "This site refuses background sign-ins. When a check is due, SD asks you to choose Sign in now; a browser window opens, SD fills your login, and you finish any check or code." };
        if (site?.OpensWindow == true)
            return new { mode = "own", title = "Signs in on its own",
                detail = "When a check is due, SD opens a browser window by itself, signs in, downloads and closes it. If a human check or a code stops it, it asks you to choose Sign in now." };
        if (site?.EmailsCode == true && s.Config.Mailbox is not null)
            return new { mode = "own", title = "Signs in on its own",
                detail = $"When the site emails a sign-in code, SD reads it from {s.Config.Mailbox.Username}." };
        var logins = Logins.Siblings(s.Config, a).Select(x => x.Id).Append(a.Id).ToHashSet();
        if (events.Any(e => logins.Contains(e.AccountId) && e.Assisted && e.Succeeded))
            return new { mode = "remembered", title = "Signs in on its own after you've signed in once",
                detail = st.Auth.RememberedUntilUtc is { } until
                    ? $"This PC is remembered until about {until.ToLocalTime():MMM d}; after that SD asks you to sign in again."
                    : "SD uses the sign-in you completed; when the site forgets this PC, it asks you again." };
        return new { mode = "own", title = "Signs in on its own", detail = "SD signs in with the saved login; no code or check needed so far." };
    }

    /// <summary>The full path of a recorded document, if the file is still there.</summary>
    private static async Task<string?> SavedFileAsync(AppServices s, string id, CancellationToken ct)
    {
        var doc = (await s.Documents.GetAsync(null, ct)).FirstOrDefault(d => d.Id == id);
        if (doc is null || string.IsNullOrWhiteSpace(doc.Path)) return null;
        var path = Path.GetFullPath(doc.Path);
        return File.Exists(path) ? path : null;
    }

    private static void ShowInFolder(string path)
    {
        var start = OperatingSystem.IsWindows() ? new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
            : OperatingSystem.IsMacOS() ? new System.Diagnostics.ProcessStartInfo("open", ["-R", path])
            : new System.Diagnostics.ProcessStartInfo("xdg-open", [Path.GetDirectoryName(path)!]);
        start.UseShellExecute = false;
        System.Diagnostics.Process.Start(start)?.Dispose();
    }

    private static object AccountView(AccountStatus st, RunTracker runs, AppServices s, IReadOnlyList<AuthEvent> events,
        IReadOnlyList<DocumentRecord> docs, ScheduleState schedule)
    {
        var a = st.Account;
        var config = s.Config;
        var mine = docs.Where(d => d.AccountId == a.Id).ToList();
        var weekAgo = DateTimeOffset.UtcNow.AddDays(-7);
        return new
        {
            pendingReview = mine.Count(d => d.Review == ReviewState.Pending),
            newThisWeek = mine.Count(d => !d.Imported && !d.Backfilled && d.FirstSeenUtc >= weekAgo),
            missing = GapFinder.Find(mine.Select(d => d.DocumentDate), schedule.ExpectFrom, schedule.GapMarks)?.MissingCount ?? 0,
            lastCheckedUtc = schedule.LastFullCheckUtc,
            signIn = SignInMode(st, s, events),
            a.Id,
            name = a.DisplayName,
            group = a.Group,
            tags = a.Tags,
            a.Site,
            kind = a.KindWord,
            a.Enabled,
            folder = a.Folder,
            schedule = a.Schedule.Mode switch { ScheduleMode.Window => $"days {a.Schedule.DayOfMonth} of each month", ScheduleMode.Interval => $"every {a.Schedule.Every}", _ => "auto (learned)" },
            auth = new { status = st.Auth.Status, reason = st.Auth.LastFailure, st.Auth.LastSuccessUtc, st.Auth.RememberedUntilUtc, explain = st.Auth.LastFailure == AuthFailureReason.None ? null : AuthTransitions.Explain(st.Auth.LastFailure).What },
            estimate = st.Estimate,
            decision = new { phase = st.Decision.Phase, run = st.Decision.Run, st.Decision.Reason },
            lastDocument = st.LastDocument is { } d ? new { d.Id, date = d.DocumentDate, d.Amount, d.StatementNumber, d.FirstSeenUtc, review = d.Review } : null,
            documentCount = st.DocumentCount,
            issues = st.OpenIssues,
            // Other accounts signed in with the same login (same site and credential).
            sharesSignInWith = Logins.Siblings(config, a).Select(x => x.DisplayName),
            running = runs.Running.TryGetValue(a.Id, out var r) ? r.Kind : null,
            lastRun = runs.LastReports.TryGetValue(a.Id, out var rep) ? rep.Summary : null,
        };
    }

    private static string LoadIndex()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("index.html")
            ?? throw new InvalidOperationException("UI resource missing.");
        return new StreamReader(stream).ReadToEnd();
    }
}
