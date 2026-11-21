# Site recipes

Each `*.yaml` file here teaches Statement Downloader one website. They are built into the
app, and each file's `tests:` samples run with `dotnet test`.

| Recipe | Site | Last verified |
|---|---|---|
| [chase](chase.yaml) | Chase bank and credit card statements (attended: asks you each time) | 2026-11-20 |
| [georgia-power](georgia-power.yaml) | Georgia Power, GA electric (you sign in in your browser each time) | 2026-11-20 |
| [dekalb-water](dekalb-water.yaml) | DeKalb County, GA water & sewer | 2026-11-20 |
| [republic-services](republic-services.yaml) | Republic Services, waste & recycling (attended: asks you each time) | 2026-11-20 |
| [scana-energy](scana-energy.yaml) | SCANA Energy, GA natural gas | 2026-11-20 |

Format and how to write one: [docs/site-recipes.md](../docs/site-recipes.md).
Contributions welcome — please use made-up numbers in `tests:` and never attach real bills.
