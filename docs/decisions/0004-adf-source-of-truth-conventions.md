# 0004. Conventions for the ADF source-of-truth JSON

Status: Accepted · Date: 2026-11-05

## Context

No Azure subscription exists, so the five pipelines in `adf/` are hand-authored in ADF's JSON format, not exported from a live factory. They define the behavior the .NET conversion must match, so ambiguous ADF behavior has to be pinned down explicitly.

## Decision

- **Layout.** `adf/{pipeline,dataset,linkedService,dataflow,trigger,integrationRuntime}/`, one resource per file, file name = resource name. `integrationRuntime/` is an addition to the PRD layout, needed so the self-hosted IR reference resolves.
- **Validation.** `tests/CreditUnion.Batch.UnitTests/Adf/AdfDefinitionValidationTests.cs` is the Phase 1 gate. It checks:
  - JSON parses
  - every reference resolves
  - activity names are unique and dependencies resolve
  - expressions only use existing activities, parameters and variables
  - Execute Pipeline, trigger and dataset arguments match their declarations
  - Execute Data Flow arguments match the data flow's parameters and endpoints
  - every stored procedure, and each parameter it is called with, is declared in `db/reporting`
  - the required feature coverage is present
- **Retry policies.** ADF's minimum retry interval is 30 seconds, so every policy uses at least 30. Copy from Db2: 3 retries. KeyBridge Web activity: 3 retries. SQL activities: 2. The .NET `RetryPolicy` values are copied from these files.
- **Parameters.** `businessDate` is a `yyyy-MM-dd` string. The reconciliation tolerance is a string (`"0.00"`) passed to a `decimal(19,2)` procedure parameter, so money never passes through a float. `batchRunId` is the master pipeline's `RunId`.
- **Business date.** The trigger fires daily at 03:00 `Pacific Standard Time` (ADF's DST-aware Windows zone ID) and passes the previous Pacific calendar day. Manual runs with an empty `businessDate` derive it the same way from `TriggerTime`.
- **Failure path.** Each Execute Pipeline in the master has an on-failure branch that logs `Failed` with the step name, then a `Fail` activity, because a handled failure would otherwise mark the pipeline as succeeded.
- **No nested If.** ADF cannot nest an If Condition in another, so `PL_Reconciliation` records exceptions first (a Lookup over `ops.usp_RecordReconExceptions`) and the single If decides only whether an alert is still due.
- **Lookups that write.** Two Lookups call procedures that write: settlement validation and recording recon exceptions. This is a common ADF pattern for returning a result from a write.
- **Rounding in the data flow.** The data flow rounds with `round(x, 2, 7)` (ROUND_HALF_EVEN) and converts straight back to `decimal`, because `round()` returns a double. The Phase 6 ADR decides how .NET reproduces this.
- **Blob tags.** ADF can set blob metadata but not index tags, so archived files carry `retain_until` metadata (value `TBD`). The .NET version sets a retain-until index tag.
- **Evidence.** The ADF master pipeline exports `ops.usp_GetRunEvidence` to `evidence/{businessDate}/batch-run-{runId}.json`.

## Consequences

- The validator proves internal consistency, not ADF runtime behavior. Expression semantics such as `coalesce` over a missing `childItems` are asserted in the PRD and acceptance tests, and confirmed against a real factory during the production migration.
