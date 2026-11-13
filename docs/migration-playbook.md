# Migration playbook: taking one ADF pipeline to .NET

This playbook shows how a credit union can move one real Azure Data Factory (ADF) pipeline to .NET, using this repository as the worked example. Each step says what was done here and what the credit union does on a real pipeline.

**Read this first.**

- Everything here was built and tested on a developer machine with synthetic data. No Azure subscription, no real KeyStone and no real ADF factory were involved.
- The ADF JSON in `adf/` was written by hand from the PRD. It was never exported from a live factory, and ADF was never run. Every "ADF does X" statement is therefore a reading of the documentation, and the ones we could not settle are listed in [Differences from ADF and items to verify](#differences-from-adf-and-items-to-verify).
- The golden outputs were signed off by an engineering review, not by a business reviewer ([ADR 0007](decisions/0007-golden-output-sign-off.md)). A business reviewer still has to confirm them before go-live.
- Nothing is deployed to Azure. The [Azure deployment checklist](#azure-deployment-checklist) lists what a deployment still has to decide.

How the pieces fit: each ADF pipeline becomes a Durable Functions orchestrator, and each ADF activity that does I/O becomes a Durable activity function. The activity functions are thin wrappers over plain classes in `CreditUnion.Batch.Core` and `CreditUnion.Batch.KeyStone` ([ADR 0001](decisions/0001-hosting-model.md)).

## The ADF-to-.NET mapping that was used

| ADF construct | .NET in this repository | Differs from the PRD table? |
| --- | --- | --- |
| Pipeline | Orchestrator function with the pipeline's name (`PipelineNames`) | No |
| Pipeline parameters | Typed input records (`PipelineRunInput`, `ReconciliationInput`, `NightlyBatchInput`) | No |
| Schedule trigger | Timer function `TR_Nightly_0300_Pacific` | No |
| Execute Pipeline | `CallSubOrchestratorAsync`; parallel children with `Task.WhenAll` | No |
| Copy, Db2 source | `DB2DataReader` into `SqlBulkCopy`, no file | No |
| Copy, CSV source | Own reader (`SettlementCsvReader`) into `SqlBulkCopy` | Yes: not CsvHelper ([ADR 0018](decisions/0018-no-dapper-or-csvhelper.md), proposed) |
| Get Metadata, Delete | Blob listing and deletes in `AzureCardBlobStore` | No |
| Lookup, Stored Procedure | Plain `Microsoft.Data.SqlClient` commands | Yes: not Dapper ([ADR 0018](decisions/0018-no-dapper-or-csvhelper.md), proposed) |
| ForEach | `BoundedParallel.ForEachAsync` with the ADF `batchCount` | No |
| If Condition, Fail | `if`, and `throw new PipelineFailedException(code, detail)` | No |
| Web | Typed `HttpClient`, no resilience handler | Yes: not `Http.Resilience` (Polly) |
| Mapping Data Flow | C# aggregator, `DailyGlTotalsAggregator` | Chosen in [ADR 0011](decisions/0011-map-and-aggregate-data-flow.md) |
| Activity retry policy | `RetryPolicy` on `CallActivityAsync`, built by `AdfRetry` | No |
| Linked services | Options classes bound from configuration | No |

## The process, step by step

| Step | What happens | Where it lives |
| --- | --- | --- |
| 1 | Export the factory JSON; it is the source of truth | `adf/`, ADR 0004 |
| 2 | Capture golden outputs from production ADF runs | `tests/TestData`, ADR 0006, 0007 |
| 3 | Write the acceptance tests first, then convert activity by activity | `tests/*`, ADR 0008 |
| 4 | Remap to the real KeyStone schema and the real KeyBridge contract | `ops.ExtractConfig`, `HttpKeyBridgeClient` |
| 5 | Deploy to Azure | [Checklist](#azure-deployment-checklist) |

### Step 1. Export the factory JSON as the source of truth

**Here.** `adf/{pipeline,dataset,linkedService,dataflow,trigger,integrationRuntime}/` holds one JSON file per resource, named after the resource ([ADR 0004](decisions/0004-adf-source-of-truth-conventions.md)). The check `AdfDefinitionValidationTests` proves that the JSON parses, that every reference resolves, that expressions only use existing activities and parameters, and that every stored procedure and its parameters exist in `db/reporting`. The policy tests for each pipeline (`ExtractPolicyTests`, `CardPolicyTests`, `MapPolicyTests`, `ReconPolicyTests`, `MasterPolicyTests`) read retry counts, intervals, timeouts, `batchCount`, dependencies and expressions from the JSON. If someone edits the JSON, the matching test fails until the code follows.

**At the credit union.**

1. Take the JSON from the factory's Git publish branch (or export the ARM template and split it). Put one resource in one file, named after the resource.
2. Run the validator on the real export first. It was written against hand-authored JSON, so expect to adjust it for fields a real export adds. This was not tried.
3. Pick one pipeline. Keep its datasets, linked services and data flows with it.
4. List what the validator cannot prove: expression behaviour at run time. Add each one to the verification checklist below.

### Step 2. Capture golden outputs from production ADF runs

**Here.** ADF could not run, so the golden outputs were derived from the specification by `BatchOracle`, then cross-checked three ways: replay through the real stored procedures, an independent T-SQL statement of the data-flow rule, and hand-checked cases ([ADR 0006](decisions/0006-test-harness-and-golden-outputs.md)). That proves the arithmetic, not that the rules match ADF. [ADR 0007](decisions/0007-golden-output-sign-off.md) records the four rule decisions that need a business reviewer.

**At the credit union.**

1. Choose a business date and freeze its inputs: the Db2 rows (from a reporting copy), the landing files, and the KeyBridge response.
2. Run the real ADF pipeline on those inputs, into a non-production copy of the reporting store.
3. Export the output tables in the canonical text form of ADR 0006 (CSV, fixed decimal scale, `\N` for NULL) and compare them with this repository's golden files, or with the new .NET output, using `TableComparison` (`tests/TestData/generator/Canonical`). It prints row counts, an order-independent SHA-256 and the first 20 mismatches.
4. Treat each difference as one of two things: a misreading, which becomes an ADR plus a generator change, or an ADF defect, which becomes a decision to keep or fix it.
5. Real captures contain member data. This repository forbids member data in test artifacts. Keep real captures outside source control, and commit only counts and checksums, as `perf-extract-1m` does.

### Step 3. Write the acceptance tests first, then convert activity by activity

**Here.** Phase 3 wrote AT-01 to AT-19 against stub entry points that threw `NotImplementedException`. The tests failed for that reason, none was skipped, and then each pipeline was converted until its tests passed ([ADR 0008](decisions/0008-acceptance-test-entry-points.md)). The order was Extract, Card Settlement, Map and Aggregate, Reconciliation, then the master.

**Recipe for one pipeline.**

1. Fix the contract: the orchestrator name, its typed input, and the configuration keys. Tests and code share them.
2. Write a policy test that reads every retry, interval, timeout, `batchCount`, dependency and expression from the pipeline JSON. Add a completeness check that fails when an activity with a policy is added or renamed.
3. Write the golden-output tests: run the pipeline on a scenario, compare tables and blobs with the golden files, then run it again under a new batch run ID and require that nothing changed (the idempotency rerun).
4. Write the fault tests: a refused connection, a 503, a bad file, an unmapped code.
5. Convert. One activity function per ADF activity that does I/O, in the ADF order and with the ADF dependencies. Pure ADF steps (Filter, If, Set Variable) become orchestrator code or pure functions.
6. Scan every log line and span for secrets and full numbers (AT-17).
7. The gate: `dotnet test` passes with nothing skipped.

### Step 4. Remap to the real KeyStone schema and the KeyBridge contract

**Here.** The Db2 schema is synthetic and the KeyBridge contract is a placeholder (`contracts/keybridge`). Source table and column names live in `ops.ExtractConfig`, the single remap point ([ADR 0003](decisions/0003-synthetic-schema-and-reporting-store.md)).

**At the credit union.**

1. Replace the rows in `ops.ExtractConfig` with the real table, key and date column names.
2. Change the `stg.Ks*` tables and the merge procedure `core.usp_MergeKeyStone` to the real columns. ADR 0003 says pipeline code is unaffected. In practice this is the largest edit, so plan for it.
3. Apply the remap checks from [ADR 0005](decisions/0005-db2-type-handling.md): confirm the time zone of KeyStone's TIMESTAMP columns (assumed Pacific, not confirmed), check that every CHAR column is ASCII, and check that TIMESTAMP precision is 7 or less.
4. Replace the KeyBridge contract. The edit stays inside `HttpKeyBridgeClient` and `KeyBridgeGlMappings` ([ADR 0012](decisions/0012-keybridge-web-activity-and-mapping-cache.md)). The orchestrator and the cache procedure keep their shape.
5. Re-run the acceptance tests. Update the golden files only through the generator.
6. Confirm the data access pattern. The PRD assumes ADF reads Db2 directly through a self-hosted integration runtime, ideally from a reporting copy. That is an assumption, not a confirmed fact.

### Step 5. Deploy to Azure

Nothing was deployed. [infra/main.bicep](../infra/main.bicep) describes the Function App: Elastic Premium EP1 on Windows, a managed identity, identity-based host storage, Application Insights, Key Vault references, and VNet integration when a subnet is given ([ADR 0020](decisions/0020-azure-hosting-plan-and-bicep-template.md)). `./build.ps1 infra` compiles and lints it. Use the [Azure deployment checklist](#azure-deployment-checklist) and treat every item marked "not verified" as work to do.

## Patterns

Each section gives the decision, the reason, and the pitfall that was found. "Found by" says how: a test, a probe or a real run.

### 1. Retries: one `RetryPolicy`, and every driver retry off

**Decision.** An ADF activity policy becomes one Durable `RetryPolicy` on the `CallActivityAsync` that implements the activity: `retry + 1` attempts, a fixed interval (`retryIntervalInSeconds`), backoff coefficient 1. `AdfRetry.Options` builds it. A validation error is not retried, so `HandleFailure` skips those exception types. The per-attempt ADF `timeout` is enforced inside the activity (`AttemptTimeout`); a timeout is retried. The three drivers' own retries are switched off:

| Driver | Default | Switch-off |
| --- | --- | --- |
| Db2 CLI (`Net.IBM.Data.Db2`) | Three connection attempts per `Open` (automatic client reroute) | `db2dsdriver.cfg` entry with `enableACR=false` for the exact database, host and port |
| `Microsoft.Data.SqlClient` | `ConnectRetryCount = 1` | `ConnectRetryCount = 0` in `Connections` |
| Azure Storage SDK | Three retries with exponential back-off | `Retry.MaxRetries = 0` |

**Reason.** A second retry layer multiplies attempts (four Durable attempts times four Polly attempts is 16 KeyBridge calls) and hides the failures from the audit trail ([ADR 0008](decisions/0008-acceptance-test-entry-points.md)).

**Pitfall found.** The Db2 driver retried by itself. AT-04 refuses the first two connections and expects two failed attempts; the extract instead succeeded with none, because both refusals fell inside one `Open` that took 5.1 s. The connection string has no keyword for it, `db2cli.ini` has no effect, and a global entry in `db2dsdriver.cfg` has no effect. Only a per-database entry works, and the driver reads the file once per process, at the first connection. `Db2DriverConfiguration` writes the file at start-up from the connection string. Any new Db2 reader must register its endpoint, or the retries come back ([ADR 0009](decisions/0009-extract-retries-and-streaming-copy.md)). The `Db2ConnectionRetryProbeTests` fail if a driver upgrade changes this. Also: the Execute Pipeline activities have no policy in ADF, so the sub-orchestrations have no `RetryPolicy` either (pattern 9).

### 2. Db2 type handling

**Decision.** Copy every value verbatim. No trimming, re-padding, rounding or time-zone conversion. A value that cannot be represented exactly fails the copy. DECIMAL is read with `GetDB2Decimal` and written as `SqlDecimal`. Source timestamps (`*Ts` columns) stay as KeyStone's wall-clock time; only timestamps the batch creates are UTC (`*Utc`) ([ADR 0005](decisions/0005-db2-type-handling.md)).

**Reason.** The rules were measured against the real driver, and `TypeHandlingProbeTests` pins each observation, so a driver upgrade that changes one fails the build.

**Pitfalls found (by probes).**

- `GetDecimal` and `GetValue` silently round DECIMAL(31,8) with no exception. A string parameter bound to DECIMAL loses digits too.
- A Db2 timestamp at hour 24 throws `InvalidCastException`.
- `GetSchemaTable` reports scale 3 for TIMESTAMP(6), so schema metadata must not drive conversion.
- Under SQL Server's default collation, `varchar` silently best-fits characters outside Windows-1252 (`Ω` becomes `O`). Free-text columns use a UTF-8 collation.
- SQL Server comparisons, and so `EXCEPT` in the merges, ignore trailing spaces and letter case. A rerun therefore does not carry a source correction that changes only those. Known limitation.
- ADF's own interim type for Db2 DECIMAL may round 31-digit values. Unconfirmed; compare `ACCRUED_INTEREST` against a real run.

### 3. Streaming Copy

**Decision.** Copy from Db2 streams `DB2DataReader` through `Db2StagingReader` (a `DbDataReader` that converts by the staging column's type) into `SqlBulkCopy`, in batches of 10,000 with a table lock (the ADF sink settings). No intermediate file. Order inside one attempt: read the staging table's column types, open Db2 and run the query, and only then `TRUNCATE` the staging table ([ADR 0009](decisions/0009-extract-retries-and-streaming-copy.md)). CSV copies stream the blob through the reader into `SqlBulkCopy` in one transaction with the pre-copy delete ([ADR 0010](decisions/0010-card-settlement-conversion.md)).

**Reason.** Memory stays flat, and a refused connection or a failing query leaves the previous staging rows alone. The delete and the insert share a transaction, so a failed attempt leaves no partial rows.

**Pitfalls found.**

- Errors name the table, row, masked key and column, never the value, and do not attach the driver's exception, because its message can quote the value.
- Measured: 1,052,000 rows in 46.3 s (1,361,948 rows per minute) against the 100,000 minimum, on one development machine, one run (`./build.ps1 test -Performance`).
- The test loader, not the product, hit two limits with a million rows: one transaction overflowed the Db2 container's log (SQL0964C), and the driver's 30 s command timeout cancelled a delete.

### 4. CSV parsing

**Decision.** `SettlementCsvReader` implements the dataset rules and streams: UTF-8, `,` delimiter, `"` quote, row delimiter LF only, no header, empty value is NULL, ten mapped columns, 200-character limit ([ADR 0010](decisions/0010-card-settlement-conversion.md)).

**Reason.** ADF documents little about the edge cases, so each one was decided and pinned by a test instead of left to a library default.

**Pitfall found.** A CRLF file makes every row's last field end in CR, so SQL validation quarantines the file with a manifest that lists each record. That is loud on purpose: silently accepting CRLF would hide a processor change. ADF's handling of CRLF with an explicit `\n` delimiter is not documented. An unreadable file (an unclosed quote, a value over 200 characters) fails at once without retries, and the file stays in the landing folder. Also: the settlement file filter matches case-insensitively, because ADF's `startsWith` and `endsWith` do ([ADR 0007](decisions/0007-golden-output-sign-off.md)).

### 5. The data flow as C#

**Decision.** `DF_MapAndAggregateGl` became `DailyGlTotalsAggregator`, a pure streaming aggregator in Core. `decimal.Round(Amount * AllocationRatio, 2, MidpointRounding.ToEven)` runs on the exact decimal product, once per allocation, before summing. A thin store reads the two sources and replaces the date's rows in one transaction ([ADR 0011](decisions/0011-map-and-aggregate-data-flow.md)).

**Reason.** T-SQL `ROUND` rounds half away from zero, so a set-based procedure needs a hand-written half-to-even `CASE`. The rule is tested without a database, and the test harness's own T-SQL statement of the rule stays an independent check.

**Pitfalls found.**

- ADF's `round()` returns a double. 4.70 × 0.25 = 1.175 gives 1.18 here (the PRD rule), but whether ADF gives 1.17 or 1.18 depends on its runtime. Unconfirmed; settle it with a captured run.
- ADF's inner join drops a transaction with no mapping row, silently. The .NET aggregator throws `GlAggregationException` instead, because losing money silently is worse than failing.
- Volume was not measured. `perf-extract-1m` covers Extract only. Revisit if a day approaches the 2-hour activity timeout.

### 6. Web activities, with secrets kept as configuration

**Decision.** A Web activity is a typed `HttpClient` inside an activity function. URL, user name and password come from options (`KeyBridge:*`, `ReconAlertWebhook:Url`), not from the orchestration input. In ADF, a Web step that reads a secret from Key Vault becomes configuration (a Key Vault reference in Azure), not an activity. The handler chain has no redirect following, no cookies, no retry handler and no HTTP-client loggers ([ADR 0012](decisions/0012-keybridge-web-activity-and-mapping-cache.md), [ADR 0013](decisions/0013-reconciliation-alert-webhook.md)).

**Reason.** An activity's input and output are stored in the orchestration history. A secret that crosses that boundary is stored with it.

**Pitfalls found.**

- The webhook token sits in the URL path. .NET redacts only the query string in its HTTP spans, so a default `HttpClient` put the token into a captured span. Fix: `SocketsHttpHandler.ActivityHeadersPropagator = null` removes .NET's diagnostics handler for that client, so there is no span and no trace header sent to the webhook. A process-wide switch would have removed the other spans too. Removing that line fails two tests.
- `Microsoft.Extensions.Http` 6.0, which was in the graph, has no `RemoveAllLoggers` and logs headers at Trace without redaction. The pin was raised to 10.0.9.
- ADF retries a 401 three times. Retrying a rejected login can lock a service account, so rejected requests (4xx other than 408 and 429, 3xx) are not retried.
- A base URL with credentials, a query or a fragment is refused before the first request, so they cannot reach a span.

### 7. If Condition and Fail

**Decision.** An If Condition is an `if` in the orchestrator. A Fail activity is `throw new PipelineFailedException(code, detail)` in the orchestrator, not in an activity, so it is never retried. Durable keeps only the type name and message of an exception, so the message is `"{code}: {detail}"` and `PipelineFailedException.TryParse` reads it back ([ADR 0008](decisions/0008-acceptance-test-entry-points.md), [ADR 0012](decisions/0012-keybridge-web-activity-and-mapping-cache.md)).

**Reason.** An unmapped transaction code is a validation failure. Retrying it cannot help.

**Pitfalls found.**

- In ADF the Fail activity depends on the log activity with the condition `Completed`, so a failed log must not hide the failure. The code catches the failed log, writes a warning, and still throws.
- ADF cannot nest an If Condition in another, so `PL_Reconciliation` records exceptions first, in a Lookup that writes, and the single If decides only whether an alert is due ([ADR 0004](decisions/0004-adf-source-of-truth-conventions.md)). Keep that shape; it makes the orchestrator simple.

### 8. ForEach with batchCount

**Decision.** `BoundedParallel.ForEachAsync` runs at most `batchCount` iterations at once, using a `Task.WhenAny` loop. The loop has no `Task.Run` or `Parallel` call, so it replays deterministically. Unbounded fan-out is `Task.WhenAll`.

**Reason.** Durable orchestrators must schedule the same work on every replay.

**Pitfalls found.**

- Work scheduled by an orchestrator starts only when it yields. `Task.WhenAll` over started calls runs in parallel; awaiting each call in turn does not.
- Once an iteration fails, no new one starts and the running ones finish. ADF's behaviour is unconfirmed. It changes nothing today (three tables with `batchCount` 3, so everything starts at once), but it would with more items than the batch count.

### 9. Execute Pipeline as sub-orchestrations

**Decision.** `CallSubOrchestratorAsync`, with `Task.WhenAll` for the two parallel children. Each child gets the master's instance ID as `BatchRunId`, so every child logs under one batch run. The child's instance ID is `{batchRunId}:{pipeline}`. The master's instance ID must be a GUID ([ADR 0014](decisions/0014-master-orchestration-and-failure-propagation.md)).

**Reason.** The IDs are deterministic under replay and show which run a child belongs to.

**Pitfalls found.**

- Add a `RetryPolicy` to a sub-orchestration only if the ADF Execute Pipeline activity has a policy. Retrying a whole child repeats steps that already succeeded and multiplies the attempts in the audit trail.
- Do not use a fixed instance ID per schedule occurrence to deduplicate. The Azure Storage backend lets a new orchestration reuse the ID of a completed one, so two runs would share a batch run ID and overwrite each other's evidence ([ADR 0016](decisions/0016-nightly-trigger-time-zone-and-missed-fires.md)).

### 10. Failure branches and child error codes

**Decision.** "Execute Pipeline, on Failed: Log, on Completed: Fail" becomes a `try/catch` around the call. The catch builds a Failed event with `StepFailure`, logs it, and throws `PipelineFailedException(BATCH_STEP_FAILED, "{step} failed: {message}")` with the child's failure as the inner exception. The event's error code is the child's `PipelineFailedException` code if it has one (`TXN_CODE_UNMAPPED`), otherwise the type name of the root cause (`DB2Exception`) ([ADR 0014](decisions/0014-master-orchestration-and-failure-propagation.md)).

**Reason.** The audit trail should name the root cause for operations and examiners ([ADR 0007](decisions/0007-golden-output-sign-off.md)).

**Pitfalls found.**

- ADF records its own numeric codes and wording; .NET has no equivalent. A type name is not a published contract, so renaming an exception changes the recorded code.
- If both parallel children fail, both Failed events are logged and the master names the first in pipeline order (Extract). The sibling is not cancelled. ADF's behaviour is unconfirmed.
- If the Failed event cannot be logged, the master still fails and `ops.BatchRun` has no Failed event for the run. The Durable failure details still name the step.
- Steps with no failure branch in ADF (Resolve Business Date, Log Batch Started, Log Batch Succeeded) fail the master with the activity's own failure.
- A failed run writes no evidence file, as in ADF.

### 11. Evidence export

**Decision.** "Copy a stored procedure's result to a JSON blob" is `Master_ExportRunEvidence`. It reads `ops.usp_GetRunEvidence` with `SequentialAccess`, writes each row with `Utf8JsonWriter`, stages 4 MiB blocks, and commits the block list, with the `retain_until` metadata, only after the last row. The blob is `evidence/{businessDate}/batch-run-{runId}.json` ([ADR 0015](decisions/0015-run-evidence-export.md)).

**Reason.** A run can be explained after the fact. A failed attempt leaves no blob, and an existing blob is replaced only by a complete one.

**Pitfalls found.**

- `BlockBlobClient.OpenWriteAsync` was rejected: its stream commits whatever was written when it is disposed, also after an exception, which leaves a truncated evidence file.
- The run ID is in the path, so a new run writes a new file. The same run ID (a retry) overwrites. Under a container immutability policy that overwrite may be refused; see the [deployment checklist](#azure-deployment-checklist).
- `retain_until` is blob metadata, as in ADF, not an index tag. ADR 0004 said the .NET version would set an index tag. No phase did, and ADR 0015 corrects it.
- ADF's exact JSON bytes (indentation, BOM, NULL handling) are unconfirmed. Only the deterministic sections are compared.

### 12. Trigger and time zone

**Decision.** `[TimerTrigger("0 0 3 * * *")]`, evaluated in `WEBSITE_TIME_ZONE` (`America/Los_Angeles` locally). 03:00 is never skipped or repeated by a daylight saving change. The business date is the Pacific day before the scheduled occurrence. `RunOnStartup` is false ([ADR 0016](decisions/0016-nightly-trigger-time-zone-and-missed-fires.md)).

**Reason.** The batch must run once a day and process the previous complete Pacific day.

**Pitfalls found.**

- The isolated worker does not give the trigger `scheduledTime`. The first design used the occurrence nearest to the fire time. A real `func start` run showed the flaw: a manual fire at 15:15 Pacific would have processed today's incomplete day. The rule is now "the latest 03:00 at or before the fire time (5 minutes' early tolerance when on time)". A unit test pins the afternoon case.
- Without `WEBSITE_TIME_ZONE` the host evaluates the schedule in UTC.
- A missed fire runs once, at start-up, for the latest missed day. Earlier days are not back-filled, as with ADF schedule triggers. The runbook is to list the dates without a master `Succeeded` event in `ops.BatchRun` and start each by hand.
- A duplicate fire starts a second master for the same date under a new GUID. The pipelines are idempotent, but two masters for one date running at the same time may interleave staging and send an alert twice. Nothing prevents it except the timer singleton.
- Through the npm shim of Core Tools, inline JSON for `func durable start-new --input` loses its quotes and the master rejects it. Pass the input as `@file.json`.

### 13. Alerting semantics

**Decision.** An alert is posted only for an exception not yet marked as sent. Mark runs only after a 2xx for a POST in the same orchestration. An undeliverable alert fails the batch at Reconciliation. Delivery is at least once ([ADR 0013](decisions/0013-reconciliation-alert-webhook.md), [ADR 0014](decisions/0014-master-orchestration-and-failure-propagation.md)).

**Reason.** Marking before posting would turn every failure into a lost alert, which is worse than a repeat for a reconciliation exception.

**Pitfalls found.**

| Failure | Result |
| --- | --- |
| POST delivered, response lost, retry sends it again | Duplicate |
| Worker crashes after the POST was delivered | Duplicate |
| POST delivered, Mark fails on all attempts | Duplicate on the next run |
| Two runs for one date overlap | Duplicate; neither alert is lost |
| All POST attempts fail | Delayed, not lost: the next run posts it |

A receiver that needs exactly-once can de-duplicate on `(businessDate, batchRunId)`. A changed difference is a new exception and alerts again. A rerun after a delivered and marked alert posts nothing.

### 14. Log hygiene

**Decision.** `host.json` sets `durableTask.tracing.traceInputsAndOutputs` and `traceReplayEvents` to false, and a unit test reads the file. Orchestrators log through the replay-safe logger, with step, code, error type and date only. Exception messages carry no values. Numbers are masked to the last four digits with `LogMask`. AT-17 scans every `ILogger` entry at Trace level, every span from any `ActivitySource`, and the failure details, for the generated secrets, the webhook URL and token, and every member, account and card number in the scenario.

**Reason.** Activity inputs and outputs include the KeyBridge mapping JSON and the alert summary. With tracing on, they would reach the host's logs.

**Pitfalls found.**

- The webhook token in the URL path (pattern 6).
- A driver's exception message can quote a value, so the driver's exception is not attached.
- A harness listener that counts spans from every `ActivitySource` flaked when a parallel test emitted an HTTP span. The fix counts only the test's own source.
- AT-17 does not cover the Functions host's own logs. The `func start` check scanned about 40,000 lines of host logs once and found none of the four secrets, no orchestration input and no mapping JSON. That is a single check, not a guarantee.

### 15. Determinism: analyzer rules plus the replay test

**Decision.** The Durable analyzer (`Microsoft.DurableTask.Analyzers`, shipped with the worker extension) runs on every build and its warnings are errors. A replay test (`OrchestratorReplayTests`) runs all five orchestrators on the worker's own replay engine and requires identical actions on re-execution ([ADR 0017](decisions/0017-orchestrator-determinism-check.md)). The pattern for a new orchestrator is `ReplayBackend` and `ReplayCheck`: register it, then script its activities.

**Reason.** The in-process test host does not replay, so it cannot see non-determinism.

**What was proved.** Probes of `DateTime.UtcNow`, `Guid.NewGuid`, `Task.Delay`, environment variables and the HTTP, SQL and Blob clients each failed the build. A hidden static counter, added as a mutation check, failed the replay test.

**The gap.** `File.Exists` was not flagged: static file-system calls are outside the analyzer's I/O rule, and so are I/O types it does not know. The analyzer does not flag static fields. The replay test misses paths it does not script, and non-determinism that returns the same value twice (a configuration value, an environment variable, `DateTime.UtcNow.Date`). Code review has to cover these.

### 16. Idempotent reruns

**Decision.** Merges insert new keys and update a row only when a business column differs, so an identical rerun changes nothing, audit columns included. `core.DailyGlTotals` and the card staging rows are replaced per business date. Every acceptance test ends with a rerun under new batch run IDs and requires that only `ops.BatchRun`, `ops.CardFileLog` and the rerun's own evidence file changed ([ADR 0003](decisions/0003-synthetic-schema-and-reporting-store.md), [ADR 0008](decisions/0008-acceptance-test-entry-points.md)).

**Pitfall.** `ops.BatchRun` and `ops.CardFileLog` are append-only. A log activity whose insert committed but whose reply was lost adds a second row on retry, as ADF would. The table cannot be corrected afterwards.

## Differences from ADF and items to verify

Check each line against a real factory. "Difference" means the .NET code deliberately behaves differently. "Unconfirmed" means we do not know what ADF does. Tick a box when it has been checked or accepted.

### Cross-cutting

- [ ] **Unconfirmed.** The golden outputs came from the specification, not from ADF runs. Compare real ADF output for the same inputs with `TableComparison` (ADR 0006).
- [ ] **Not met.** A business reviewer confirms the four decisions in ADR 0007: case-insensitive file names, the child's error code in the master's Failed event, banker's rounding on the exact decimal product per allocation, and decimal scale in notes.
- [ ] **Unconfirmed.** The data access pattern (direct Db2 read through a self-hosted integration runtime, from a reporting copy) is an assumption (PRD).
- [ ] **Unconfirmed.** Expression behaviour that the validator cannot prove, such as `coalesce` over a missing `childItems` (ADR 0004). Get Metadata is assumed non-recursive, and a missing folder is assumed to behave like an empty one (ADR 0006).
- [ ] **Unconfirmed.** The time zone of KeyStone's TIMESTAMP columns is assumed Pacific (ADR 0005).
- [ ] **Placeholder.** The KeyBridge contract is a placeholder (`contracts/keybridge`).

### PL_ExtractKeyStone

- [ ] **Difference.** After one iteration fails, no new iteration starts (ADR 0009). ADF's behaviour is unconfirmed.
- [ ] **Difference.** The staging table is truncated after the Db2 query succeeds, not before it (ADR 0009).
- [ ] **Difference.** Data errors (hour-24 timestamp, non-ASCII CHAR, scale mismatch) fail without retries and name no value (ADR 0005, 0009).
- [ ] **Difference.** Db2 DECIMAL is read losslessly. ADF may round 31-digit values; compare `ACCRUED_INTEREST` (ADR 0005).
- [ ] **Known limitation.** A rerun does not carry a source correction that changes only trailing spaces or letter case (ADR 0005).

### PL_IngestCardSettlement

- [ ] **Difference.** The pre-copy delete and the insert share one transaction (ADR 0010).
- [ ] **Unconfirmed.** `DeleteIfExists`: ADF's Delete on a missing file (ADR 0010).
- [ ] **Difference.** An unreadable file fails at once without retries; ADF would retry the Copy twice, then fail. The file stays in landing (ADR 0010).
- [ ] **Difference.** After one iteration fails, no new iteration starts. ADF is unconfirmed (ADR 0010).
- [ ] **Unconfirmed.** CSV edge cases: a CRLF file is quarantined, empty lines are skipped, invalid UTF-8 becomes U+FFFD (ADR 0010).
- [ ] **Unconfirmed.** Case-insensitive file name matching rests on ADR 0007's reading of `startsWith` and `endsWith`.

### PL_MapAndAggregate

- [ ] **Difference.** Not retried: KeyBridge 401, 403, 404, other 4xx and 3xx; a 2xx body that is not JSON, has no `mappings` or exceeds 4 MB; a mapping the cache procedure rejects. ADF retries any failure (ADR 0012).
- [ ] **Unconfirmed.** Redirects are not followed. ADF's behaviour is unknown (ADR 0012).
- [ ] **Difference.** A transaction with no mapping row at aggregation fails the run; ADF's inner join drops it silently (ADR 0011).
- [ ] **Difference.** The sink's delete and insert share a transaction (ADR 0011).
- [ ] **Difference.** A missing `version` in the KeyBridge response becomes NULL; ADF would fail the expression (ADR 0012).
- [ ] **Unconfirmed.** 1.175 rounds to 1.18 here (decimal, half to even, per the PRD). ADF's double-based `round()` may give 1.17 (ADR 0011, ADR 0007).
- [ ] **Unconfirmed.** ADF runs the pre-SQL delete even when the date has no transactions (ADR 0011).
- [ ] **Not measured.** Volume of the C# aggregation against the database (ADR 0011).

### PL_Reconciliation

- [ ] **Difference.** The Key Vault read has no counterpart, so its retry and timeout do not exist (ADR 0013).
- [ ] **Difference.** Not retried: 4xx other than 408 and 429, 3xx, a missing URL. Redirects are not followed (ADR 0013).
- [ ] **Difference.** The alert summary is JSON-escaped; ADF would send invalid JSON for a quote or backslash (ADR 0013).
- [ ] **Difference.** A NULL `ItemsOutOfTolerance` reads as 0; ADF would send invalid JSON and an empty note (ADR 0013).
- [ ] **Difference.** A negative tolerance, or one with more than two decimals, fails; ADF would let SQL round it (ADR 0013).
- [ ] **Accepted risk.** Delivery is at least once (ADR 0013).
- [ ] **Unconfirmed.** How ADF prints a `decimal(19,2)` from a Lookup (`0.10` or `0.1`), and what a parallel branch does when its sibling fails (ADR 0013).

### PL_Master_NightlyBatch and the trigger

- [ ] **Difference.** The error code of a child that failed in an activity is the root-cause .NET type name, not an ADF numeric code. The message is Durable's wording (ADR 0014).
- [ ] **Unconfirmed.** If both parallel children fail, whether ADF cancels the running sibling, and which Fail message it reports (ADR 0014).
- [ ] **Difference.** Child instance IDs are `{batchRunId}:{pipeline}`. ADF child runs have their own run IDs (ADR 0014).
- [ ] **Difference.** A manual run's trigger time is the orchestration's first execution time (ADR 0014).
- [ ] **Unconfirmed.** Evidence JSON indentation, BOM and NULL handling. The blob is written atomically; ADF's behaviour on a failed Copy is not documented (ADR 0015).
- [ ] **Difference.** An unusable `retainUntil` fails at once without retries (ADR 0015).
- [ ] **Difference.** `TriggerTimeUtc` is the scheduled occurrence, not the actual fire time. The occurrence is computed from the fire time because `scheduledTime` is not available (ADR 0016).
- [ ] **Difference.** A past-due start-up runs one missed occurrence and does not back-fill (ADR 0016).
- [ ] **Unconfirmed.** The timer host's behaviour inside skipped or repeated local hours is not documented. The schedule avoids them with 03:00 (ADR 0008, 0016).

## Azure deployment checklist

Nothing has been deployed or tested in Azure. "Not verified" means the repository has no evidence either way. "In the template" means [infra/main.bicep](../infra/main.bicep) covers the item ([ADR 0020](decisions/0020-azure-hosting-plan-and-bicep-template.md)). The template compiles and lints, and `InfrastructureTemplateTests` checks it against the code and the ADF JSON, but it has never been deployed.

| Item | What to do | Status |
| --- | --- | --- |
| Hosting | A Functions app (isolated worker, .NET 10, Functions v4) with Durable Functions on the Azure Storage backend. Use a separate task hub name per environment (`CreditUnionBatch` locally). Plan: Elastic Premium EP1 on Windows, because the Db2 driver package is Windows-only and activity timeouts reach 2 hours. `host.json` sets `functionTimeout` to 2 h 30 min, because the Premium default of 30 minutes would kill the long activities ([ADR 0020](decisions/0020-azure-hosting-plan-and-bicep-template.md)). | In the template; not deployed |
| Network path to Db2 | The self-hosted integration runtime is not needed, but the Functions app must reach the Db2 reporting copy over a private path. The design is outside this repository. | Not designed |
| Db2 driver package | `Net.IBM.Data.Db2` is the Windows package. A Linux plan needs the `-lnx` variant (PRD). | Not verified |
| Key Vault secrets | The ADF JSON uses these names: `keystone-db2-extract-password`, `reporting-sql-writer-connection-string`, `keybridge-api-password`, `recon-alert-webhook-url`. The .NET options need whole connection strings (`KeyStoneDb2:ConnectionString`, `ReportingSql:ConnectionString`), so decide the Db2 secret: a new connection-string secret, or compose it ([ADR 0019](decisions/0019-keystone-db2-secret.md) proposes composing it). Use Key Vault references in app settings. | In the template, with option A for Db2 (`keystone-db2-extract-connection-string`) until ADR 0019 is decided |
| App settings | `KeyStoneDb2__ConnectionString`, `ReportingSql__ConnectionString`, `BlobStorage__ConnectionString`, `KeyBridge__BaseUrl`, `KeyBridge__Username` (default `cu-batch`), `KeyBridge__Password`, `ReconAlertWebhook__Url`, and optionally `BlobStorage__RetainUntil` and the container names | In the template (all except the optional ones); not verified in Azure |
| `WEBSITE_TIME_ZONE` | `Pacific Standard Time` on a Windows plan, `America/Los_Angeles` on a Linux plan (ADR 0016). Without it the schedule runs in UTC. | In the template (`Pacific Standard Time`); not verified in Azure |
| Db2 driver configuration | The app must write to its temp folder, or set `DB2DSDRIVER_CFG_PATH` to a file deployed with the app that has `enableACR=false` for the production KeyStone host and port (ADR 0009). | Not verified in Azure |
| Managed identity | ADF used the factory's managed identity for Blob. Here `BlobStorage:ConnectionString` holds an account key, so a managed identity needs a code change: build `BlobServiceClient` from a URI in `AzureCardBlobStore` and `AzureEvidenceBlobStore`. The app also needs an identity that can read Key Vault secrets. SQL with Entra authentication is untested. | Partly in the template: a user-assigned identity for host storage, the package, Key Vault and Application Insights. Until the code changes, the template writes the data account's connection string (with its key) into the Key Vault secret `batch-blob-storage-connection-string` |
| Encryption in transit | Local SQL Server uses `TrustServerCertificate=True`, and the local Db2 connection is plain. Outside Docker, require encrypted connections to both. The ADF linked service reaches Db2 over TLS (port 50001, `certificateCommonName`), so the Db2 connection string must turn TLS on with the IBM provider's keywords ([ADR 0019](decisions/0019-keystone-db2-secret.md)). Db2 TLS was not tested. | Not done |
| Retention | `BlobStorage:RetainUntil` and `ops.RetentionPolicy` hold `TBD`. The period comes from the credit union's records preservation policy (PRD, "Still open"). | Open |
| Immutability policy | Add a container immutability policy on the evidence, archive and quarantine containers if examiners need write-once storage (ADR 0015). Check first whether the same-name overwrites still work: an evidence export retried under the same run ID, and the archive and quarantine copies and manifest, all overwrite a blob. A write-once policy is likely to refuse that. | Not verified |
| Retain-until index tag | Decide whether to add an index tag next to the `retain_until` metadata, for all three containers together. A lifecycle rule can filter on a tag, not on metadata. Azurite's tag support was not verified (ADR 0015). | Open |
| OpenTelemetry | Not implemented. `Program.cs` adds no OpenTelemetry, so there are no batch traces in the Aspire dashboard and nothing in Application Insights. Add an exporter, tag spans with `batchRunId`, and decide how the Durable worker's spans correlate. This needs new packages. | Gap |
| Alert channel | Replace the WireMock webhook with the real Teams or email endpoint. Change `HttpReconAlertClient` only. Add an idempotency key if the endpoint offers one. | Open |
| Database deployment | `tools/CreditUnion.Batch.DbDeploy` creates SQL logins in the reporting database. Azure SQL has a different login model, and this was not tried. | Not verified |
| Timer and manual starts | The timer is a singleton through a blob lease. The admin endpoint needs the master key. The Durable HTTP management API was not exercised (ADR 0016). | Not verified |
| Scans before release | `./build.ps1 scan` (gitleaks) and `dotnet list package --vulnerable --include-transitive` | Pass locally |

## Traceability: every ADF activity and its .NET implementation

The table covers every activity in `adf/pipeline/*.json`, nested ones included (61 rows). The ADF type and policy columns are checked against the JSON by `DocumentationConsistencyTests`, which also fails if an activity has no row, if a row matches no activity, or if the ADR column is empty or cites a missing ADR.

"Activity" means a Durable activity function. "Orchestrator code" means the step is plain code inside the orchestrator, with no activity of its own. The retry policy of each activity call is built from the ADF policy by `AdfRetry` (retry + 1 attempts, fixed interval). Where the table says "none", ADF has no policy and the .NET call has none.

| Pipeline | ADF activity | ADF type | .NET implementation | ADF policy (retries, interval, timeout) | ADR |
| --- | --- | --- | --- | --- | --- |
| `PL_Master_NightlyBatch` | `Resolve Business Date` | SetVariable | Orchestrator code: `BusinessCalendar.Resolve`, a pure function | none | 0014, 0016 |
| `PL_Master_NightlyBatch` | `Log Batch Started` | SqlServerStoredProcedure | Activity `Master_LogBatchRunEvent` | 2 retries, 30 s, 5 min | 0014 |
| `PL_Master_NightlyBatch` | `Execute Extract KeyStone` | ExecutePipeline | Orchestrator code: `CallSubOrchestratorAsync(PL_ExtractKeyStone)`, parallel with Card via `Task.WhenAll` | none | 0014 |
| `PL_Master_NightlyBatch` | `Execute Ingest Card Settlement` | ExecutePipeline | Orchestrator code: `CallSubOrchestratorAsync(PL_IngestCardSettlement)` | none | 0014 |
| `PL_Master_NightlyBatch` | `Execute Map And Aggregate` | ExecutePipeline | Orchestrator code: `CallSubOrchestratorAsync(PL_MapAndAggregate)` | none | 0014 |
| `PL_Master_NightlyBatch` | `Execute Reconciliation` | ExecutePipeline | Orchestrator code: `CallSubOrchestratorAsync(PL_Reconciliation)` | none | 0014 |
| `PL_Master_NightlyBatch` | `Export Run Evidence` | Copy | Activity `Master_ExportRunEvidence` (`RunEvidenceFile`, `AzureEvidenceBlobStore`) | 2 retries, 30 s, 10 min | 0015 |
| `PL_Master_NightlyBatch` | `Log Batch Succeeded` | SqlServerStoredProcedure | Activity `Master_LogBatchRunEvent` | 2 retries, 30 s, 5 min | 0014 |
| `PL_Master_NightlyBatch` | `Log Failed At Extract` | SqlServerStoredProcedure | Activity `Master_LogBatchRunEvent` with `StepFailure` | 2 retries, 30 s, 5 min | 0014 |
| `PL_Master_NightlyBatch` | `Fail At Extract` | Fail | Orchestrator code: `throw new PipelineFailedException("BATCH_STEP_FAILED", ...)` | none | 0008, 0014 |
| `PL_Master_NightlyBatch` | `Log Failed At Card Settlement` | SqlServerStoredProcedure | Activity `Master_LogBatchRunEvent` with `StepFailure` | 2 retries, 30 s, 5 min | 0014 |
| `PL_Master_NightlyBatch` | `Fail At Card Settlement` | Fail | Orchestrator code: `throw new PipelineFailedException("BATCH_STEP_FAILED", ...)` | none | 0008, 0014 |
| `PL_Master_NightlyBatch` | `Log Failed At Map` | SqlServerStoredProcedure | Activity `Master_LogBatchRunEvent` with `StepFailure` | 2 retries, 30 s, 5 min | 0014 |
| `PL_Master_NightlyBatch` | `Fail At Map` | Fail | Orchestrator code: `throw new PipelineFailedException("BATCH_STEP_FAILED", ...)` | none | 0008, 0014 |
| `PL_Master_NightlyBatch` | `Log Failed At Reconciliation` | SqlServerStoredProcedure | Activity `Master_LogBatchRunEvent` with `StepFailure` | 2 retries, 30 s, 5 min | 0014 |
| `PL_Master_NightlyBatch` | `Fail At Reconciliation` | Fail | Orchestrator code: `throw new PipelineFailedException("BATCH_STEP_FAILED", ...)` | none | 0008, 0014 |
| `PL_Master_NightlyBatch` | `Log Failed At Evidence` | SqlServerStoredProcedure | Activity `Master_LogBatchRunEvent` with `StepFailure` | 2 retries, 30 s, 5 min | 0014, 0015 |
| `PL_Master_NightlyBatch` | `Fail At Evidence` | Fail | Orchestrator code: `throw new PipelineFailedException("BATCH_STEP_FAILED", ...)` | none | 0008, 0014 |
| `PL_ExtractKeyStone` | `Log Extract Started` | SqlServerStoredProcedure | Activity `Extract_LogBatchRunEvent` | 2 retries, 30 s, 5 min | 0009 |
| `PL_ExtractKeyStone` | `Lookup Extract Config` | Lookup | Activity `Extract_LookupExtractConfig` | 2 retries, 30 s, 5 min | 0009 |
| `PL_ExtractKeyStone` | `For Each Source Table` | ForEach | Orchestrator code: `BoundedParallel.ForEachAsync`, batch count 3 | none | 0009 |
| `PL_ExtractKeyStone` | `Copy Db2 To Staging` | Copy | Activity `Extract_CopyDb2ToStaging` (`Db2StagingCopier`: `DB2DataReader` into `SqlBulkCopy`) | 3 retries, 30 s, 60 min | 0005, 0009 |
| `PL_ExtractKeyStone` | `Log Staging Row Count` | SqlServerStoredProcedure | Activity `Extract_LogBatchRunEvent`, inside the ForEach | 2 retries, 30 s, 5 min | 0009 |
| `PL_ExtractKeyStone` | `Merge KeyStone To Core` | SqlServerStoredProcedure | Activity `Extract_MergeKeyStoneToCore` | 2 retries, 30 s, 30 min | 0009 |
| `PL_ExtractKeyStone` | `Log Extract Succeeded` | SqlServerStoredProcedure | Activity `Extract_LogBatchRunEvent` | 2 retries, 30 s, 5 min | 0009 |
| `PL_IngestCardSettlement` | `Log Card Ingest Started` | SqlServerStoredProcedure | Activity `Card_LogBatchRunEvent` | 2 retries, 30 s, 5 min | 0010 |
| `PL_IngestCardSettlement` | `Reset Card Staging` | SqlServerStoredProcedure | Activity `Card_ResetCardStaging` | 2 retries, 30 s, 5 min | 0010 |
| `PL_IngestCardSettlement` | `List Landing Files` | GetMetadata | Activity `Card_ListLandingFiles` (`AzureCardBlobStore`) | 2 retries, 30 s, 5 min | 0010 |
| `PL_IngestCardSettlement` | `Filter Settlement Files` | Filter | Orchestrator code: `SettlementFileFilter.Select`, a pure function | none | 0007, 0010 |
| `PL_IngestCardSettlement` | `For Each Settlement File` | ForEach | Orchestrator code: `BoundedParallel.ForEachAsync`, batch count 4 | none | 0010 |
| `PL_IngestCardSettlement` | `Copy File To Raw Staging` | Copy | Activity `Card_CopyFileToRawStaging` (`SettlementCsvReader` into `SqlBulkCopy`) | 2 retries, 30 s, 30 min | 0010 |
| `PL_IngestCardSettlement` | `Validate Settlement File` | Lookup | Activity `Card_ValidateSettlementFile` (SQL procedure) | 2 retries, 30 s, 10 min | 0010 |
| `PL_IngestCardSettlement` | `If File Valid` | IfCondition | Orchestrator code: `if` | none | 0010 |
| `PL_IngestCardSettlement` | `Promote Valid File` | SqlServerStoredProcedure | Activity `Card_PromoteValidFile` | 2 retries, 30 s, 10 min | 0010 |
| `PL_IngestCardSettlement` | `Archive Settlement File` | Copy | Activity `Card_ArchiveSettlementFile` | 2 retries, 30 s, 10 min | 0010 |
| `PL_IngestCardSettlement` | `Delete Archived Landing File` | Delete | Activity `Card_DeleteArchivedLandingFile` | 2 retries, 30 s, 5 min | 0010 |
| `PL_IngestCardSettlement` | `Log File Archived` | SqlServerStoredProcedure | Activity `Card_LogFileArchived` | 2 retries, 30 s, 5 min | 0010 |
| `PL_IngestCardSettlement` | `Write Error Manifest` | Copy | Activity `Card_WriteErrorManifest` | 2 retries, 30 s, 10 min | 0010 |
| `PL_IngestCardSettlement` | `Quarantine Settlement File` | Copy | Activity `Card_QuarantineSettlementFile` | 2 retries, 30 s, 10 min | 0010 |
| `PL_IngestCardSettlement` | `Delete Quarantined Landing File` | Delete | Activity `Card_DeleteQuarantinedLandingFile` | 2 retries, 30 s, 5 min | 0010 |
| `PL_IngestCardSettlement` | `Log File Quarantined` | SqlServerStoredProcedure | Activity `Card_LogFileQuarantined` | 2 retries, 30 s, 5 min | 0010 |
| `PL_IngestCardSettlement` | `Merge Card Settlement To Core` | SqlServerStoredProcedure | Activity `Card_MergeCardSettlementToCore` | 2 retries, 30 s, 30 min | 0010 |
| `PL_IngestCardSettlement` | `Log Card Ingest Succeeded` | SqlServerStoredProcedure | Activity `Card_LogBatchRunEvent` | 2 retries, 30 s, 5 min | 0010 |
| `PL_MapAndAggregate` | `Log Map Started` | SqlServerStoredProcedure | Activity `Map_LogBatchRunEvent` | 2 retries, 30 s, 5 min | 0012 |
| `PL_MapAndAggregate` | `Get GL Mapping From KeyBridge` | WebActivity | Activity `Map_GetGlMappingFromKeyBridge` (`HttpKeyBridgeClient`); URL and password are configuration | 3 retries, 30 s, 5 min | 0012 |
| `PL_MapAndAggregate` | `Cache GL Mapping` | SqlServerStoredProcedure | Activity `Map_CacheGlMapping` | 2 retries, 30 s, 5 min | 0012 |
| `PL_MapAndAggregate` | `Lookup Unmapped Txn Codes` | Lookup | Activity `Map_LookupUnmappedTxnCodes` | 2 retries, 30 s, 5 min | 0012 |
| `PL_MapAndAggregate` | `If Any Txn Code Unmapped` | IfCondition | Orchestrator code: `if (UnmappedCount > 0)` | none | 0012 |
| `PL_MapAndAggregate` | `Log Unmapped Txn Codes` | SqlServerStoredProcedure | Activity `Map_LogUnmappedTxnCodes` | 2 retries, 30 s, 5 min | 0012 |
| `PL_MapAndAggregate` | `Fail Txn Code Unmapped` | Fail | Orchestrator code: `throw new PipelineFailedException("TXN_CODE_UNMAPPED", ...)` | none | 0008, 0012 |
| `PL_MapAndAggregate` | `Aggregate Daily GL Totals` | ExecuteDataFlow | Activity `Map_AggregateDailyGlTotals` (`DailyGlTotalsAggregator`, C# instead of the data flow) | 1 retries, 60 s, 120 min | 0011 |
| `PL_MapAndAggregate` | `Log Map Succeeded` | SqlServerStoredProcedure | Activity `Map_LogBatchRunEvent` | 2 retries, 30 s, 5 min | 0012 |
| `PL_Reconciliation` | `Log Recon Started` | SqlServerStoredProcedure | Activity `Recon_LogBatchRunEvent` | 2 retries, 30 s, 5 min | 0013 |
| `PL_Reconciliation` | `Lookup Card Totals` | Lookup | Activity `Recon_LookupCardTotals`, started together with the GL lookup (`Task.WhenAll`) | 2 retries, 30 s, 5 min | 0013 |
| `PL_Reconciliation` | `Lookup GL Totals` | Lookup | Activity `Recon_LookupGlTotals` | 2 retries, 30 s, 5 min | 0013 |
| `PL_Reconciliation` | `Record Recon Exceptions` | Lookup | Activity `Recon_RecordReconExceptions` | 2 retries, 30 s, 5 min | 0013 |
| `PL_Reconciliation` | `If Alert Required` | IfCondition | Orchestrator code: `if (recorded.AlertRequired)` | none | 0013 |
| `PL_Reconciliation` | `Get Alert Webhook Url` | WebActivity | Configuration: `ReconAlertWebhook:Url` (Key Vault reference in Azure), read inside `Recon_PostReconAlert`; no activity | 2 retries, 30 s, 1 min | 0013 |
| `PL_Reconciliation` | `Post Recon Alert` | WebActivity | Activity `Recon_PostReconAlert` (`HttpReconAlertClient`) | 3 retries, 30 s, 2 min | 0013 |
| `PL_Reconciliation` | `Mark Recon Alert Sent` | SqlServerStoredProcedure | Activity `Recon_MarkReconAlertSent` | 2 retries, 30 s, 5 min | 0013 |
| `PL_Reconciliation` | `Log Recon Outcome` | SqlServerStoredProcedure | Activity `Recon_LogBatchRunEvent` | 2 retries, 30 s, 5 min | 0013 |

### Other ADF resources

| ADF resource | .NET implementation | ADR |
| --- | --- | --- |
| `TR_Nightly_0300_Pacific` (trigger) | Timer function `TR_Nightly_0300_Pacific` (`NightlyBatchTrigger`) and `NightlySchedule` | 0008, 0016 |
| `DF_MapAndAggregateGl` (data flow) | `DailyGlTotalsAggregator` and `SqlMapAndAggregateStore` | 0011 |
| `IR_SelfHosted_KeyStone` (integration runtime) | Not needed; the worker connects to Db2 directly (network path not designed) | 0001 |
| `LS_KeyStone_Db2`, `LS_Reporting_Sql`, `LS_Blob_Storage` (linked services) | Options `KeyStoneDb2`, `ReportingSql`, `BlobStorage` | 0008 |
| `LS_KeyVault` (linked service) | Configuration: Key Vault references or a configuration provider; no code | 0013 |
| `DS_KeyStone_Db2` (dataset) | `ExtractQueryBuilder` and the rows in `ops.ExtractConfig` | 0003, 0009 |
| `DS_Blob_CardSettlementCsv` (dataset) | `SettlementCsvReader` | 0010 |
| `DS_Blob_JsonFile` (dataset) | Evidence path and format in `RunEvidenceFile` | 0015 |
| `DS_Blob_BinaryFile`, `DS_Blob_Folder`, `DS_Reporting_SqlTable` (datasets) | Container and prefix handling in `AzureCardBlobStore`; table names in the stores | 0010 |
