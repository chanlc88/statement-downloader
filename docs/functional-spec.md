# Statement Downloader — Functional Specification

Status: initial draft · Last updated: 2026-11-19

## 1. Purpose

Statement Downloader signs in to the websites you have accounts with (utilities,
vendors, banks, card issuers), downloads new bills, invoices and statements as
PDFs, files them in the folder you choose, and tells you clearly what happened —
especially when it needs you.

It runs on your own computer (Windows or macOS). Your logins stay in your password
manager; the app reads them only at the moment it signs in.

### Goals

1. **Configurable accounts** — any number of sites and accounts, each with its own
   destination folder and file-name pattern.
2. **Clear notifications** — every message says what happened, to which account,
   and what (if anything) you need to do.
3. **Smart scheduling** — check each account around the time its next document is
   expected, learned from the dates on past documents, instead of polling every site
   every day.
4. **Human-assisted sign-in** — when a site needs a verification code, a CAPTCHA, or
   a fresh "remember this device", the app asks you, opens the site for you, and
   finishes the job as soon as you're done.
5. **Actions after download** — optionally hand each new document to something else:
   call a webhook, or run a script (for example one that records it as an expense in
   your accounting system).

### Non-goals

- Bypassing CAPTCHAs, verification codes or any other bot protection. The app always
  hands those to a person.
- Paying bills.
- Running in the cloud. It is a local, per-user app.

## 2. Concepts

| Term | Meaning |
|---|---|
| **Site** | A website the app knows how to sign in to and download from (e.g. `dekalb-water`). Each site is described by a *site recipe* — a YAML file ([format](site-recipes.md)) — or, for unusual sites, implemented in code. You can add your own recipes without rebuilding the app. |
| **Account** | One of *your* accounts on a site. One site can have several accounts (e.g. two Georgia Power accounts). Configured in your config file. |
| **Document** | A downloaded bill, invoice or statement: a PDF plus what was read from it (document date, statement number, amount). |
| **Kind** | `bill`, `invoice` or `statement` — used in file names and notification wording. |
| **Cycle** | How often an account produces a new document, and how much the date varies. Learned from past document dates. |
| **Window** | The date range when the next document is expected. |
| **Issue** | Something waiting on you (sign-in help, wrong password, overdue document). Open until resolved. |
| **Action** | Something done with a new document after it's filed (e.g. `webhook`, `run-command`). |

## 3. Configuration

One YAML file per user per machine, outside the repository:

- Windows: `%APPDATA%\statement-downloader\config.yaml`
- macOS: `~/Library/Application Support/statement-downloader/config.yaml`
- Override with `--config <path>`.

See [`config.sample.yaml`](../config.sample.yaml) for the full annotated example.

### 3.1 Per-machine settings

- `documentsRoot` — the folder account folders are relative to. It differs per
  machine (e.g. `G:/My Drive` on Windows, `~/Library/CloudStorage/GoogleDrive-…/My Drive`
  on macOS) so the same account list works on both.
- `ui.port` — port for the local web UI (default 5187, bound to `localhost` only).
- `mailbox` (optional) — where sites email one-time sign-in codes, for recipes with
  `signIn.emailCode`. Read over IMAP, read-only:

  ```yaml
  mailbox:
    host: imap.gmail.com        # the default; port 993 (TLS)
    username: me@gmail.com
    password: bitwarden:<id>    # for Gmail, an app password (myaccount.google.com/apppasswords)
  ```

  The app password opens the whole mailbox; a mailbox that only receives sign-in codes
  (e.g. by a forwarding filter) limits that. `statement-downloader mail recent <sender>`
  shows recent emails from a sender, for writing `emailCode` patterns.

### 3.2 Changing where documents are saved

The web UI's **Settings** page lists accounts by group (with a filter box) and edits
`documentsRoot` and each account's `group`, `tags`, `folder` and `fileName`, showing where the latest document would be saved as you type. Saving
rewrites only those lines of `config.yaml` (comments and layout are kept, the previous
file is kept as `config.yaml.bak`), validates the result first, and applies it to the
running agent without a restart. Documents already saved are not moved.

### 3.3 Accounts

| Field | Required | Description |
|---|---|---|
| `id` | yes | Short unique id, used in commands, URLs and notifications. |
| `name` | no | Display name. Defaults to `id`. |
| `group` | no | The section the account is listed under in the dashboard and Settings, e.g. the business it belongs to. Accounts without one are listed under "Other". |
| `tags` | no | Labels for filtering across groups, e.g. `[utility, gas]`. Stored lower-case. The dashboard shows a chip per tag; selecting chips shows accounts with any of them. |
| `site` | yes | Which site fetcher to use. `statement-downloader sites` lists them. |
| `kind` | no | `bill` (default), `invoice` or `statement`. |
| `credential` | yes | Secret reference for the login (see §6). |
| `siteOptions` | no | Site-specific settings, e.g. the account number when one login covers several accounts. |
| `folder` | yes | Destination folder, relative to `documentsRoot` — no drive letter, leading slash or `..`; files can never be saved outside `documentsRoot`. Tokens: `{year}`, `{month}`, `{account}`, `{kind}`. |
| `fileName` | yes | File name pattern. Tokens: `{date:FORMAT}`, `{statement}`, `{kind}`, `{account}`. Must contain a `{date:…}` token so history can be read back from file names. |
| `schedule` | no | See §4. Default `auto`. |
| `review` | no | `true` = new documents wait in the review list before actions run. Default `false`. |
| `actions` | no | List of actions to run on each new document (§5). |
| `enabled` | no | Default `true`. |

## 4. Scheduling

### 4.1 How the app runs

The app runs as a per-user background agent started at sign-in (Task Scheduler on
Windows, a LaunchAgent on macOS). It wakes every hour, decides which accounts are
due, and runs those. If the computer was asleep, the next wake-up catches up. It can
also be run once from the command line (`statement-downloader tick`), e.g. from any
other scheduler.

### 4.2 Schedule modes

| Mode | Config | Behaviour |
|---|---|---|
| **auto** (default) | `schedule: auto` (or omit) | Learns the cycle from past document dates (§4.3). Until it has 3 documents, checks once a day. |
| **window** | `schedule: { dayOfMonth: "15-22" }` | Checks only between those days of the month. Wraps across month end (e.g. `"28-4"`). |
| **interval** | `schedule: { every: 7d }` | Checks every N days/hours, for sites with no regular cycle. |

In every mode the account can also be run on demand from the UI or CLI.

### 4.3 Learning the cycle (auto mode)

From the sorted document dates of an account:

1. **Interval** — the median number of days between consecutive documents, using at
   most the last 12 intervals so recent behaviour counts most.
2. **Spread** — the largest difference between any actual interval and the median,
   rounded up, then clamped to between 2 and 7 days.
3. **Posting delay** — the median number of days between a document's date and the
   day the app first found it online (only for documents the app downloaded itself;
   1 day until known).
4. **Next expected date** = last document date + interval + posting delay.
5. **Window** = next expected date ± spread.

History is seeded when an account is added: the app reads the dates of PDFs already
in the account's folder from their file names, so an account with a year of bills
on disk has a good estimate from day one.

The UI shows the learned window for each account; when a fixed `dayOfMonth` window
is also configured, it shows both so disagreements are visible.

### 4.4 What happens when

| Phase | When | What the app does |
|---|---|---|
| Quiet | Before the pre-flight | Nothing (optionally a keep-alive sign-in every `keepAliveDays`). |
| **Pre-flight sign-in** | `preflightDays` (default 3) before the window opens | Signs in only. If it needs you, you find out days before the document arrives, not on the day. Done once per window. |
| **In window** | Window start → window end | Full check `checksPerDay` times (default 2) until a new document is found. |
| **Overdue** | Window end → + `overdueGraceDays` (default 7) | Sends one *overdue* notice, keeps checking once a day. |
| **Needs attention** | After the grace period | Raises an issue ("no new document for N days — check the site or the schedule") and drops to a weekly check. |
| Found | A new document arrives | The estimate moves to the next cycle; back to quiet. |

## 5. Documents and actions

### 5.1 Filing

For each document a site fetcher finds:

1. The target path is built from `folder` and `fileName`.
2. If a file with the same content is already there, it's skipped. If a *different*
   file has that name, the new one gets a ` (2)` suffix and an issue is raised.
3. The document is recorded in the app's history (date, statement number, amount,
   path, SHA-256, when it was first seen).

Site fetchers are given the statement numbers and dates already on record, so they
only download what's new.

### 5.2 Review (optional)

With `review: true`, new documents appear in the **Review** list in the UI with the
values read from them. Approve runs the actions; Reject leaves the file in place
and runs nothing.

### 5.3 Actions

Each action runs once per document. Its result is stored with the document so it is
never repeated, and failures are retried on the next run and reported. Actions are
remembered by identity, not position: an optional `id` setting (letters, digits, `-`, `_`),
or else the action's type (`webhook`, `webhook#2` for a second one). Give actions an `id`
when an account has two of the same type you may reorder. An action added to an account
later applies to documents found from then on; it isn't replayed over older ones.
Approving a document on the Review page runs its actions under the same lock as checks,
so an action never runs twice at once.

| Type | What it does |
|---|---|
| `webhook` | POSTs the document details (JSON) to a URL. Settings: `url`, optional `authorizationSecret`. |
| `run-command` | Runs a program. Settings: `command`, optional `args`, `workingDirectory`, `timeoutMinutes`. The first line it prints is shown as the result; a non-zero exit code is a failure, retried on the next run. |

`args` can use these tokens:

| Token | Value |
|---|---|
| `{path}`, `{fileName}` | The saved PDF's full path, and just its name |
| `{date}`, `{date:FORMAT}` | The document date as `yyyy-MM-dd`, or in any .NET date format |
| `{date-1M:FORMAT}`, `{date+7d:FORMAT}` | The date shifted by months (`M`) or days (`d`) — e.g. `{date-1M:MMMM yyyy}` for a bill issued the month after the usage it covers |
| `{year}`, `{month}` | From the document date |
| `{statement}`, `{amount}` | The statement or invoice number and the amount read from the PDF. If the document has none, the command isn't run and the action fails, so a missing amount can never be passed on as blank or zero. |
| `{vendorAccount}` | The account number read from the PDF (blank if none) |
| `{account}`, `{kind}` | The account id, and `bill` / `invoice` / `statement` |

The same details are in `SD_*` environment variables (`SD_PATH`, `SD_FILE_NAME`,
`SD_DATE`, `SD_STATEMENT`, `SD_AMOUNT`, `SD_VENDOR_ACCOUNT`, `SD_ACCOUNT_ID`, …).
A script that records documents somewhere should check for an existing record first and
treat "already there" as success, so a retry never creates a duplicate.

## 6. Credentials

- Logins are stored in **Bitwarden Secrets Manager**. A `credential` reference looks
  like `bitwarden:<secret-id>`. The secret's value is either JSON
  `{"username": "...", "password": "..."}`, or the password with the username on the
  first line of the secret's note.
- The app authenticates to Bitwarden with a machine-account access token kept in the
  operating system's credential store (Windows Credential Manager / macOS Keychain),
  set once with `statement-downloader secrets set-token`.
- For development: `env:<VARIABLE>` reads the same JSON from an environment variable.
- The app never writes logins to disk, logs or notifications.

## 7. Sign-in handling

### 7.1 Remembered sign-ins

Each account has its own browser profile on the machine, so cookies — including a
site's "remember this device" — persist between runs exactly as in a normal browser.
The app reads the expiry of the site's sign-in cookies to know how long the sign-in
is remembered.

### 7.2 Sign-in states

| State | Meaning |
|---|---|
| Unknown | Never signed in on this machine. |
| Healthy | Last sign-in worked. |
| Expiring soon | The remembered sign-in lapses within `expiryWarningDays` (default 5). |
| Needs you | Verification code, CAPTCHA, or remembered sign-in expired. |
| Signing in | An assisted sign-in window is open. |
| Broken | Wrong password, site changed, or site down — signing in again won't fix it. |

### 7.3 Failure reasons

Each failed sign-in is classified: `verification-code`, `captcha`,
`session-expired`, `password-rejected`, `site-changed`, `site-unavailable`,
`credential-unavailable` (the login couldn't be read from the password manager),
`assist-abandoned`. The
reason decides the state, the notification wording, and the suggested fix. On every
failure the app saves a screenshot and the page HTML to the account's diagnostics
folder (never the password) so a changed site can be fixed quickly.

### 7.4 Assisted sign-in

1. You choose **Sign in now** (from a notification link or the UI).
2. The app opens a visible browser window with that account's profile and fills in
   the username and password.
3. You enter the code / solve the CAPTCHA / tick "remember me". The app waits (up to
   10 minutes) until it sees the signed-in page.
4. It records the sign-in and how long it's remembered, resolves the issue, and
   **immediately runs the check that was waiting**, catching up on anything missed.

An assisted sign-in must happen on the machine whose profile is used. While
travelling, either wait (documents keep), connect to that machine remotely, or sign
in on the other machine, which keeps its own profile.

### 7.5 History

Every sign-in attempt is recorded: account, time, machine, result, reason, whether a
person helped, and remembered-until. The UI shows each account's history and when it
is next likely to need you.

## 8. Notifications

### 8.1 Levels

| Level | Examples | Delivery |
|---|---|---|
| **Action needed** | Sign-in help needed; password rejected; site changed; document overdue past grace; file-name clash; action failed | Immediately, with a link. Reminder every `reminderHours` (default 24) while open. |
| **Heads-up** | Sign-in expiring soon; document overdue (within grace) | Immediately. |
| **Info** | New document filed (kind, account, date, amount); action completed ("recorded as expense 9974"); issue resolved | Immediately or in a daily digest (`digest: true`). |

Every message states **what happened**, **which account**, and **what to do**.
Example: *"DeKalb Water needs you to sign in — the site asked for a verification
code. Open Statement Downloader → Sign in now. The waiting bill will be fetched right
after."*

### 8.2 Channels

| Channel | Use |
|---|---|
| `ntfy` | Phone/desktop push via ntfy.sh or a self-hosted ntfy server. |
| `email` | SMTP. |
| `log` | Written to the app log only (always on). |

Each level is routed to one or more channels (e.g. action-needed → phone, info →
email digest). Quiet hours hold pushes until they end.

### 8.3 Issues

Action-needed and heads-up notifications open an **issue** keyed to its cause (e.g.
the account's sign-in). While it is open the same cause doesn't produce new
notifications — only the scheduled reminder. When the cause clears, the issue is
resolved and an info notification says so. The UI lists open and recent issues.

## 9. User interface

A local web UI at `http://localhost:5187` (localhost only):

- **Dashboard** — a summary first (accounts that need you, documents waiting for review,
  new documents this week, with the accounts needing attention listed on top), then one row
  per account: its status in plain words, when it was last checked, the latest document
  (*Open* shows the PDF), and the one action it needs now — *Sign in*, *Review* or
  *Check now*. Everything else (*Sign in now*, open the latest document, show it in its
  folder, details) is in the row's ⋯ menu.
- **Review** — documents waiting for approval, each with *Open PDF*.
- **Issues** — *Needs you* (each issue with a short reason, the button that fixes its cause
  — *Sign in*, *Check now*, *Open PDF* — and any automation log collapsed under *Technical
  details*), *Worth knowing* (heads-ups), and *Activity*: every notification, including ones
  held for quiet hours or the daily summary, with where it went or when it will be sent.
- **Account** — status, when it was last checked, how it signs in; when its documents come
  and how SD checks for them, in words; documents by year (the newest open, each with *Open*
  and *Show in folder*); recent sign-ins.

## 10. Command line

| Command | Purpose |
|---|---|
| `statement-downloader serve` | Run the background agent and the web UI (what the OS starts at sign-in). |
| `statement-downloader tick` | Run whatever is due now, once, then exit. |
| `statement-downloader check <account>` | Full check now, regardless of schedule. |
| `statement-downloader assist <account>` | Assisted sign-in, then a full check. |
| `statement-downloader status` | Print accounts, states, next windows. |
| `statement-downloader history import <account>` | Seed history from files already in the account's folder. |
| `statement-downloader secrets set-token` | Store the Bitwarden access token in the OS credential store. |
| `statement-downloader sites` | List supported sites (built-in and your own recipes) and where your recipes go. |
| `statement-downloader install` / `uninstall` | Register / remove the agent at sign-in (Task Scheduler / LaunchAgent). |
| `statement-downloader setup-browser` | Download the Chromium build Playwright uses. |
| `statement-downloader secrets test <reference>` | Check a secret reference resolves (prints no secret). |
| `statement-downloader pdf-text <file>` | Print a PDF's text as recipes see it (for writing `parse` patterns). |
| `statement-downloader paths` | Show the config and data locations. |

All commands accept `--config <path>` and `--data <folder>` (use a separate data folder for testing).

## 11. Supported sites (initial)

| Site id | Site | Notes |
|---|---|---|
| `scana-energy` | SCANA Energy, GA natural gas (account.scanaenergy.com) | Email/password, no CAPTCHA. One login can hold several billing accounts (a new one may open each season); the active one is read unless `siteOptions.billingAccount` is set. PDFs download by clicking the invoice number. |
| `dekalb-water` | DeKalb County, GA water & sewer (myaccount.dekalbcountyga.gov) | Username/password form, no CAPTCHA. Bills come from Bills & Payment → My Bills (about two years listed); each PDF downloads directly. Session cookies only, so every run signs in. |

Each is a recipe in [`sites/`](../sites/). Planned: Georgia Power, Republic Services.

## 12. Responsible use

Automating access may be restricted by some sites' terms of service. You are
responsible for checking that your use complies. The app never bypasses
verification or bot-protection measures; it asks a person.
