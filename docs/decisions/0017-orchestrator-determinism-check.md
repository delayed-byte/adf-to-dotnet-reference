# 0017. Orchestrator determinism: analyzer rules and a replay test on the worker's own engine

Status: Accepted · Date: 2026-11-06

## Context

Durable Functions replays an orchestrator from its history after every await. Code that reads the wall clock, random values or I/O directly makes the replay schedule something different from the history, and the orchestration fails or silently uses stale inputs.

The PRD's risk table answers this with "analyzer rules and a unit test that replays each orchestrator". The in-process host the acceptance tests use does not replay (ADR 0008), so Phase 8 must add the check.

## Decision

### 1. Analyzer rules (static, every build)

`Microsoft.DurableTask.Analyzers` 0.2.0 comes with the Durable worker extension. It analyses every method reachable from an `[OrchestrationTrigger]`, in the Functions project and in the test projects, and the build treats its warnings as errors.

Probes, each added to the master, built, and reverted. These calls failed `dotnet build`:

| Rule | Call |
| --- | --- |
| DURABLE0001 | `DateTime.UtcNow` |
| DURABLE0002 | `Guid.NewGuid()` |
| DURABLE0003 | `Task.Delay`, `Thread.Sleep` |
| DURABLE0005 | `HttpClient`, `SqlConnection`, `BlobServiceClient` |
| DURABLE0006 | `Environment.GetEnvironmentVariable` |

**`File.Exists` was not flagged:** static file-system calls are outside the analyzer's I/O rule, and so is any I/O type it does not know. In the test project, `DURABLE2003` (activity name not found) is suppressed for one file only, `OrchestratorReplayTests`: its control orchestrators call scripted activity names on purpose.

### 2. Replay test (dynamic, on the real engine)

`tests/CreditUnion.Batch.UnitTests/Determinism`:
- **`ReplayBackend`** is a minimal Durable backend. It runs each orchestrator through `GrpcOrchestrationRunner.LoadAndRun`, the entry point the isolated worker uses to execute an orchestrator against a history: the SDK's shim over DurableTask.Core's replay engine.
  - It runs one episode at a time, as the host does: every episode replays the whole history so far plus the new events.
  - Completions arrive one per episode, so each orchestrator is replayed many times.
  - Activities are answered from a script. Sub-orchestrations run recursively on the same backend. Durable timers fire at their due time, which exercises the `RetryPolicy` retries.
  - The histories it records are genuine Durable histories (`OrchestratorStarted`, `ExecutionStarted`, `TaskScheduled`, `TaskCompleted` and `TaskFailed`, `SubOrchestrationInstanceCreated` and `Completed` or `Failed`, `TimerCreated` and `TimerFired`).
- **`ReplayCheck`** waits 30 ms, then executes every recorded episode again on its own history, and the final episode (the whole history) twice more. Each replay must return **exactly the same actions**: activity and sub-orchestration names, serialized inputs, instance IDs, timer due times, and the completion with its result or failure. Trace contexts and stack-trace text are ignored: the worker adds them, and span IDs differ on every execution.
- **Scenarios** (`OrchestratorReplayTests`), covering all five orchestrators:
  - A successful batch: three tables, a valid and a quarantined settlement file, a reconciliation alert whose first POST fails and is retried after a durable timer.
  - A failed batch: the Extract copy fails twice and is retried; Map fails with TXN_CODE_UNMAPPED; the master logs `Failed` and fails with `BATCH_STEP_FAILED: PL_MapAndAggregate failed: Unmapped transaction codes: P2PD`. This also confirms that the child's error code survives the real engine, not only the in-process host.
- **Controls:**
  - An orchestrator that puts `DateTime.UtcNow.Ticks` into an activity input is caught: the replay's input differs.
  - An orchestrator whose branch depends on state that changes between executions (a stand-in for I/O) is rejected by the engine itself with `NonDeterministicOrchestrationException`.
  - An orchestrator that uses only `context.CurrentUtcDateTime`, `context.NewGuid()` and a durable timer passes.
- **Mutation check:** a hidden static counter that changed one child's input on re-execution failed the successful-batch test at the master's episode 1. The in-process tests cannot see it, and the analyzer does not flag static fields. The mutation was reverted.

No new package: `Microsoft.DurableTask.Worker.Grpc` and `Microsoft.DurableTask.Grpc` 1.27.0 are already in the graph through the Durable worker extension.

### What this proves, and what it does not

**Proves:**
- For the recorded histories, all five orchestrators emit identical actions when replayed at another time, on the engine the isolated worker uses.
- The exercised paths do not depend on the wall clock, random values or changing state in a way that alters a scheduled call, an input or a branch. Those paths are: success, a child failing with a code, activity retries with timers, the quarantine branch and the alert branch.
- The master's failure propagation works on the real engine.
- Together with the analyzer, direct `DateTime.UtcNow`, `Guid.NewGuid`, `Task.Delay`, environment-variable reads and the HTTP, SQL and Blob clients in orchestrator code fail the build. Static file I/O does not (above).

**Does not prove:**
- **Paths not scripted.** Both parallel children failing, the evidence export failing, and a Failed event that cannot be logged are covered by the in-process tests, which do not replay.
- **Non-determinism that yields the same value twice.** Examples: `DateTime.UtcNow.Date`, a configuration value, an environment variable, or I/O that returns the same answer both times. Wall-clock reads are caught only when they carry sub-second precision or happen to cross a boundary between the two executions. The analyzer covers the common APIs statically.
- **The Functions extension's own wrapper.** The test calls the orchestrator method with the SDK's context and typed input; it does not use the extension's middleware, its input deserialization, or Azure Storage history encoding, large payloads or extended sessions. The `func start` check covered those end to end once: a manual run and a timer-started run both completed with real Azurite-backed histories.
- **Thread affinity** (`ConfigureAwait(false)`). The in-process host covers it and fails with "Multithreaded execution was detected".

## Consequences

- A new orchestrator must be registered in `ReplayBackend.ForFunctionsApp`, and the scripts need outcomes for its activities. The script throws on an unknown activity name, so a new activity fails the test until it is scripted.
- If the extension stops shipping `Microsoft.DurableTask.Worker.Grpc` transitively, the test project must pin it in `Directory.Packages.props`.
