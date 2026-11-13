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

## Phase 6 – Convert Map and Aggregate (2026-11-06)

**Implemented (`PL_MapAndAggregate`, body of `MapAndAggregateOrchestrator`; ADR 0011, ADR 0012):**
- The orchestrator mirrors the ADF activities, order and dependencies, which `MapPolicyTests` reads from the JSON: log Started, get the GL mapping from KeyBridge, cache it (`ref.usp_UpsertTxnCodeGlMap`), look up unmapped codes (`ref.usp_GetUnmappedTxnCodes`), then one of two branches:
  - **Unmapped codes:** log the `Failed` event (CheckTxnCodeMapping, TXN_CODE_UNMAPPED), then throw `PipelineFailedException(TXN_CODE_UNMAPPED, "Unmapped transaction codes: …")`. The throw runs even if the log fails, as the Fail activity's `Completed` dependency says. It is never retried, and nothing is aggregated.
  - **Otherwise:** run the data flow, then log Succeeded with `ObjectName` core.DailyGlTotals.
- The orchestrator does no I/O and reads the time only from `context.CurrentUtcDateTime`. The `PipelineRunInput` record, the function name and the output are unchanged. The KeyBridge base URL and credential come from `KeyBridgeOptions`.
- **Activities:** six `Map_*` activities, thin wrappers over `MapAndAggregateSteps` (Core), registered through `AddCreditUnionBatch`.
- **The data flow** (`DF_MapAndAggregateGl`) is a pure, streaming C# aggregator, `DailyGlTotalsAggregator` (Core):
  - Inner join on TxnCode, exact match.
  - `decimal.Round(Amount * AllocationRatio, 2, MidpointRounding.ToEven)` on the exact decimal product, once per allocation. No floating point anywhere.
  - Debit or credit by `DrCr`; per-GL-account sums; `countDistinct(TxnId)`; `NetAmount`.
  - `SqlMapAndAggregateStore` (KeyStone) only reads the two sources and replaces the date's `core.DailyGlTotals` (delete and insert in one transaction).
- **KeyBridge:** `HttpKeyBridgeClient` (KeyStone) is a typed `HttpClient` that makes exactly one request per call.
  - `HttpClient.Timeout` is the ADF `httpRequestTimeout` (1 min); `AttemptTimeout` enforces the activity timeout (5 min).
  - Basic credential from options; no redirects, no cookies; no retry handler and no HTTP-client loggers.

**Decisions and ADRs:**
- **ADR 0011 (data flow):** set-based SQL vs C#, and why C#. Also: the business-date filter (`PostingDate`, verbatim), reversals (ordinary rows), zero allocations (counted, row written), empty days (the delete still runs), `TxnCount`, and the sink transaction.
- **ADR 0012:** activity granularity; the HTTP handling and its error classification (408, 429, 5xx, timeouts and connection failures are retried; 401 and other non-2xx, unusable bodies and bad settings are not); how the mapping JSON travels and is cached; data protection; the new dependency.
- **Dependency:** `Microsoft.Extensions.Http` pinned at 10.0.9 and referenced by Functions. It was already in the graph at 6.0.0 transitively, but 6.0 lacks `RemoveAllLoggers` and logs headers at Trace without redaction. No Polly.
- **Driver retries:** SqlClient `ConnectRetryCount` is 0 (`Connections`). No Db2 and no blob are read, so `Db2DriverConfiguration` and the Storage retry settings are not involved.

**Gate:**
- **Build:** clean, warnings as errors.
- **AT-08 to AT-11 pass**, each with its idempotency rerun. AT-11 shows 3 KeyBridge calls and 2 failed attempts, retried 30 s apart under the Web activity policy (4 attempts).
- **Full run** (no `Category=Performance`), on both stacks: 450 tests, **441 pass, 9 fail, 0 skipped**.
  - Default Testcontainers mode (`./build.ps1 test`, fresh containers): 441 / 9 / 0.
  - `CU_TESTS_USE_COMPOSE=1` against the `./build.ps1 up` stack: 441 / 9 / 0.
  - **UnitTests: 363 pass** (119 new).
  - **AcceptanceTests: 87 cases; 78 pass, 9 fail** (6 new cases in `MapAndAggregateConversionTests`).
- **The 9 failures**, identical on both stacks, each the `NotImplementedException` of a stub:
  - AT-12 and AT-13 get past Extract, Card and Map and stop at `PL_Reconciliation` (Phase 7).
  - AT-14 to AT-16 (3) and the three AT-17 cases stop at `PL_Master_NightlyBatch` (Phase 8).
  - AT-18 stops at `TR_Nightly_0300_Pacific` (Phase 8).
- **Still passing:** AT-01 to AT-07, AT-19, the harness self-tests (including `GoldenConsistencyTests`' independent T-SQL statement of the rule) and every unit test.
- **Scans:** `./build.ps1 scan` found no leaks; `dotnet list package --vulnerable --include-transitive` found none.

**Tests added:**
- `DailyGlTotalsAggregatorTests`:
  - hand-checked midpoints, including the full `map-midpoint-rounding` worksheet and negative values;
  - per-allocation vs per-sum rounding;
  - D and C on the same account; reversals; zero allocations; the exact join;
  - four property tests over 500 seeds each, against an integer half-to-even reference;
  - every golden `core.DailyGlTotals` date reproduced from the golden transactions and mapping.
- `MapPolicyTests`: every policy, the dependency chain, the Fail expression, the Web request, the cache and lookup parameters and the data flow script lines, all read from the ADF JSON, plus a completeness check.
- `KeyBridgeClientTests`: the request; each status class; timeouts; refused connections; config refusals; no secret in messages; and the production registration's handler chain, logs and spans.
- `MapOrchestratorTests` (in-process host with fakes).
- `MapAndAggregateConversionTests` (acceptance):
  - the AT-17 rules applied to Map runs on three scenarios, including a retried 503;
  - a 401 is not retried;
  - an invalid mapping is not retried and leaves the cache unchanged;
  - the sink replaces only its date, atomically and in chunks.

**Test-isolation fix (approved):** `LeastPrivilegeTests.AT19_the_reader_login_cannot_write_to_the_reporting_store` now resets the reporting store first. Its admin positive control inserts `core.DailyGlTotals (2026-11-04, '1010')`, which the now-passing Map tests leave behind; the test failed whenever it ran after them. The assertions are unchanged.

**Differences from ADF a reviewer should know (ADR 0011, ADR 0012):**
- **Not retried:**
  - KeyBridge 401, 403, 404, other 4xx and 3xx (ADF retries any failure 3 times);
  - a 2xx body that is not JSON, has no `mappings` or exceeds 4 MB;
  - a mapping that `ref.usp_UpsertTxnCodeGlMap` rejects (53000 to 53004; ADF would retry twice).
- **Redirects** are not followed, so the credential reaches the configured host only. ADF's behaviour is unconfirmed.
- **A transaction with no mapping row** at aggregation time fails with `GlAggregationException`; ADF's inner join drops it silently. It is reachable only if the mapping changes after the check, or if a code differs from its mapping in case: the check is case-insensitive (database collation), the join case-sensitive.
- **The sink's delete and insert share a transaction**; ADF runs the pre-SQL and the write separately.
- **A missing `version`** in the KeyBridge response becomes NULL; ADF would fail the expression.
- **Rounding:** 1.175 rounds to 1.18 (decimal, half to even, per the PRD). Whether ADF's double-based `round()` gives 1.17 or 1.18 depends on its runtime and is unconfirmed (ADR 0011, ADR 0007).
- **Unconfirmed:** ADF is assumed to run the pre-SQL delete even when the date has no transactions.

**Notes for later phases:**
- **Phase 7 (Reconciliation)** reads `core.DailyGlTotals` by `BusinessDate`, one row per GL account (`char(10)`, padded): `DebitTotal` and `CreditTotal` (`decimal(19,2)`), `NetAmount` = Debit − Credit, and `TxnCount`.
  - Rows exist only for GL accounts touched by that date's mapped transactions.
  - A date is either completely replaced or unchanged, never partial. A Map rerun with the same inputs leaves the rows identical (no audit columns).
  - `ops.fn_ReconDetail` compares them per account and side with `core.GlEntry`.
- **Phase 8 (Master):**
  - TXN_CODE_UNMAPPED arrives as a `TaskFailedException`. `PipelineFailedException.TryParse` on its inner failure recovers the code for the master's Failed event (`BATCH_STEP_FAILED`, child code TXN_CODE_UNMAPPED).
  - Other Map failures log no Failed event (no failure branch in ADF).
  - The KeyBridge client's spans carry `url.full` (base URL plus fixed path, no credentials, no query).
  - Keep host logging of activity inputs off. The mapping JSON is an activity input; it is not sensitive, but it is large.
- **Azure deployment:** `KeyBridge:Password` comes from Key Vault (`keybridge-api-password`). A base URL containing credentials, a query or a fragment is refused at the first call.

## Phase 7 – Convert Reconciliation (2026-11-06)

**Implemented (`PL_Reconciliation`, body of `ReconciliationOrchestrator`; ADR 0013):**
- The orchestrator mirrors the ADF activities, order and dependencies, which `ReconPolicyTests` reads from the JSON: log Started; Lookup Card Totals and Lookup GL Totals together (`Task.WhenAll`); Record Recon Exceptions (a Lookup that writes); if `AlertRequired`, Post Recon Alert then Mark Recon Alert Sent; log the outcome (`Exceptions` or `Reconciled`, `RecordCount` = ExceptionCount, `Notes` as the ADF expression builds them, with SQL scale).
  - It does no I/O and reads the time only from `context.CurrentUtcDateTime`. `ReconciliationInput`, the function name and the output are unchanged.
- **Activities:** six `Recon_*` activities, thin wrappers over `ReconciliationSteps` (Core), registered through `AddCreditUnionBatch`. `SqlReconciliationStore` (KeyStone) only calls the Phase 1 procedures; the rules stay in SQL.
- **Get Alert Webhook Url is configuration, not an activity.** The URL comes from `ReconAlertWebhookOptions` (a Key Vault reference in Azure) and is read inside the posting activity. It is never an activity input or output and so never in orchestration history; a test fails if a recon contract gains a `Uri`, `Url` or `Token` property.
- **Post Recon Alert:** `HttpReconAlertClient`, a typed `HttpClient`. One POST per attempt; `Content-Type: application/json` as the ADF header says; `httpRequestTimeout` 1 min and activity timeout 2 min; the Durable `RetryPolicy` (4 attempts, 30 s) is the only retry layer. No redirects, no cookies, no HTTP-client loggers, no trace spans. Connection failures, timeouts, 408, 429 and 5xx are retried; other non-2xx and bad settings are not.
- **The alert body** (`ReconFormat.AlertBody`) is the ADF expression's body in its property order, with the summary JSON-escaped (ADF does not escape).
- **Shared:** `AdfRetry.IsRetriable` also skips the three alert exceptions and `ReconInputException`.

**Decisions and ADRs:** ADR 0013 covers the granularity and the Key Vault step, the alert body and its escaping, the NULL GL count and tolerance handling, the delivery semantics (what the code guarantees and what it cannot), and webhook URL redaction. No new package; SqlClient `ConnectRetryCount` is 0. This phase reads no Db2 and no blob, so `Db2DriverConfiguration` and the Storage retry settings are not involved.

**Webhook URL redaction (found by test, not assumed):**
- `System.Net.Http` records the full URL of a request in a span; .NET redacts only the query string and the token is in the path. A positive control in `ReconAlertClientTests` shows a default `HttpClient` puts the token into a captured span.
- Fix: `SocketsHttpHandler.ActivityHeadersPropagator = null` removes .NET's diagnostics handler for this client (no span, and no trace headers sent to the webhook), with `RemoveAllLoggers()` for the factory logs. A process-wide switch would also have removed the KeyBridge and Durable worker spans.
- Mutation check: removing that line fails the production-registration test and the acceptance log scan.

**Gate:**
- **Build:** clean, warnings as errors (`./build.ps1 build`).
- **AT-12 and AT-13 pass**, each with its idempotency rerun. AT-13 shows exactly one alert, equal to the golden alert, and the rerun posts none.
- **Full run** (no `Category=Performance`): 556 tests (Phase 6 ended at 450; 106 new).
  - `CU_TESTS_USE_COMPOSE=1` against the `./build.ps1 up` stack: **549 pass, 7 fail, 0 skipped.**
  - Default Testcontainers mode (`./build.ps1 test`): **548 pass, 8 fail, 0 skipped.** The 8th is a flake, below.
  - **The 7 failures**, identical on both stacks, each the `NotImplementedException` of a stub: AT-14 to AT-16 and the three AT-17 cases (`PL_Master_NightlyBatch`, Phase 8) and AT-18 (`TR_Nightly_0300_Pacific`, Phase 8).
  - **Known flake, not fixed:** `LogCaptureAndPolicyTests.Findings_cover_…` (a Phase 3 harness self-test) failed once. `LogCapture` listens to every `ActivitySource` in the process, so an HTTP span from a test running at the same time lands in its capture and shifts its expected `span #1` to `span #5`. The KeyBridge tests could cause it too; the new HTTP tests make it more likely. It passes alone and on the compose run. Proposed fix, for approval because it edits an existing harness test: have that test count only spans from its own `ActivitySource`.
- **Scans:** `./build.ps1 scan` found no leaks; `dotnet list package --vulnerable --include-transitive` found none.

**Tests added:**
- `ReconPolicyTests`: every policy, the dependency chain, the If expression, both Web activities (Key Vault read and POST), the lookup and Mark parameters, the tolerance default, and the Notes, Status and RecordCount expressions, all read from the ADF JSON, plus a completeness check over the nested activities. The body test fills the ADF expression's placeholders and requires the same bytes.
- `ReconFormatTests`: notes, scale and culture, the golden alert, escaping (quote, backslash, control characters, a crafted summary, non-ASCII, a lone surrogate), 500 random amounts, tolerance validation.
- `ReconOrchestratorTests` (in-process host, a fake store that applies the procedures' alert rule): order, parallel lookups, retries 30 s apart, a webhook that stays down, a rejected request, a POST delivered but timed out, a Mark that fails after a delivered POST, a Record that committed but lost its reply, a changed difference.
- `ReconAlertClientTests`: the request, every status class, timeout, refused connection, cancellation, unsafe URLs, the production handler chain, logs and spans with a positive control, and a probe of the platform's exception messages.
- `ReconciliationConversionTests` (acceptance): the AT-17 rules on Reconciliation runs (two scenarios, a retried 503); a webhook that is down for four attempts and then a rerun that alerts once; a rejected 404; a GL difference beyond and equal to the tolerance; an empty date; a tolerance SQL would round.

**Differences from ADF a reviewer should know (ADR 0013):**
- The Key Vault step has no counterpart: its retry (2, 30 s) and timeout (1 min) do not exist, and a missing URL fails the Post activity.
- Not retried: 4xx other than 408 and 429, 3xx, a missing URL; ADF retries any failure 3 times. Redirects are not followed.
- The summary is JSON-escaped; ADF would send invalid JSON for a quote or backslash.
- `ItemsOutOfTolerance` is NULL in SQL when no GL item exists; it reads as 0 in the body and the notes (ADF would send invalid JSON and an empty note).
- A negative tolerance, or one with more than two decimals, fails with `ReconInputException`; ADF would let SQL round it.
- Delivery is at-least-once: a POST delivered but not acknowledged is sent again, and a Mark that fails after a delivered POST makes the next run post again. A lost alert was judged worse than a repeat. See ADR 0013 for the full table.
- Unconfirmed against ADF: how it prints a `decimal(19,2)` from a Lookup (`0.10` or `0.1`) and what a parallel branch does when its sibling fails.

**Notes for later phases:**
- **Phase 8 (Master):**
  - A failed Reconciliation arrives as a `TaskFailedException`; the inner type says why (`ReconAlertUnavailableException`, `ReconAlertRejectedException`, `ReconAlertConfigurationException`, `ReconInputException`, or a SQL error). No `Failed` event is logged, because the ADF pipeline has no failure branch.
  - On a failure after Record, the exceptions are already in `ops.ReconExceptions` and unalerted if the POST never succeeded, so a rerun alerts them once. Decide in the master whether a recon alert failure fails the batch.
  - The master's rerun for a date whose alert was delivered and marked posts nothing; a changed difference is a new exception and alerts again.
  - The evidence export (`ops.usp_GetRunEvidence`) reads `ops.ReconExceptions` including `alertSentUtc` (`-` when the alert was never sent, as after a failed POST).
- **Webhook redaction:** AT-17's three cases run the same recon path through the master. The client already has no span or HTTP logger, so they need nothing more here, but keep host logging of activity inputs off (the alert summary is an activity input).
- **Azure deployment:** `ReconAlertWebhook:Url` must come from Key Vault (secret `recon-alert-webhook-url`) as an app-setting reference or a configuration provider. A URL that is not absolute http(s) is refused at the first alert.

## Phase 8 – Orchestrate (2026-11-06)

**Implemented (`PL_Master_NightlyBatch`, body of `NightlyBatchOrchestrator`; `TR_Nightly_0300_Pacific`, body of `NightlyBatchTrigger`; ADR 0014 to ADR 0017):**
- **The master** mirrors the ADF activities, order, dependencies and conditions, which `MasterPolicyTests` reads from the JSON:
  - Resolve Business Date: a pure function, `BusinessCalendar.Resolve`.
  - Log Batch Started, with Notes `trigger=…; triggerTimeUtc=…`.
  - Extract and Card Settlement as parallel sub-orchestrations (`Task.WhenAll`).
  - Map after both; Reconciliation after Map, with `reconTolerance`.
  - Export Run Evidence; Log Batch Succeeded, with the evidence path as ObjectName.
  - Five failure branches ("Log Failed At …" then "Fail At …").
- **Children:** each gets `PipelineRunInput` or `ReconciliationInput` with the master's instance ID as `BatchRunId`, and the instance ID `{batchRunId}:{pipeline}`. The name, input, output and instance-ID rule are unchanged (ADR 0008).
- **Failure branches** (`StepFailure`, Core):
  - The Failed event carries the step name and the child's code: `TXN_CODE_UNMAPPED` via `PipelineFailedException.TryParse`, otherwise the root cause's type name (e.g. `DB2Exception`). The message is the child's.
  - The master fails with `BATCH_STEP_FAILED: {step} failed: {message}`, with the child's chain as the inner failure. No later step runs.
  - If the Failed event cannot be logged, the master still fails (the Fail activity's `Completed` dependency).
  - If both parallel children fail, both Failed events are logged and the master names Extract.
  - An undeliverable reconciliation alert fails the batch at Reconciliation.
- **Evidence export** (`Master_ExportRunEvidence`; `RunEvidenceFile` and `NightlyBatchSteps` in Core; `SqlNightlyBatchStore` and `AzureEvidenceBlobStore` in KeyStone):
  - `ops.usp_GetRunEvidence` is streamed with `SequentialAccess` through `Utf8JsonWriter` into staged blocks, which are committed with `retain_until` metadata only when complete.
  - The file is `{EvidenceContainer}/{date}/batch-run-{runId}.json`, an array of `{section, item, value}` in the procedure's order, UTF-8 without BOM.
  - A new run ID writes a new file; the same run ID (a retry) overwrites with a complete one.
- **The trigger** reads the fire time from the injected `TimeProvider` and maps it to the latest 03:00 Pacific at or before it (5 minutes' early tolerance when on time, none when past due, `NightlySchedule`). It starts one master per invocation under a new GUID, with `BusinessDate` = the previous Pacific day, `TriggerName` and `TriggerTimeUtc` = the occurrence. It logs a warning when past due.
  - Signature change: the trigger gained a `FunctionContext` parameter, to reach the `TimeProvider` and the logger. Its name, schedule and other bindings are unchanged.
- **Determinism:** the orchestrators do no I/O and read time only from `context.CurrentUtcDateTime`. The Durable analyzer is live: probes of `DateTime.UtcNow`, `Guid.NewGuid`, `Task.Delay`, environment variables and the HTTP, SQL and Blob clients each failed the build (DURABLE0001 to 0006). `File.Exists` was not flagged. The replay test is ADR 0017.
- **Log hygiene:** `host.json` sets `durableTask.tracing.traceInputsAndOutputs` and `traceReplayEvents` to false; a unit test reads the file. Orchestrator logs carry the step, code, error type and date only.
- **Retries:** `AdfRetry` on every activity call (3 attempts, 30 s; timeouts 5 and 10 min). The sub-orchestrations have no retry, because the Execute Pipeline activities have no policy. `RunEvidenceInputException` is not retried. No Polly; the Storage SDK and SqlClient retries stay off.
- **No new package.** The replay test uses `Microsoft.DurableTask.Worker.Grpc` and `Microsoft.DurableTask.Grpc` 1.27.0, already in the graph through the Durable worker extension.

**Harness fix (approved):** `LogCaptureAndPolicyTests.Findings_cover_…` now counts only spans from its own `ActivitySource`. It drops findings about other sources' spans and numbers the expected span by its real position, so a concurrent test's HTTP span can no longer shift `span #1`. The assertions on logs, host text, the secret and the numbers are unchanged. No acceptance test, golden file or in-process host file was edited.

**Decisions and ADRs:**
- **ADR 0014:** activity mapping; failure propagation and the child's error code (root-cause type when there is no ADF code); a failed Failed-event log; both parallel children failing; steps without a failure branch; an alert failure fails the batch; no retry on sub-orchestrations; log hygiene through sub-orchestrations.
- **ADR 0015:** the evidence format and path; `retain_until` as metadata, not an index tag (ADR 0004's index-tag sentence was never implemented and is now marked superseded); all-or-nothing staged-block commit; idempotency (new ID: new file; same ID: overwrite).
- **ADR 0016:** `WEBSITE_TIME_ZONE`; occurrence and business-date rule; `TriggerTimeUtc` is the occurrence; missed fires (one past-due run, no back-fill, a runbook); duplicate fires and reruns of a processed date (allowed, not deduplicated, risks stated); manual start without an HTTP starter.
- **ADR 0017:** analyzer rules plus a replay test on the worker's own engine, and what it does and does not prove.

**Determinism and replay check (ADR 0017):**
- `ReplayBackend` runs all five orchestrators episode by episode through `GrpcOrchestrationRunner.LoadAndRun`, the isolated worker's real replay path, and records genuine Durable histories. `ReplayCheck` replays every episode 30 ms later, and the final history twice more, and requires identical actions.
- Scenarios: a successful batch (quarantined file, retried alert with a durable timer) and a failed one (Extract retried, Map TXN_CODE_UNMAPPED, master BATCH_STEP_FAILED on the real engine).
- Controls: a wall-clock input is caught; a branch on changing state is rejected by the engine (`NonDeterministicOrchestrationException`); a context-only orchestrator passes.
- Mutation check: a hidden static counter in the master failed the test at episode 1. It was reverted.
- It does not prove unscripted paths, non-determinism that yields the same value twice, or the Functions extension's own wrapper (the `func start` check covered that once).

**`func start` check (what was run and seen):**
- Azure Functions Core Tools was not installed. With approval it was installed with `npm i -g azure-functions-core-tools@4`. The package's post-install step downloaded the 4.16.0 zip but did not extract it under Node 24, so the zip was extracted by hand into `%APPDATA%\npm\node_modules\azure-functions-core-tools\bin`. `%APPDATA%\npm` is not on PATH; it was added for the session.
- **Stack and settings:** `./build.ps1 up` was already running. `./build.ps1 run` was started with settings as environment variables taken from `.env` (`KeyStoneDb2__ConnectionString` as ksextract, `ReportingSql__ConnectionString` as cu_batch_writer, `BlobStorage__ConnectionString=UseDevelopmentStorage=true`, KeyBridge on the compose WireMock at `http://localhost:8081/api`, a webhook URL on the same WireMock).
- **Host start:**
  - The host started (Core Tools 4.16.0, runtime 4.1054) and indexed all 37 functions.
  - It printed the next occurrences of `TR_Nightly_0300_Pacific` as `11/07/2026 03:00:00-08:00 (11/07/2026 11:00:00Z)`, confirming the schedule is evaluated in Pacific time.
- **Manual run:**
  - The command was `func durable start-new --function-name PL_Master_NightlyBatch --id 83bd62b3-c425-4042-9947-482c5418d474 --input @input.json --task-hub-name CreditUnionBatch`, with the input `{"BusinessDate":"2026-11-04"}`. The DB held `standard-day` with Extract not yet merged.
  - The run **completed**. The real Durable history showed Extract and Card created in the same episode, then Map, Recon, Export Run Evidence and Log Batch Succeeded.
  - `ops.BatchRun` gained 17 events for the run ID, all dated 2026-11-04:
    - master Started (`trigger=Manual; triggerTimeUtc=2026-11-06T23:15:48…Z`);
    - Extract 5,000 / 800 / 300 rows copied and merged as inserts;
    - Card: no landing files left;
    - Map Succeeded;
    - Recon Reconciled (`card difference=0.00; GL items out of tolerance=0; tolerance=0.00`);
    - master Succeeded with ObjectName `evidence/2026-11-04/batch-run-83bd62b3-….json`.
  - The blob existed in Azurite: `application/json`, `retain_until=TBD`, 7,235 bytes, 48 entries.
  - An earlier attempt passed the input inline. Through the npm shim it lost its quotes and became a JSON string, and the master failed at once with "could not be converted to NightlyBatchInput", writing nothing. Hence `@input.json` (ADR 0016).
- **Timer run:**
  - The command was `POST http://localhost:7071/admin/functions/TR_Nightly_0300_Pacific` (HTTP 202) at 23:19:18Z, which is 15:19 Pacific.
  - The trigger logged `Started PL_Master_NightlyBatch 9a7c2d4c-… for business date 2026-11-05 (occurrence 2026-11-06 11:00:00Z…)`. The master **completed** with Started (`trigger=TR_Nightly_0300_Pacific; triggerTimeUtc=2026-11-06T11:00:00.0000000Z`), the four children (an empty day), Reconciled, and Succeeded with `evidence/2026-11-05/batch-run-9a7c2d4c-….json`.
  - This check exposed a flaw in the first trigger design, which used the nearest occurrence: an afternoon run would have processed the current, incomplete day. The design was fixed to "latest at or before" before this run, and a unit test pins the case.
- **Host logs:** both runs' host logs (about 40,000 lines, mostly Azure SDK traffic) were scanned. They contain none of the four secrets used (KSEXTRACT and cu_batch_writer passwords, KeyBridge password, webhook token), no orchestration input and no mapping JSON. The host was then stopped.

**Gate:**
- **Build:** `./build.ps1 build` clean, warnings as errors.
- **Full suite, no `Category=Performance`, 647 tests (549 unit, 98 acceptance), on both stacks:**

  | Stack | Run 1 | Run 2 |
  | --- | --- | --- |
  | `CU_TESTS_USE_COMPOSE=1` (the `./build.ps1 up` stack) | 647 pass, 0 fail, 0 skipped (4 m 18 s) | 647 pass, 0 fail, 0 skipped (3 m 37 s) |
  | Testcontainers (`./build.ps1 test`, fresh containers) | 647 pass, 0 fail, 0 skipped (7 m 34 s) | 647 pass, 0 fail, 0 skipped (6 m 55 s) |

- AT-01 to AT-19 all pass, each with its rerun assertion, none skipped. Phase 7 ended at 556 tests (549 pass, 7 fail): the 7 stubs now pass and 91 tests are new.
- **Scans:** `./build.ps1 scan` found no leaks; `dotnet list package --vulnerable --include-transitive` found none.

**Tests added (91):**
- `MasterPolicyTests`: every policy, the dependency graph including the `Failed` and `Completed` conditions, the child parameters, the parameter defaults, the Resolve, Notes, ObjectName, Failed and Fail expressions, the Copy source and sink, the dataset, the non-retried input error, and `host.json` tracing.
- `BusinessDateAndScheduleTests`: hand-checked midnight and DST cases, occurrences on both change days, early, late, past-due and afternoon fires, the golden DST schedule, the attribute against the ADF trigger and `local.settings.json`.
- `MasterRunEventsTests`: the Failed-event builder (coded, uncoded, deep chain, cutting), the Started, Succeeded and Failed events, the evidence path and metadata, the `retainUntil` validation, the JSON format.
- `MasterOrchestratorTests` (in-process host, the real children, fakes): order and parallelism; children's inputs and instance IDs; no sub-orchestration retry; evidence content; manual and triggered notes; every failure branch, including both children failing, the Failed log failing, an alert failure, evidence retries and permanent evidence failure, the Started log failing, and a non-GUID ID.
- `NightlyTriggerTests`: one master per invocation, a new GUID each time, DST days, early fire, past due, the started master's events.
- `OrchestratorReplayTests` (5): ADR 0017.
- `NightlyBatchConversionTests` (acceptance, 3):
  - Db2 refused through the master: Failed at Extract with `DB2Exception`, Card finishes, no evidence, a rerun changes nothing, and the AT-17 rules hold on the logs and failure details.
  - The evidence file equals the procedure's result set in order.
  - The sink against Azurite: a failed write leaves no blob, a failed retry leaves the old file intact, a three-block file is committed.

**Differences from ADF a reviewer should know (ADR 0014 to ADR 0016):**
- **Child error codes:** TXN_CODE_UNMAPPED is kept; a child that failed in an activity is recorded with the root cause's .NET type name (e.g. `DB2Exception`, `TimeoutException`), not an ADF numeric code. ErrorMessage is Durable's wording, not ADF's.
- **Parallel failure:** when both Extract and Card fail, both Failed events are logged and the master names Extract. Whether ADF cancels the running sibling, and which Fail message it reports, is unconfirmed.
- **Failure without a branch:** a failed Log Batch Started or Succeeded fails the master with the activity's failure, not BATCH_STEP_FAILED, and logs no Failed event. ADF also has no branch there.
- **Evidence:**
  - Written atomically; overwritten only by the same run ID.
  - `retain_until` is metadata, as in ADF, with no index tag.
  - ADF's indentation, BOM and NULL handling are unconfirmed; only the deterministic sections are compared.
  - An unusable `retainUntil` fails at once, without retries.
- **Trigger:**
  - `TriggerTimeUtc` is the scheduled occurrence, not the actual fire time.
  - A past-due start-up runs the latest missed day once and does not back-fill.
  - A duplicate fire runs a second, idempotent master under a new GUID; two masters for one date running at the same time are not prevented.
  - A manual run's trigger time is the orchestration's first execution time.

**Not done in this phase (stated, not hidden):**
- **OpenTelemetry export** to the Aspire dashboard (PRD observability NFR) is not configured. `Program.cs` adds no OpenTelemetry, and it would need new packages. The batch emits `ILogger` logs and .NET's own spans only. A follow-up should add the OTLP exporter and a `batchRunId` tag, and decide how the Durable worker's spans correlate.
- **A retain-until blob index tag** (PRD wording, ADR 0004) is not set anywhere (ADR 0015).

**Notes for Phase 9 (README and migration playbook):**
- **Sub-orchestrations:**
  - Execute Pipeline (`waitOnCompletion`) becomes `CallSubOrchestratorAsync`, parallel children `Task.WhenAll`, and the child gets the master's instance ID as `batchRunId`.
  - Retry a sub-orchestration only if the ADF activity has a policy.
  - Give children deterministic, readable instance IDs.
- **Failure branches:**
  - Map "Execute → on Failed: Log → on Completed: Fail" to try/catch around the call, log Failed with `StepFailure`, and throw `PipelineFailedException(BATCH_STEP_FAILED)`.
  - The child's code survives only if it was a `PipelineFailedException`; otherwise it is the root-cause type.
  - Say what happens when the log fails and when parallel siblings both fail.
- **Evidence export:**
  - Copy of a procedure to a JSON blob: stream it, and commit staged blocks only when complete (never `OpenWriteAsync`).
  - Put the run ID in the path; metadata versus index tags; write-once storage is a deployment policy.
- **Trigger and time zone:**
  - `WEBSITE_TIME_ZONE` per environment (Windows `Pacific Standard Time`, Linux `America/Los_Angeles`); 03:00 avoids DST hours.
  - The business date comes from the occurrence, not the fire time; mention the afternoon pitfall.
  - Past-due and no-back-fill runbook; `RunOnStartup` false.
  - Manual starts: `func durable start-new --input @file` and the admin endpoint.
  - Core Tools via npm needed a manual extract on Node 24; winget is the PRD's route.
- **Alerting semantics:**
  - An undeliverable alert fails the batch at Reconciliation and alerts on the next run.
  - A delivered and marked alert is silent on reruns; delivery is at least once (ADR 0013).
- **Log hygiene:**
  - `traceInputsAndOutputs` false; secrets never cross Durable boundaries; exception messages carry no values; mask numbers with `LogMask`.
  - AT-17 scans logs, spans and failure details through the master; the real host's own logs were scanned in the `func start` check.
- **Determinism:** the analyzer rules are build errors, and `ReplayBackend` and `ReplayCheck` are the pattern for replay-testing any new orchestrator.

## Phase 9 – Document (2026-11-06)

**Delivered (documentation and one test; no change to `src`, the acceptance tests, golden files, ADF JSON or the harness):**
- `README.md`, rewritten for a reviewer who has never seen the repo: what it is and is not, prerequisites with versions, the one command, every `build.ps1` command with measured times, the fast loop, running under `func start`, what AT-01 to AT-19 prove, known limitations, troubleshooting, a documentation map.
- `docs/migration-playbook.md`: the mapping, the five-step process with "here" and "at the credit union" for each step, 16 pattern sections (decision, reason, pitfall found), a checklist of every "difference from ADF" and "unconfirmed against ADF" item, an Azure deployment checklist, and the traceability table (61 rows: every activity in `adf/pipeline/*.json`, nested ones included) plus a table for the other ADF resources.
- `tests/CreditUnion.Batch.UnitTests/Docs/DocumentationConsistencyTests.cs` (5 tests, no new package):
  - each ADF activity has exactly one traceability row, and each row's type and policy equal the ADF JSON, and it names an implementation and an existing ADR;
  - every relative link in `README.md` and `docs/**/*.md` resolves, including heading anchors;
  - every `./build.ps1 <command>` and switch in the README exists in `build.ps1`, and every command is documented.
- Mutation check of the new test: deleting a row, changing a policy, a broken link, a broken anchor, an unknown command and an unknown switch each failed it.
- The ADF columns of the traceability table were generated from the JSON by a script. The .NET and ADR columns were written by hand from the ADRs.

**Fresh-clone walkthrough (the PRD gate).**
- **Setup:** `git clone` of HEAD (`5607c18`) into `C:\src\cu-clean`: no `.env`, no `bin` or `obj`. The uncommitted Phase 9 files were copied over it. A new NuGet package folder (`NUGET_PACKAGES=C:\src\nuget-cold`) emulated a machine that had never restored the repo. The dev stack was stopped, so the Testcontainers run was timed alone.
- **What the README did not say, and now does:**

  | # | Found | Change |
  | --- | --- | --- |
  | 1 | The first clone sat in a deeply nested temporary folder, and `./build.ps1 build` failed with `MSB3030: Could not copy the file ... because it was not found`. The path was 279 characters, over Windows' 260. The same clone built at `C:\src\cu-clean`. The longest path found is 169 characters below the repo root, so the root must stay under about 90 characters. | Prerequisites and troubleshooting require a short clone path. Long-path support was not tried. |
  | 2 | `func durable start-new` fails ("Unable to find project root") outside `src/CreditUnion.Batch.Functions`. | The README says to run it from that folder. |
  | 3 | After `up` and `seed`, Db2 holds only reference data, so a manual run processes an empty day. | The README adds a step that loads `standard-day` by running AT-02 against the stack (13 s), and says what the run then shows. |
  | 4 | Nothing said how to read the evidence blob. | A PowerShell Shared Key snippet with Azurite's published development key. It was extracted from the README and run verbatim. |
  | 5 | My first `ops.BatchRun` query ordered by column 1, a GUID. | Ordered by `BatchRunEventId`. The final query was run through `docker exec` as `cu_report_reader`. |
  | 6 | Db2 took 3 min 43 s to be ready under Testcontainers (container start 17:12:27, readiness polling ended 17:16:11). The PRD says one to three minutes. | Troubleshooting and the timings table state the measured figures. |
  | 7 | The stub README promised traces in the Aspire dashboard. The batch exports none. | Corrected, and listed as a gap. |

- **Not fixed, because it changes the repository: the fresh clone failed 34 of 652 tests** (618 passed, 0 skipped; 7 min 59 s including the build).
  - Cause: `.gitignore` excludes `evidence/` and `*.tmp`, so ten golden files (`tests/TestData/golden/*/evidence/run-N.json`) and `tests/TestData/inputs/standard-day/blobs/card-landing/2026-11-04/settlement_004.csv.tmp` were never committed. `git ls-files | grep evidence/` lists nothing.
  - Failing tests: the six drift tests (`GoldenFilesTests`), and every test that loads `standard-day` or an evidence file, such as AT-01, AT-02, AT-04, AT-05, AT-08, AT-11, AT-12, AT-14, AT-16, AT-17 and AT-18, and the golden consistency tests.
  - Diagnosis check: the 11 files were copied from the working tree into the clone. Then **652 of 652 passed, 0 skipped**, in 6 min 54 s (`./build.ps1 test`, Testcontainers).
  - Proposed fix, for approval: add `!tests/TestData/**/evidence/` and `!tests/TestData/**/*.tmp` to `.gitignore`, and commit the 11 files. The README states the problem under "Known limitations"; remove that note when it is fixed. Phase 8's 647 passes ran in the working tree, where the files exist, so they could not see this.
- **Also checked, in the working tree against the stack:**
  - `./build.ps1 run` started the host. `func durable start-new` for 2026-11-04 completed with 17 `ops.BatchRun` events (three settlement files, 1,857 rows, Reconciled, evidence written).
  - The timer function, run through the admin endpoint (HTTP 202), started a master for 2026-11-05.
  - The evidence blob read back with HTTP 200, `retain_until` = `TBD`, 51 entries.
  - The host log (32,756 lines) contained none of the four secrets used.
- **What this cannot prove:**
  - winget installs: the package IDs and current versions were checked with `winget show`; nothing was installed.
  - Docker Desktop setup and the WSL memory setting on a new machine. This machine has no `.wslconfig` and Docker gets 15.5 GB by default, so the 8 GB advice comes from the PRD and was not tested.
  - A first image pull. All images were already present.
  - A machine with a different execution policy, drive, user or Windows version. The npm route for Core Tools was not repeated (Phase 8 recorded it).
  - `./build.ps1 up` and `run` were followed in the working tree, not in the clone, because the clone shares fixed container names with the dev stack. `up` was timed after `docker compose down`: images and volumes present, Db2 empty.
  - The cold NuGet cache was emulated with a separate package folder, on a machine with network access.

**Gate:**
- **Docs complete:** README and playbook cover the required items. The consistency test passes (5 tests).
- **Build:** `./build.ps1 build` is clean, warnings as errors, in the working tree and in the fresh clone (124 s on a cold cache).
- **Full suite, no `Category=Performance`: 652 tests (Phase 8's 647 plus 5 new), 0 skipped.**

  | Where | Result | Time |
  | --- | --- | --- |
  | Fresh clone of the committed repo, Testcontainers | 618 pass, **34 fail** (the `.gitignore` defect) | 7 min 59 s with the build |
  | Fresh clone plus the 11 files, Testcontainers | 652 pass, 0 fail | 6 min 54 s |
  | Working tree, `CU_TESTS_USE_COMPOSE=1`, stack freshly started | 652 pass, 0 fail | 3 min 38 s |

  The gate "the full suite passes once from the fresh clone" is therefore met only with the 11 files.
- **Performance test** (`./build.ps1 test -Performance`, against the stack): the test passed, so it met the assertion of at least 100,000 rows per minute. The whole command took 5 min 24 s (324 s) against the running stack. The test prints its rate only when it fails, so no new rate was recorded; the last recorded figure is Phase 4's 1,052,000 rows in 46.3 s (1,361,948 rows per minute).
- **Scans:** `./build.ps1 scan` (gitleaks) found no leaks (20 s). `dotnet list package --vulnerable --include-transitive` found none in any project. Both ran in the working tree, after the Phase 9 files were written.

**Definition of done (PRD):**

| Item | Status |
| --- | --- |
| All acceptance tests AT-01 to AT-19 pass, none skipped, two consecutive runs | **Met in the working tree** (Phase 8: two runs on each stack; Phase 9: two more full runs, 652 of 652). **Not met from a fresh clone of the committed repo** until the `.gitignore` defect is fixed (34 failures). |
| Golden outputs signed off by a person who knows the business rules | **Not met.** An engineering review signed them off (ADR 0007). A business reviewer must confirm the four decisions, and real ADF output must be compared, before go-live. |
| Secret scan, vulnerability scan and analyzers pass | **Met** (see Scans; the analyzers run as build errors). |
| README, migration playbook and ADRs complete | **Met, with two gaps:** no ADR records why Dapper and CsvHelper (named in the PRD) are not used; and OpenTelemetry export (a PRD requirement) is not implemented, which is documented as a gap. |
| Every ADF activity traced to its .NET implementation in the playbook | **Met.** 61 activities, checked by `DocumentationConsistencyTests`. |

**Open items and risks found while writing (decisions for the repository owner):**
1. **The `.gitignore` defect** above: it needs a decision to fix and commit.
2. **No ADR for Dapper and CsvHelper.** The code uses plain `SqlClient` and an own CSV reader. The reason was never written down, so the docs say so and do not invent one.
3. **Immutability versus overwrite (unverified).** Evidence, archive and quarantine writes overwrite a blob of the same name on a retry (ADR 0010, ADR 0015). A container immutability policy, which ADR 0015 suggests for examiners, would likely refuse that overwrite. It was not tested; Azurite has no such policy. The deployment checklist lists it.
4. **Db2 secret name.** The ADF JSON stores only the Db2 password in Key Vault, but the .NET options need a full connection string. No ADR decides how to store it.
5. **OpenTelemetry export** is still not implemented (recorded in Phase 8).
6. **Long clone paths** break the build (above).
7. **No CI.**

## Phase 9 follow-up – loose ends (2026-11-06)

Done after commit `3a5b65d`, at the owner's request ("fix whatever you can"). No change to `src`, the acceptance tests, golden files, the ADF JSON or the harness.

Commit hashes in this file refer to the history after the author email was changed to the GitHub noreply address (same day, before publishing). Every commit kept its content, message and dates.

- **`.gitignore` defect fixed.** Two rules at the end of `.gitignore`, `!tests/TestData/golden/**/evidence/` and `!tests/TestData/inputs/**/*.tmp`, make the 11 missing test-data files visible to git. `git status` showed exactly those 11 and nothing else. They contain only LF (no CR, no NUL), as `.gitattributes` expects for test data, and no secrets or card numbers.
  - **Round-trip check:** the change and the files were committed in a scratch clone (not this repository), and that clone was cloned again. All 11 files were byte-identical after the round trip. `./build.ps1 test` in the second clone (Testcontainers, no `bin` or `obj`): **652 passed, 0 failed, 0 skipped, 8 min 31 s including the first build.**
  - The README's "Known problem" note and its first "Known limitations" item were removed. They are true again only if the 11 files are not committed together with `.gitignore`.
- **ADR 0018** (Proposed, retrospective) records that the code uses plain `SqlClient` and `SettlementCsvReader` rather than Dapper and CsvHelper. It says plainly that the original reason was not recorded, and gives only reasons visible in the code. It lists, as not tested, whether CsvHelper could reproduce the ADR 0010 cases.
- **ADR 0019** (Proposed) sets out the Db2 secret options. It recommends keeping `keystone-db2-extract-password` and adding a `KeyStoneDb2:Password` setting, a code change that needs approval. It also records a gap not written down before: the ADF linked service uses TLS for Db2 (port 50001, `certificateCommonName`), the .NET connection string sets no TLS keywords, and Db2 TLS was never tested.
- README, playbook mapping table and deployment checklist now point to ADR 0018 and 0019. The README's Db2 start-up time in Troubleshooting now matches the measured figure.
- `DocumentationConsistencyTests` 5 of 5 pass, and `./build.ps1 scan` found no leaks.

**Status changes for the Phase 9 definition of done:**
- All acceptance tests pass from a fresh clone: **met once the 11 files and `.gitignore` are committed.** Proven in a scratch round trip, not yet in this repository's history.
- README, playbook and ADRs: the Dapper/CsvHelper gap now has an ADR, still Proposed. OpenTelemetry remains a gap.

**Still open:** business sign-off of golden outputs (ADR 0007); approval of ADR 0018 and ADR 0019; immutability versus overwrite; retention period; retain-until index tag; OpenTelemetry export (needs approval to change `src`); CI (the repository has no remote); Azure deployment; long clone paths.

## Azure template – Bicep for the Function App (2026-11-13)

Done at the owner's request. Deploying stays a v1 non-goal (PRD). This turns the deployment checklist's hosting rows into a template that compiles and is tested against the code, but is **not deployed**.

- **ADR 0020** (Proposed) chooses **Elastic Premium EP1 on Windows**:
  - Windows Consumption stops a function after 10 minutes, and ADF timeouts reach 2 hours.
  - Flex Consumption is Linux-only and needs the untested `-lnx` Db2 driver.
  - EP1 supports VNet integration for the private path to Db2.
- **`infra/main.bicep`** creates:
  - a user-assigned identity;
  - identity-only host storage (shared key off), with a `deployments` container for the run-from-package URL, so there is no Azure Files;
  - Log Analytics and Application Insights, with local auth off;
  - the EP1 plan and the Function App, with optional VNet integration.
- **`infra/modules/shared-access.bicep`** grants the identity Key Vault Secrets User on the existing vault. It also writes `batch-blob-storage-connection-string` from the existing data account's key. That is a stopgap until the blob code uses an identity.
- **`infra/main.prod.bicepparam`** is an example parameter file, using the ADF names for the vault and the storage account.
- **Settings and secrets.** Key Vault references use the ADF secret names. Db2 uses ADR 0019 option A (`keystone-db2-extract-connection-string`) until that ADR is decided.
- **`host.json` `functionTimeout` = `02:30:00`.** It was unset, and Premium's 30-minute default would have killed `Copy Db2 To Staging` (1 h) and `Aggregate Daily GL Totals` (2 h) before their per-attempt timeouts fired.
- **`./build.ps1 infra`** compiles and lints the template and the parameter file with the Bicep CLI (0.49.2, about 10 s). It reports no warnings.
- **`InfrastructureTemplateTests`** (7 tests) read the template as text and check:
  - option app settings against the options classes;
  - secret names against the ADF JSON;
  - `WEBSITE_TIME_ZONE` against the trigger;
  - `functionTimeout` against the longest ADF timeout;
  - the task hub prefix.

  Breaking the template on purpose (removing `ReconAlertWebhook__Url`, renaming a secret, setting `UTC`, setting `functionTimeout` to 1 h 30 min) made 5 of the 7 fail, as intended.
- **Docs.** README (commands, limitations, documentation map) and the playbook (Step 5 and checklist rows) point to the template.
- **Results:**
  - Full suite (`dotnet test`, performance excluded, Testcontainers): **659 passed, 0 failed, 0 skipped, 7 min 28 s.**
  - `./build.ps1 scan`: no leaks. Its first run flagged the Key Vault Secrets User role GUID in a variable named `…SecretsUser`. That was a false positive, fixed by renaming the variable.

**NuGet audit failure, fixed by upgrading WireMock.Net.**
- **Cause.** `./build.ps1 build` and `./build.ps1 test` failed with NU1902 (warnings as errors). `Microsoft.AspNetCore.WebUtilities` 2.3.0 has a moderate advisory, GHSA-6527-9fp8-r4x6 (CVE-2026-118750, published 2026-11-10, patched in 2.3.14). It came in through `WireMock.Net.Minimal` 2.19.0 → `WireMock.Net.Shared` → `Microsoft.AspNetCore.Http` 2.3.9, in the three test projects only. It was not caused by this change.
- **Fix.** At the owner's choice, `WireMock.Net.Minimal` went from 2.19.0 to **2.31.0**. That is the only release that clears the advisory: it depends on `Microsoft.AspNetCore.Http` and `WebUtilities` 2.3.14. 2.21.0 and 2.30.0 still pull 2.3.0.
- **Age.** 2.31.0 was published 2026-11-12, one day before the upgrade. It was taken anyway because no older release has the fix.
- **After the upgrade:**
  - `dotnet list package --vulnerable --include-transitive` finds nothing in any project;
  - `./build.ps1 build` succeeds;
  - `./build.ps1 test` (no audit override): **659 passed, 0 failed, 0 skipped, 6 min 48 s.**

**Not verified (no subscription):**
- any deployment or `what-if`;
- .NET 10 isolated on a Windows EP plan;
- run-from-package URL with no Azure Files on EP;
- identity-based host storage with the Durable Azure Storage backend;
- the Db2 driver writing `db2dsdriver.cfg` under `%TEMP%` in App Service.

**Still open:** as before, plus approval of ADR 0020.
