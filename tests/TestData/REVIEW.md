# Golden output review

The PRD requires golden outputs to be "reviewed and signed off by a person who knows the business rules" before the acceptance tests are written (Phase 3). They were derived from the specification, not captured from ADF, so this review is the control against a misreading of the spec (PRD risk table).

**No business reviewer was available.** At the repository owner's direction, the review below is an engineering review, and its open questions were settled by the decisions in [ADR 0007](../../docs/decisions/0007-golden-output-sign-off.md). Before go-live, a business reviewer must confirm those decisions, and the real ADF factory's outputs for these inputs must be compared with the golden files.

## What was checked automatically

- Every golden table, error manifest, recon outcome and evidence checksum agrees with the Phase 1 stored procedures when the scenario's inputs are replayed through them (`GoldenConsistencyTests`).
- `core.DailyGlTotals` agrees with a separate T-SQL statement of the data-flow rule.
- The extract tables equal the Db2 rows read back with the ADR 0005 rules.
- Inputs satisfy the Db2 schema constraints and contain only synthetic, reserved-range identifiers.

The review is therefore about whether the **rules** are right, not whether the arithmetic is.

## Checklist

- [x] **standard-day:** the card check reconciles (settlement 114,679.36 = KeyStone CPOS − CRFD), and all nine GL accounts' derived debits and credits equal KeyStone's. *Evidence: summary.md; the replay produces Reconciled with a card difference of 0.00.*
- [x] **standard-day:** the `ops.BatchRun` events and notes are those the ADF pipelines log, one per logging activity in `adf/pipeline/*.json`. *Evidence: 17 events; the merge notes match the procedures.*
- [x] **standard-day:** only `card-landing/2026-11-04/settlement_*.csv` is processed. `readme.txt`, `settlement_004.csv.tmp`, `old/` and the 2026-11-03 file stay in landing. *Evidence: manifest blob list.*
- [x] **extract-db2-types:** each value in `core.*` is exactly the Db2 value. *Evidence: the Db2 round trip and the replay through `core.usp_MergeKeyStone`, both compared on every column.*
- [x] **card-quarantine-trailer-mismatch:** the manifest says "Trailer record count 17 does not match 16 detail records.", which is clear enough for operations. `ops.CardFileLog` records the destination and `TRAILER_COUNT_MISMATCH x1`. The upper-case `SETTLEMENT_003.CSV` is archived (ADR 0007).
- [x] **card-empty-landing:** zero rows and the note "No settlement files found in the landing folder." are the desired outcome, not a failure. Reconciliation would surface any missing settlement.
- [x] **map-unmapped-code:** the batch fails at Map after Extract and Card have loaded, Reconciliation does not run, and the master's Failed event carries `TXN_CODE_UNMAPPED` (ADR 0007).
- [x] **map-midpoint-rounding:** banker's rounding on the exact decimal product, per allocation (PRD, ADR 0007). The worksheet's 24 allocations were hand-checked; 10 differ from half away from zero.
- [x] **recon-card-off-by-one-cent:** one exception (difference 0.01, tolerance 0.00) is alerted once. The payload names the business date, the difference and the summary, and carries no member, account or card numbers.
- [x] **rerun-earlier-date:** the rerun finds no files (archived on the first run), merges nothing (`unchanged=` all rows), reconciles, and touches only 2026-11-03.
- [x] **dst-transitions:** the trigger fires at 03:00 Pacific once per local day (10:00Z under PDT, 11:00Z under PST, hand-checked), and `businessDate` is the previous Pacific day. The 23:45 transaction KeyStone posted to 03-08 belongs to 03-08.

Open questions: none. The two raised in Phase 2 (file-name case, the master's error code) are decided in ADR 0007.

## Sign-off

| Reviewer | Role | Date | Scenarios | Notes |
| --- | --- | --- | --- | --- |
| Claude (AI assistant), at the direction of the repository owner | Engineering review in lieu of a business reviewer | 2026-11-05 | All ten, generator version 1 | Decisions in ADR 0007. Not a substitute for business review: confirm before go-live and compare with captured ADF outputs. |
