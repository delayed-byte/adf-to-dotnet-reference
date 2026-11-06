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

## Phase 4 – Convert Extract (2026-11-05)

**Implemented (`PL_ExtractKeyStone`, body of `ExtractKeyStoneOrchestrator`; ADR 0009):**
- The orchestrator mirrors the ADF activities: log Started, look up `ops.ExtractConfig`, copy each table Db2 to staging with parallelism bounded to `batchCount` 3 and log its staging row count, `core.usp_MergeKeyStone`, log Succeeded.
  - It does no I/O and reads the time only from `context.CurrentUtcDateTime`.
  - Four activities (`Extract_*`) are thin `[Function]` wrappers over `ExtractKeyStoneSteps` (Core). `AddCreditUnionBatch` registers the services.
- Each `CallActivityAsync` carries a `RetryPolicy` with `retry + 1` attempts and a fixed 30 s interval (`AdfRetry`).
  - The values are in `ExtractActivityPolicies.Adf`; `ExtractPolicyTests` compares each one, and `batchCount`, with the ADF JSON.
  - `ExtractDataException` and `ExtractConfigurationException` are not retried.
  - The ADF activity timeouts are enforced per attempt in the steps.
- Copy Db2 To Staging (`Db2StagingCopier` in `CreditUnion.Batch.KeyStone`) streams `DB2DataReader` through `Db2StagingReader` into `SqlBulkCopy`, batches of 10,000 with a table lock.
  - No intermediate file is written.
  - The Db2 query runs before the staging table is truncated.
  - The reader follows ADR 0005: `GetDB2Decimal` to `SqlDecimal`, CHAR padding kept, timestamps verbatim, hour-24 timestamps, non-ASCII CHAR values and scale mismatches as data errors.
  - Errors name the table, row number, masked key and column, never the value, and do not attach the driver's exception.
- Connection settings: Db2 uses KSEXTRACT from `KeyStoneDb2Options`; the reporting store uses `ReportingSqlOptions`. Logs carry counts and table names only.

**Driver-level retries (ADR 0009):**
- **SqlClient:** `ConnectRetryCount` is set to 0.
- **Db2 CLI:** it retried a refused connection by itself, up to 3 attempts per `Open` with a 5 s wait.
  - AT-04 showed it: both refusals were absorbed with no failed attempt in the trace.
  - There is no connection-string keyword, and `db2cli.ini` has no effect.
  - The only switch is a `db2dsdriver.cfg` entry for the exact database, host and port with `enableACR=false`, which the driver reads once per process.
  - `Db2DriverConfiguration` writes the file from the connection string at startup.
  - The harness registers the fault proxy's reserved port block (`127.0.0.1:15600-15615`) before its first Db2 connection.
  - `Db2ConnectionRetryProbeTests` pins the behaviour.

**Harness changes (no acceptance test, golden file or in-process host was edited):**
- The proxy and `BatchEnvironment` changes above.
- The Db2 loader inserts tables of 5,000 rows or more as 500-row `INSERT … VALUES` statements with exact literals, each committing on its own. The reset deletes in 50,000-row chunks with no command timeout. The Db2 container's log overflowed on one big transaction, and the driver's 30 s default cancelled a delete chunk.
- New tests: `ExtractDataErrorTests` (hour-24 timestamp and non-ASCII CHAR against real Db2: masked message, no retry, nothing merged, no full account number in any log or span), `ExtractPerformanceTests`, `Db2ConnectionRetryProbeTests`.

**Gate:**
- **Build:** clean, warnings as errors.
- **AT-01 to AT-04 pass.**
- **UnitTests: 165 pass** (67 new: query builder and masking, policies against the ADF JSON, `BoundedParallel`, value rules, steps with timeouts, the orchestrator on the in-process host with fakes, driver configuration).
- **Full run** (`dotnet test`, no `Category=Performance`, against the `./build.ps1 up` stack): 244 tests, **228 pass, 16 fail, 0 skipped**.
  - The 16 are AT-05 to AT-07 (Card), AT-08 to AT-13 (they now get past Extract and stop at the Card precondition), AT-14 to AT-16 and three AT-17 cases (Master), and AT-18 (trigger).
  - Each fails with the `NotImplementedException` of a stub. Card stays not-converted until Phase 5.
  - AT-19 and the 44 harness self-tests still pass.
  - The run used `CU_TESTS_USE_COMPOSE=1`; the Testcontainers mode was not rerun after this phase's changes.
- **Performance scenario** (`perf-extract-1m`, `./build.ps1 test -Performance`): **1,052,000 rows (1,000,000 transactions, 50,000 accounts, 2,000 GL entries) extracted in 46.3 s = 1,361,948 rows per minute** against the 100,000 minimum. The result matches the golden row counts and checksums. Loading those rows into Db2 took 249 s, outside the measurement. One run on a development machine.
- **Scans:** `./build.ps1 scan` found no leaks; `dotnet list package --vulnerable --include-transitive` found none.

**Notes for later phases:**
- **Retries:** any new Db2 reader must register its endpoint with `Db2DriverConfiguration`, or the CLI retries underneath the `RetryPolicy` again.
- **Deployment:** the Functions app needs a writable temp directory, or `DB2DSDRIVER_CFG_PATH` pointing to a file that carries the `enableACR=false` entry for the production KeyStone host and port.
- **ForEach failure:** `BoundedParallel` starts no new iteration after one fails. ADF's behaviour is not confirmed; it makes no difference with three tables and `batchCount` 3.
- **Phase 8:** a failed extract throws `TaskFailedException` and logs no `Failed` event, because the ADF pipeline has no failure branch. The master turns it into `BATCH_STEP_FAILED` and records the failure.
- **Phase 5:** Card settlement can reuse `BoundedParallel`, `AdfRetry`, `ExtractKeyStoneSteps`' timeout pattern and the `Connections` helpers.


## Phase 5 – Convert Card Settlement (2026-11-06)

**Implemented (`PL_IngestCardSettlement`, body of `IngestCardSettlementOrchestrator`; ADR 0010):**
- The orchestrator mirrors the ADF activities in order: log Started, reset staging, list `card-landing/{businessDate}/`, filter, then per file (at most four at once, `BoundedParallel`) copy to raw staging, validate, and either promote, archive, delete the landing file and log `Archived`, or write the error manifest, quarantine, delete and log `Quarantined`; then merge into core and log Succeeded (`RecordCount` is the filtered file count; `Notes` as the ADF expression builds them).
  - It does no I/O and reads the time only from `context.CurrentUtcDateTime`. The `PipelineRunInput` record, the function name and the output are unchanged; the containers and `retainUntil` come from `BlobStorageOptions`.
  - Each I/O activity is its own retried call (14 `Card_*` functions, thin wrappers over `CardSettlementSteps` in Core). The Filter is a pure function (`SettlementFileFilter`), matched case-insensitively (ADR 0007).
  - A quarantined file is not a failure. Any other failure throws and logs no `Failed` event.
- `SettlementCsvReader` (Core) parses the CSV as `DS_Blob_CardSettlementCsv` says and streams it; `SqlCardSettlementStore.LoadRawAsync` bulk-copies it in batches of 10,000 inside one transaction with the pre-copy delete. No intermediate file. Validation, promotion, the manifest rows and the merge are the Phase 1 procedures; nothing from the oracle's C# port is in `src`.
- `AzureCardBlobStore` (`CreditUnion.Batch.KeyStone`) lists, reads, copies (with `retain_until` and `batch_run_id` metadata), deletes (`DeleteIfExists`) and writes blobs.
- Shared: `AttemptTimeout` (the per-attempt ADF timeout, moved out of `ExtractKeyStoneSteps`) and `BatchRunLog` (one `ops.usp_LogBatchRunEvent` call, used by both pipelines' stores). `AdfRetry` also skips `CardFileDataException`.

**Retries (ADR 0010):** each `CallActivityAsync` has `retry + 1` attempts at a fixed 30 s interval (all 15 ADF policies are retry 2 and 30 s; the timeouts differ per activity). `CardPolicyTests` reads every value, `batchCount` and the parameter defaults from the ADF JSON and fails if an activity with a policy is added or renamed. SqlClient `ConnectRetryCount` is 0 and the Azure Storage SDK's `Retry.MaxRetries` is 0. No Polly. This pipeline reads no Db2, so `Db2DriverConfiguration` is untouched.

**Decisions and ADRs:** ADR 0010 covers the activity granularity, the CSV rules (BOM, LF-only row delimiter so a CR is data, empty lines skipped, quoted fields, empty is null, 10 mapped columns, 200-character limit), the blob SDK retries, idempotency and data protection. No new package: `Azure.Storage.Blobs` was already pinned.

**Gate:**
- **Build:** clean, warnings as errors (`./build.ps1 build`).
- **AT-05, AT-06 and AT-07 pass**, each with its idempotency rerun (new batch run IDs; the landing files are gone, so nothing is rewritten, `core.CardSettlement` and every blob are unchanged, and only `ops.BatchRun` and `ops.CardFileLog`'s audit growth is allowed).
- **AT-01 to AT-04, AT-19, the harness self-tests and every unit test pass; none is skipped.**
- **Full run** (`dotnet test --solution`, no `Category=Performance`): 325 tests, **312 pass, 13 fail, 0 skipped**, on both stacks:
  - `CU_TESTS_USE_COMPOSE=1` against the `./build.ps1 up` stack: 312 / 13 / 0.
  - Default Testcontainers mode (`./build.ps1 test`, fresh Db2, SQL Server and Azurite): 312 / 13 / 0. (Phase 4 had run only the compose stack.)
  - **UnitTests: 244 pass** (79 new: file filter and notes, CSV reader, policy table against the ADF JSON, the orchestrator on the in-process host with fakes, no driver-level retry).
  - **AcceptanceTests: 81 cases; 68 pass, 13 fail** (one new class, `CardBlobStoreFaultTests`, 2 cases).
- **The 13 failures**, each the `NotImplementedException` of a stub, identical on both stacks:
  - AT-08 to AT-11 (4) and AT-12, AT-13 (2): they get past Extract and Card and stop at `PL_MapAndAggregate` (Phase 6).
  - AT-14 to AT-16 (3) and the three AT-17 cases: `PL_Master_NightlyBatch` (Phase 8).
  - AT-18 (1): `TR_Nightly_0300_Pacific` (Phase 8).
- **Scans:** `./build.ps1 scan` found no leaks; `dotnet list package --vulnerable --include-transitive` found none.

**Tests added:** `CardBlobStoreFaultTests` (acceptance): Azurite behind the fault proxy refuses one connection; the store fails on its first call and makes no second connection, while a default-SDK client (the positive control) absorbs the same fault. In compose mode the test spells out Azurite's published development account key, because the `UseDevelopmentStorage` shortcut cannot be pointed at a proxy port.

**Differences from ADF a reviewer should know (all in ADR 0010):**
- The pre-copy delete and the bulk insert share a transaction (ADF's are two steps), so a failed attempt leaves no partial rows.
- Delete uses `DeleteIfExists`; ADF's behaviour on a missing file is unconfirmed.
- An unreadable file (a quoted field that never closes, or a value over 200 characters) fails at once without retries; ADF would retry the Copy twice and then fail too. The file stays in the landing folder.
- The ForEach starts no new iteration after a failure (as in Phase 4); ADF's behaviour is unconfirmed.
- The row delimiter is LF only, as the dataset says. ADF's handling of CRLF with an explicit `\n` delimiter is not documented; here a CRLF file is quarantined. Empty lines are skipped and invalid UTF-8 bytes become U+FFFD, also unconfirmed against ADF.
- The filter's case-insensitivity rests on ADR 0007's reading of ADF's `startsWith` and `endsWith`.

**Notes for later phases:**
- **Phase 6 and 7:** the Map and Recon tests now reach their own stubs. `core.CardSettlement` is what Recon compares; a card rerun leaves it unchanged.
- **Phase 8:** a failed card ingest arrives as a `TaskFailedException`; a `CardFileDataException` shows as the inner failure's type. Sub-orchestrations need the `PipelineRunInput` with the master's instance ID as `BatchRunId`.
- **Audit:** `ops.CardFileLog` is append-only. A retried `Log File …` activity whose first insert committed but whose reply was lost would add a second row (as ADF would).
- **Azure deployment:** the container names and `RetainUntil` are `BlobStorage:*` settings; the connection string holds the account key and must come from Key Vault. A managed identity would need `BlobServiceClient` constructed from a URI instead of `AzureCardBlobStore`'s connection string.
