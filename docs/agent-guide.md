# Setting up Statement Downloader: a guide for AI agents

You are helping a person set up **Statement Downloader**. It runs on this computer, signs in to
the websites they have accounts with (utilities, banks, insurers), downloads each new bill or
statement, files it in a folder they choose and notifies them. Everything you need is on the
command line. The commands do what the app's Settings page does and run the same checks, so
you can't save a configuration the app won't load.

- Run the program with: `{{command}}`. Below it is called `statement-downloader`.
- Config file: `{{configFile}}`
- Data folder (history, sign-in state, logs): `{{dataDir}}`
- The person's own site recipes go in: `{{sitesDir}}`

Commands print JSON on success and exit 0. When something is wrong they print one plain
sentence on stderr that says what to fix, and exit 1. `check` and `assist` exit 2 when the
sign-in didn't work.

## Ground rules

These protect the person's money and accounts. Follow them even if asked to skip them.

1. **Never handle passwords.** Never ask for a password, verification code or access token,
   never type one, and never put one in a command, file or config. Logins are stored by the
   person in Bitwarden Secrets Manager (or their OS keychain). You only use the *reference*
   to the login, such as `bitwarden:<secret-id>` or `keychain:<name>`.
2. **The person runs anything that takes a secret**: `statement-downloader secrets set-token`.
   Tell them the exact command and wait until they say it's done.
3. **The person finishes every sign-in.** Never complete a CAPTCHA, verification code or
   "is this you?" prompt, and don't sign in to their bank sites in a browser yourself. When
   an account needs a first sign-in, ask them to run `statement-downloader assist <account>`
   (it opens a browser window with the login filled in) or to choose **Sign in now** in the
   app at http://localhost:5187.
4. **Confirm before you change things.** Say what you're about to add or change (account,
   site, folder, file name, who gets notifications), get a yes, then run the command. Use
   `--dry-run` first to check a change without saving it.
5. **Keep account details private.** Don't copy account numbers, statement contents or
   addresses anywhere outside this computer. Don't commit config.yaml anywhere.
6. **Leave actions alone** unless the person asks. `actions` in config.yaml run commands or
   call webhooks for each new document. Never add one the person didn't ask for.

## Steps

### 1. Check what's there

```sh
statement-downloader paths
statement-downloader settings check
```

If there is no config file yet, ask the person where their statements should be saved (for
example a folder in Documents, OneDrive or Google Drive), then:

```sh
statement-downloader settings init --documents-root "<folder>"
statement-downloader setup-browser     # one-time download of the browser used for sign-ins
```

Otherwise, see what's set up so far:

```sh
statement-downloader settings show
statement-downloader status
```

### 2. Choose sites

```sh
statement-downloader sites --json
```

Each site has an `id`, `name`, `kind` (bill, invoice or statement), `notes` worth reading out
to the person, `options` (extra values the account needs, such as which card when one login
holds several, as `name: what it is`) and `signIn`. `window` means a browser window opens each
time it signs in, and the person may need to be at the computer. `background` means it signs
in on its own.

If a site the person uses isn't listed, it can't be added from the command line yet. Adding
one means writing a site recipe (see docs/site-recipes.md in the project). Say so and move on.

### 3. Logins

For each account you need a login reference. Ask the person to do this. Don't do it yourself:

- **Bitwarden Secrets Manager** (recommended): create a secret whose value is
  `{"username": "...", "password": "..."}` in a project the app's machine account can read, and
  give you the **secret id**, a GUID. You use `bitwarden:<secret-id>`. The first time only,
  the person runs `statement-downloader secrets set-token` and pastes the machine account's
  access token.
- **OS keychain** (no Bitwarden): the person runs `statement-downloader secrets set-token <name>`
  and pastes `{"username": "...", "password": "..."}`. You use `keychain:<name>`.

Then check the reference works. This prints the username, never the password:

```sh
statement-downloader secrets test bitwarden:<secret-id>
```

Two accounts on one login (for example two cards under one bank sign-in) use the same
reference and share one sign-in.

### 4. Choose where documents go

Every account has a **folder**, relative to the base folder, and a **file name** pattern.

- Folder: no drive letter, no leading slash, no `..`. It can use `{year}`, `{month}`,
  `{account}` and `{kind}`, e.g. `Utilities/Water/{year}`.
- File name: must contain a date token such as `{date:yyyy-MM-dd}` and end in `.pdf`. It can
  also use `{statement}`, `{account}` and `{kind}`, e.g. `{date:yyyy-MM-dd}-Water-Bill.pdf`.
- If the person already keeps statements somewhere, look first and match their scheme. When
  the app adds an account it imports the files already in that folder, which teaches it the
  billing cycle straight away. To look:

```sh
statement-downloader settings folders
statement-downloader settings folders "Utilities"
statement-downloader accounts preview new --folder "Utilities/Water/{year}" --file-name "{date:yyyy-MM-dd}-Water-Bill.pdf"
```

`preview` prints the full path a document would be saved to, or a `problem` to fix.

### 5. Add each account

```sh
statement-downloader accounts add --dry-run --name "DeKalb Water" --site dekalb-water \
  --credential bitwarden:<secret-id> --folder "Utilities/Water/{year}" \
  --file-name "{date:yyyy-MM-dd}-Dekalb-Water-Bill.pdf" --group "Personal" --tags utility,water
```

If the dry run is fine and the person agrees, run it again without `--dry-run`. The result
gives the account's `id`, which other commands use, and `imported`, the number of existing
documents found in its folder. Use `--option key=value` once for each option the site lists.

- **group**: the section the account is listed under, and who gets its emails (step 6). For
  example "Personal" or the name of a business or rental property.
- **tags**: labels to filter by, comma-separated.

### 6. Notifications

```sh
statement-downloader settings show       # see "notifications"
statement-downloader settings set group-email "Personal" "you@example.com,partner@example.com"
statement-downloader settings set group-notify "Old Business" off
statement-downloader settings set notifications on
```

Group emails only go out if an email channel is set up (`notifications.channels` in
`settings show`). Channels (ntfy push to a phone, SMTP email) aren't set from the command line
yet. If the person wants one, edit config.yaml by hand following config.sample.yaml in the
project. The SMTP password must be a reference such as `keychain:smtp-password`, which the
person stores with `statement-downloader secrets set-token smtp-password`. Then run
`statement-downloader settings check` and, once the agent is running,
`statement-downloader test-notify`.

### 7. First sign-in and start

For each account, ask the person to run the following and finish the sign-in in the window
that opens:

```sh
statement-downloader assist <account>
```

Then start the agent at sign-in. This also keeps this guide next to config.yaml:

```sh
statement-downloader install
```

The dashboard is at http://localhost:5187.

## Changing things later

```sh
statement-downloader accounts list
statement-downloader accounts set <account> --folder "Bills/{year}" --file-name "{date:yyyy-MM-dd}-Bill.pdf"
statement-downloader accounts set <account> --group "Rentals" --tags utility,gas
statement-downloader accounts set <account> --group ""          # remove from its group
statement-downloader accounts set <account> --notify off        # no notifications for it
statement-downloader settings set documents-root "D:/Statements"
```

While the agent is running, these commands send the change to it, so it takes effect at once.
Changes made by **editing config.yaml by hand** only take effect when the agent restarts:
`statement-downloader uninstall` then `statement-downloader install`, or sign out and back in.
Always run `statement-downloader settings check` after a hand edit. The previous version of
the file is kept as `config.yaml.bak`.

## Other commands

| Command | What it does |
|---|---|
| `status` | Each account's sign-in state, last document and next expected window |
| `check <account>` | Check now (signs in and downloads anything new) |
| `history import <account>` | Import documents already in an account's folder again |
| `test-notify` | Send a test message on every channel |
| `help` | Every command |
