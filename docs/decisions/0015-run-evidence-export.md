# 0015. Run evidence export: format, retain_until metadata, all-or-nothing streaming and idempotency

Status: Accepted · Date: 2026-11-06

## Context

The PRD's examiner-evidence requirement: each run exports an evidence file (files received, row counts, checksums, reconciliation results, exceptions) to `evidence/{businessDate}/` in blob storage. In ADF this is the Copy activity "Export Run Evidence":
- source: `ops.usp_GetRunEvidence(@BatchRunId, @BusinessDate)`, `queryTimeout` 10 min;
- sink: `JsonSink` with `filePattern arrayOfObjects` and metadata `retain_until = @pipeline().parameters.retainUntil`;
- dataset `DS_Blob_JsonFile`: UTF-8, container `evidence`, folder `{resolvedBusinessDate}`, file `batch-run-{RunId}.json`.

Open points: ADF's exact JSON bytes; whether the retain-until marker is metadata or a blob index tag; what a failed or repeated attempt leaves behind; and what a rerun does.

## Decision

### Format and path

| | Decision |
| --- | --- |
| Content | The procedure's rows in its order, as a JSON array of objects `{"section", "item", "value"}` (the result-set column names). |
| JSON details | Indented. A NULL value is written as `null`, not omitted. Non-ASCII and `<>&'` are written as they are, not escaped. |
| Encoding | UTF-8 without a byte-order mark. |
| Blob name | `{businessDate:yyyy-MM-dd}/batch-run-{batchRunId}.json`, run ID lowercase and hyphenated, as `pipeline().RunId` prints it. |
| Container | `BlobStorage:EvidenceContainer`, default `evidence`, which is ADF's literal. |
| Log Batch Succeeded `ObjectName` | `{container}/{blob name}`. |
| Content type | `application/json`. |

ADF's exact bytes are unconfirmed: whether it indents, writes a BOM or omits NULL properties. GoldenAssert compares the deterministic sections semantically (ADR 0006), so none of these choices affects the golden outputs. `NightlyBatchConversionTests` requires the file to equal the procedure's own result set, row for row, apart from `generatedUtc` and the master's Succeeded event, which is logged after the export, as in ADF.

### retain_until: metadata, not an index tag

The golden files and the ADF sink both put `retain_until` in **metadata** (`TBD` until the records policy sets a value), so the evidence blob does too. ADR 0004 said the .NET version would set a retain-until **index tag** instead. Phase 5 did not do that for archived and quarantined settlement files either: they carry metadata only (ADR 0010). ADR 0004's sentence is therefore not implemented anywhere, and the PRD's "archived blobs carry a retain-until tag" is met by metadata.

An index tag would let an Azure lifecycle-management rule filter on it, which metadata cannot do. Adding one is a deployment decision for all three containers together, once a retention period exists. It was left out here because no period exists yet (PRD: "Still open"), and Azurite's support for tags on upload was not verified.

`retainUntil` must be 1 to 256 printable ASCII characters with no leading or trailing space, because it becomes an HTTP header. Any other value fails with `RunEvidenceInputException` before any I/O, and is not retried.

### All-or-nothing streaming

The export streams with no intermediate file. `SqlNightlyBatchStore` reads the procedure with `CommandBehavior.SequentialAccess`; `RunEvidenceFile.WriteJsonAsync` (Core) writes each row with `Utf8JsonWriter`; and `AzureEvidenceBlobStore` stages 4 MiB blocks and **commits the block list, with the metadata and content type, only after the last row was written**.

`BlockBlobClient.OpenWriteAsync` was rejected: its stream commits whatever was written when it is disposed, also after an exception, which would leave a truncated evidence file. With staged blocks:
- a failed attempt leaves no blob;
- an existing blob of the same name is replaced only by a complete one;
- uncommitted blocks expire on the service side after seven days.

`NightlyBatchConversionTests` proves all three against Azurite, including a three-block file.

Per attempt, the activity timeout and the source `queryTimeout` are both 10 minutes; `AttemptTimeout` enforces them. The Storage SDK's own retries are off (`MaxRetries = 0`, ADR 0010), as are SqlClient's (`ConnectRetryCount = 0`, ADR 0009). The `RetryPolicy` (3 attempts, 30 s apart) is the only retry layer.

### Idempotency

| Case | Result |
| --- | --- |
| A rerun for any date, under a new batch run ID | Writes a **new** file. It cannot touch another run's file, because the name holds the run ID. This is the only blob change `Idempotency.cs` allows a rerun. |
| The same run ID writes again (an export retry after a lost reply, or Durable re-running the activity after a crash) | **Overwrites** with a complete file. Nothing logs for the run between two attempts, so the content differs only in `generatedUtc`. |
| A failed run (any earlier step failed) | No file, as in ADF. The run's record is `ops.BatchRun`. |

Overwrite, not fail-if-exists, for the same run ID: ADF's Copy sink overwrites, and fail-if-exists would turn a lost reply into a failed batch whose evidence exists. Across runs, evidence is never overwritten, because the path is unique per run. Write-once storage, if examiners require it, is a container immutability policy at deployment, not code.

### Data protection

The evidence holds:
- file names, row counts and checksums;
- GL account numbers and reconciliation amounts;
- the run's `ops.BatchRun` events, with their notes and error codes.

The procedure emits no member, account or card number. Evidence values are never logged; the step logs the date, the path and the entry count.

## Differences from ADF a reviewer should know

- Indentation, BOM and NULL handling of ADF's JSON sink are unconfirmed (above). Only the deterministic sections are compared.
- Metadata `retain_until` as in ADF. No index tag, contrary to ADR 0004's sentence, which no phase implemented.
- The blob appears atomically (commit after the last row). ADF's behaviour on a failed Copy is not documented.
- An unusable `retainUntil` fails at once and is not retried; ADF would retry the Copy twice.

## Consequences

- A container immutability policy, or a lifecycle rule driven by an index tag, can be added at deployment without changing the export.
- Changing the procedure's columns changes the file's property names. `MasterPolicyTests` and `NightlyBatchConversionTests` fail until the code follows.
