# recon-card-off-by-one-cent

One settlement detail is 0.01 higher than the KeyStone card transaction it settles. The card check finds a difference of 0.01; one exception row is written and exactly one alert is posted. The GL check reconciles.

- Acceptance tests: AT-13
- Seed: 20261110; generator version 1
- KeyBridge mapping: contract default (contracts/keybridge/__files/gl-mappings.json), version 2026-11-01

## Runs and expected outcomes

| Run | Entry point | Business date | Trigger (UTC) | Status | Error code | Failed step | Not run | KeyBridge calls | Alerts |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 1 | PL_Master_NightlyBatch | 2026-11-04 | manual | Succeeded | - | - | - | 1 | 1 |

## Inputs

| Db2 table | Rows | Rows per date (date column) |
| --- | ---: | --- |
| KS.TXN_CODE (added) | 0 | - |
| KS.MEMBER | 58 | - |
| KS.ACCOUNT | 122 | 2026-11-04: 80; 42 dormant (older dates) |
| KS.POSTED_TXN | 300 | 2026-11-04: 300 |
| KS.GL_ENTRY | 25 | 2026-11-04: 25 |

| Settlement file | Detail rows | Trailer count | Trailer total | Signed total (SALE - RFND) |
| --- | ---: | ---: | ---: | ---: |
| card-landing/2026-11-04/settlement_001.csv | 53 | 53 | 4123.30 | 3782.12 |
| card-landing/2026-11-04/settlement_002.csv | 54 | 54 | 3711.18 | 3649.54 |

## Golden tables

| Table | Rows | SHA-256 (ADR 0006) |
| --- | ---: | --- |
| [core.PostedTransaction](tables/core.PostedTransaction.csv) | 300 | `7b1db257ec22f187a7e3ce5588a65f07586a077d6565f5b094cc6cd857e64223` |
| [core.Account](tables/core.Account.csv) | 80 | `ce8a96f7cf205503d20e35a15358d60fee41d259dedc5740236c13e5cb9faed9` |
| [core.GlEntry](tables/core.GlEntry.csv) | 25 | `960c0bd7366cc4b5926e396ce9958696e8f77eaca14d2f3242591c1fcd7a5a7a` |
| [core.CardSettlement](tables/core.CardSettlement.csv) | 107 | `a51e8c8d7b76e9221f05afe9db0355dfc5bf1c1678358537628f929a62e2aeea` |
| [ops.CardFileLog](tables/ops.CardFileLog.csv) | 2 | `4c45356fd3fe9909fc8dfa4d29106c3b431c65bd92fc7410e691da1a96c36a0f` |
| [ref.TxnCodeGlMap](tables/ref.TxnCodeGlMap.csv) | 21 | `6594962706fcc31ec174243fc32d23b052539233f9798e4f7fbba774334d7515` |
| [core.DailyGlTotals](tables/core.DailyGlTotals.csv) | 9 | `188fac9a942766c14023c679655a4bd1d986027ef3ac239de3a48f94f4fcd53b` |
| [ops.ReconExceptions](tables/ops.ReconExceptions.csv) | 1 | `7cc3fc9dd71c307f0fc7f0b0ec38397a97520c100db633129a90d62c1e711088` |
| [ops.BatchRun](tables/ops.BatchRun.csv) | 17 | `9702c4206bdd424050509d5b824740b75a116d6277dff39ad48edf1f140bebdb` |

## Business date 2026-11-04

Card check: settlement 7431.66, KeyStone 7431.65, difference 0.01.

| GL account | Debit total | Credit total | Net | Txns | KeyStone debit | KeyStone credit |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 1010 | 30683.06 | 5320.84 | 25362.22 | 37 | 30683.06 | 5320.84 |
| 1020 | 0.00 | 6580.00 | -6580.00 | 26 | 0.00 | 6580.00 |
| 1110 | 66554.38 | 33686.98 | 32867.40 | 76 | 66554.38 | 33686.98 |
| 1300 | 0.00 | 7669.83 | -7669.83 | 21 | 0.00 | 7669.83 |
| 2100 | 62529.22 | 97855.77 | -35326.55 | 300 | 62529.22 | 97855.77 |
| 2400 | 201.41 | 7633.06 | -7431.65 | 107 | 201.41 | 7633.06 |
| 4100 | 0.00 | 1353.51 | -1353.51 | 21 | 0.00 | 1353.51 |
| 4300 | 0.00 | 285.00 | -285.00 | 18 | 0.00 | 285.00 |
| 5100 | 416.92 | 0.00 | 416.92 | 15 | 416.92 | 0.00 |

Exception: CardSettlement ALL, KeyStone 7431.65, compared 7431.66, difference 0.01, alerted in run 1.
