# Progress

## Phase 0 – Scaffold (2026-11-05)

- Solution `AdfToDotnet.sln` with `CreditUnion.Batch.Core`, `.KeyStone`, `.Functions` and `.UnitTests`, `.AcceptanceTests` (xUnit v3, Shouldly), .NET 10.
- `Directory.Build.props`: nullable, warnings as errors, analyzers; `Directory.Packages.props`: central pinned versions.
- `docker-compose.yml`: Db2 12.1.0.0, SQL Server 2022 CU14, Azurite 3.33.0, WireMock 3.9.1, Aspire dashboard 9.0.
- `build.ps1` (`up | down | build | seed | test | run | scan`), `.gitattributes`, `.gitignore`, `CLAUDE.md`, `README.md`, ADR 0001.
- Repository folder layout per the PRD created (`adf/`, `db/`, `contracts/`, `tests/TestData/`).
- Db2 runs without a persistent volume: a recreated container cannot re-initialise an existing `/database` volume (`db2icrt` fails), so it starts fresh on every `up` and is re-seeded by script. SQL Server and Azurite keep volumes.
- Deviation: `docs/PRD.md` is a copy of the original PRD file (whose name contains a space).

## Phase 1 – Source of truth (2026-11-05)

**ADF JSON (`adf/`), hand-authored:**

| Resource type | Resources |
| --- | --- |
| Pipelines (5) | `PL_Master_NightlyBatch`, `PL_ExtractKeyStone`, `PL_IngestCardSettlement`, `PL_MapAndAggregate`, `PL_Reconciliation` |
| Linked services (4) | Db2, Azure SQL, Blob, Key Vault |
| Datasets (6) | |
| Data flows (1) | `DF_MapAndAggregateGl` |
| Triggers (1) | `TR_Nightly_0300_Pacific` |
| Integration runtimes (1) | Self-hosted |

- **Activity types used:** Copy (Db2, CSV, binary and SQL→JSON), Lookup, Get Metadata, Filter, ForEach, If Condition, Stored Procedure, Web, Execute Pipeline, Execute Data Flow, Set Variable, Delete, Fail.

**Synthetic KeyStone Db2 schema (`db/keystone-db2`):**
- Tables: `KS.MEMBER`, `ACCOUNT`, `POSTED_TXN`, `GL_ENTRY`, `GL_ACCOUNT`, `TXN_CODE`.
- Reference data: GL accounts and transaction codes.
- Read-only `KSEXTRACT` login, backed by an OS user that the `/var/custom` script creates.

**Reporting store (`db/reporting`):**
- Schemas `ops`, `stg`, `core` and `ref`; 15 procedures and 2 inline functions.
- `ops.BatchRun` and `ops.CardFileLog` are append-only (trigger plus DENY).
- Logins: `cu_batch_writer` and `cu_report_reader`.

**Deploy tool (`tools/CreditUnion.Batch.DbDeploy`):**
- DbUp for SQL Server, plus a small custom DbUp provider for Db2.
- `./build.ps1 seed` runs it; password keys are added to `.env` automatically.

**Contracts:**
- `contracts/card-processor/settlement-file-format.md` (H/D/T CSV).
- `contracts/keybridge/README.md` (placeholder GL-mapping endpoint).

**ADRs:** 0002 (DbUp and the Db2 provider), 0003 (schema and reporting store design), 0004 (ADF JSON conventions).

**Gate:**
- **ADF validator:** `AdfDefinitionValidationTests` (10 tests) passes. It was mutation-checked: a broken data flow reference, an unknown procedure parameter, a misspelled activity reference and a missing procedure were each caught.
- **Fresh containers:** both databases deployed into fresh containers (`docker compose down -v`, `up`, `seed`), and a second `seed` was a no-op for migrations.
- **Least privilege, checked ad hoc:**
  - `KSEXTRACT` can SELECT but its INSERT and CREATE TABLE are rejected.
  - `cu_report_reader` INSERT is denied.
  - `cu_batch_writer` cannot UPDATE `ops.BatchRun`.
  - AT-19 automates these checks in Phase 3.

**Notes for later phases:**
- VS Code's T-SQL language service flags Db2 syntax in `db/keystone-db2/*.sql` (e.g. `GRANT ... TO USER`). These are false positives; Db2 accepts the scripts.
- `PUBLIC` holds no database authorities but does hold EXECUTE on the 89 `NULLID` CLI packages (including `SYSSH200`), so the .NET driver works as `KSEXTRACT` without extra grants.

## Phase 2 – Test harness and data (2026-11-05)

**Decisions:**
- ADR 0005 (Db2 type handling) is based on probes against the real driver and database, and `TypeHandlingProbeTests` pins each observed behaviour.
  - `DB2DataReader.GetDecimal` silently rounds DECIMAL(31,8), and string parameters lose digits, so decimals go through `DB2Decimal` and `SqlDecimal`.
  - Under the default collation, SQL Server `varchar` silently best-fits text outside Windows-1252, so migration 0007 moves the six extracted free-text columns to a UTF-8 collation.
  - CHAR padding, TIMESTAMP values and DATE values are copied verbatim, with no time-zone conversion.
- ADR 0006 covers the harness, the golden format, the checksum and comparison rules, how golden outputs are derived, and the ADF semantics they encode, which need human sign-off.

**Generator and oracle (`tests/TestData/generator`, `CreditUnion.Batch.TestData`):**
- Seeded SplitMix64 data with forked streams.
- A world builder keeps GL entries and settlement files consistent with the transactions.
- `BatchOracle` states the five pipelines in memory; golden outputs are its state after a scenario's runs.
- `./build.ps1 golden` regenerates the committed files.

**Scenarios:** ten, covering AT-01–AT-18 and the performance NFR (catalogue in `tests/TestData/README.md`).
- Inputs and golden files total about 3 MB.
- `perf-extract-1m` commits only its manifest; its 135 MB of inputs are regenerated into the git-ignored `tests/TestData/generated`.
- Each scenario has a `summary.md`. The review checklist and sign-off table are in `tests/TestData/REVIEW.md`.

**Harness (`tests/CreditUnion.Batch.TestHarness`):**
- `BatchEnvironment`: Testcontainers Db2, SQL Server and Azurite, started once per run with generated passwords. `CU_TESTS_USE_COMPOSE=1` reuses the `up` stack.
- Deployment uses the DbUp deployers; reset uses TRUNCATE (bypassing the append-only triggers) and deletes the Db2 scenario rows.
- Loaders for Db2 rows and landing blobs; canonical SQL snapshots.
- `GoldenAssert`: row counts, order-independent SHA-256, keyed diffs showing the first 20 mismatches, blobs, error manifests, evidence and alerts.
- WireMock.Net stubs: KeyBridge (Basic auth, `FailNextRequests`) and the alert webhook (secret-token URL).
- `FaultInjectingTcpProxy` for AT-04.
- WireMock mappings for the compose stack are in `contracts/keybridge/mappings`.

**Gate (harness self-tests):**
- **UnitTests: 77 pass**, plus 1 `Category=Performance`.
  - Known answers: PRNG vectors and the checksum.
  - CSV, diff and canonical values.
  - Rounding, settlement rules and DST schedule, all hand-checked.
  - Byte-for-byte drift of every committed scenario.
  - Manifest checksums and input constraints.
  - Synthetic-data safety: no SSN-like values, no Luhn-valid card numbers, reserved ranges only.
  - Stubs, the fault proxy and JSON equivalence.
- **AcceptanceTests: 45 harness self-tests pass against real containers.**
  - Environment and reset.
  - Type probes.
  - Lossless Db2 and blob round trips for every scenario.
  - Golden extract tables equal the Db2 source rows read by the ADR 0005 rules.
  - Every scenario replayed through the Phase 1 procedures, with an independent T-SQL data-flow rule, agrees with its golden outputs.
  - Six malformed settlement files give identical results from the C# port and `stg.usp_ValidateCardSettlementFile`.
- `dotnet list package --vulnerable --include-transitive`: none. Build: warnings as errors, clean.
- `./build.ps1 scan`: no leaks. The new `.gitleaks.toml` excludes only git-ignored local files (`.env`, `bin`/`obj`, `tests/TestData/generated`). Before it, `detect --no-git` flagged the generated `.env` on any machine that had run `up`.

**Phase 1 defects found by the cross-check (fixed):**
- `ops.usp_GetRunEvidence` failed to deploy after migration 0007: `STRING_AGG` could not resolve the mixed collations. Each checksum line now states its collation explicitly.
- The same procedure hashed a NULL `char(4)` Mcc as `'\N  '`, because `ISNULL` takes the first argument's type. It now converts to `nvarchar` first, as its comment specifies.

**Notes for later phases:**
- Golden outputs are signed off by engineering review (`tests/TestData/REVIEW.md`, ADR 0007), because no business reviewer was available; one must confirm before go-live.
  - Settlement file names match case-insensitively, as ADF's `startsWith` does. `card-quarantine-trailer-mismatch` now includes `SETTLEMENT_003.CSV`, so Phase 5 must do the same.
  - On a child failure, the master's Failed event carries the child's error code.
- Phase 3 needs an in-process orchestration runner (a test `TaskOrchestrationContext`) that executes activities directly. With virtual time, retries "per policy" (30 s intervals) can be asserted without waiting.
- The Db2 loader inserts row by row (standard-day loads in a few seconds). `perf-extract-1m` will need `DB2BulkCopy` or `LOAD` in Phase 4.
- Known limitation (ADR 0005): merge change detection ignores trailing-space-only and case-only corrections.

## Phase 3 – Acceptance tests (2026-11-05)

**Entry points (stubs) and contracts (ADR 0008):**
- One orchestrator function per ADF pipeline, named after it (`PipelineNames`), and the timer function `TR_Nightly_0300_Pacific`. Each body throws `NotImplementedException` naming the phase that converts it.
- Typed inputs: `PipelineRunInput`, `ReconciliationInput` and `NightlyBatchInput`. The master's instance ID is its `batchRunId`.
- `PipelineFailedException` carries ADF error codes as `"{code}: {detail}"`.
- Options classes, one per linked service.
- `AddCreditUnionBatch`, the one composition root used by `Program.cs` and the tests.
- `local.settings.json` sets `WEBSITE_TIME_ZONE=America/Los_Angeles`.

**Harness additions (`tests/CreditUnion.Batch.TestHarness`):**
- `Orchestration/InProcessFunctionHost`: runs the app's own `[Function]` methods with a custom `TaskOrchestrationContext`.
  - Inputs and outputs cross as JSON, and failures as `TaskFailedException`.
  - `RetryPolicy` is applied as DurableTask.Core applies it, on a virtual clock that is also the batch's `TimeProvider`.
  - Scheduled work starts only when the orchestrator yields, and leaving the orchestrator thread fails.
  - A trace records every attempt and sub-orchestration.
- `RecordingDurableTaskClient` and `TimerTriggerEmulator` (NCRONTAB in `WEBSITE_TIME_ZONE`, NCrontab.Signed 3.3.0) for AT-18.
- `Logging/LogCapture`: every `ILogger` entry and every `ActivitySource` span, scanned for secrets and for full member, account and card numbers.
- `Adf/AdfActivityPolicy` reads retry policies from `adf/pipeline`.
- `GoldenAssert` blob comparison takes a container filter.

**Acceptance tests (`tests/CreditUnion.Batch.AcceptanceTests`):**
- AT-01 to AT-19, one folder per pipeline plus `CrossCutting`, tagged `[Trait("AT", "AT-nn")]`.
- Every test ends with an idempotency rerun under new batch run IDs.
- Map and Reconciliation tests run their upstream pipelines as preconditions.
- AT-04 and AT-11 assert retries through the trace against the ADF policy (3 retries, 30 s), plus the fault counts (2 refused connections; 3 KeyBridge calls).
- AT-10 restates banker's rounding from the inputs: 16.30 and 5.43, against 16.35 and 5.48 with half away from zero.
- AT-17 runs three scenarios: happy path, alert and failure.
- AT-19 runs each write first as admin in a rolled-back transaction (a positive control), then as the restricted login.

**Gate:**
- **Build:** clean, warnings as errors.
- **UnitTests: 98 pass**, including 21 new self-tests of the host, the timer emulator, log capture, ADF policies and the entry-point contracts.
  - The host self-tests passed 15 repeated runs.
- **AcceptanceTests: 76 cases, none skipped.**
  - 44 harness self-tests pass.
  - The 20 cases of AT-01 to AT-18 fail with the `NotImplementedException` of a stub: Extract (Phase 4), Card (Phase 5), Master (Phase 8) and the trigger (Phase 8). Map and Recon tests fail at their Extract precondition.
  - The 12 cases of AT-19 pass by design: the logins and grants were built in Phase 1.
  - Checked by a script over the xUnit XML report.
- **Scans:** `./build.ps1 scan` found no leaks; `dotnet list package --vulnerable --include-transitive` found none.

**Notes for later phases:**
- **Retries:** use `RetryPolicy` on the activity call. Do not add Polly or driver-level retries on top. If the Db2 driver retries connections itself, AT-04 will see no failed attempts and fail.
- **Validation failures** (unmapped codes) must not be retried.
- **Timer trigger:** it must set `NightlyBatchInput.BusinessDate` and take the fire time from `TimeProvider`; `ScheduleStatus` is null in AT-18.
- **The in-process host does not replay orchestrators.** The PRD's determinism replay test is Phase 8 work.
- **AT-17 needs explicit handling in Phase 8:** the webhook token is in the URL path, and HTTP-client logs and spans do not redact it.
