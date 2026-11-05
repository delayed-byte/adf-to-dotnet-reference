# 0003. Synthetic KeyStone schema and reporting store design

Status: Accepted · Date: 2026-11-05

## Context

KeyStone's real schema is licensed and unavailable. The reporting store must support idempotent reruns, an append-only audit trail and least-privilege access (PRD: Non-functional requirements).

## Decision

**Synthetic Db2 schema (`KS`).**
- `MEMBER`, `ACCOUNT`, `POSTED_TXN`, `GL_ENTRY` and the reference tables `GL_ACCOUNT` and `TXN_CODE`.
- The schema deliberately includes the types AT-03 exercises:
  - `CHAR` padding (`CHANNEL 'BR '`, `CHAR(10)` GL accounts)
  - nullable columns
  - `DECIMAL(31,8)` at Db2's maximum precision
  - `TIMESTAMP(6)`
- Extraction date columns: `POSTING_DATE` for transactions and GL entries, `LAST_ACTIVITY_DATE` for accounts.
- All source table and column names live in `ops.ExtractConfig`, which is the single remap point for the real schema.

**Reporting store schemas.**

| Schema | Holds |
| --- | --- |
| `ops` | Configuration and audit |
| `stg` | Staging, reloaded every run |
| `core` | Conformed data keyed on source keys |
| `ref` | KeyBridge cache |

**Type mapping (schema level).**
- `stg.Ks*` tables mirror Db2 names and types losslessly: `CHAR(n)`→`char(n)`, `DECIMAL(p,s)`→`decimal(p,s)`, `TIMESTAMP(6)`→`datetime2(6)`.
- `core` keeps the same types with PascalCase names.
- Runtime conversion rules (trimming, time zones) are fixed by the Phase 2 Db2 type-handling ADR.

**Idempotency.**
- Merges insert new keys and update a row only when a business column differs (`EXISTS (SELECT s.* EXCEPT SELECT t.*)`), so an identical rerun changes nothing, audit columns included.
- An account is never overwritten by an older `LAST_MAINT_TS`, so re-extracting an earlier date cannot roll it back (AT-16).
- `core.DailyGlTotals` is replaced per business date.

**Audit.**
- `ops.BatchRun` and `ops.CardFileLog` are append-only, enforced twice: by `INSTEAD OF UPDATE, DELETE` triggers and by `DENY` to the writer role.
- A run is the set of events with one `BatchRunId`.
- Retention is a TBD placeholder in `ops.RetentionPolicy`; a constraint keeps automatic deletion disabled.

**Reconciliation exceptions.**
- One row per distinct (date, check, item, difference). Rows are never deleted, and only the alert columns may change.
- A rerun that finds the same difference adds no row and sends no alert, so each exception is alerted exactly once.

**Least privilege.**

| Login | Rights |
| --- | --- |
| `KSEXTRACT` (Db2) | `CONNECT` and `SELECTIN` on `KS` only |
| `cu_batch_writer` (SQL Server) | Writes `stg`, `core` and `ref`; appends to `ops`; executes procedures; `ALTER` only on `stg` (for `TRUNCATE`) |
| `cu_report_reader` (SQL Server) | Reads `core`, `ref` and `ops`; every write is denied |

**Card settlement validation.**
- Files land untyped in `stg.CardSettlementRaw`, and `stg.usp_ValidateCardSettlementFile` reports every problem at once.
- Validation failures are returned as data, never thrown, so they are not retried.

## Consequences

- Golden comparisons and evidence checksums must exclude load-audit columns (`LoadedBatchRunId`, `LoadedUtc`) and `ops` tables, which grow on every run by design.
- Remapping to the real KeyStone schema touches `ops.ExtractConfig`, the `stg.Ks*` tables and the merge procedure. Pipeline code is unaffected.
