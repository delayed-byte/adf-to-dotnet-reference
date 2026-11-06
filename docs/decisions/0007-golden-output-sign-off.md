# 0007. Golden output sign-off without a business reviewer

Status: Accepted · Date: 2026-11-05

## Context

The PRD's definition of done requires golden outputs to be "reviewed and signed off by a person who knows the business rules" before Phase 3. The repository owner has no business or banking reviewer available and asked for the review to be done by engineering judgement and recorded as the sign-off. ADR 0006 left choices about ADF semantics open, because ADF could not be run.

## Decision

The golden outputs are signed off as an **engineering review** (`tests/TestData/REVIEW.md`), on these grounds:

- Every checklist item is backed by automated evidence: the stored-procedure replay, an independent T-SQL statement of the data-flow rule, and hand-checked unit cases.
- The open questions are settled by the rules below.
- The record says plainly that no business reviewer took part.

| Question | Decision | Reason |
| --- | --- | --- |
| Is a settlement file named with different letter case (`SETTLEMENT_003.CSV`) processed? | **Yes.** The file filter is case-insensitive. Scenario `card-quarantine-trailer-mismatch` now includes such a file. | ADF documents `startsWith` and `endsWith` as case-insensitive, so this is the equivalent behaviour. Skipping the file would also leave a settlement unloaded and surface only later as a recon exception. |
| On a child failure, which `ErrorCode` does the master's Failed event carry? | **The child's code** (for example `TXN_CODE_UNMAPPED`). The master run itself fails with `BATCH_STEP_FAILED`. | The audit trail should name the root cause for operations and examiners. ADF's own propagation could not be observed; the .NET conversion deliberately records the root cause. |
| How are half-cent allocations rounded? | **Banker's rounding on the exact decimal product, per allocation.** | The PRD states it explicitly (`MidpointRounding.ToEven`, money as `decimal`). Where ADF's double-based `round()` differs (4.70 × 0.25 = 1.175), the PRD rule wins. |
| Are decimals in BatchRun notes written at their SQL scale (`0.00`)? | **Yes.** | Matches the values the procedures return; it is unambiguous for auditors. |

## Consequences

- The PRD item "signed off by a person who knows the business rules" is met by an engineering review, not literally. Before go-live, the credit union must still:
  - run the real ADF factory and the .NET conversion on the same inputs and compare the outputs with `TableComparison`;
  - have a business reviewer confirm the four decisions above.

  Any difference becomes a new ADR and a generator change.
- Phase 5 (Card Settlement) must match file names case-insensitively; AT-06 fails otherwise.
- Changing any of these decisions means changing the generator and regenerating the golden files (ADR 0006); the drift test enforces it.
