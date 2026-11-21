# Site recipes

Each `*.yaml` file here teaches Statement Downloader one website. They are built into the
app, and each file's `tests:` samples run with `dotnet test`.

| Recipe | Site | Last verified |
|---|---|---|
| [dekalb-water](dekalb-water.yaml) | DeKalb County, GA water & sewer | 2026-11-20 |
| [republic-services](republic-services.yaml) | Republic Services, waste & recycling (attended: asks you each time) | 2026-11-20 |
| [scana-energy](scana-energy.yaml) | SCANA Energy, GA natural gas | 2026-11-20 |

Format and how to write one: [docs/site-recipes.md](../docs/site-recipes.md).
Contributions welcome — please use made-up numbers in `tests:` and never attach real bills.
