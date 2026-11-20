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

        var app = builder.Build();
        var html = LoadIndex().Replace("__UI_KEY__", services.UiKey);

        // Pages: one client-side app for every route.
        foreach (var route in new[] { "/", "/accounts/{id}", "/review", "/issues", "/settings" })
            app.MapGet(route, () => Results.Content(html, "text/html; charset=utf-8"));

        var api = app.MapGroup("/api");
        api.MapGet("/status", async (AppServices s, RunTracker runs, CancellationToken ct) =>
        {
            var statuses = await s.Ticks.GetStatusAsync(ct);
            return Results.Ok(new
            {
                machine = Environment.MachineName,
                documentsRoot = s.Config.DocumentsRoot,
                accounts = statuses.Select(st => AccountView(st, runs)),
            });
        });

        api.MapGet("/accounts/{id}", async (string id, AppServices s, RunTracker runs, CancellationToken ct) =>
        {
            var st = (await s.Ticks.GetStatusAsync(ct)).FirstOrDefault(x => x.Account.Id == id);
            if (st is null) return Results.NotFound();
            var docs = await s.Documents.GetAsync(id, ct);
            var events = await s.Auth.GetEventsAsync(id, ct);
            return Results.Ok(new
            {
                account = AccountView(st, runs),
                documents = docs.OrderByDescending(d => d.DocumentDate).Select(d => new { d.Id, date = d.DocumentDate, d.StatementNumber, d.Amount, d.Path, d.Imported, d.FirstSeenUtc, review = d.Review, actions = d.Actions }),
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

        // State-changing endpoints need the per-install key (blocks cross-site requests).
        var write = api.MapGroup("").AddEndpointFilter(async (ctx, next) =>
            ctx.HttpContext.Request.Headers["X-SD-Key"] == services.UiKey ? await next(ctx) : Results.StatusCode(403));

        write.MapPost("/accounts/{id}/check", (string id, AppServices s, RunTracker runs) => StartRun(s, runs, id, interactive: false));
        write.MapPost("/accounts/{id}/assist", (string id, AppServices s, RunTracker runs) => StartRun(s, runs, id, interactive: true));
        write.MapPost("/accounts/{id}/import-history", async (string id, AppServices s, CancellationToken ct) =>
        {
            var (added, skipped) = await HistoryImport.RunAsync(s, s.Account(id), ct);
            return Results.Ok(new { added, skipped });
        });

        write.MapPost("/review/{docId}/{decision}", async (string docId, string decision, AppServices s, CancellationToken ct) =>
        {
            var doc = (await s.Documents.GetAsync(null, ct)).FirstOrDefault(d => d.Id == docId);
            if (doc is null || doc.Review != ReviewState.Pending) return Results.NotFound();
            var account = s.Account(doc.AccountId);
            if (decision == "reject")
            {
                doc.Review = ReviewState.Rejected;
                await s.Documents.UpdateAsync(doc, ct);
                return Results.Ok(new { failed = 0 });
            }
            if (decision != "approve") return Results.BadRequest();
            doc.Review = ReviewState.Approved;
            await s.Documents.UpdateAsync(doc, ct);
            var failed = await s.Actions.RunForDocumentAsync(account, doc, ct);
            await s.Notify.SendDueAsync(ct);
            return Results.Ok(new { failed });
        });

        write.MapPost("/notifications/test", async (AppServices s, CancellationToken ct) =>
            Results.Ok((await s.Notify.SendTestAsync(ct)).Select(r => new { channel = r.Channel, ok = r.Error is null, error = r.Error })));

        write.MapPost("/issues/{key}/dismiss", async (string key, AppServices s, CancellationToken ct) =>
        {
            await s.Notify.ResolveAsync(key, null, ct);
            return Results.Ok();
        });

        // Settings: where documents are saved. Edits config.yaml in place (keeping comments) and
        // applies the change to the running agent, so no restart is needed.
        api.MapGet("/settings", async (AppServices s, CancellationToken ct) => Results.Ok(await SettingsView(s, ct)));

        api.MapGet("/settings/preview", async (string account, string? folder, string? fileName, string? root, AppServices s, CancellationToken ct) =>
        {
            var a = s.Account(account);
            folder ??= a.Folder; fileName ??= a.FileName; root = string.IsNullOrWhiteSpace(root) ? s.Config.DocumentsRoot : ConfigLoader.ExpandHome(root.Trim());
            var problem = ConfigEditor.FolderProblem(folder) ?? ConfigEditor.FileNameProblem(fileName);
            if (problem is not null) return Results.Ok(new { problem });
            return Results.Ok(new { path = await ExamplePathAsync(s, a, root, folder, fileName, ct) });
        });

        write.MapPost("/settings", async (SettingsChange change, AppServices s, CancellationToken ct) =>
        {
            await SettingsLock.WaitAsync(ct);
            try
            {
                var path = s.Paths.ConfigFile;
                var yaml = await File.ReadAllTextAsync(path, ct);
                var root = change.DocumentsRoot?.Trim();
                if (root is not null)
                {
                    if (root.Length == 0) return Results.BadRequest(new { error = "The base folder is required." });
                    yaml = ConfigEditor.SetDocumentsRoot(yaml, root.Replace('\\', '/'));
                }
                foreach (var a in change.Accounts ?? [])
                {
                    var account = s.Account(a.Id);
                    var folder = (a.Folder ?? account.Folder).Trim().Replace('\\', '/').Trim('/');
                    var fileName = (a.FileName ?? account.FileName).Trim();
                    if ((ConfigEditor.FolderProblem(folder) ?? ConfigEditor.FileNameProblem(fileName)) is { } problem)
                        return Results.BadRequest(new { error = $"{account.DisplayName}: {problem}" });
                    if (folder != account.Folder) yaml = ConfigEditor.SetAccountValue(yaml, account.Id, "folder", folder);
                    if (fileName != account.FileName) yaml = ConfigEditor.SetAccountValue(yaml, account.Id, "fileName", fileName);
                }

                AppConfig saved;
                try { saved = ConfigEditor.Check(yaml); }
                catch (Exception ex) when (ex is InvalidDataException or YamlDotNet.Core.YamlException) { return Results.BadRequest(new { error = ex.Message }); }
                ConfigEditor.Save(path, yaml);

                // Apply to the running agent.
                s.Config.DocumentsRoot = saved.DocumentsRoot;
                foreach (var a in s.Config.Accounts)
                    if (saved.Accounts.FirstOrDefault(x => x.Id == a.Id) is { } n) { a.Folder = n.Folder; a.FileName = n.FileName; }
                return Results.Ok(await SettingsView(s, ct));
            }
            catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
            finally { SettingsLock.Release(); }
        });

        return app;
    }

    private static readonly SemaphoreSlim SettingsLock = new(1, 1);

    public sealed record SettingsChange(string? DocumentsRoot, List<AccountLocationChange>? Accounts);
    public sealed record AccountLocationChange(string Id, string? Folder, string? FileName);

    private static async Task<object> SettingsView(AppServices s, CancellationToken ct)
    {
        var accounts = new List<object>();
        foreach (var a in s.Config.Accounts)
            accounts.Add(new { a.Id, name = a.DisplayName, a.Folder, a.FileName, example = await ExamplePathAsync(s, a, s.Config.DocumentsRoot, a.Folder, a.FileName, ct) });
        return new
        {
            configFile = s.Paths.ConfigFile,
            documentsRoot = s.Config.DocumentsRoot,
            documentsRootExists = Directory.Exists(s.Config.DocumentsRoot),
            accounts,
        };
    }

    /// <summary>Where the newest document on record would be saved with these settings (or a made-up one dated today).</summary>
    private static async Task<string> ExamplePathAsync(AppServices s, AccountConfig a, string root, string folder, string fileName, CancellationToken ct)
    {
        var last = (await s.Documents.GetAsync(a.Id, ct)).OrderByDescending(d => d.DocumentDate).FirstOrDefault();
        var date = last?.DocumentDate ?? DateOnly.FromDateTime(DateTime.Today);
        var statement = last?.StatementNumber ?? "12345678";
        return Path.GetFullPath(Path.Combine(root,
            FileNameTemplate.Render(folder, a, date, statement),
            FileNameTemplate.Render(fileName, a, date, statement)));
    }

    private static IResult StartRun(AppServices s, RunTracker runs, string id, bool interactive)
    {
        var account = s.Account(id);
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

    private static object AccountView(AccountStatus st, RunTracker runs)
    {
        var a = st.Account;
        return new
        {
            a.Id,
            name = a.DisplayName,
            a.Site,
            kind = a.KindWord,
            a.Enabled,
            folder = a.Folder,
            schedule = a.Schedule.Mode switch { ScheduleMode.Window => $"days {a.Schedule.DayOfMonth} of each month", ScheduleMode.Interval => $"every {a.Schedule.Every}", _ => "auto (learned)" },
            auth = new { status = st.Auth.Status, reason = st.Auth.LastFailure, st.Auth.LastSuccessUtc, st.Auth.RememberedUntilUtc, explain = st.Auth.LastFailure == AuthFailureReason.None ? null : AuthTransitions.Explain(st.Auth.LastFailure).What },
            estimate = st.Estimate,
            decision = new { phase = st.Decision.Phase, run = st.Decision.Run, st.Decision.Reason },
            lastDocument = st.LastDocument is { } d ? new { date = d.DocumentDate, d.Amount, d.StatementNumber } : null,
            documentCount = st.DocumentCount,
            issues = st.OpenIssues,
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
