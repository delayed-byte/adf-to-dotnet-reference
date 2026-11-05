# Golden output review

The PRD requires golden outputs to be "reviewed and signed off by a person who knows the business rules" before the acceptance tests are written (Phase 3). They were derived from the specification, not captured from ADF, so this review is the control against a misreading of the spec (PRD risk table).

## What has already been checked automatically

- Every golden table, error manifest, recon outcome and evidence checksum agrees with the Phase 1 stored procedures when the scenario's inputs are replayed through them (`GoldenConsistencyTests`).
- `core.DailyGlTotals` agrees with a separate T-SQL statement of the data-flow rule.
- The extract tables equal the Db2 rows read back with the ADR 0005 rules.
- Inputs satisfy the Db2 schema constraints and contain only synthetic, reserved-range identifiers.

The review is therefore about whether the **rules** are right, not whether the arithmetic is.

## Checklist

For each scenario, read `golden/{scenario}/summary.md`; open the CSVs where useful.

- [ ] **standard-day:** the card check reconciles (settlement total = KeyStone CPOS − CRFD), and every GL account's derived debit and credit equal KeyStone's.
- [ ] **standard-day:** the `ops.BatchRun` events and notes are what the ADF pipelines would log.
- [ ] **standard-day:** only `card-landing/2026-11-04/settlement_*.csv` is processed. `readme.txt`, `settlement_004.csv.tmp`, the `old/` subfolder and the 2026-11-03 file stay in landing.
- [ ] **extract-db2-types:** each value in `core.*` is exactly the Db2 value: padding kept, `""` distinct from NULL, 31-digit decimals intact, timestamps not shifted (ADR 0005).
- [ ] **card-quarantine-trailer-mismatch:** the manifest text and the `ops.CardFileLog` row for the quarantined file are acceptable to operations.
- [ ] **card-empty-landing:** zero rows and the note "No settlement files found in the landing folder." are the desired outcome. It is not a failure.
- [ ] **map-unmapped-code:**
  - The batch should fail at Map, and Extract and Card should still have loaded.
  - The master's Failed event should carry the child's `TXN_CODE_UNMAPPED`.
- [ ] **map-midpoint-rounding:** banker's rounding on the exact decimal product is the intended rule. Check the worksheet, including 4.70 × 0.25 = 1.175 → 1.18, where ADF's double-based `round()` may give 1.17.
- [ ] **recon-card-off-by-one-cent:** one exception, alerted once; the alert payload is acceptable.
- [ ] **rerun-earlier-date:** the rerun finds no files (they were archived on the first run), merges nothing, reconciles, and touches only 2026-11-03.
- [ ] **dst-transitions:**
  - The trigger fires at 03:00 Pacific once per day (10:00Z under PDT, 11:00Z under PST).
  - `businessDate` is the previous Pacific day.
  - A transaction posted at 23:45 on 03-07 but assigned to business date 03-08 belongs to 03-08.

Open questions for the reviewer (ADR 0006, "ADF semantics the golden output encodes"):

1. Should a settlement file named `Settlement_001.CSV` be processed? ADF's `startsWith` and `endsWith` are case-insensitive; no scenario uses such a name yet.
2. When the real factory runs the same inputs, does the master's `ErrorCode` show the child's code or ADF's own?

## Sign-off

| Reviewer | Role | Date | Scenarios | Notes |
| --- | --- | --- | --- | --- |
| | | | | |
