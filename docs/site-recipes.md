# Site recipes

A **site recipe** is a YAML file that tells Statement Downloader how to get documents from
one website: how to sign in, where the list of bills or statements is, how to download each
one, and how to read its date, number and amount. Most sites need only a recipe — no code.

- Built-in recipes live in [`sites/`](../sites/) and ship inside the app.
- Your own recipes go in a `sites` folder next to your `config.yaml` (`statement-downloader sites`
  prints the exact path). A recipe there with the same `id` as a built-in one replaces it, so you
  can fix a site that changed before the next release.

An account uses a recipe by its id: `site: dekalb-water`.

## A complete example

[`sites/dekalb-water.yaml`](../sites/dekalb-water.yaml), abridged:

```yaml
id: dekalb-water
name: DeKalb County Water & Sewer (GA)
kind: bill
verified: 2026-11-20

signIn:
  url: https://myaccount.dekalbcountyga.gov/app/
  username: '#accessCode'
  password: '#password'
  submit: '#login_btn'
  signedIn: "#logoff, a:has-text('Logout')"

documents:
  steps:
    - click: "a[href='#tabs-BILLINQ']"
  list:
    frame: '#iframe-BILLINQ'
    rows: '#billTable tbody tr'
    wait: 45s
    date: { column: 2 }
    amount: { column: 3 }
    link:
      selector: "a[onclick*='eBillViewerServlet']"
      attribute: onclick
      pattern: "MM_openBrWindow\\('([^']+)'"
  download: fetch

parse:
  require: 'Water\s*&\s*Sewer'
  date: 'Bill Date:?\s*(\d{1,2}/\d{1,2}/\d{2,4})'
  statementNumber:
    - 'Account Number\s+Statement Number\s+\d{6,}\s+(\d{5,})'
    - 'Statement Number\s*(\d{5,})'
  amount: 'Current Charges\s*\$\s*([\d,]+\.\d{2})'

tests:
  documents:
    - text: "… Bill Date: 11/18/26 … Current Charges $73.21 …"
      expect: { date: 2026-11-18, statementNumber: '98765432', amount: 73.21 }
  rows:
    - cells: ['View Bill', 'Nov 18, 2026', '$73.21', 'Dec 09, 2026']
      link: "MM_openBrWindow('/app/eBillViewerServlet?key=abc123','bill')"
      expect: { date: 2026-11-18, amount: 73.21, url: '/app/eBillViewerServlet?key=abc123' }
```

## One login, several accounts

Some sites show several accounts behind one login and a picker to switch between them.
Declare an option and use it in steps that only run when it's set:

```yaml
options:
  billingAccount: Billing account number to read (default the active one).
documents:
  steps:
    - click: '[data-testid=billing-account-select] button'
      ifOption: billingAccount
    - click: "[role=option]:has-text('{billingAccount}')"
      ifOption: billingAccount
```

and in the config, per account:

```yaml
  - id: gas-old-account
    site: scana-energy
    siteOptions: { billingAccount: "1234567" }
```

See [`sites/scana-energy.yaml`](../sites/scana-energy.yaml).

Accounts with the same `site` and the same `credential` are treated as **one login**: they
share one browser profile (so one sign-in and one verification code cover them all), raise
one "needs you" notification between them, and **Sign in now** on any of them fetches all of
them. The dashboard and Settings show "same sign-in as …" on each.

## Reference

Selectors are [Playwright selectors](https://playwright.dev/docs/other-locators) — CSS plus
extras such as `:has-text('Log out')`. Durations look like `30s`, `2m`, `400d`. Regexes are
.NET regexes, matched case-insensitively; **group 1 is the value**. Write them in `'single
quotes'` so backslashes need no escaping.

### Top level

| Key | Required | Meaning |
|---|---|---|
| `id` | yes | Lowercase letters, digits and dashes. What accounts put in `site:`. |
| `name` | yes | Shown in the dashboard and notifications. |
| `kind` | | `bill` (default), `statement` or `invoice`. |
| `region`, `website`, `notes` | | For people reading the recipe. |
| `verified` | built-in: yes | Date (`yyyy-MM-dd`) the recipe last worked against the live site. |
| `options` | | Settings an account can give in its `siteOptions`, as `name: what it is`. Steps use them as `{name}`. |

### `signIn`

| Key | Required | Meaning |
|---|---|---|
| `url` | yes | `https://` page to open. Shows the sign-in form, or the signed-in page when the browser profile is remembered. |
| `username`, `password`, `submit` | yes | The form fields and the button. |
| `next` | | Button to click between username and password, for two-page sign-ins. |
| `signedIn` | yes | Something that exists only when signed in (a Log out link, the account menu). The app also treats a visible password field as "not signed in". |
| `rememberCookies` | | Names of the cookies that keep you signed in, for the "remembered until" date. Default: all persistent cookies for the site. |
| `yourBrowser` | | `true` for sites whose bot check refuses automated browsers even with you present ("I am human" boxes that won't accept a tick). The app opens a regular Chrome window (Edge when Chrome isn't installed) with its own profile; you pass the check and sign in, and the app connects to that window and does the rest. Implies `attended`. |
| `emailCode` | | For sites that email a one-time code: `from` (regex for the sender), `pattern` (regex, group 1 is the code, matched against the subject and text), and optionally `field` (the code field; default a field marked one-time-code or named otp/passcode/code), `submit` (default: press Enter) and `wait` (default `2m`). With a `mailbox` in the config, the app reads the newest matching email sent after the sign-in and enters the code; without one, or if no email comes, you enter it. |
| `unattended` | | With `yourBrowser`: `true` when the site's human check usually passes by itself in that window. Scheduled checks then open the window on their own, sign in once, download and close it. If the check doesn't pass or the sign-in fails, they stop and ask you to choose **Sign in now**; nothing is retried until you do. |
| `attended` | | `true` for sites that refuse unattended (background) browsers — they drop the connection or show a bot check every time. The app then never visits the site on its own: when a check is due it asks you to choose **Sign in now**, and does the sign-in and download in a visible window. |

Verification codes, CAPTCHAs, bot checks ("I am human", "Just a moment…") and "is this
you?" pages need no recipe settings: the app recognises them, notifies you and hands the
browser to you (assisted sign-in). It never tries to get past them itself. The app sends the
sign-in form once; when a human check is on it, it first waits a little for the check to pass by
itself (it often does in your own browser). If it doesn't, the app fills your username and
password and leaves the check and the sign-in click to you, and it never sends the form again,
so a failing sign-in can't loop.

### `documents`

| Key | Default | Meaning |
|---|---|---|
| `steps` | none | Done in order after signing in to reach the list. Each step is one of `click: <selector>`, `follow: <selector>` (activate a link that's in the page but hidden in a collapsed menu, as the site's own menu would — for in-app links), `goto: <url, may be relative>`, `waitFor: <selector>` (if it doesn't appear, the page is refreshed up to three times and checked again), with optional `frame: <iframe selector>`, `timeout` (default `30s`), `ifOption: <name>` (run only when the account sets that option) and `optional: true` (skip the step when its element doesn't turn up — e.g. click a panel's header only while it's collapsed). |
| `list.rows` | required | One element per document, usually `table tbody tr`. May use `{option}` placeholders, e.g. to keep one account's documents from a list that shows every account. |
| `list.frame` | | The iframe the list is in. |
| `list.wait` | `30s` | How long to wait for the first row. |
| `list.date` | required | Where the document date is in a row (below). Rows without a readable date are skipped. |
| `list.amount` | | Where the amount is, used when the PDF doesn't say. |
| `list.link.selector` | the row | Element in the row that leads to the PDF. |
| `list.link.attribute` | `href` | Attribute holding the URL. |
| `list.link.pattern` | | Regex to pull the URL out of the attribute (e.g. from an `onclick`). |
| `list.expand` | | Element in the row to click first when the link is in a details panel the row opens, or is covered until the row is opened. The link selector can reach into the panel with `xpath=following-sibling::tr[1]//a`. |
| `download` | `fetch` | `fetch`: request the URL with the browser's cookies (fast, works for direct PDF links). `click`: click the link and save the file the browser downloads — the link then needs no URL (script-driven buttons are fine). `popup`: click the link; the site opens the PDF in a new window (often a `blob:` address made by its own script), which is saved and closed. Use it when the site fetches PDFs with its own sign-in token, so a plain `fetch` gets "Unauthorized". |
| `firstRunLookback` | `400d` | With nothing on record yet, how far back to download. |

A **field** (`date`, `amount`) is either `{ column: N }` — the Nth cell of the row,
counting from 1 — or `{ selector: <inside the row> }`, plus an optional `pattern` to take
part of the text: `{ column: 1, pattern: 'Statement (\S+)' }`.

Only rows dated after the newest document on record are downloaded.

### `parse`

Read from the PDF's text, exactly as `statement-downloader pdf-text <file>` prints it
(all pages, whitespace collapsed to single spaces).

| Key | Required | Meaning |
|---|---|---|
| `require` | | Pattern(s) that must all match, so a notice or insert in the same list isn't filed as a bill. |
| `date` | yes | Document date. Common US formats are understood (`11/18/26`, `Nov 18, 2026`, `2026-11-18`…). |
| `dateFormats` | | Extra .NET date formats, e.g. `['dd.MM.yyyy']`. |
| `statementNumber` | | Statement or invoice number. Used to avoid duplicates and in action references, so worth having. |
| `amount` | | The amount to record — usually this period's charges rather than a total that includes past balances. |
| `accountNumber` | | Your account number with the company. |

Each can be one pattern or a list; the first that matches wins. A statement number equal
to the account number is discarded (it means a pattern matched the wrong label).

### `tests`

Samples the test suite checks on every build. **Use made-up numbers and names** — never
paste a real bill.

- `documents`: `text` (PDF text in `pdf-text` order) and `expect` with `date`,
  `statementNumber`, `amount`, `accountNumber`; `expect: null` means "not this site's
  document".
- `rows`: a row's `cells` (column texts; for fields read by `selector`, their texts in the
  order date, amount) and `link` (the link attribute), with the
  `date`, `amount` and `url` it should give.

## Writing a recipe

1. **Add the account** to your config with `site: my-site` and create `sites/my-site.yaml`
   next to your config. Start from the DeKalb recipe.
2. **Sign-in**: open the site in a normal browser, right-click the username field → Inspect,
   and note the selectors for the fields, the button and something that only appears when
   signed in.
3. **Run** `statement-downloader check my-account --capture --data <test folder>`. A separate
   data folder keeps your real history clean. `--capture` saves a screenshot and the HTML of
   the signed-in page and the list page under the data folder's `diagnostics`, so you can find
   the selectors for `steps` and `list`.
4. **Parse**: run `statement-downloader pdf-text <a downloaded pdf>` and write patterns
   against that text.
5. **Add tests** with made-up numbers, set `verified`, and run `dotnet test`.

To share it, open a pull request adding the file to [`sites/`](../sites/). Never include
screenshots, HTML or PDFs from your account in an issue or pull request without removing
names, addresses and numbers first.

## When a recipe isn't enough

Some sites need logic a recipe can't express — choosing among several accounts on one
login, documents only available as HTML, or multi-step date pickers. Those are written in
C# by deriving from `PlaywrightSiteFetcher` and adding the class to `SiteRegistry` (see
[architecture](architecture.md#5-site-fetchers)). If a feature would help many sites, prefer
adding it to the recipe format instead.

## Safety

A recipe decides where your username and password are typed, so:

- Recipes are **not downloaded at run time**. Built-in recipes are reviewed in pull requests
  and ship with each release; anything else is a file you put on your own computer.
- `signIn.url` must be `https://`.
- Read a recipe from someone else before using it, and check its `url` is the company's real
  site.
