# 0016. TR_Nightly_0300_Pacific: time zone, business date, missed and duplicate fires, manual start

Status: Accepted · Date: 2026-11-06

## Context

The ADF schedule trigger fires daily at 03:00 `Pacific Standard Time` (ADF's DST-aware Windows ID). It passes only `businessDate`, as the previous Pacific day of `trigger().scheduledTime`; `reconTolerance` and `retainUntil` keep their defaults.

The .NET version is a timer-triggered starter function (ADR 0008). The following needed a decision: where the time zone comes from; which occurrence an invocation belongs to, since the isolated worker gives no `scheduledTime`; and what a missed fire, a duplicate fire or a run for an already processed date does. The PRD also asks to confirm that the batch runs under `func start`, which needs a way to start it by hand.

## Decision

### Schedule and time zone

- `[TimerTrigger("0 0 3 * * *")]`, evaluated by the Functions host in `WEBSITE_TIME_ZONE`:
  - locally `America/Los_Angeles` in `local.settings.json`;
  - in Azure, an app setting: `Pacific Standard Time` on Windows plans, `America/Los_Angeles` on Linux.
- Under `func start`, the host printed the next occurrences as `11/07/2026 03:00:00-08:00 (11/07/2026 11:00:00Z)`.
- 03:00 is never skipped or repeated, so it fires exactly once a day. AT-18 drives the trigger across both 2026 changes through the timer emulator.
- `RunOnStartup` stays false: a start-up run would start a second master for a date already processed. `UseMonitor` stays true: the schedule monitor is what detects a missed occurrence.
- A unit test ties the attribute to the ADF recurrence (Day/1, hours [3], minutes [0], `Pacific Standard Time`) and to `local.settings.json`.

### Which occurrence, and which business date

The fire time comes from the injected `TimeProvider`, never from the wall clock, and `ScheduleStatus` is not used: AT-18 passes none, and its meaning during an invocation is not documented precisely. `NightlySchedule.ScheduledOccurrence` (Core) works the occurrence out:

- **On time:** the latest 03:00 Pacific at or before the fire time plus **5 minutes**. A fire a little early still belongs to that 03:00.
- **Past due** (`TimerInfo.IsPastDue`): the latest 03:00 at or before the fire time, with no tolerance.
- `businessDate` = the previous Pacific day of that occurrence. `TriggerTimeUtc` = the occurrence, as the contract's doc comment says. ADF's `pipeline().TriggerTime` is the actual fire time, a second or so later.

A first design took the occurrence *nearest* to the fire time. The `func start` check showed why that is wrong: running the timer by hand at 15:15 Pacific would map to tomorrow's 03:00 and process today, an incomplete day. "Latest at or before" maps any manual or late invocation to the last complete day. A unit test pins the afternoon case.

The instance ID is a new GUID per invocation (`Guid.NewGuid().ToString("D")`), and it is the batch run ID.

### Missed fires

When the host was down at 03:00, the schedule monitor fires **once** at start-up with `IsPastDue`. The trigger then runs the latest missed occurrence's date and logs a warning. If the host was down two days or more, the earlier days are **not back-filled**: ADF schedule triggers do not back-fill either; only tumbling-window triggers do.

Runbook: list the dates without a `PL_Master_NightlyBatch` `Succeeded` event in `ops.BatchRun` and start a manual run for each (below). A past-due fire shortly before 03:00 still means the missed occurrence; a unit test pins it.

### Duplicate fires and reruns for a processed date

The host's timer is a singleton (blob lease) and the schedule monitor records each occurrence, so a duplicate fire should not happen. If one does, it starts a **second master under a new GUID** for the same date. That is deliberate:
- Every pipeline is idempotent for a rerun of the same date (AT-16 and every test's rerun assertion), and `ops.BatchRun` shows both runs.
- A deterministic instance ID per occurrence would deduplicate, but the Azure Storage backend lets a new orchestration reuse the ID of a *completed* one. Two runs would then share a batch run ID, interleave their `ops.BatchRun` events and overwrite each other's evidence.
- Known risk: two masters for one date running **at the same time** may interleave staging (Extract truncates per table; Card resets per date), and may post a reconciliation alert twice (ADR 0013). The data converges, because the merges are keyed, but the overlapping run's counts in `ops.BatchRun` can be misleading. Nothing in the batch prevents this; the timer singleton does.

A rerun for a date that already succeeded is **not skipped**. ADF has no such check, a skip would need a lookup the ADF pipeline does not have, and deliberate reruns (AT-16) must work.

### Manual start

No HTTP starter was added: no new package, and no endpoint to secure in production. Two existing routes were used for the `func start` check:
- `func durable start-new --function-name PL_Master_NightlyBatch --id <new GUID> --input @input.json --task-hub-name CreditUnionBatch`, with the input `{"BusinessDate":"yyyy-MM-dd"}` and optionally `ReconTolerance` and `RetainUntil`. Pass the input as a file: through the npm shim, inline JSON lost its quotes, became a JSON string, and the master rejected it before doing anything.
- `POST /admin/functions/TR_Nightly_0300_Pacific` with `{"input":""}` runs the real timer function, which processes the previous Pacific day. Locally it needs no key; in Azure it needs the master key.
- In Azure, the Durable extension's HTTP management API can also start an orchestration with a chosen instance ID; it was not exercised here.

A manual start must use a new GUID as the instance ID. The master refuses an ID that is not a GUID.

## Differences from ADF a reviewer should know

- `TriggerTimeUtc` (and the Started note) is the scheduled occurrence, not the actual fire time.
- The occurrence is computed from the fire time (5-minute early tolerance), because `scheduledTime` is not available.
- A past-due start-up runs one missed occurrence; ADF has no past-due concept and normally does not miss.
- `WEBSITE_TIME_ZONE` must be set in every environment. Without it the host evaluates the schedule in UTC and the batch would run at 03:00 UTC.

## Consequences

- Moving the schedule changes `NightlySchedule.Expression`, the attribute, the ADF JSON and this ADR together; the unit tests fail otherwise.
- A deployment checklist item: `WEBSITE_TIME_ZONE` = `Pacific Standard Time` (Windows) or `America/Los_Angeles` (Linux).
