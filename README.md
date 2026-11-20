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
- **Optional actions** on each new document: record it as an expense in
  [ezpropmgr](docs/functional-spec.md#53-actions), call a webhook, or run a script.

> Status: early. One site (DeKalb County, GA water) is implemented and mapped against the
> live site. See [docs/](docs/) for the spec and architecture.

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
5. Open <http://localhost:5187> for the dashboard, review list and issues.

## Commands

| Command | |
|---|---|
| `serve` | Background agent + web UI |
| `tick` | Run whatever is due, once |
| `check <account>` / `assist <account>` | Check now / assisted sign-in then check (`--capture` saves each page, for mapping a new site) |
| `status` | Sign-in states and learned windows |
| `history import <account\|--all>` | Seed history from existing files |
| `secrets set-token [name]` / `secrets test <ref>` | Store the Bitwarden token (or any named token, used as `keychain:<name>`) / check a reference |
| `sites` | Supported sites |
| `install` / `uninstall` | Start the agent at sign-in |
| `test-notify` | Send a test message to every notification channel |
| `pdf-text <file>` | Show PDF text as parsers see it |

## Adding a site

Derive from `PlaywrightSiteFetcher` (sign-in URL, selectors, signed-in check, how to list
documents) and add an `IDocumentParser`; register it in `SiteRegistry`. Use
`statement-downloader pdf-text` on a sample to write the parser, and add a test with
synthetic text (never commit real bills). See [architecture](docs/architecture.md#9-extending).

## Responsible use

Automated access may be restricted by some sites' terms of service; you are responsible
for checking that your use complies. This tool never bypasses CAPTCHAs, verification
codes or other bot protection — it hands them to you.

## License

[MIT](LICENSE)
