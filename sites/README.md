# Site recipes

Each `*.yaml` file here teaches Statement Downloader one website. They are built into the
app, and each file's `tests:` samples run with `dotnet test`.

| Recipe | Site | Last verified |
|---|---|---|
| [american-express](american-express.yaml) | American Express personal card statements, several cards on one login (a Chrome window; a code on new devices) | 2026-11-27 |
| [bank-of-america](bank-of-america.yaml) | Bank of America bank and credit card statements (a Chrome window; a code on new devices) | 2026-11-25 |
| [capital-one](capital-one.yaml) | Capital One credit card statements, Discover cards included, several cards on one login (a Chrome window; a code on new devices) | 2026-11-27 |
| [chase](chase.yaml) | Chase bank and credit card statements (a visible window; a code on new devices) | 2026-11-20 |
| [georgia-power](georgia-power.yaml) | Georgia Power, GA electric (a Chrome window; a human check that usually passes by itself) | 2026-11-20 |
| [citi](citi.yaml) | Citi credit card statements, last 24 months, several cards on one login (a Chrome window; a code on new devices) | 2026-11-28 |
| [dekalb-water](dekalb-water.yaml) | DeKalb County, GA water & sewer | 2026-11-20 |
| [republic-services](republic-services.yaml) | Republic Services, waste & recycling (a Chrome window; a human check that usually passes by itself) | 2026-11-20 |
| [scana-energy](scana-energy.yaml) | SCANA Energy, GA natural gas | 2026-11-20 |
| [us-bank](us-bank.yaml) | U.S. Bank credit card statements (a Chrome window; a code on new devices) | 2026-11-28 |
| [westfield](westfield.yaml) | Westfield Insurance policy bills (a code on new devices) | 2026-11-24 |

Format and how to write one: [docs/site-recipes.md](../docs/site-recipes.md).
Contributions welcome — please use made-up numbers in `tests:` and never attach real bills.
