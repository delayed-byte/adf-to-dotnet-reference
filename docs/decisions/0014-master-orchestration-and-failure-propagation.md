# 0014. PL_Master_NightlyBatch: sub-orchestrations, failure branches, child error codes and log hygiene

Status: Accepted · Date: 2026-11-06

## Context

Phase 8 converts `PL_Master_NightlyBatch`, the pipeline that runs the four children. ADR 0008 fixed its contract: the orchestrator function `PL_Master_NightlyBatch`, input `NightlyBatchInput`, no output, and the instance ID is the batch run ID (a GUID) that every child receives. ADR 0007 decided that the master's Failed event carries the child's error code and that the master itself fails with `BATCH_STEP_FAILED`.

The ADF JSON leaves several things open:
- what `activity(…).error.errorCode` and `.error.message` mean when the child is a Durable sub-orchestration;
- what the master does when the Failed event itself cannot be logged;
- what happens when Extract and Card Settlement, which run in parallel, both fail (ADF's behaviour is not documented);
- how a failure detail travels up through two orchestration levels without exposing a secret or a full account number (AT-17).

## Decision

### Activities

The body of `NightlyBatchOrchestrator` replaces the stub. The name, input, output and the instance-ID rule are unchanged. `MasterPolicyTests` reads the order, dependencies, conditions, parameters and expressions below from `adf/pipeline/PL_Master_NightlyBatch.json`.

| ADF activity | .NET | Retry / timeout (ADF JSON) |
| --- | --- | --- |
| Resolve Business Date (Set Variable) | `BusinessCalendar.Resolve`, a pure function in the orchestrator | none |
| Log Batch Started | `Master_LogBatchRunEvent`; Notes `trigger={name}; triggerTimeUtc={time}` | 3 attempts, 30 s / 5 min |
| Execute Extract KeyStone, Execute Ingest Card Settlement | `CallSubOrchestratorAsync`, both started together and awaited with `Task.WhenAll` | **no policy in ADF, so no `RetryPolicy`** |
| Execute Map And Aggregate | sub-orchestration after both succeeded | none |
| Execute Reconciliation | sub-orchestration with `ReconciliationInput(…, reconTolerance)` | none |
| Export Run Evidence (Copy) | `Master_ExportRunEvidence` (ADR 0015) | 3, 30 s / 10 min |
| Log Batch Succeeded | `Master_LogBatchRunEvent`; ObjectName is the evidence path | 3, 30 s / 5 min |
| Log Failed At Extract / Card Settlement / Map / Reconciliation / Evidence | `Master_LogBatchRunEvent` with `StepName`, `ErrorCode`, `ErrorMessage` | 3, 30 s / 5 min |
| Fail At … (five) | `throw new PipelineFailedException(BATCH_STEP_FAILED, "{step} failed: {message}")` | none |

- Each child gets `PipelineRunInput(resolvedBusinessDate, batchRunId)` (Reconciliation also the tolerance), where `batchRunId` is the master's instance ID. Children get the instance ID `{batchRunId}:{pipeline}`: it is deterministic under replay and shows which run a child belongs to in the Durable instance list.
- The two activities are thin wrappers over `NightlyBatchSteps` (Core), registered through `AddCreditUnionBatch`. The orchestrator does no I/O and reads the time only from `context.CurrentUtcDateTime`.
- A manual run has no trigger. Its Started note reads `trigger=Manual` (ADF's `pipeline().TriggerName` for "Trigger now"), with the orchestration's first `CurrentUtcDateTime` as the trigger time. Under `func start` that was 30 s after the instance was created, because that is when the worker first picked it up.

### Failure propagation and the child's error code

A failed child arrives as a `TaskFailedException`. Its `FailureDetails` hold the child's failure chain: type names and messages only (ADR 0008). `StepFailure.From` (Core, unit-tested) builds the Failed event from the chain:

| | Value | Example |
| --- | --- | --- |
| `ErrorCode` | The code of a `PipelineFailedException` anywhere in the chain (`PipelineFailedException.TryParse`). Otherwise the short type name of the root cause, the innermost failure. | `TXN_CODE_UNMAPPED`; `DB2Exception`; `TimeoutException`; `ReconAlertUnavailableException` |
| `ErrorMessage` | The child's failure message as Durable reports it (ADF `error.message`). For a `PipelineFailedException`, its detail, since the code is already in ErrorCode. | `Unmapped transaction codes: P2PD`; `Task 'Extract_CopyDb2ToStaging' (#4) failed with an unhandled exception: …` |
| Master failure | `BATCH_STEP_FAILED: {step} failed: {ErrorMessage}`. The evidence step's prefix is `Run evidence export failed: `. The child's `TaskFailedException` is the inner exception, so the master's failure details keep the whole chain. | `BATCH_STEP_FAILED: PL_MapAndAggregate failed: Unmapped transaction codes: P2PD` |

ErrorCode is cut to 50 characters and ErrorMessage to 2,000, the procedure's parameter sizes.

**Root-cause type as ErrorCode.** ADF would record its own numeric code, such as `2011` or `2200`. .NET has no equivalent, and the type of the root cause tells operations and examiners what failed. It is stable, has no values in it, and the conversion test pins `DB2Exception` for a refused Db2 connection. A type name is not a published contract: renaming an exception changes the code.

**When the Failed event cannot be logged.** "Fail At …" depends on "Log Failed At …" with the condition `Completed`. After the log's own three attempts, the master logs a warning through the replay-safe logger and fails with `BATCH_STEP_FAILED` anyway. `ops.BatchRun` then has no Failed event for the run. The failure details of the Durable instance still name the step and the code. This is the same choice ADR 0011 made for "Log Unmapped Txn Codes".

**When both parallel children fail.**
- Each failure branch runs as soon as its own child fails, as ADF's dependency graph does. The sibling is not cancelled and runs to its end.
- After both have finished, the master fails naming the **first failed step in pipeline order**: Extract before Card Settlement. Both Failed events are in `ops.BatchRun`.
- ADF's behaviour here is unconfirmed. It is not known whether ADF stops the running sibling when one Fail activity runs, or which Fail activity's message the pipeline run reports.
- The test oracle makes the same choice (both events, Extract named first). No golden scenario has both children fail; `MasterOrchestratorTests` covers it.

**No later step runs after a failure** (AT-15: Reconciliation does not run when Map fails). Export Run Evidence depends on Reconciliation succeeding, so **a failed run writes no evidence file**, as in ADF. A failed run's audit record is `ops.BatchRun`.

**Steps without a failure branch.** Resolve Business Date, Log Batch Started and Log Batch Succeeded have none in ADF. If one fails, the master fails with that activity's `TaskFailedException`, not `BATCH_STEP_FAILED`, and logs no Failed event, because the log store is the thing that failed. A Log Batch Succeeded that fails after the export leaves an evidence file for a run with no final event. The next run, under a new ID, writes its own.

**A reconciliation alert that cannot be delivered fails the batch.** Phase 7 asked whether it should. It does: the child fails, the master logs `Failed` at `PL_Reconciliation` with `ReconAlertUnavailableException` (or `…Rejected…`), and no evidence is written. The exceptions are already recorded and unalerted, so the next run alerts them, once (ADR 0013). Treating the alert as optional would hide an undelivered reconciliation exception behind a green batch.

### Retries

- Activity calls get `AdfRetry.Options(policy)`: `retry + 1` attempts at a fixed interval, no second layer.
- `RunEvidenceInputException` (an unusable `retainUntil`) is not retried.
- The sub-orchestration calls have no retry: the Execute Pipeline activities have no policy, and each child's own activities already retry per their own policies. Retrying a whole child would repeat steps that already succeeded and multiply the attempts recorded in the audit trail.

### Log hygiene through sub-orchestrations (AT-17)

What crosses a Durable boundary, and where it could be logged:

| What | Content | Kept out of logs by |
| --- | --- | --- |
| Master and child inputs | dates, GUIDs, tolerance, `retainUntil`, trigger name and time. No secret, no account number. | — |
| Child activity inputs and outputs | Include the KeyBridge mapping JSON and the alert summary (GL account numbers, amounts). | `host.json`: `durableTask.tracing.traceInputsAndOutputs = false`, `traceReplayEvents = false`. A unit test reads the file. The Durable client logs only the input size. |
| Failure details | Type name and message of each failure. Our exceptions never carry values (ADR 0005, 0010, 0012, 0013). The master's message embeds the child's message, so it exposes nothing new. | Scanned by AT-17 (`map-unmapped-code`) and by `NightlyBatchConversionTests`, whose Extract failure is a platform `DB2Exception`. |
| The webhook URL | Never an input, output or message (ADR 0013). | The client has no span and no logger. |
| Orchestrator logs | Step name, error code, error type and date only, through `CreateReplaySafeLogger`. | — |

The three AT-17 cases run through the master and scan every `ILogger` entry, span and failure detail for every generated secret, the KeyBridge credential, the webhook URL and token, and every full member, account and card number. ADR 0008 puts the host's own logs out of scope, but the `func start` check scanned them anyway. Both host logs together (about 40,000 lines) contained none of the four secrets used, no orchestration input and no mapping JSON.

No new package was added for any of this.

## Differences from ADF a reviewer should know

- **ErrorCode** of a child that failed in an activity is the root-cause .NET type name (e.g. `DB2Exception`), not an ADF numeric code. A child with a Fail activity (TXN_CODE_UNMAPPED) keeps its own code (ADR 0007).
- **ErrorMessage** is Durable's failure message (`Task '…' (#n) failed with an unhandled exception: …`), not ADF's wording (`Operation on target … failed: …`). ADR 0006 already compares messages by "contains".
- **Both parallel children failing:** both Failed events are logged, and the master names Extract. Whether ADF cancels a running sibling, and which message it reports, is unconfirmed.
- **Sub-orchestration instance IDs** are `{batchRunId}:{pipeline}`; ADF child runs get their own run IDs. The children still log under the master's batch run ID, as in ADF.
- **A manual run's trigger time** is the orchestration's first execution time, not the time the run was requested.

## Consequences

- Changing an activity, a dependency, a parameter or an expression in the master's ADF JSON fails `MasterPolicyTests` until the code and this ADR follow.
- Renaming an exception type changes the ErrorCode recorded for failures caused by it. Dashboards that group by ErrorCode must follow.
- Phase 9's playbook should present the pattern "Execute Pipeline + Failed branch + Fail = `CallSubOrchestratorAsync` in try/catch + log + throw", with the parallel-failure caveat.
