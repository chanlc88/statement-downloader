using Microsoft.Extensions.Logging;
using StatementDownloader.Actions;
using StatementDownloader.Core;
using StatementDownloader.Secrets;
using StatementDownloader.Sites;

namespace StatementDownloader.App;

/// <summary>Composition root: wires config, stores, sites, actions and notifications.</summary>
public sealed class AppServices : IDisposable
{
    public required AppConfig Config { get; init; }
    public required AppPaths Paths { get; init; }
    public required ILoggerFactory Loggers { get; init; }
    public required SecretProvider Secrets { get; init; }
    public required ITokenStore Tokens { get; init; }
    public required IReadOnlyDictionary<string, ISiteFetcher> Sites { get; init; }
    public required IDocumentStore Documents { get; init; }
    public required IAuthStateStore Auth { get; init; }
    public required IScheduleStateStore Schedule { get; init; }
    public required NotificationCenter Notify { get; init; }
    public required ActionRunner Actions { get; init; }
    public required TickRunner Ticks { get; init; }
    public required UiLinks Links { get; init; }
    public required string UiKey { get; init; }

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(90) };

    public static AppServices Build(AppPaths paths, ILoggerFactory loggers)
    {
        var config = ConfigLoader.Load(paths.ConfigFile);
        Directory.CreateDirectory(paths.DataDir);
        var clock = TimeProvider.System;
        var zone = TimeZoneInfo.Local;
        var tokens = TokenStores.ForCurrentOs();
        var secrets = new SecretProvider(config.Secrets.Bitwarden, paths, tokens);
        var links = new UiLinks($"http://localhost:{config.Ui.Port}");

        var sites = SiteRegistry.All(paths.SitesDir, loggers.CreateLogger("Sites")).ToDictionary(s => s.SiteId, StringComparer.OrdinalIgnoreCase);
        var actions = new IDocumentAction[] { new WebhookAction(Http), new RunCommandAction() }
            .ToDictionary(a => a.Type, StringComparer.OrdinalIgnoreCase);

        var documents = new JsonDocumentStore(paths);
        var auth = new JsonAuthStateStore(paths);
        var schedule = new JsonScheduleStateStore(paths);
        var notify = new NotificationCenter(NotificationChannels.Create(config.Notifications.Channels, secrets, Http),
            config.Notifications, config.Schedule, paths, clock, zone, loggers.CreateLogger<NotificationCenter>(), () => config.Accounts);
        var actionRunner = new ActionRunner(actions, secrets, documents, schedule, notify, links, clock, loggers);
        var runner = new AccountRunner(config, paths, sites, secrets, auth, documents, schedule, actionRunner, notify, links, clock, loggers);
        var ticks = new TickRunner(config, paths, runner, documents, schedule, auth, notify, links, clock, zone, loggers.CreateLogger<TickRunner>());

        foreach (var a in config.Accounts.Where(a => !sites.ContainsKey(a.Site)))
            loggers.CreateLogger<AppServices>().LogWarning("Account {Account} uses unknown site '{Site}'. Known: {Sites}", a.Id, a.Site, string.Join(", ", sites.Keys));

        return new AppServices
        {
            Config = config, Paths = paths, Loggers = loggers, Secrets = secrets, Tokens = tokens, Sites = sites,
            Documents = documents, Auth = auth, Schedule = schedule, Notify = notify, Actions = actionRunner, Ticks = ticks,
            Links = links, UiKey = LoadOrCreateUiKey(paths),
        };
    }

    public AccountConfig Account(string id) =>
        Config.Accounts.FirstOrDefault(a => a.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException($"No account '{id}'. Accounts: {string.Join(", ", Config.Accounts.Select(a => a.Id))}");

    /// <summary>Random per-install key required on state-changing UI requests.</summary>
    private static string LoadOrCreateUiKey(AppPaths paths)
    {
        var file = paths.File("ui-key");
        if (File.Exists(file)) return File.ReadAllText(file).Trim();
        var key = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24));
        File.WriteAllText(file, key);
        return key;
    }

    public void Dispose() => Secrets.Dispose();
}

/// <summary>Seeds document history from PDFs already in an account's folder.</summary>
public static class HistoryImport
{
    /// <summary>
    /// Records the account's files already on disk. Waits for any running check first (up to
    /// <paramref name="lockWait"/>, default 15 minutes): a check comparing against a half-imported
    /// history would download statements the import is about to record.
    /// </summary>
    public static Task<(int Added, int Skipped)> RunAsync(AppServices s, AccountConfig account, CancellationToken ct = default, TimeSpan? lockWait = null) =>
        s.Ticks.UnderRunLockAsync(() => ImportAsync(s, account, ct), lockWait ?? TimeSpan.FromMinutes(15));

    private static async Task<(int Added, int Skipped)> ImportAsync(AppServices s, AccountConfig account, CancellationToken ct)
    {
        var existing = await s.Documents.GetAsync(account.Id, ct);
        var parser = s.Sites.GetValueOrDefault(account.Site) is IHasDocumentParser p ? p.Parser : null;
        int added = 0, skipped = 0;
        foreach (var (path, fileDate) in HistoryImporter.Scan(s.Config.DocumentsRoot, account).OrderBy(x => x.Date))
        {
            var hash = await DocumentFiler.HashAsync(path, ct);
            if (existing.Any(d => d.Sha256 == hash || string.Equals(d.Path, path, StringComparison.OrdinalIgnoreCase))) { skipped++; continue; }

            ParsedDocument? parsed = null;
            if (parser is not null)
                try { parsed = parser.Parse(PdfText.Extract(path)); } catch (Exception) { /* not readable; use the file name date */ }

            await s.Documents.AddAsync(new DocumentRecord
            {
                AccountId = account.Id,
                Kind = account.Kind,
                // The date printed on the document wins over the file name (they can differ).
                DocumentDate = parsed?.DocumentDate ?? fileDate,
                StatementNumber = parsed?.StatementNumber,
                Amount = parsed?.Amount,
                VendorAccountNumber = parsed?.AccountNumber,
                Path = path,
                Sha256 = hash,
                Imported = true,
            }, ct);
            added++;
        }
        return (added, skipped);
    }
}
