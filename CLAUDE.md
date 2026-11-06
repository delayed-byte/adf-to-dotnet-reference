# CLAUDE.md

Reference implementation of an Azure Data Factory → .NET C# migration for a credit union on Corelation KeyStone.
**The PRD is the entry point: [docs/PRD.md](docs/PRD.md).** Work phase by phase; record progress in [docs/progress.md](docs/progress.md).

## Commands (PowerShell 7, Windows)

| Command | Purpose |
| --- | --- |
| `./build.ps1 up` | Start Db2, SQL Server, Azurite, WireMock, Aspire dashboard (generates `.env`) |
| `./build.ps1 seed` | Deploy the Db2 (`db/keystone-db2`) and SQL Server (`db/reporting`) schemas with DbUp; rerunnable |
| `./build.ps1 build` | `dotnet build` with warnings as errors |
| `./build.ps1 test` | Run all tests except `Category=Performance` (Testcontainers starts Db2, SQL Server, Azurite; `CU_TESTS_USE_COMPOSE=1` reuses the `up` stack) |
| `./build.ps1 golden` | Regenerate `tests/TestData` inputs and golden outputs from the seeded generator (`-Scenario NAME` for one) |
| `./build.ps1 run` | Start the Functions host (`func start`) |
| `./build.ps1 scan` | gitleaks secret scan |

## Rules

- Acceptance tests for a pipeline are written and failing **before** its implementation. A phase is done only when `dotnet test` passes with zero skipped acceptance tests.
- Ambiguity → write a short ADR in `docs/decisions/` (`NNNN-title.md`); do not guess silently.
- `ADF JSON in adf/` is the source of truth for behavior (retry policies, batch counts, timeouts). Keep `AdfDefinitionValidationTests` green when editing it; conventions are in ADR 0004.
- Golden outputs in `tests/TestData` change only through the generator (`./build.ps1 golden`); a drift test fails on hand edits. Harness, formats and comparison rules: ADR 0006. Db2 value handling: ADR 0005.
- Database changes: add a new numbered script to `db/*/migrations` (never edit an applied one); procedures in `db/reporting/procedures` are `CREATE OR ALTER` and rerun on every deploy. Db2 scripts: one statement per `;`, no compound statements (ADR 0002).
- Each ADF pipeline is an orchestrator function named after it (`PipelineNames`), with the typed inputs in `CreditUnion.Batch.Core.Pipelines`; acceptance tests drive them through the in-process Durable host in `tests/CreditUnion.Batch.TestHarness/Orchestration` (ADR 0008). Don't change these contracts without updating ADR 0008 and the tests.
- ADF activity retry policies become a `RetryPolicy` on the `CallActivityAsync`, with `retry + 1` attempts and a fixed interval. Don't add a second retry layer inside the activity (Polly, driver retries). Fail activities throw `PipelineFailedException` (ADR 0008).
- `CreditUnion.Batch.Core` has no Azure Functions dependency. Orchestrators contain no I/O, `DateTime.UtcNow` or random values; use `context.CurrentUtcDateTime` and injected `TimeProvider`.
- Money is `decimal`, never `double`; state the `MidpointRounding` explicitly.
- Timestamps the batch creates are UTC (`*Utc` columns); KeyStone source timestamps (`*Ts`) are copied verbatim, never converted (ADR 0005). `businessDate` is a `DateOnly` in `America/Los_Angeles`.
- Never log secrets or full member/account/card numbers (mask to last 4). No SSNs, even synthetic.
- Nullable on, warnings as errors, no `TODO` in merged code. NuGet versions are pinned centrally in `Directory.Packages.props`; container image tags are pinned in `docker-compose.yml`.
- SQL, shell and container-mounted files use LF endings (`.gitattributes`).
- No secrets in the repo; container passwords are generated into the git-ignored `.env`.
