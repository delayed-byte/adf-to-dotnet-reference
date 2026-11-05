# map-unmapped-code

KeyStone posted three transactions with a new code, P2PD, that the KeyBridge mapping does not cover. Map and Aggregate fails with TXN_CODE_UNMAPPED naming P2PD; the master records the failure at the Map step and does not run Reconciliation.

- Acceptance tests: AT-09, AT-15
- Seed: 20261108; generator version 1
- KeyBridge mapping: contract default (contracts/keybridge/__files/gl-mappings.json), version 2026-11-01

## Runs and expected outcomes

| Run | Entry point | Business date | Trigger (UTC) | Status | Error code | Failed step | Not run | KeyBridge calls | Alerts |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 1 | PL_Master_NightlyBatch | 2026-11-04 | manual | Failed | BATCH_STEP_FAILED | PL_MapAndAggregate | PL_Reconciliation | 1 | 0 |

Expected error messages (ops.BatchRun.ErrorMessage contains):

- run 1, PL_MapAndAggregate / CheckTxnCodeMapping: `Unmapped transaction codes: P2PD`
- run 1, PL_Master_NightlyBatch / PL_MapAndAggregate: `Unmapped transaction codes: P2PD`

## Inputs

| Db2 table | Rows | Rows per date (date column) |
| --- | ---: | --- |
| KS.TXN_CODE (added) | 1 | - |
| KS.MEMBER | 50 | - |
| KS.ACCOUNT | 100 | 2026-11-04: 60; 40 dormant (older dates) |
| KS.POSTED_TXN | 203 | 2026-11-04: 203 |
| KS.GL_ENTRY | 20 | 2026-11-04: 20 |

| Settlement file | Detail rows | Trailer count | Trailer total | Signed total (SALE - RFND) |
| --- | ---: | ---: | ---: | ---: |
| card-landing/2026-11-04/settlement_001.csv | 67 | 67 | 4266.05 | 4064.37 |

## Golden tables

| Table | Rows | SHA-256 (ADR 0006) |
| --- | ---: | --- |
| [core.PostedTransaction](tables/core.PostedTransaction.csv) | 203 | `45b471c515aa8891f7f534c98c6181d2c8994a280f16389e7bc93faab6a0e55d` |
| [core.Account](tables/core.Account.csv) | 60 | `ccb341c96a614fa15d3e462575c60e99717220ec2f8f2fcd6a217dedd39639cd` |
| [core.GlEntry](tables/core.GlEntry.csv) | 20 | `b172de15947e9728abbe9f0055491733a6303c889d59b0c7901ccaa062c7e25a` |
| [core.CardSettlement](tables/core.CardSettlement.csv) | 67 | `f34143e771299dda96ba2c7bb149f78cae71e834254b9f6b014df974303afaa9` |
| [ops.CardFileLog](tables/ops.CardFileLog.csv) | 1 | `95aa38b9688087e3d15b7aeddd8560850c7cf574eefbc3239c56ee331939fc67` |
| [ref.TxnCodeGlMap](tables/ref.TxnCodeGlMap.csv) | 21 | `6594962706fcc31ec174243fc32d23b052539233f9798e4f7fbba774334d7515` |
| [core.DailyGlTotals](tables/core.DailyGlTotals.csv) | 0 | `e23fcbdc3bb07647bb36ddfe95e2eb3e7538e90aafd11551012aa9abf2bd5046` |
| [ops.ReconExceptions](tables/ops.ReconExceptions.csv) | 0 | `6191af985ed1fa8cae39eedc6c47f8ad09ed9b0bdd9b911f97e507153f0a1b00` |
| [ops.BatchRun](tables/ops.BatchRun.csv) | 15 | `351b07bf53b683c4f089e234f19a6ef1f501143e3021559c2c0d0a27c0270e35` |

## Business date 2026-11-04

Card check: settlement 4064.37, KeyStone 4064.37, difference 0.00.

