# map-midpoint-rounding

Loan payments whose GL allocations (75% principal, 25% interest) land exactly on half a cent, e.g. 0.50 x 0.25 = 0.125. Banker's rounding gives 0.12; rounding half away from zero would give 0.13. Includes 4.70 x 0.25 = 1.175, which rounds differently in binary floating point (ADR 0006).

- Acceptance tests: AT-10
- Seed: 20261109; generator version 1
- KeyBridge mapping: scenario override (inputs/map-midpoint-rounding/keybridge/gl-mappings.json), version 2026-11-01

## Runs and expected outcomes

| Run | Entry point | Business date | Trigger (UTC) | Status | Error code | Failed step | Not run | KeyBridge calls | Alerts |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 1 | PL_Master_NightlyBatch | 2026-11-04 | manual | Succeeded | - | - | - | 1 | 0 |

## Inputs

| Db2 table | Rows | Rows per date (date column) |
| --- | ---: | --- |
| KS.TXN_CODE (added) | 0 | - |
| KS.MEMBER | 1 | - |
| KS.ACCOUNT | 3 | 2026-11-04: 2; 1 dormant (older dates) |
| KS.POSTED_TXN | 13 | 2026-11-04: 13 |
| KS.GL_ENTRY | 8 | 2026-11-04: 8 |

## Golden tables

| Table | Rows | SHA-256 (ADR 0006) |
| --- | ---: | --- |
| [core.PostedTransaction](tables/core.PostedTransaction.csv) | 13 | `9c0b30cb7f2a5284055f3232c9216cb28ec135dd3ec147238e16fedcf4485d59` |
| [core.Account](tables/core.Account.csv) | 2 | `cebcb00fe2566b7905ce7918ed0fec7ba8124c983b3a3bab1b87e860cc3c3beb` |
| [core.GlEntry](tables/core.GlEntry.csv) | 8 | `ece890c7cc817f16df20e2eacf2c874d7c2c450d756069a81bc22d8e8cf80dfc` |
| [core.CardSettlement](tables/core.CardSettlement.csv) | 0 | `e43a75d025fd93f2ce9326370dc36917f9c8970b2e3edad1f3cf10f46ec3f76b` |
| [ops.CardFileLog](tables/ops.CardFileLog.csv) | 0 | `c57d6fe7a56519afc8a7780ebaf2f95ce557c57b1e593cb1285f80fe11b896ba` |
| [ref.TxnCodeGlMap](tables/ref.TxnCodeGlMap.csv) | 21 | `632f81231bcf3f6a045fcf474e36b797ee6e3ed819249d11befe6a06b237a447` |
| [core.DailyGlTotals](tables/core.DailyGlTotals.csv) | 4 | `8d55d03c516d308f02ba5248ab49c43531c8c065f1d731f89ded62b086bff34e` |
| [ops.ReconExceptions](tables/ops.ReconExceptions.csv) | 0 | `6191af985ed1fa8cae39eedc6c47f8ad09ed9b0bdd9b911f97e507153f0a1b00` |
| [ops.BatchRun](tables/ops.BatchRun.csv) | 17 | `af4b692c29239354f35bd7221575ea7f5d6b3f34b3f474fcf528ab0264e7302b` |

## Business date 2026-11-04

Card check: settlement 0.00, KeyStone 0.00, difference 0.00.

| GL account | Debit total | Credit total | Net | Txns | KeyStone debit | KeyStone credit |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 1010 | 1000.00 | 0.00 | 1000.00 | 1 | 1000.00 | 0.00 |
| 1300 | 0.00 | 16.30 | -16.30 | 12 | 0.00 | 16.30 |
| 2100 | 21.73 | 1000.00 | -978.27 | 13 | 21.73 | 1000.00 |
| 4100 | 0.00 | 5.43 | -5.43 | 12 | 0.00 | 5.43 |

## Rounding worksheet (review)

Each loan payment is split 75% / 25%. Golden values use banker's rounding on the exact decimal product.

| Amount | x 0.75 exact | 1300 (to even) | half away from zero | x 0.25 exact | 4100 (to even) | half away from zero |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 0.50 | 0.3750 | 0.38 | 0.38 | 0.1250 | 0.12 | 0.13 |
| 0.10 | 0.0750 | 0.08 | 0.08 | 0.0250 | 0.02 | 0.03 |
| 0.10 | 0.0750 | 0.08 | 0.08 | 0.0250 | 0.02 | 0.03 |
| 0.10 | 0.0750 | 0.08 | 0.08 | 0.0250 | 0.02 | 0.03 |
| 0.30 | 0.2250 | 0.22 | 0.23 | 0.0750 | 0.08 | 0.08 |
| 0.70 | 0.5250 | 0.52 | 0.53 | 0.1750 | 0.18 | 0.18 |
| 1.10 | 0.8250 | 0.82 | 0.83 | 0.2750 | 0.28 | 0.28 |
| 4.70 | 3.5250 | 3.52 | 3.53 | 1.1750 | 1.18 | 1.18 |
| 2.70 | 2.0250 | 2.02 | 2.03 | 0.6750 | 0.68 | 0.68 |
| 10.10 | 7.5750 | 7.58 | 7.58 | 2.5250 | 2.52 | 2.53 |
| 1.00 | 0.7500 | 0.75 | 0.75 | 0.2500 | 0.25 | 0.25 |
| 0.33 | 0.2475 | 0.25 | 0.25 | 0.0825 | 0.08 | 0.08 |

4.70 x 0.25 = 1.175 exactly, so banker's rounding gives 1.18. As a binary double, 1.175 is 1.17499999999999982236431605997495353221893310546875 and rounds to 1.17. ADF's round() returns a double (ADR 0004); the golden value follows the PRD (decimal, MidpointRounding.ToEven). Confirm against a production ADF run.
