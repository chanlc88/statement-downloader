using StatementDownloader.App;
using StatementDownloader.Core;
using StatementDownloader.Secrets;
using StatementDownloader.Sites;

const string Usage = """
    statement-downloader — download bills, invoices and statements from your accounts

    Usage: statement-downloader <command> [--config <path>] [--data <folder>]

      serve [--hidden]           Run the background agent and web UI (http://localhost:5187);
                                 --hidden (Windows) hides the window and logs to agent.log
      tick                       Run whatever is due now, once
      check <account>            Full check now, regardless of schedule
      assist <account>           Assisted sign-in (opens a browser window), then a full check
                                 add --capture to save each page visited (for mapping a site)
      status                     Accounts, sign-in states, next windows
      history import <account|--all>
                                 Seed history from files already in the account's folder
      secrets set-token [name]   Store a token in the OS credential store (default: the Bitwarden
                                 access token; any other name is usable as keychain:<name>)
      secrets test <reference>   Check that a secret reference resolves (prints no secret)
      test-notify                Send a test message to every notification channel
      sites                      List supported sites
      setup-browser              Download the Chromium build used for sign-ins
      pdf-text <file>            Print a PDF's text as the parsers see it (for writing parsers)
      mail recent <from> [hours] Show recent emails from a sender (regex) in the configured mailbox
                                 (for writing emailCode patterns; default the last 72 hours)
      install | uninstall        Start / stop starting the agent at sign-in
      paths                      Show config and data locations
      sites --json               Supported sites with the options each takes (JSON)

    Setting up with an AI agent: these commands do what the Settings page does, with JSON output.
    Run `statement-downloader agent-guide` for instructions written for the agent.

    """ + AgentCommands.Usage;

Console.OutputEncoding = System.Text.Encoding.UTF8;
var argList = args.ToList();
string? configPath = null;
var ci = argList.IndexOf("--config");
if (ci >= 0 && ci + 1 < argList.Count) { configPath = argList[ci + 1]; argList.RemoveRange(ci, 2); }
string? dataPath = null;
var di = argList.IndexOf("--data");
if (di >= 0 && di + 1 < argList.Count) { dataPath = argList[di + 1]; argList.RemoveRange(di, 2); }
var command = argList.FirstOrDefault() ?? "help";
string Arg(int i) => argList.Count > i ? argList[i] : throw new ArgumentException($"Missing argument.\n\n{Usage}");

var paths = AppPaths.Default(configPath is null ? null : Path.GetFullPath(configPath));
if (dataPath is not null) paths = paths with { DataDir = Path.GetFullPath(dataPath) };

try
{
    if (AgentCommands.RunWithoutServices(paths, argList) is { } done) return done;

    switch (command)
    {
        case "help" or "--help" or "-h":
            Console.WriteLine(Usage);
            return 0;

        case "paths":
            Console.WriteLine($"Config: {paths.ConfigFile}\nData:   {paths.DataDir}");
            return 0;

        case "sites":
            foreach (var s in SiteRegistry.All(paths.SitesDir))
            {
                var r = (s as StatementDownloader.Sites.Recipes.RecipeSiteFetcher)?.Recipe;
                var verified = r?.Verified is { } v ? $"verified {v}" : "";
                Console.WriteLine($"{s.SiteId,-20} {s.DisplayName,-40} {verified,-20} {(r is null || r.Source == "built-in" ? "" : r.Source)}");
            }
            Console.WriteLine();
            Console.WriteLine($"Your own recipes go in {paths.SitesDir} (see docs/site-recipes.md).");
            return 0;

        case "pdf-text":
            Console.WriteLine(PdfText.Extract(Arg(1)));
            return 0;

        case "setup-browser":
            return Microsoft.Playwright.Program.Main(["install", "chromium"]);

        case "install":
            // An agent opened in the config folder finds how to set the app up.
            if (File.Exists(paths.ConfigFile))
                foreach (var file in AgentCommands.WriteGuide(paths, Path.GetDirectoryName(paths.ConfigFile)!)) Console.WriteLine($"Wrote {file}");
            return Install.Register(configPath);

        case "uninstall":
            return Install.Unregister();

        case "secrets" when argList.ElementAtOrDefault(1) == "set-token":
            {
                var store = TokenStores.ForCurrentOs();
                var name = argList.ElementAtOrDefault(2) ?? SecretProvider.BitwardenTokenName;
                Console.Write(name == SecretProvider.BitwardenTokenName
                    ? "Paste the Bitwarden machine-account access token, then press Enter: "
                    : $"Paste the value for '{name}', then press Enter: ");
                var token = ReadHidden();
                if (string.IsNullOrWhiteSpace(token)) { Console.Error.WriteLine("No token entered."); return 1; }
                store.Set(name, token.Trim());
                Console.WriteLine(name == SecretProvider.BitwardenTokenName
                    ? $"Saved in {store.Description}."
                    : $"Saved in {store.Description}. Reference it as keychain:{name}.");
                return 0;
            }
    }

    // Before any logger is made: console loggers keep the output they were created with.
    if (command == "serve" && argList.Contains("--hidden")) Install.RunHidden(paths.File("agent.log"));
    using var loggers = LoggerFactory.Create(b => b.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Information));
    using var services = AppServices.Build(paths, loggers);

    switch (command)
    {
        case "serve":
            {
                // A sign-in that was under way when the app last stopped didn't finish.
                foreach (var st in await services.Auth.GetAllAsync())
                    if (st.Status == AuthStatus.Assisting) await services.Auth.SaveAsync(st with { Status = AuthStatus.NeedsAssist });
                var app = WebUi.Build(services, []);
                Console.WriteLine($"Statement Downloader running at {services.Links.Dashboard}");
                await app.RunAsync();
                return 0;
            }

        case "test-notify":
            {
                var results = await services.Notify.SendTestAsync();
                if (results.Count == 0) Console.WriteLine("No notification channels are configured.");
                foreach (var (channel, error) in results) Console.WriteLine($"{channel}: {error ?? "sent"}");
                return results.All(r => r.Error is null) ? 0 : 2;
            }

        case "tick":
            foreach (var r in await services.Ticks.TickAsync()) Console.WriteLine($"{r.AccountId}: {r.Summary}");
            return 0;

        case "check" or "assist":
            {
                var account = services.Account(Arg(1));
                if (command == "assist") Console.WriteLine("A browser window will open. Finish signing in there (code, CAPTCHA, \"remember me\"); this waits up to 10 minutes.");
                var capture = argList.Contains("--capture");
                var r = await services.Ticks.RunNowAsync(account, interactive: command == "assist", capture: capture);
                StatementDownloader.Sites.BrowserSession.CloseKeptWindows();
                if (capture) Console.WriteLine($"Page snapshots: {services.Paths.DiagnosticsDir(account.Id)}");
                Console.WriteLine($"{r.AccountId}: {r.Summary}");
                return r.Auth is AuthStatus.Healthy or AuthStatus.ExpiringSoon ? 0 : 2;
            }

        case "status":
            {
                foreach (var st in await services.Ticks.GetStatusAsync())
                {
                    var window = st.Estimate is { } e ? $"{e.WindowStart:MM-dd}..{e.WindowEnd:MM-dd} (every ~{e.IntervalDays}d ±{e.SpreadDays})" : "learning";
                    Console.WriteLine($"{st.Account.Id,-22} sign-in: {st.Auth.Status,-13} last: {st.LastDocument?.DocumentDate.ToString("yyyy-MM-dd") ?? "-",-10} window: {window}");
                    Console.WriteLine($"{"",-22} now: {st.Decision.Phase} — {st.Decision.Reason}");
                    foreach (var i in st.OpenIssues) Console.WriteLine($"{"",-22} ! {i.Title}");
                }
                return 0;
            }

        case "history" when argList.ElementAtOrDefault(1) == "import":
            {
                var target = Arg(2);
                var accounts = target == "--all" ? services.Config.Accounts : [services.Account(target)];
                foreach (var a in accounts)
                {
                    var (added, skipped) = await HistoryImport.RunAsync(services, a);
                    Console.WriteLine($"{a.Id}: imported {added}, already known {skipped}");
                }
                return 0;
            }

        case "mail" when argList.ElementAtOrDefault(1) == "recent":
            {
                if (services.Config.Mailbox is not { } mb) { Console.Error.WriteLine("No mailbox: is set in the config."); return 1; }
                var hours = argList.Count > 3 ? double.Parse(argList[3], System.Globalization.CultureInfo.InvariantCulture) : 72;
                var mail = await new Mailbox(mb, services.Secrets).RecentAsync(Arg(2), DateTimeOffset.Now.AddHours(-hours), 5);
                if (mail.Count == 0) Console.WriteLine("No matching emails.");
                foreach (var m in mail)
                    Console.WriteLine($"{m.Date:yyyy-MM-dd HH:mm} | {m.From} | {m.Subject}\n  {(m.Text.Length > 700 ? m.Text[..700] + "…" : m.Text)}\n");
                return 0;
            }

        case "secrets" when argList.ElementAtOrDefault(1) == "test":
            {
                var reference = Arg(2);
                try
                {
                    var c = await services.Secrets.GetCredentialAsync(reference);
                    Console.WriteLine($"OK: login for user '{c.Username}' (password {c.Password.Length} characters).");
                }
                catch (InvalidDataException)
                {
                    var v = await services.Secrets.GetValueAsync(reference);
                    Console.WriteLine($"OK: value of {v.Length} characters.");
                }
                return 0;
            }

        default:
            if (await AgentCommands.RunAsync(services, argList) is { } code) return code;
            Console.Error.WriteLine($"Unknown command '{command}'.\n\n{Usage}");
            return 1;
    }
}
catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException or ArgumentException or SettingsProblem)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

static string ReadHidden()
{
    if (Console.IsInputRedirected) return Console.ReadLine() ?? "";
    var sb = new System.Text.StringBuilder();
    while (true)
    {
        var key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter) { Console.WriteLine(); return sb.ToString(); }
        if (key.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Length--; continue; }
        if (!char.IsControl(key.KeyChar)) sb.Append(key.KeyChar);
    }
}
