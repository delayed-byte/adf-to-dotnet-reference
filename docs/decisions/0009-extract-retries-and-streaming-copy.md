# 0009. Extract: ADF retry policies, driver-level retries and the streaming copy

Status: Accepted · Date: 2026-11-05

## Context

Phase 4 converts `PL_ExtractKeyStone`. Three things needed a decision beyond ADR 0005 (value rules) and ADR 0008 (entry points and `RetryPolicy`):

- how the ForEach, the activity policies and the non-retried data errors map to Durable;
- **a driver that retries underneath the `RetryPolicy`**. ADR 0008 forbids a second retry layer. AT-04 found one nobody had written: the Db2 CLI driver retries a failed connection by itself;
- how a million rows get from Db2 to staging without a file, and how the harness gets a million rows into Db2.

## Decision

### Orchestration

`ExtractKeyStoneOrchestrator` (the stub's body; its name, input and output are unchanged) calls four activities in the ADF order. It does no I/O and reads the clock only through `context.CurrentUtcDateTime`.

| ADF activity | Activity function | Durable `RetryPolicy` (from the ADF JSON) |
| --- | --- | --- |
| Log Extract Started / Log Staging Row Count / Log Extract Succeeded | `Extract_LogBatchRunEvent` | 3 attempts, 30 s |
| Lookup Extract Config | `Extract_LookupExtractConfig` | 3 attempts, 30 s |
| Copy Db2 To Staging (inside the ForEach) | `Extract_CopyDb2ToStaging` | 4 attempts, 30 s |
| Merge KeyStone To Core | `Extract_MergeKeyStoneToCore` | 3 attempts, 30 s |

- The policies are `ExtractActivityPolicies.Adf` in `CreditUnion.Batch.Core`. `ExtractPolicyTests` reads every value (retry, interval, timeout, `batchCount`) from `adf/pipeline/PL_ExtractKeyStone.json`, so the copy cannot drift.
- `AdfRetry.Options` builds `retry + 1` attempts, the fixed interval, backoff coefficient 1. `HandleFailure` skips `ExtractDataException` and `ExtractConfigurationException`: retrying a timestamp at hour 24 cannot help (PRD: validation errors are not retried).
- The ADF activity `timeout` is enforced for one attempt inside `ExtractKeyStoneSteps` with a linked `CancellationTokenSource`. That is a timeout, not a retry; it surfaces as a `TimeoutException`, which the policy retries.
- The ForEach is `BoundedParallel.ForEachAsync` with `batchCount = 3`: a `Task.WhenAny` loop, so it is deterministic under replay. **Difference from ADF:** once an iteration has failed, no further iteration starts (running ones finish). With the three configured tables and `batchCount` 3, every iteration starts at once, so this changes nothing today.
- The pipeline has no failure branch in ADF, so a failed extract throws the activity's `TaskFailedException` and logs no `Failed` event. The master (Phase 8) turns it into `BATCH_STEP_FAILED` and records the failure.
- Activities are thin `[Function]` wrappers over `ExtractKeyStoneSteps` (Core). The work is in `Db2StagingCopier` and `SqlExtractReportingStore` (`CreditUnion.Batch.KeyStone`), registered by `AddCreditUnionBatch`.

### No retries under the RetryPolicy

| Driver | Default | Decision |
| --- | --- | --- |
| `Microsoft.Data.SqlClient` | `ConnectRetryCount = 1` (a failed or broken connection is retried once) | `Connections.CreateReportingConnection` sets it to 0 |
| Db2 CLI (`Net.IBM.Data.Db2` 10.0) | **Three connection attempts per `Open`**: an immediate retry, then one after 5 seconds. This is automatic client reroute (ACR) retrying the primary server. The server advertises no alternate server. | Disabled with `db2dsdriver.cfg` (below) |

**How the Db2 retry was found.** AT-04 refuses the first two Db2 connections and expects them to show up as two failed attempts of the Copy activity. The extract succeeded with no failed attempt: both refusals fell inside one `DB2Connection.Open`, which took 5.1 s. A probe pins it (`Db2ConnectionRetryProbeTests`): 1 refusal costs 0.1 s, 2 cost 5.1 s, 3 fail after 5.1 s.

**What does not turn it off.** The .NET connection string has no keyword for it (`EnableACR`, `MaxAcrRetries` and similar are rejected as "Invalid argument"). `db2cli.ini` ignores `EnableACR`, `MaxAcrRetries`, `acrRetryInterval` and `ConnectTimeout` here. A global `<parameters>` entry in `db2dsdriver.cfg` has no effect.

**What does.** A `db2dsdriver.cfg` entry for the exact database, host and port:

```xml
<database name="KEYSTONE" host="db2.example" port="50000">
  <acr><parameter name="enableACR" value="false"/></acr>
</database>
```

With it, 1 refusal fails the `Open` in 0.7 s after one attempt. Two constraints follow:

1. The entry needs the exact host and port; no wildcard works.
2. The driver reads the file **once per process**, at its first connection, from `DB2DSDRIVER_CFG_PATH` (or `clidriver/cfg`). Writing it later, or releasing the object pool (`ReleaseObjectPool` is marked unsafe), changes nothing.

`Db2DriverConfiguration.DisableClientReroute` therefore writes the file from the KeyStone connection string and points `DB2DSDRIVER_CFG_PATH` at it. `AddCreditUnionBatch` calls it, so the Functions host does it at startup before any connection. It uses a content-hashed file under the temp directory and never overwrites an existing `DB2DSDRIVER_CFG_PATH`: if an operator supplies their own file, it must carry the same `enableACR=false` entry for the KeyStone endpoint.

**Test harness consequence.** The acceptance-test process connects to Db2 (to deploy and load) before any test builds a `BatchHost`, and AT-04's proxy would otherwise listen on an unpredictable port. So:

- `FaultInjectingTcpProxy` listens on the first free port of the reserved block `127.0.0.1:15600-15615` and falls back to an ephemeral port only if all are busy;
- `BatchEnvironment.StartAsync` calls `Db2DriverConfiguration.DisableClientReroute` for that block before its first Db2 connection;
- `Db2ConnectionRetryProbeTests` fails if the proxy is outside the block or a refused connection costs more than one attempt, so a driver upgrade or a lost registration cannot make AT-04 pass or fail for the wrong reason.

### Streaming copy

`Db2StagingCopier` (the Copy activity) runs, in this order:

1. read the staging table's column types from SQL Server (`SELECT <columns> FROM stg.X WHERE 1 = 0`, schema only);
2. open Db2 as KSEXTRACT and run the ADF query (`ExtractQueryBuilder.BuildSelect`, a `DATE('yyyy-MM-dd')` predicate, no `SELECT *`);
3. only then `TRUNCATE TABLE` the staging table (the ADF pre-copy script), so a refused connection or a failing query leaves the previous staging rows untouched;
4. `SqlBulkCopy` with `BatchSize = 10000` and `TableLock` (the ADF sink's `writeBatchSize` and `sqlWriterUseTableLock`), fed by `Db2StagingReader`.

`Db2StagingReader` is a `DbDataReader` over `DB2DataReader`. It reads each cell by the **staging column's** type, per ADR 0005 (`StagingValueRules` holds the rules):

- DECIMAL: `GetDB2Decimal`, then `SqlDecimal`, never `GetDecimal`; a fraction longer than the staging column's scale is a scale mismatch, not a rounding;
- CHAR/VARCHAR: verbatim; a non-ASCII CHAR value or a value longer than the column fails;
- DATE/TIMESTAMP: the naive `DateTime`, never converted; hour 24 (`InvalidCastException` from the driver) fails;
- NULL stays NULL.

A failing value ends the read with `ExtractDataException`: `stg.KsAccount row 7 (key ACCOUNT_NBR=****0001) column LAST_MAINT_TS: …; the value is not logged.` The driver's own exception is not attached, because its message can quote the value. Keys are masked to their last four characters. `ExtractDataErrorTests` checks the message, that the activity ran once, that nothing reached core and that the full account number appears in no log or span.

Rows flow one at a time: nothing is buffered beyond `SqlBulkCopy`'s batch, and no file is written.

### Harness loading for the performance scenario

`KeyStoneDb2.LoadAsync` inserted row by row, which is too slow for a million rows. Tables of 5,000 rows or more now go in as multi-row `INSERT … VALUES` statements of 500 rows with exact literals (`Db2Values.ToLiteral`), each committing by itself (one transaction of a million rows overflows the container's log, SQL0964C). The reset deletes in chunks of 50,000 rows for the same reason, and with no command timeout (the driver's default of 30 seconds cancelled a chunk). `standard-day` has exactly 5,000 transactions, so AT-01, AT-02 and AT-04 load it through the literal path and their golden comparisons cover its exactness; the harness's own lossless round-trip tests still load every scenario.

## Consequences

- A conversion of another Db2 reader must register its endpoint with `Db2DriverConfiguration` too, or the Durable retries will be hidden again.
- Azure deployment: the Functions app must be able to write to its temp directory, or set `DB2DSDRIVER_CFG_PATH` to a file deployed with the app that contains the entry for the production KeyStone host and port.
- `db2dsdriver.cfg` is keyed by host and port. A DNS alias that resolves to a different host later is fine; a change of port needs the file regenerated (it is, on every start).
- The Copy activity can run again after a half-finished attempt: the next attempt truncates first, so the table never holds two copies.
- If a driver upgrade makes the CLI ignore `enableACR`, `Db2ConnectionRetryProbeTests` and AT-04 fail.
