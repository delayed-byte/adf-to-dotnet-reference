# 0008. Acceptance-test entry points and the in-process Durable host

Status: Accepted · Date: 2026-11-05

## Context

Phase 3 writes AT-01 to AT-19 against "the public orchestration entry points, which exist only as stubs", before any pipeline is converted. The tests therefore fix several things the later phases must follow:

- what the entry points are called and what they take;
- how a test runs Durable orchestrations without the Functions host or Azure Storage;
- where ADF retry policies live, so "retries per policy" (AT-04, AT-11) can be observed;
- how an ADF error code (`TXN_CODE_UNMAPPED`, `BATCH_STEP_FAILED`) survives Durable's failure serialization;
- how the schedule trigger is tested across daylight saving changes (AT-18);
- what "any log line" covers (AT-17).

## Decision

**Entry points** (`src/CreditUnion.Batch.Functions`, contracts in `CreditUnion.Batch.Core.Pipelines`):

| ADF | .NET |
| --- | --- |
| Pipeline `PL_*` | Orchestrator function with the same name (`PipelineNames`), input from `context.GetInput<T>()` |
| `businessDate`, `batchRunId` | `PipelineRunInput` (Extract, Card, Map), `ReconciliationInput` (adds `Tolerance`) |
| Master parameters | `NightlyBatchInput` (`BusinessDate`, `ReconTolerance`, `RetainUntil`, `TriggerName`, `TriggerTimeUtc`) |
| `pipeline().RunId` of the master | The master's orchestration instance ID, which must be a GUID; children receive it as `BatchRunId` |
| Trigger `TR_Nightly_0300_Pacific` | Timer function with the same name; it sets `BusinessDate` explicitly, as the ADF trigger passes `businessDate` |
| Linked services | Options classes (`KeyStoneDb2`, `ReportingSql`, `BlobStorage`, `KeyBridge`, `ReconAlertWebhook` sections) |
| Child parameters that no caller overrides (containers, `keyBridgeBaseUrl`, card-file `retainUntil`) | Configuration, not input |

`AddCreditUnionBatch(IServiceCollection, IConfiguration)` is the one composition root. `Program.cs` and the acceptance tests both call it, so the tests exercise the production wiring.

**Error codes.** An ADF Fail activity becomes `PipelineFailedException(errorCode, detail)`. Durable keeps only an exception's type name and message, so the message is `"{ErrorCode}: {detail}"` and `PipelineFailedException.TryParse` reads it back. A failed master throws `BATCH_STEP_FAILED`; the child's code goes into the master's `ops.BatchRun` Failed event (ADR 0007).

**Retries.** An ADF activity policy becomes a Durable `RetryPolicy` on the `CallActivityAsync` that implements the activity:

- `retry + 1` attempts, `FirstRetryInterval = retryIntervalInSeconds`, `BackoffCoefficient = 1` (ADF waits a fixed interval).
- No second retry layer inside the activity: no Polly retry on the KeyBridge `HttpClient`, and no driver-level reconnect retries. Otherwise the counts multiply (4 × 4 = 16 KeyBridge calls) and the retries become invisible to the audit trail. A resilience pipeline may still enforce the per-attempt timeout (`httpRequestTimeout`, the activity `timeout`).
- Failures that are not transient (validation errors, unmapped codes) are not retried. Use `RetryPolicy.HandleFailure`, or don't model them as activity failures.

AT-04 and AT-11 check this through the runner's trace. They read the expected policy from `adf/pipeline`: every failed attempt was called with the equivalent policy and was retried after the ADF interval, and the KeyBridge stub saw exactly three calls.

**In-process Durable host** (`tests/CreditUnion.Batch.TestHarness/Orchestration`). Tests run the Functions app's own `[Function]` methods with a custom `TaskOrchestrationContext`. It keeps the Durable rules a conversion can break:

- Inputs, outputs and activity arguments cross every boundary as JSON through `JsonDataConverter`.
- Failures cross as `TaskFailedException` carrying only type name, message and stack.
- `RetryPolicy` and retry handlers apply as `DurableTask.Core`'s retry interceptor applies them. `Handle` receives the Core exception, so `HandleFailure` works.
- Work an orchestrator schedules starts only when it yields. `Task.WhenAll` really runs in parallel; awaiting each call in turn does not.
- Orchestrator code must stay on its own thread. `ConfigureAwait(false)` fails with Durable's "Multithreaded execution was detected".
- Durable timers advance a virtual clock instantly. The same clock is the batch's injected `TimeProvider`, so 30-second ADF intervals cost nothing.
- Function classes are created per invocation from DI. Bindings supported: the trigger parameter, `[DurableClient] DurableTaskClient`, `FunctionContext`, `CancellationToken`.

The host does **not** replay orchestrators, so it cannot detect non-deterministic orchestrator code. The PRD's replay unit test (risk table) belongs to Phase 8. If a conversion needs a binding or Durable feature the host lacks, extend the host and its self-tests (`InProcessFunctionHostTests`); do not weaken an acceptance test.

**Trigger (AT-18).** The test emulates the Functions host's timer:

- It reads the NCRONTAB expression from the function's `[TimerTrigger]` attribute (resolving a `%setting%`).
- It evaluates the expression with NCrontab.Signed in the wall-clock time of `WEBSITE_TIME_ZONE`, taken from `local.settings.json`. Azure app settings must carry the same value.
- It invokes the function 1.5 s after each occurrence, with `ScheduleStatus` null, so the trigger must take the fire time from the injected `TimeProvider`.
- A recording `DurableTaskClient` captures what the trigger starts, and the test runs it.

The emulator skips non-existent local times and fires repeated ones once. The host's behaviour inside those hours is not documented precisely, which is why the schedule is 03:00.

**Logs (AT-17).** In scope:

- Every `ILogger` entry at Trace level, including structured values, scopes and exception text.
- Every span from any `ActivitySource`, exported or not.
- The failure details the host would log.

Out of scope: the Functions host's own logs and `EventSource` output.

Each entry is checked against the generated secrets, the KeyBridge Basic credential, the webhook URL and token, and every member, account and card (BIN + last 4) number in the scenario. Numbers are matched inside runs of digits, so a value masked to its last four digits passes. Reports name a label, never the value.

**Gate interpretation.** "Fail for the right reason" means each acceptance test fails with the `NotImplementedException` of a stub entry point: the one under test, or an upstream pipeline it runs as a precondition. AT-19 is the exception. Its subject is the database logins and grants delivered in Phase 1, so it passes now. Each rejected write is first executed by an admin login inside a rolled-back transaction, so the test proves a missing privilege and cannot pass vacuously.

## Consequences

- Later phases replace stub bodies without editing tests. Changing a name, input record, configuration key or the error-code format is a contract change: update this ADR and the tests together.
- Pipeline tests run upstream pipelines as preconditions (ADR 0006). AT-08 to AT-13 therefore pass only once Extract and Card Settlement are converted, which the phase order guarantees.
- Every acceptance test ends with an idempotency rerun under new batch run IDs. The rerun must end the same way and post no alert. Every table must be unchanged except `ops.BatchRun` and `ops.CardFileLog`, and every blob except the rerun's own evidence file.
- The Phase 8 implementation must:
  - keep host logging of orchestration inputs off;
  - keep the webhook URL out of HTTP-client logs and spans. .NET redacts only query strings, and the token is in the path.
