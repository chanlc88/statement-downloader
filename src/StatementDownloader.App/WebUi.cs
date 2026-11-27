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
            return Results.Ok(new
            {
                machine = Environment.MachineName,
                documentsRoot = s.Config.DocumentsRoot,
                accounts = statuses.Select(st => AccountView(st, runs, s, events)),
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
                account = AccountView(st, runs, s, events),
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
            if (decision is not ("approve" or "reject")) return Results.BadRequest();
            try
            {
                var failed = await s.Ticks.ReviewAsync(docId, decision == "approve", ct);
                return failed is null ? Results.Conflict(new { error = "This document was already approved or rejected." }) : Results.Ok(new { failed });
            }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
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

        // Settings: where documents are saved. Edits config.yaml in place (keeping comments) and
        // applies the change to the running agent, so no restart is needed.
        api.MapGet("/settings", async (AppServices s, CancellationToken ct) => Results.Ok(await SettingsView(s, ct)));

        api.MapGet("/settings/preview", async (string account, string? folder, string? fileName, string? root, AppServices s, CancellationToken ct) =>
        {
            var a = s.Account(account);
            folder ??= a.Folder; fileName ??= a.FileName; root = string.IsNullOrWhiteSpace(root) ? s.Config.DocumentsRoot : ConfigLoader.ExpandHome(root.Trim());
            var problem = AccountLocation.FolderProblem(folder) ?? AccountLocation.FileNameProblem(fileName);
            if (problem is not null) return Results.Ok(new { problem });
            var path = await ExamplePathAsync(s, a, root, folder, fileName, ct);
            return Results.Ok(new { path, relative = Relative(root, path) });
        });

        // Existing subfolders of the base folder, for suggestions while typing a folder.
        api.MapGet("/settings/folders", (string? under, AppServices s) =>
        {
            var rel = (under ?? "").Replace('\\', '/').Trim('/');
            if (rel.Split('/').Any(p => p == "..") || Path.IsPathRooted(rel)) return Results.BadRequest();
            var dir = Path.Combine(s.Config.DocumentsRoot, rel);
            if (!Directory.Exists(dir)) return Results.Ok(new { folders = Array.Empty<string>() });
            try
            {
                var folders = new DirectoryInfo(dir).EnumerateDirectories()
                    .Where(d => !d.Name.StartsWith('.') && !d.Attributes.HasFlag(FileAttributes.Hidden))
                    .Select(d => rel.Length == 0 ? d.Name : rel + "/" + d.Name)
                    .Order(StringComparer.OrdinalIgnoreCase).Take(300).ToList();
                return Results.Ok(new { folders });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Results.Ok(new { folders = Array.Empty<string>() }); }
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
                    if ((AccountLocation.FolderProblem(folder) ?? AccountLocation.FileNameProblem(fileName)) is { } problem)
                        return Results.BadRequest(new { error = $"{account.DisplayName}: {problem}" });
                    if (folder != account.Folder) yaml = ConfigEditor.SetAccountValue(yaml, account.Id, "folder", folder);
                    if (fileName != account.FileName) yaml = ConfigEditor.SetAccountValue(yaml, account.Id, "fileName", fileName);
                    if (a.Group is { } group && group.Trim() != (account.Group ?? "")) yaml = ConfigEditor.SetAccountGroup(yaml, account.Id, group);
                    if (a.Tags is { } tags && !ConfigEditor.NormalizeTags(tags).SequenceEqual(account.Tags)) yaml = ConfigEditor.SetAccountTags(yaml, account.Id, tags);
                }

                AppConfig saved;
                try { saved = ConfigEditor.Check(yaml); }
                catch (Exception ex) when (ex is InvalidDataException or YamlDotNet.Core.YamlException) { return Results.BadRequest(new { error = ex.Message }); }
                ConfigEditor.Save(path, yaml);

                // Apply to the running agent.
                s.Config.DocumentsRoot = saved.DocumentsRoot;
                foreach (var a in s.Config.Accounts)
                    if (saved.Accounts.FirstOrDefault(x => x.Id == a.Id) is { } n) { a.Folder = n.Folder; a.FileName = n.FileName; a.Group = n.Group; a.Tags = n.Tags; }
                return Results.Ok(await SettingsView(s, ct));
            }
            catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
            finally { SettingsLock.Release(); }
        });

        return app;
    }

    private static readonly SemaphoreSlim SettingsLock = new(1, 1);

    public sealed record SettingsChange(string? DocumentsRoot, List<AccountLocationChange>? Accounts);
    public sealed record AccountLocationChange(string Id, string? Folder, string? FileName, string? Group = null, List<string>? Tags = null);

    private static async Task<object> SettingsView(AppServices s, CancellationToken ct)
    {
        var accounts = new List<object>();
        foreach (var a in s.Config.Accounts)
        {
            var example = await ExamplePathAsync(s, a, s.Config.DocumentsRoot, a.Folder, a.FileName, ct);
            accounts.Add(new { a.Id, name = a.DisplayName, a.Site, group = a.Group, tags = a.Tags, sharesSignInWith = Logins.Siblings(s.Config, a).Select(x => x.DisplayName), a.Folder, a.FileName, example, relative = Relative(s.Config.DocumentsRoot, example) });
        }
        return new
        {
            configFile = s.Paths.ConfigFile,
            documentsRoot = s.Config.DocumentsRoot,
            documentsRootExists = Directory.Exists(s.Config.DocumentsRoot),
            groups = s.Config.Accounts.Select(a => a.Group).OfType<string>().Where(g => g.Length > 0).Distinct().Order(StringComparer.OrdinalIgnoreCase),
            tags = s.Config.Accounts.SelectMany(a => a.Tags).Distinct().Order(StringComparer.OrdinalIgnoreCase),
            accounts,
        };
    }

    private static string Relative(string root, string path)
    {
        var r = Path.GetRelativePath(Path.GetFullPath(root), path);
        return r.StartsWith("..", StringComparison.Ordinal) ? path : r.Replace('\\', '/');
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

    private static object AccountView(AccountStatus st, RunTracker runs, AppServices s, IReadOnlyList<AuthEvent> events)
    {
        var a = st.Account;
        var config = s.Config;
        return new
        {
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
            lastDocument = st.LastDocument is { } d ? new { date = d.DocumentDate, d.Amount, d.StatementNumber } : null,
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
