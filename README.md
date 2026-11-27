# Statement Downloader

Automatically download bills, invoices and statements from the websites you have
accounts with, file them in the folders you choose, and get clear notifications —
especially when a site needs you to sign in.

- **Runs on your computer** (Windows or macOS). Logins stay in Bitwarden Secrets Manager.
- **Learns each account's billing cycle** from the dates on past documents and checks
  around the expected date instead of every day.
- **Signs in ahead of time**: a few days before a document is due it checks the sign-in
  still works, so a verification code or expired "remember me" is caught early.
- **Asks for help instead of bypassing protections**: when a site wants a code or a
  CAPTCHA, it notifies you, opens the site with your login filled in, and carries on
  as soon as you're done.
- **Optional actions** on each new document: call a webhook, or
  [run your own script](docs/functional-spec.md#53-actions) — for example one that
  records the bill in your accounting system.

> Status: early. Eight site recipes so far (American Express, Bank of America and Chase statements; DeKalb County water,
> Georgia Power and SCANA Energy gas in Georgia; Republic Services waste; and Westfield Insurance bills), verified against the live sites. See [docs/](docs/) for the spec, architecture and recipe format.

## Quick start

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```sh
git clone https://github.com/chanlc88/statement-downloader
cd statement-downloader
dotnet run --project src/StatementDownloader.App -- setup-browser   # downloads Chromium for Playwright
dotnet run --project src/StatementDownloader.App -- paths           # shows where config.yaml goes
```

1. Copy [`config.sample.yaml`](config.sample.yaml) to the config path shown and edit it:
   your documents root, accounts, folders and notification channels.
2. Put each site login in Bitwarden Secrets Manager as JSON
   `{"username": "...", "password": "..."}` and reference it as `bitwarden:<secret-id>`.
   Create a machine account with read access to that project, then store its token:
   ```sh
   statement-downloader secrets set-token
   statement-downloader secrets test bitwarden:<secret-id>
   ```
3. Seed history from the PDFs you already have, so the billing cycle is known on day one:
   ```sh
   statement-downloader history import --all
   statement-downloader status
   ```
4. Sign in once with help (opens a browser window), then start the agent at sign-in:
   ```sh
   statement-downloader assist <account-id>
   statement-downloader install
   ```
5. Open <http://localhost:5187> for the dashboard, review list and issues. Accounts can have a **group** (dashboard sections) and **tags** (filter chips). **Settings** adds accounts and changes groups, tags and where each account's documents are saved (base folder, folder and file name, with a live preview); it edits `config.yaml` in place, keeping your comments.

## Commands

| Command | |
|---|---|
| `serve` | Background agent + web UI |
| `tick` | Run whatever is due, once |
| `check <account>` / `assist <account>` | Check now / assisted sign-in then check (`--capture` saves each page, for mapping a new site) |
| `status` | Sign-in states and learned windows |
| `history import <account\|--all>` | Seed history from existing files |
| `secrets set-token [name]` / `secrets test <ref>` | Store the Bitwarden token (or any named token, used as `keychain:<name>`) / check a reference |
| `sites` | Supported sites and where your own recipes go |
| `install` / `uninstall` | Start the agent at sign-in |
| `test-notify` | Send a test message to every notification channel |
| `pdf-text <file>` | Show PDF text as parsers see it |
| `accounts list\|add\|set\|preview`, `settings show\|set\|folders\|check\|init` | The Settings page on the command line, with JSON output (`--dry-run` checks a change without saving) |
| `agent-guide [--write]` | Instructions for an AI agent setting the app up |

## Setting up with an AI agent

An AI agent that can run commands on your computer (Claude Code, Codex and similar) can set up your accounts,
folders and notification emails for you. Open it in the config folder (`statement-downloader paths`), where
`install` and `settings init` leave an `AGENTS.md` (and a `CLAUDE.md` pointing to it), or ask it to run
`statement-downloader agent-guide`. The guide is [docs/agent-guide.md](docs/agent-guide.md). The agent only
handles references to your logins, never the passwords: you store logins in Bitwarden, run `secrets set-token`
yourself, and finish each first sign-in (`assist`) yourself.

## Adding a site

Most sites are a short YAML **site recipe**: sign-in selectors, the clicks to reach the
list of bills, which columns hold the date and amount, and regexes to read the PDF. Put
your own in a `sites` folder next to your config (no rebuild needed), or contribute one to
[`sites/`](sites/) with a pull request so others don't have to map the same site again.
See [docs/site-recipes.md](docs/site-recipes.md) and the
[DeKalb recipe](sites/dekalb-water.yaml). Never commit real bills or account numbers.

## Responsible use

Automated access may be restricted by some sites' terms of service; you are responsible
for checking that your use complies. This tool never bypasses CAPTCHAs, verification
codes or other bot protection — it hands them to you.

## License

[MIT](LICENSE)
