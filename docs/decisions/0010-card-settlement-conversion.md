# 0010. Card settlement: activity granularity, CSV parsing, blob retries and idempotency

Status: Accepted · Date: 2026-11-06

## Context

Phase 5 converts `PL_IngestCardSettlement` on the pattern of ADR 0009 (Extract). Three things needed a decision beyond ADR 0008 (entry points, `RetryPolicy`) and ADR 0009:

- how the activities, the per-file `If Condition` and the `Filter` map to Durable;
- how the dataset `DS_Blob_CardSettlementCsv` is read: BOM, line ends, quotes, empty values, over-long values;
- the Azure Storage SDK retries underneath the `RetryPolicy`, and what a rerun leaves behind once the landing files are gone.

## Decision

### Orchestration and activity granularity

`IngestCardSettlementOrchestrator` (the stub's body; name, input `PipelineRunInput` and output are unchanged) calls one activity per ADF activity that does I/O, in the ADF order and with the ADF dependencies. It does no I/O and reads the clock only through `context.CurrentUtcDateTime`. Container names and `retainUntil` come from `BlobStorageOptions` (registered as `CardSettlementSettings`), not from the input.

| ADF activity | Activity function | Retry / timeout (from the ADF JSON) |
| --- | --- | --- |
| Log Card Ingest Started / Succeeded | `Card_LogBatchRunEvent` | 3 attempts, 30 s / 5 min |
| Reset Card Staging | `Card_ResetCardStaging` | 3, 30 s / 5 min |
| List Landing Files | `Card_ListLandingFiles` | 3, 30 s / 5 min |
| Filter Settlement Files | none: `SettlementFileFilter.Select`, a pure function the orchestrator calls | none in ADF |
| Copy File To Raw Staging | `Card_CopyFileToRawStaging` | 3, 30 s / 30 min |
| Validate Settlement File | `Card_ValidateSettlementFile` | 3, 30 s / 10 min |
| Promote Valid File | `Card_PromoteValidFile` | 3, 30 s / 10 min |
| Archive Settlement File | `Card_ArchiveSettlementFile` | 3, 30 s / 10 min |
| Delete Archived Landing File | `Card_DeleteArchivedLandingFile` | 3, 30 s / 5 min |
| Log File Archived | `Card_LogFileArchived` | 3, 30 s / 5 min |
| Write Error Manifest | `Card_WriteErrorManifest` | 3, 30 s / 10 min |
| Quarantine Settlement File | `Card_QuarantineSettlementFile` | 3, 30 s / 10 min |
| Delete Quarantined Landing File | `Card_DeleteQuarantinedLandingFile` | 3, 30 s / 5 min |
| Log File Quarantined | `Card_LogFileQuarantined` | 3, 30 s / 5 min |
| Merge Card Settlement To Core | `Card_MergeCardSettlementToCore` | 3, 30 s / 30 min |

- Each is its own retried call, so a transient failure of the delete does not repeat the copy, and the audit trail (the trace in the tests) names the activity that failed.
- The Filter has no policy and no I/O in ADF, so it is not an activity here: calling a pure function from the orchestrator keeps one fewer round trip and stays deterministic under replay. `SettlementFileFilterTests` cover it.
- `CardActivityPolicies.Adf` holds the values; `CardPolicyTests` reads every one (retry, interval, timeout, `batchCount`, the pipeline parameter defaults) from the ADF JSON, and a completeness test fails if the JSON gains or renames an activity that has a policy.
- The ForEach is `BoundedParallel.ForEachAsync` with `batchCount` 4. The per-file sequence runs inside it. **Difference from ADF**, as in ADR 0009: once an iteration has failed, no further iteration starts (running ones finish). ADF's behaviour is not confirmed.
- A quarantined file is data, not a failure (AT-06). Any other failure throws the activity's `TaskFailedException`; the pipeline has no failure branch in ADF, so no `Failed` event is logged and the master (Phase 8) records it.
- Validation stays in SQL. `stg.usp_ValidateCardSettlementFile` is called and its first row carried through; nothing from the test oracle's C# port is used in `src`.
- The activities are thin `[Function]` wrappers over `CardSettlementSteps` (Core, no Azure Functions dependency). The SQL calls are `SqlCardSettlementStore` and the blob calls `AzureCardBlobStore`, both in `CreditUnion.Batch.KeyStone`: a separate Blob project would have held one class.

### Reading the CSV (`SettlementCsvReader`)

The dataset says: UTF-8, `,` delimiter, `"` as quote and escape, row delimiter `\n`, no header, `nullValue ""`, every column a string. The copy maps ordinal 1 to `RecordType` and 2 to 10 to `Field01` to `Field09`. ADF's behaviour on the cases below is not documented precisely, so each is decided here and pinned by `SettlementCsvReaderTests`:

| Case | Decision |
| --- | --- |
| UTF-8 byte-order mark | Skipped; not part of the first field. |
| Invalid UTF-8 bytes | Decoded to U+FFFD, not an error. The record then fails validation in SQL like any other bad value. |
| Row delimiter | **LF only**, as the dataset states (`rowDelimiter "\n"`). A CR is data, so a CRLF file's last field ends in CR and SQL validation quarantines it. The file-format contract says LF. This is loud (every row is reported), where silently accepting CRLF would hide a processor change. |
| Empty lines | Skipped, including trailing ones; a missing final LF is fine. A line of spaces is data. |
| Quoted fields | May contain commas, line feeds and `""` (one literal quote). A quote inside an unquoted field, or text after a closing quote, is data. |
| Empty value | Null, quoted or not (`nullValue ""`). |
| Fewer than 10 fields | The rest are null (a header has 4, a trailer 3). |
| More than 10 fields | The extra ones are not mapped and are dropped, as unmapped ordinals are in ADF. |
| Quoted field never closed | `CardFileDataException` (record number, no value). |
| Value longer than 200 characters (the staging column) | `CardFileDataException` (record, field, no value). |

`CardFileDataException` is **not retried** (`AdfRetry.IsRetriable`): the same bytes fail the same way. **Difference from ADF**, whose Copy activity would also fail but would retry twice first. The failure still fails the pipeline, and the file stays in the landing folder. Messages never contain a value, so a card number in a malformed field cannot reach a log, a span or the failure details.

### Copying into staging

`SqlCardSettlementStore.LoadRawAsync` streams the blob (`OpenReadAsync`, no intermediate file) through `SettlementCsvReader` and `SqlBulkCopy` in batches of 10,000 (the sink's `writeBatchSize`), mapping columns by name. The pre-copy script, `DELETE … WHERE BusinessDate AND SourceFile`, runs first with parameters instead of a concatenated string. **Difference from ADF:** the delete and the insert share one transaction, so a failed attempt leaves no partial file and a retry cannot double rows. Types are never converted: the staging columns are `nvarchar`, and the reader yields strings only.

### Retries underneath the `RetryPolicy`

| Driver | Default | Decision |
| --- | --- | --- |
| `Microsoft.Data.SqlClient` | `ConnectRetryCount = 1` | Set to 0 by `Connections.CreateReportingConnection`, as in Phase 4. |
| Azure Storage SDK (`Azure.Storage.Blobs` 12.30.1) | Three retries with exponential backoff on connection failures and 5xx | `BlobClientOptions.Retry.MaxRetries = 0` in `AzureCardBlobStore.CreateClientOptions`. A fault now reaches the orchestrator's `RetryPolicy`, which counts it and waits the ADF interval. |
| Db2 CLI | (Phase 4) | Not used by this pipeline, so `Db2DriverConfiguration` needs no new endpoint. |

`CardBlobStoreFaultTests` proves the blob setting against Azurite behind the fault proxy: with one refused connection the store fails on its first call and no second connection is made, while a client with the SDK defaults (the positive control) absorbs the same fault. `NoDriverRetryTests` pins both option values. No Polly is used.

Every call is limited to its ADF `timeout` per attempt (`AttemptTimeout`, moved out of `ExtractKeyStoneSteps` so both pipelines share it). That is a timeout, not a retry.

### Idempotency

Landing files are deleted after the first run, so a rerun of the same `businessDate` finds none:

- Reset clears the date's rows in `stg.CardSettlementRaw`, `stg.CardSettlementValidationError` and `stg.CardSettlement`. None of them is a golden table. `core.CardSettlement` is untouched, because the merge of an empty staging table changes nothing.
- No blob is rewritten: the archive and quarantine blobs keep the first run's `batch_run_id` metadata.
- `ops.BatchRun` gains the rerun's events and says "No settlement files found in the landing folder." (audit tables grow by design).

Within a run, a retry of any activity is safe: staging load replaces the file's rows; validation deletes and rewrites the file's errors; promote deletes and rewrites the file's typed rows; the blob copy and the manifest overwrite; the delete uses `DeleteIfExists`, so a retry after a lost reply succeeds (**difference from ADF**, whose Delete may fail on a missing file; unconfirmed). The two `ops.usp_LogCardFile` calls append: a retry after a committed insert whose reply was lost would add a second audit row, as in ADF; the append-only table cannot be corrected afterwards.

### Data protection

- Logs, spans and exception messages carry file names, counts, the business date and container names. Row values are never logged, and the connection strings (account key, passwords) are never part of any message.
- The file format carries only BIN and last 4, so the error manifest and `ops.CardFileLog` hold no full card number. The manifest is the SQL procedure's output unchanged; its messages name fields, not values, except a record type (at most 10 characters) and a header date.
- AT-17 (`standard-day` and the two other scenarios) scans every log line and span for the scenario's full card and account numbers and all secrets.

### Dependencies

`Azure.Storage.Blobs` was already pinned in `Directory.Packages.props` (the test harness uses it); `CreditUnion.Batch.KeyStone` now references it. No new package or version.

## Consequences

- Changing a file name pattern or a policy in the ADF JSON fails `CardPolicyTests` until the code follows.
- A processor that starts sending CRLF files sees every file quarantined, with a manifest that lists each record. To accept CRLF, change `SettlementCsvReader` and this ADR together and confirm with the processor.
- Phase 8 (master) wraps this orchestrator as a sub-orchestration. A failure arrives as a `TaskFailedException`; `CardFileDataException` shows as the inner failure's type.
