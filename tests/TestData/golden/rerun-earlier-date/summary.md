# rerun-earlier-date

Nightly batch for 2026-11-03, then 2026-11-04, then 2026-11-03 again. The rerun changes nothing and touches only 2026-11-03.

- Acceptance tests: AT-16
- Seed: 20261111; generator version 1
- KeyBridge mapping: contract default (contracts/keybridge/__files/gl-mappings.json), version 2026-11-01

## Runs and expected outcomes

| Run | Entry point | Business date | Trigger (UTC) | Status | Error code | Failed step | Not run | KeyBridge calls | Alerts |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 1 | PL_Master_NightlyBatch | 2026-11-03 | manual | Succeeded | - | - | - | 1 | 0 |
| 2 | PL_Master_NightlyBatch | 2026-11-04 | manual | Succeeded | - | - | - | 1 | 0 |
| 3 | PL_Master_NightlyBatch | 2026-11-03 | manual | Succeeded | - | - | - | 1 | 0 |

## Inputs

| Db2 table | Rows | Rows per date (date column) |
| --- | ---: | --- |
| KS.TXN_CODE (added) | 0 | - |
| KS.MEMBER | 63 | - |
| KS.ACCOUNT | 132 | 2026-11-03: 28, 2026-11-04: 50; 54 dormant (older dates) |
| KS.POSTED_TXN | 300 | 2026-11-03: 150, 2026-11-04: 150 |
| KS.GL_ENTRY | 30 | 2026-11-03: 15, 2026-11-04: 15 |

| Settlement file | Detail rows | Trailer count | Trailer total | Signed total (SALE - RFND) |
| --- | ---: | ---: | ---: | ---: |
| card-landing/2026-11-03/settlement_001.csv | 52 | 52 | 3568.33 | 3472.33 |
| card-landing/2026-11-04/settlement_001.csv | 66 | 66 | 4882.99 | 4551.01 |

## Golden tables

| Table | Rows | SHA-256 (ADR 0006) |
| --- | ---: | --- |
| [core.PostedTransaction](tables/core.PostedTransaction.csv) | 300 | `30dc66153b65da7b8c30bf550707654cca9891d3decf3e36d63304aaae511167` |
| [core.Account](tables/core.Account.csv) | 78 | `b9ee2d4e4026e0141624406c47d211a780459e1f159d75341ae3937aac320fa0` |
| [core.GlEntry](tables/core.GlEntry.csv) | 30 | `d35aae87a196c135f1ddc6c094d7422b260dd8483a4501c2a5e29365fe08e71e` |
| [core.CardSettlement](tables/core.CardSettlement.csv) | 118 | `67df394d99a0a737847d62e640539dec4cc657ed2cf67b20b75f1cacea79452b` |
| [ops.CardFileLog](tables/ops.CardFileLog.csv) | 2 | `45e665488cbe3fc9a49932f7eb7a089dbd79cd480cb5339c9349bad3c38514b6` |
| [ref.TxnCodeGlMap](tables/ref.TxnCodeGlMap.csv) | 21 | `6594962706fcc31ec174243fc32d23b052539233f9798e4f7fbba774334d7515` |
| [core.DailyGlTotals](tables/core.DailyGlTotals.csv) | 18 | `843cba7d69ef7456778d3ccc6c92b8e4a0dcc6ee29ca173b41fb6b150bfa08c0` |
| [ops.ReconExceptions](tables/ops.ReconExceptions.csv) | 0 | `6191af985ed1fa8cae39eedc6c47f8ad09ed9b0bdd9b911f97e507153f0a1b00` |
| [ops.BatchRun](tables/ops.BatchRun.csv) | 51 | `570dc1b69ffabf1e11156b8446b4a2f42b3653a0892fe57b8fb344ab35093500` |

## Business date 2026-11-03

Card check: settlement 3472.33, KeyStone 3472.33, difference 0.00.

| GL account | Debit total | Credit total | Net | Txns | KeyStone debit | KeyStone credit |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 1010 | 14393.35 | 4414.21 | 9979.14 | 24 | 14393.35 | 4414.21 |
| 1020 | 0.00 | 2660.00 | -2660.00 | 13 | 0.00 | 2660.00 |
| 1110 | 24704.35 | 17009.94 | 7694.41 | 34 | 24704.35 | 17009.94 |
| 1300 | 0.00 | 2723.07 | -2723.07 | 9 | 0.00 | 2723.07 |
| 2100 | 31033.10 | 39244.48 | -8211.38 | 150 | 31033.10 | 39244.48 |
| 2400 | 48.00 | 3520.33 | -3472.33 | 52 | 48.00 | 3520.33 |
| 4100 | 0.00 | 480.55 | -480.55 | 9 | 0.00 | 480.55 |
| 4300 | 0.00 | 225.00 | -225.00 | 12 | 0.00 | 225.00 |
| 5100 | 98.78 | 0.00 | 98.78 | 6 | 98.78 | 0.00 |

## Business date 2026-11-04

Card check: settlement 4551.01, KeyStone 4551.01, difference 0.00.

| GL account | Debit total | Credit total | Net | Txns | KeyStone debit | KeyStone credit |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 1010 | 12715.42 | 1016.97 | 11698.45 | 16 | 12715.42 | 1016.97 |
| 1020 | 0.00 | 2600.00 | -2600.00 | 11 | 0.00 | 2600.00 |
| 1110 | 43330.10 | 13889.14 | 29440.96 | 38 | 43330.10 | 13889.14 |
| 1300 | 0.00 | 5046.61 | -5046.61 | 11 | 0.00 | 5046.61 |
| 2100 | 28225.29 | 56345.61 | -28120.32 | 150 | 28225.29 | 56345.61 |
| 2400 | 165.99 | 4717.00 | -4551.01 | 66 | 165.99 | 4717.00 |
| 4100 | 0.00 | 890.57 | -890.57 | 11 | 0.00 | 890.57 |
| 4300 | 0.00 | 65.00 | -65.00 | 4 | 0.00 | 65.00 |
| 5100 | 134.10 | 0.00 | 134.10 | 4 | 134.10 | 0.00 |
