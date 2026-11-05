# CLAUDE.md

Reference implementation of an Azure Data Factory → .NET C# migration for a credit union on Corelation KeyStone.
**The PRD is the entry point: [docs/PRD.md](docs/PRD.md).** Work phase by phase; record progress in [docs/progress.md](docs/progress.md).

## Commands (PowerShell 7, Windows)

| Command | Purpose |
| --- | --- |
| `./build.ps1 up` | Start Db2, SQL Server, Azurite, WireMock, Aspire dashboard (generates `.env`) |
| `./build.ps1 build` | `dotnet build` with warnings as errors |
| `./build.ps1 test` | Run all tests (`dotnet test --solution AdfToDotnet.sln`) |
| `./build.ps1 run` | Start the Functions host (`func start`) |
| `./build.ps1 scan` | gitleaks secret scan |

## Rules

- Acceptance tests for a pipeline are written and failing **before** its implementation. A phase is done only when `dotnet test` passes with zero skipped acceptance tests.
- Ambiguity → write a short ADR in `docs/decisions/` (`NNNN-title.md`); do not guess silently.
- `ADF JSON in adf/` is the source of truth for behavior (retry policies, batch counts, timeouts).
- `CreditUnion.Batch.Core` has no Azure Functions dependency. Orchestrators contain no I/O, `DateTime.UtcNow` or random values; use `context.CurrentUtcDateTime` and injected `TimeProvider`.
- Money is `decimal`, never `double`; state the `MidpointRounding` explicitly.
- Timestamps in UTC; `businessDate` is a `DateOnly` in `America/Los_Angeles`.
- Never log secrets or full member/account/card numbers (mask to last 4). No SSNs, even synthetic.
- Nullable on, warnings as errors, no `TODO` in merged code. NuGet versions are pinned centrally in `Directory.Packages.props`; container image tags are pinned in `docker-compose.yml`.
- SQL, shell and container-mounted files use LF endings (`.gitattributes`).
- No secrets in the repo; container passwords are generated into the git-ignored `.env`.
