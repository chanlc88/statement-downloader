# Statement Downloader — Architecture

Status: initial draft · Last updated: 2026-11-19 · See also [functional-spec.md](functional-spec.md)

## 1. Overview

A single cross-platform .NET 10 application. One process runs as a per-user
background agent with an hourly scheduler and a localhost web UI; the same
executable also offers one-shot CLI commands.

```
          ┌────────────────────── StatementDownloader.App ───────────────────────┐
 OS start │  CLI commands      Agent (hourly tick)        Web UI (localhost)     │
 at login │        └──────────────┬──────────┴─────────────────┘                 │
          └───────────────────────┼──────────────────────────────────────────────┘
                                  ▼
          ┌────────────────────── StatementDownloader.Core ──────────────────────┐
          │ Config · SchedulePlanner · CycleEstimator · AccountRunner            │
          │ DocumentFiler · Auth state · NotificationCenter · Stores (JSON)      │
          │ Interfaces: ISiteFetcher · ISecretProvider · IDocumentAction ·       │
          │             INotificationChannel                                     │
          └────────┬──────────────────┬───────────────────┬─────────────────────┘
                   ▼                  ▼                   ▼
          .Sites (Playwright,  .Secrets (Bitwarden   .Actions (ezpropmgr,
           PdfPig parsers)      SDK, OS keychain)     webhook, run-command)
```

Core has no dependency on Playwright, Bitwarden or any OS API; everything external
sits behind an interface, which keeps it testable and keeps the platform-specific
code small.

## 2. Projects

| Project | Responsibility | Key dependencies |
|---|---|---|
| `StatementDownloader.Core` | Domain model, config loading, scheduling, cycle learning, account runs, filing, auth state, notifications, JSON stores. | YamlDotNet |
| `StatementDownloader.Sites` | One `ISiteFetcher` per website, a Playwright base class (profiles, downloads, assisted sign-in, diagnostics), PDF text extraction and per-site parsers. | Microsoft.Playwright, PdfPig |
| `StatementDownloader.Secrets` | `ISecretProvider` for Bitwarden Secrets Manager and env vars; `ITokenStore` for Windows Credential Manager / macOS Keychain. | Bitwarden.Secrets.Sdk |
| `StatementDownloader.Actions` | `IDocumentAction` implementations: `ezpropmgr-expense`, `webhook`, `run-command`. Notification channels (`ntfy`, `email`). | — |
| `StatementDownloader.App` | Composition root: CLI parsing, hosted agent, minimal-API web UI, OS install. | ASP.NET Core |
| `StatementDownloader.Tests` | Unit tests for core logic and parsers. | xUnit |

## 3. Key flows

### 3.1 Tick

```
Agent timer (hourly) / `tick`
  └─ acquire run lock (dataDir/run.lock)
  └─ for each enabled account:
        decision = SchedulePlanner.Decide(account, schedule state, document history, auth state, now)
        None          → skip
        SignInOnly    → AccountRunner.Run(signInOnly)
        FullCheck     → AccountRunner.Run(full)
        Overdue/Stale → NotificationCenter.Raise(...) and maybe FullCheck
  └─ NotificationCenter.SendReminders(now); flush held notifications
```

### 3.2 Account run

```
AccountRunner.Run(account, mode, interactive)
  1. Auth pre-check: expired remembered sign-in → NeedsAssist (unattended runs stop here)
  2. credential = ISecretProvider.Get(account.credential)
  3. result = ISiteFetcher.Run(SiteContext{ credential, profileDir, tempDir,
                                            known documents, signInOnly, interactive })
  4. sign-in failed  → AuthTransitions.Failed, record AuthEvent, Raise issue "auth:<id>"
     sign-in worked  → AuthTransitions.Succeeded, record AuthEvent, Resolve "auth:<id>"
  5. for each new document: DocumentFiler.File → DocumentStore.Add
        review? → pending ; else → run actions, store results
  6. Notify: documents filed / action results / failures
```

### 3.3 Assisted sign-in

`POST /api/accounts/{id}/assist` (from the UI or a notification link) sets the state
to *Signing in* and runs `AccountRunner.Run(full, interactive: true)` in the
background. The Playwright base class launches a headed browser with the account's
persistent profile, fills the credentials, then polls the site's `IsSignedIn` check
every 2 seconds for up to 10 minutes while the person completes the challenge. On
success the run continues straight into the document check.

## 4. Scheduling model

`CycleEstimator.Estimate(dates, postingDelays, options)` returns
`{ IntervalDays, SpreadDays, PostingDelayDays, NextExpected, WindowStart, WindowEnd }`
or `null` with fewer than 3 dates. It's a pure function, unit-tested with real
billing histories.

`SchedulePlanner.Decide(...)` is also pure: given the account's schedule config,
the estimate, `ScheduleState` (last full check, last sign-in check, overdue notices
sent for which expected date) and `now`, it returns one decision. Because the
estimate is always derived from the latest document, finding a document
automatically moves the window to the next cycle — there is no "cycle complete"
flag to keep in sync.

## 5. Site fetchers

```csharp
public interface ISiteFetcher
{
    string SiteId { get; }
    string DisplayName { get; }
    Task<SiteResult> RunAsync(SiteContext context, CancellationToken ct);
}
```

`PlaywrightSiteFetcher` (abstract) implements the shared parts:

- **Persistent profile** per account: `dataDir/profiles/<account-id>` via
  `LaunchPersistentContextAsync`, headless unless interactive.
- **Sign-in skeleton**: `IsSignedInAsync` → if not, `FillAndSubmitSignInAsync` →
  `ClassifySignInFailureAsync` (verification code / CAPTCHA / password rejected /
  site changed). In interactive mode it waits for the person instead of failing on a
  challenge.
- **Remembered-until**: max expiry of the cookies the site names as its sign-in
  cookies (or of all persistent cookies for the site's domain).
- **Downloads** into a temp folder; the core files them.
- **Diagnostics**: on any failure, a screenshot and HTML snapshot to
  `dataDir/diagnostics/<account-id>/<timestamp>/`.

Concrete sites override only: sign-in URL and selectors, the signed-in check, how
to list documents, and how to parse a downloaded PDF (`IDocumentParser`).

## 6. Persistence

All state is JSON in the per-user data folder (Windows `%LOCALAPPDATA%\statement-downloader`,
macOS `~/Library/Application Support/statement-downloader`), written atomically
(temp file + rename):

| File | Contents |
|---|---|
| `documents.json` | Every filed document per account: date, statement #, amount, path, hash, first seen, action results, review state. |
| `auth-state.json` | Current sign-in state per account. |
| `auth-events.json` | Sign-in history. |
| `schedule-state.json` | Last checks and overdue notices per account. |
| `issues.json` | Open and resolved issues, notification history, held notifications. |
| `profiles/` | Browser profiles (cookies). Treat as sensitive. |
| `diagnostics/` | Failure screenshots and HTML. |

JSON files are adequate for one user with tens of accounts; the stores sit behind
interfaces so SQLite can replace them later.

## 7. Security

- No credentials in config, repo, logs, notifications or diagnostics. Credentials
  are fetched per run and held only in memory.
- The Bitwarden access token lives in the OS credential store; a machine account
  should be granted access only to the project holding these logins.
- The web UI binds to `127.0.0.1` only and requires a per-install random key
  (stored in the data folder, included in notification links) for state-changing
  requests.
- Browser profiles contain session cookies; they live in the per-user data folder
  and are never synced.

## 8. Platform specifics

| Concern | Windows | macOS |
|---|---|---|
| Start at sign-in | Task Scheduler task "At log on" running `serve` | `~/Library/LaunchAgents/…plist` with `RunAtLoad` |
| Token store | Credential Manager (`CredReadW`/`CredWriteW`) | Keychain (`security` CLI) |
| Data folder | `%LOCALAPPDATA%\statement-downloader` | `~/Library/Application Support/statement-downloader` |
| Browser | Playwright Chromium (`playwright install chromium`) | same |

## 9. Extending

- **New site**: add a class deriving from `PlaywrightSiteFetcher` (or implementing
  `ISiteFetcher`) plus an `IDocumentParser`, register it in `SiteRegistry`, add a
  parser test with a synthetic sample.
- **New action**: implement `IDocumentAction` with a unique `Type` and register it.
- **New notification channel**: implement `INotificationChannel`.

## 10. Open questions

1. ~~DeKalb Water post-sign-in navigation~~ — mapped: `#billTable` inside
   `iframe#iframe-BILLINQ`, PDFs from `/app/eBillViewerServlet`.
2. Cross-machine coordination when both a PC and a laptop run the agent — today each
   machine keeps its own state; server-side duplicate checks in actions prevent
   double-recording. A shared lock in the documents folder is a possible addition.
3. Daily digest timing and format.
