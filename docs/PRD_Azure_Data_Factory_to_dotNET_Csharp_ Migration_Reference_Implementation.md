# PRD: Azure Data Factory to .NET C# Migration — Reference Implementation

Nov 4, 2026

## Overview

Build a working reference implementation that shows how a credit union running Corelation KeyStone migrates its Azure Data Factory (ADF) pipelines to .NET C#, with acceptance tests written first as the completion gate. The sample runs entirely on a developer machine with no Azure subscription, using Docker containers for IBM Db2, SQL Server and Azurite and synthetic data.

The repository will contain both sides of the migration: the original ADF pipeline JSON (the source of truth for behavior) and the converted C# solution. Every converted pipeline must pass the same acceptance suite before it counts as done.

**Instructions for Claude Code:** treat this PRD as the entry point. Work phase by phase (see Build phases), write the acceptance tests for a pipeline before its implementation, and do not mark a phase complete until `dotnet test` passes with zero skipped acceptance tests. When a requirement is ambiguous, record the decision in `docs/decisions/` as a short ADR rather than guessing silently.

## Goals and non-goals

The sample succeeds when a reviewer can clone the repo, run one command, and see every migrated pipeline proven equivalent to its ADF definition by automated tests.

**Goals**

- Demonstrate a repeatable ADF-to-.NET migration pattern for pipelines fed by KeyStone.
- Run 100% on a developer machine: no Azure subscription, no cloud cost, no external network calls during tests.
- Cover the ADF features most common in core-banking workloads: Copy (Db2 and CSV sources), Lookup, Get Metadata, ForEach, If Condition, Stored Procedure, Web, Execute Pipeline, a Mapping Data Flow, parameters, and a schedule trigger.
- Prove equivalence through acceptance tests written before the conversion code.
- Meet credit union expectations for auditability, idempotency, secrets handling, and observability.
- Provide a migration playbook other teams can follow.

**Non-goals**

- Migrating any real pipeline, or using real member data or KeyStone's licensed database schema.
- Connecting to a real KeyStone or KeyBridge instance.
- Deploying to Azure in v1. The design keeps that path open, but nothing requires it.
- Performance tuning beyond the stated non-functional targets.

**Success criteria**

| Criterion | Target |
| --- | --- |
| Acceptance tests passing | 100%, none skipped |
| Setup on a clean machine | One command (`./build.ps1 test`) after installing .NET SDK and Docker |
| Full test suite runtime | Under 10 minutes locally |
| ADF activity types covered | All 11 listed in Goals |
| Output equivalence | Row counts, checksums and column values match golden outputs exactly |
| Documentation | README, migration playbook, one ADR per significant decision |

## Context and assumptions

The institution is a credit union running Corelation's KeyStone core, which stores its data in IBM Db2 and exposes the KeyBridge open API for real-time integration ([source](https://corelationinc.com/keystone/)). Its ADF pipelines pull the day's data out of KeyStone each night, combine it with partner files, and load a reporting and reconciliation store.

There is no Azure subscription, so every Azure service is replaced by a local equivalent. The code depends on abstractions and configuration only, so moving to Azure later means changing configuration, not pipelines.

**Local stand-ins**

| Production component | Local stand-in |
| --- | --- |
| KeyStone database (IBM Db2) | Db2 Community Edition container, seeded with a synthetic KeyStone-like schema |
| KeyBridge API | WireMock.Net stub driven by a contract file the team owns |
| ADF self-hosted integration runtime | Not needed; the .NET code connects to Db2 directly |
| Azure Blob Storage | Azurite container |
| Azure SQL (reporting store) | SQL Server 2022 Developer Edition container |
| Azure Functions + Durable Functions | Azure Functions Core Tools, with Azurite as the Durable storage backend |
| Azure Key Vault | .NET user-secrets and environment variables |
| Application Insights | OpenTelemetry exported to the standalone .NET Aspire dashboard container |
| Alert webhook (Teams, email) | WireMock.Net stub |

**Assumptions to confirm**

- Data access follows the most common pattern: direct Db2 reads for bulk extraction, KeyBridge for reference lookups, and partner files in blob storage. This is an assumption, not confirmed with the credit union.
- The synthetic Db2 schema models core banking concepts (members, share and loan accounts, transactions, GL entries) but is not KeyStone's actual schema, which is licensed. Mapping to real tables happens only inside the credit union's environment.
- KeyBridge request and response formats are proprietary. The stub uses a placeholder contract, isolated behind `IKeyBridgeClient`, until the real specification is available.
- The card processor delivers daily settlement files as CSV.

**Sample business domain**

A nightly batch that extracts the day's posted transactions and GL entries from KeyStone, ingests the card processor's settlement files, maps transactions to GL accounts, and reconciles both against KeyStone. All data is synthetic and produced by a seeded generator so runs are deterministic. No SSNs are generated, member and account numbers come from a reserved range, and card numbers use a test BIN.

## Sample pipelines (functional scope)

The sample contains five ADF pipelines, authored as JSON under `adf/`, then converted one at a time. Together they exercise every activity type listed in Goals.

| Pipeline | Purpose | ADF features exercised |
| --- | --- | --- |
| `PL_Master_NightlyBatch` | Orchestrates the nightly run for a `businessDate` | Schedule trigger, parameters, Execute Pipeline (sequential and parallel), failure path |
| `PL_ExtractKeyStone` | Copies the day's data from KeyStone Db2 into SQL staging | Lookup, ForEach, Copy (Db2 source), Stored Procedure |
| `PL_IngestCardSettlement` | Loads card processor settlement files | Get Metadata, ForEach, Copy (CSV source), archive and quarantine |
| `PL_MapAndAggregate` | Maps transactions to GL accounts and totals them | Web (KeyBridge), Lookup, If Condition, Mapping Data Flow |
| `PL_Reconciliation` | Checks totals against KeyStone and the processor | Lookup, If Condition, Stored Procedure, Web (alert) |

**PL\_Master\_NightlyBatch**

1. Triggered daily at 03:00 Pacific Time (`America/Los_Angeles`), a time that occurs exactly once every day across daylight saving changes; `businessDate` defaults to the previous Pacific calendar day and can be overridden for reruns.
2. Runs Extract and Card Settlement in parallel, then Map and Aggregate, then Reconciliation, passing `businessDate` and a `batchRunId`.
3. On any child failure, writes a `BatchRun` record with status Failed and the failing step, then stops.

**PL\_ExtractKeyStone**

1. Looks up `ops.ExtractConfig`, which lists the source tables (transactions, accounts, GL entries), their key columns and date columns.
2. For each table (parallel, batch count 3): copies rows where the posting date equals `businessDate` from Db2 into the matching `stg` table.
3. Calls `usp_MergeKeyStone`, which upserts into `core` tables on source keys, so reruns never duplicate rows.
4. Records row counts per table in `ops.BatchRun`.
5. Db2 type handling (CHAR padding, DECIMAL precision, DATE and TIMESTAMP) follows rules written in an ADR before coding.

**PL\_IngestCardSettlement**

1. Lists files in `card-landing/{businessDate}/` matching `settlement_*.csv`.
2. For each file (parallel, batch count 4): validates the header, row schema and trailer record count, then copies rows to `stg.CardSettlement`.
3. Invalid files move to `card-quarantine/` with an error manifest; valid files move to `card-archive/`.
4. Calls `usp_MergeCardSettlement`, keyed on the processor reference number.

**PL\_MapAndAggregate**

1. Calls KeyBridge (stubbed) for the transaction-code-to-GL-account mapping and caches it in `ref.TxnCodeGlMap`.
2. Looks up the distinct transaction codes posted on `businessDate`.
3. If any code is unmapped, fails with error code `TXN_CODE_UNMAPPED` and lists the codes.
4. Data flow: joins transactions to the mapping, applies debit or credit sign, rounds derived amounts to 2 decimals with banker's rounding (`MidpointRounding.ToEven`), and aggregates per GL account into `core.DailyGlTotals`.

**PL\_Reconciliation**

1. Card check: compares settlement file totals with KeyStone card transactions for `businessDate`.
2. GL check: compares `core.DailyGlTotals` with the GL entries extracted from KeyStone.
3. If either difference exceeds the tolerance (default 0.00), writes a row to `ops.ReconExceptions` and posts one alert to the webhook (stubbed).
4. Otherwise, records status Reconciled in `ops.BatchRun`.

## Target architecture

Each ADF pipeline becomes a Durable Functions orchestrator, and each ADF activity becomes a Durable activity function backed by a plain, unit-testable service class. Business logic lives in a class library with no Azure Functions dependency, so the same code runs under the test harness, locally under Azure Functions Core Tools, and in Azure later if the credit union chooses.

**Tech stack**

| Concern | Choice |
| --- | --- |
| Developer platform | Windows 10 or 11 (x86-64), PowerShell 7, Docker Desktop with the WSL 2 backend |
| Runtime | .NET 10 LTS (confirmed) |
| Orchestration | Azure Functions isolated worker + Durable Functions, run locally with Core Tools v4 and Azurite as storage |
| KeyStone source | Db2 Community Edition container; `Net.IBM.Data.Db2` driver (the Windows package; add the `-lnx` variant only if tests later run on Linux CI agents) |
| Blob storage | `Azure.Storage.Blobs` against Azurite |
| Reporting store | SQL Server 2022 Developer container; `Microsoft.Data.SqlClient`, Dapper, `SqlBulkCopy` |
| Schema migrations | DbUp for both SQL Server and the Db2 seed |
| HTTP calls | `IHttpClientFactory` + `Microsoft.Extensions.Http.Resilience` (Polly) |
| Configuration and secrets | Options pattern; user-secrets and environment variables locally; Key Vault provider added only when an Azure environment exists |
| Logging and tracing | OpenTelemetry exported to the .NET Aspire dashboard container |
| Testing | xUnit, Testcontainers (SQL Server, Azurite and Db2), WireMock.Net, Shouldly |
| Machine requirements | 16 GB RAM recommended; set WSL 2 memory to at least 8 GB in `%UserProfile%\.wslconfig`, because the Db2 container alone needs about 4 GB |

**Windows-specific rules for Claude Code**

- `build.ps1` is the primary entry point (`./build.ps1 up | seed | test | run`); no Makefile or bash scripts required.
- Add a `.gitattributes` that forces LF line endings for `*.sql`, `*.sh` and anything mounted into Linux containers, so Db2 seed scripts don't fail on CRLF.
- Keep the repo on the Windows file system (e.g. `C:\src`), not inside a WSL distro, so Visual Studio and the .NET tooling see it directly.
- Install prerequisites with winget: .NET SDK, Docker Desktop, Azure Functions Core Tools, PowerShell 7.

**ADF-to-.NET mapping**

| ADF construct | .NET equivalent |
| --- | --- |
| Pipeline | Durable orchestrator function |
| Pipeline parameters | Strongly typed orchestration input record |
| Schedule trigger | Timer-triggered starter function |
| Execute Pipeline | `CallSubOrchestratorAsync`; parallel children via `Task.WhenAll` |
| Copy activity, Db2 source | `DB2DataReader` streamed into `SqlBulkCopy`, no intermediate file |
| Copy activity, CSV source | Blob stream parsed with CsvHelper into `SqlBulkCopy` |
| Self-hosted integration runtime | Not needed; the worker connects to Db2 directly |
| Get Metadata | `BlobContainerClient.GetBlobsAsync` with prefix filter |
| Lookup | Dapper query returning a typed result |
| Stored Procedure | Dapper `ExecuteAsync` with `CommandType.StoredProcedure` |
| ForEach (parallel) | Fan-out with `Task.WhenAll`, bounded to the ADF batch count |
| If Condition | `if` in the orchestrator |
| Web activity | Typed `HttpClient` with resilience pipeline |
| Mapping Data Flow | Set-based SQL in a stored procedure, or a C# transformation service for small volumes; decision recorded in an ADR |
| Activity retry policy | `RetryPolicy` on `CallActivityAsync`, values copied from the ADF JSON |
| Linked services | Options classes bound from configuration |

Orchestrators must stay deterministic: no `DateTime.UtcNow`, random values, or I/O inside them, only in activities.

## Repository structure

Claude Code should create this layout in Phase 0 and keep it stable.

```text
adf-to-dotnet-reference/
├── CLAUDE.md                     # points to this PRD, coding rules, test command
├── README.md
├── docs/
│   ├── PRD.md                    # this document, exported as Markdown
│   ├── progress.md               # phase summaries written by Claude Code
│   ├── migration-playbook.md
│   └── decisions/                # ADRs, e.g. 0001-hosting-model.md
├── adf/                          # original ADF JSON (source of truth)
│   ├── pipeline/
│   ├── dataset/
│   ├── linkedService/
│   ├── dataflow/
│   └── trigger/
├── db/
│   ├── keystone-db2/             # synthetic KeyStone-like Db2 schema + seed scripts
│   └── reporting/                # SQL Server schemas, tables, stored procs
├── contracts/
│   └── keybridge/                # placeholder KeyBridge contract + WireMock mappings
├── src/
│   ├── CreditUnion.Batch.Core/       # services, domain records, no Functions dependency
│   ├── CreditUnion.Batch.KeyStone/   # Db2 source reader + IKeyBridgeClient
│   └── CreditUnion.Batch.Functions/  # orchestrators, activities, timer trigger
├── tests/
│   ├── CreditUnion.Batch.UnitTests/
│   ├── CreditUnion.Batch.AcceptanceTests/  # written first, one folder per pipeline
│   └── TestData/
│       ├── generator/            # seeded synthetic data generator
│       ├── inputs/{scenario}/
│       └── golden/{scenario}/    # expected outputs, reviewed by a human
├── .gitattributes                # LF endings for SQL and container-mounted files
├── docker-compose.yml            # Db2, SQL Server, Azurite, WireMock, Aspire dashboard
└── build.ps1                     # up, seed, test, run
```

## Acceptance tests (written first)

A pipeline's conversion is complete only when all of its acceptance tests pass. Tests are committed, reviewed and failing before any implementation code for that pipeline exists.

**Strategy**

- Tests run end to end on the local machine: Testcontainers starts Db2, SQL Server and Azurite, WireMock.Net stands in for KeyBridge and the alert webhook, and the orchestration runs in-process through a test harness that calls the same activity services.
- Each scenario has fixed inputs in `TestData/inputs/{scenario}` (Db2 seed rows and settlement files) and expected outputs in `TestData/golden/{scenario}`.
- Comparison covers row counts, an order-independent SHA-256 checksum per table, and column-level diffs that print the first 20 mismatches.
- Without an Azure subscription, ADF cannot run, so golden outputs are derived from the pipeline specification and reviewed by a human. In the real migration, they are captured from production ADF runs against the same inputs.
- Every test is idempotency-checked: run the batch twice and assert the outputs are unchanged.
- The Db2 container is started once per test run and reset between scenarios, because it takes one to three minutes to start.

**Test cases**

| ID | Pipeline | Scenario | Expected result |
| --- | --- | --- | --- |
| AT-01 | Extract | Seeded day with 5,000 transactions, 800 accounts, 300 GL entries | Counts and checksums in `core` match golden |
| AT-02 | Extract | Same `businessDate` extracted twice | No duplicate rows |
| AT-03 | Extract | Db2 CHAR padding, NULLs, max-precision DECIMAL, TIMESTAMP values | Stored exactly per the type-handling ADR |
| AT-04 | Extract | Db2 connection refused for the first two attempts | Retries per policy and succeeds |
| AT-05 | Card | Three valid settlement files | All rows loaded; files in archive |
| AT-06 | Card | One file whose trailer count does not match its rows | File quarantined with manifest; other files load |
| AT-07 | Card | Empty landing folder | Succeeds with zero rows; `BatchRun` notes no files |
| AT-08 | Map | All transaction codes mapped | `core.DailyGlTotals` matches golden to the cent |
| AT-09 | Map | One unmapped transaction code | Fails with `TXN_CODE_UNMAPPED` naming the code |
| AT-10 | Map | Derived amounts at a midpoint, such as 0.125 | Banker's rounding applied |
| AT-11 | Map | KeyBridge stub returns 503 twice, then 200 | Retries per policy and succeeds |
| AT-12 | Recon | Card and GL totals both match | Status Reconciled; no alert sent |
| AT-13 | Recon | Card totals differ by 0.01 | Exception row written; exactly one alert posted |
| AT-14 | Master | Full happy path for a business date | Extract and Card run in parallel, then Map, then Recon; all succeed; evidence file written |
| AT-15 | Master | Map fails | Recon not run; `BatchRun` Failed at Map |
| AT-16 | Master | Rerun for an earlier `businessDate` | Only that date's data is affected |
| AT-17 | All | Logs and traces | No secrets, and no full member, account or card numbers, in any log line |
| AT-18 | Master | Runs on the spring-forward and fall-back days in Pacific Time | Trigger fires once; `businessDate` is the correct previous Pacific day; transactions near midnight land on the right date |
| AT-19 | All | Extract login attempts a write to Db2; reader login attempts a write to the reporting store | Both writes rejected by the database |

## Non-functional, security and compliance requirements

The sample must show the controls a bank's risk and audit teams will look for, even though it uses synthetic data.

| Area | Requirement |
| --- | --- |
| Secrets | No secrets in code, committed config or logs. Locally: user-secrets and environment variables; container passwords generated per test run. Build fails if gitleaks finds anything. |
| Data protection | Member, account and card numbers masked in logs (last 4 digits only). No SSNs anywhere, even synthetic. |
| NCUA alignment: member information safeguards | Reflect the GLBA-based guidelines in NCUA Part 748, Appendix A: a read-only Db2 login for extraction, separate writer and reader logins for the reporting store, masking in logs, encrypted connections outside the local Docker network, and no member data in test artifacts. |
| NCUA alignment: records preservation | Part 749 requires a written records preservation program, and NCUA has recently moved away from prescribing retention periods, so they come from the credit union's own policy. Retention for `ops.BatchRun`, `ops.ReconExceptions` and archived files is configurable (placeholder marked TBD), and archived blobs carry a retain-until tag. No automatic deletion in v1. |
| NCUA alignment: examiner evidence | Each run exports an evidence file (files received, row counts, checksums, reconciliation results, exceptions) to `evidence/{businessDate}/` in blob storage, so a run can be explained after the fact. |
| Auditability | Every run writes `ops.BatchRun` with run ID, business date, start and end times, row counts per step, status and error code. Records are append-only. |
| Idempotency | Any pipeline can rerun for the same `businessDate` without duplicates or side effects beyond the first run. |
| Money handling | `decimal` only, never `double`. Rounding mode stated explicitly in code. |
| Time | Timestamps stored in UTC; `businessDate` is a `DateOnly` in Pacific Time (`America/Los_Angeles`). Use an injected `TimeProvider` so tests control time, including daylight saving transitions. |
| Resilience | Retry counts, intervals and timeouts match the ADF activity policies. Transient Db2, SQL and HTTP errors retried; validation errors not retried. |
| Observability | OpenTelemetry traces span the orchestration and each activity, correlated by `batchRunId`, viewable in the local Aspire dashboard. |
| Offline | Tests make no calls outside the local Docker network once images are pulled. |
| Performance | Extract at least 100,000 rows per minute from Db2 on a developer laptop; tested with a 1-million-row scenario tagged `Category=Performance`, excluded from the default run. |
| Code quality | Nullable reference types on, warnings as errors, analyzers enabled, no `TODO` left in merged code. |
| Dependencies | Pinned NuGet and container image versions; `dotnet list package --vulnerable` passes. |

## Build phases

Claude Code works through these phases in order. Each phase ends at a gate; it does not start the next phase until the gate passes and a short summary is written to `docs/progress.md`.

1. **Phase 0 – Scaffold.** Create the solution, projects, `CLAUDE.md`, `docker-compose.yml` and build script. CI is optional (GitHub Actions free tier works, since no Azure is needed).
   - Gate: `dotnet build` succeeds with warnings as errors; `docker compose up` brings all containers to healthy.
2. **Phase 1 – Source of truth.** Author the five ADF pipelines and their datasets, linked services, data flow and trigger as ADF JSON in `adf/`. Write the synthetic KeyStone Db2 schema and seed scripts, and the SQL Server schema and stored procedures.
   - Gate: JSON parses and every activity, dataset and linked service reference resolves (checked by a small validator test); both databases build cleanly in fresh containers.
3. **Phase 2 – Test harness and data.** Build the seeded data generator, Testcontainers fixtures, KeyBridge and webhook stubs, and the output comparison utility. Generate inputs and golden outputs for every scenario. Write the Db2 type-handling ADR.
   - Gate: harness self-tests pass; golden files committed for human review.
4. **Phase 3 – Acceptance tests.** Write AT-01 to AT-19 against the public orchestration entry points, which exist only as stubs.
   - Gate: all acceptance tests compile and fail for the right reason (not implemented), none skipped.
5. **Phase 4 – Convert Extract.** Implement `PL_ExtractKeyStone` with unit tests for its services.
   - Gate: AT-01 to AT-04 pass.
6. **Phase 5 – Convert Card Settlement.** Implement `PL_IngestCardSettlement`.
   - Gate: AT-05 to AT-07 pass.
7. **Phase 6 – Convert Map and Aggregate.** Implement `PL_MapAndAggregate`, including the data flow ADR.
   - Gate: AT-08 to AT-11 pass.
8. **Phase 7 – Convert Reconciliation.** Implement `PL_Reconciliation`.
   - Gate: AT-12 and AT-13 pass.
9. **Phase 8 – Orchestrate.** Implement `PL_Master_NightlyBatch`, the timer trigger and cross-cutting concerns (audit, logging, masking). Confirm the batch also runs under `func start`.
   - Gate: all acceptance tests pass, including AT-14 to AT-19, twice in a row.
10. **Phase 9 – Document.** Write the README and the migration playbook, mapping each step to how the credit union would apply it to a real pipeline.
    - Gate: a reviewer can follow the README on a clean machine without help.

**Model and effort per phase**

Use Opus where a mistake would carry into every later phase, and Sonnet where the spec and tests already pin the work down. Set the model with `/model` and the effort with `/effort` at the start of each phase, then `/clear` before the next one.

| Phase | Model | Effort | Why |
| --- | --- | --- | --- |
| 0 – Scaffold | Sonnet 5.5 | medium | Boilerplate with a clear target layout |
| 1 – Source of truth | Opus 5.5 | high | ADF JSON, Db2 schema and stored procedures define everything downstream |
| 2 – Test harness and data | Opus 5.5 | xhigh | Golden data, comparison logic and the Db2 type-handling ADR are the most consequential decisions in the project |
| 3 – Acceptance tests | Opus 5.5 | high | The tests define "done"; a wrong test makes a wrong implementation pass |
| 4 – Convert Extract | Sonnet 5.5 | high | Well specified, but Db2 driver and type quirks need care |
| 5 – Convert Card Settlement | Sonnet 5.5 | medium | Straightforward file parsing and bulk load |
| 6 – Convert Map and Aggregate | `opusplan` | high | Opus decides the data flow approach and rounding semantics; Sonnet implements |
| 7 – Convert Reconciliation | Sonnet 5.5 | medium | Simple comparisons against existing tables |
| 8 – Orchestrate | Opus 5.5 | xhigh | Durable Functions determinism, parallel children, failure paths and log masking interact in subtle ways |
| 9 – Document | Sonnet 5.5 | medium | Writing from finished code and ADRs |

**Escalation rule:** if a phase gate fails twice on Sonnet, switch to Opus 5.5 at high; if it still fails, use xhigh and ask Claude Code to explain the root cause before changing code. If an effort level isn't supported by the active model, Claude Code falls back to the highest supported level below it. Model availability and usage limits depend on the Claude plan.

## Definition of done, risks and open questions

**Definition of done**

- [ ] All acceptance tests AT-01 to AT-19 pass, none skipped, two consecutive runs
- [ ] Golden outputs reviewed and signed off by a person who knows the business rules
- [ ] Secret scan, vulnerability scan and analyzers pass
- [ ] README, migration playbook and ADRs complete
- [ ] Every ADF activity in `adf/` traced to its .NET implementation in the playbook

**Risks**

| Risk | Mitigation |
| --- | --- |
| Golden outputs encode a misreading of the spec, since ADF cannot be run to produce them | Human review of golden files before Phase 3; capture real ADF outputs during the production migration |
| Synthetic Db2 schema differs from KeyStone's real schema | Keep all table and column names in configuration and one mapping layer; document the remap step in the playbook |
| Db2 container is heavy (about 4 GB RAM, one to three minutes to start) | Raise WSL 2 memory limit; one shared container per test run, reset between scenarios |
| KeyBridge contract unknown | All calls behind `IKeyBridgeClient`; stub contract versioned in `contracts/keybridge/` |
| Data flow semantics (nulls, type coercion, rounding) differ subtly in C# | Dedicated edge-case scenarios; ADR documenting each semantic choice |
| Durable Functions determinism bugs | Analyzer rules and a unit test that replays each orchestrator |
| Sample is mistaken for production-ready code | README states it is a reference implementation using synthetic data |

**Decisions**

| Question | Decision |
| --- | --- |
| How the ADF pipelines get data from KeyStone | Unknown, so the sample assumes the most common pattern for a core database: ADF reads Db2 directly through a self-hosted integration runtime, ideally against a reporting copy rather than production, uses KeyBridge for occasional reference lookups, and receives partner files in storage. Confirm before the real migration. |
| KeyBridge specification and KeyStone data dictionary | Not available. The sample keeps the synthetic Db2 schema and placeholder KeyBridge contract, with all table, column and endpoint names in configuration and one mapping layer, so remapping is a contained task once the documents exist. |
| Target hosting if moved to Azure | Azure Durable Functions. |
| .NET version | .NET 10. |
| Reporting database | SQL Server (Azure SQL if moved to Azure). |
| Business date time zone | Pacific Time (`America/Los_Angeles`). |
| Regulatory scope | NCUA. The sample models the controls under "NCUA alignment" in the non-functional requirements. It demonstrates practices; it does not claim compliance. |

**Still open**

- What retention periods apply to batch records, exceptions and archived files? These come from the credit union's own records preservation policy.
- Is the assumed KeyStone data access pattern above correct?
