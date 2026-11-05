# dst-transitions

Trigger-driven runs on and after the 2026 spring-forward (2026-03-08) and fall-back (2026-11-01) days in America/Los_Angeles, with transactions at the first and last microsecond of each day, in the repeated 01:00 hour, either side of the skipped 02:00 hour, and one posted at 23:45 that KeyStone assigned to the next business date.

- Acceptance tests: AT-18
- Seed: 20260308; generator version 1
- KeyBridge mapping: contract default (contracts/keybridge/__files/gl-mappings.json), version 2026-11-01

## Runs and expected outcomes

| Run | Entry point | Business date | Trigger (UTC) | Status | Error code | Failed step | Not run | KeyBridge calls | Alerts |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 1 | PL_Master_NightlyBatch | 2026-03-07 | 2026-03-08 10:00 | Succeeded | - | - | - | 1 | 0 |
| 2 | PL_Master_NightlyBatch | 2026-03-08 | 2026-03-09 10:00 | Succeeded | - | - | - | 1 | 0 |
| 3 | PL_Master_NightlyBatch | 2026-10-31 | 2026-11-01 11:00 | Succeeded | - | - | - | 1 | 0 |
| 4 | PL_Master_NightlyBatch | 2026-11-01 | 2026-11-02 11:00 | Succeeded | - | - | - | 1 | 0 |

## Inputs

| Db2 table | Rows | Rows per date (date column) |
| --- | ---: | --- |
| KS.TXN_CODE (added) | 0 | - |
| KS.MEMBER | 40 | - |
| KS.ACCOUNT | 88 | 2026-03-06: 1, 2026-03-07: 7, 2026-03-08: 11, 2026-03-09: 2, 2026-10-30: 4, 2026-10-31: 12, 2026-11-01: 14, 2026-11-02: 5; 32 dormant (older dates) |
| KS.POSTED_TXN | 213 | 2026-03-06: 10, 2026-03-07: 42, 2026-03-08: 44, 2026-03-09: 11, 2026-10-30: 10, 2026-10-31: 41, 2026-11-01: 44, 2026-11-02: 11 |
| KS.GL_ENTRY | 48 | 2026-03-07: 12, 2026-03-08: 12, 2026-10-31: 12, 2026-11-01: 12 |

| Settlement file | Detail rows | Trailer count | Trailer total | Signed total (SALE - RFND) |
| --- | ---: | ---: | ---: | ---: |
| card-landing/2026-03-07/settlement_001.csv | 11 | 11 | 393.55 | 338.77 |
| card-landing/2026-03-08/settlement_001.csv | 16 | 16 | 1281.66 | 1281.66 |
| card-landing/2026-10-31/settlement_001.csv | 11 | 11 | 534.55 | 502.15 |
| card-landing/2026-11-01/settlement_001.csv | 13 | 13 | 1054.52 | 1054.52 |

## Golden tables

| Table | Rows | SHA-256 (ADR 0006) |
| --- | ---: | --- |
| [core.PostedTransaction](tables/core.PostedTransaction.csv) | 171 | `eeba8d56f6434ccb7ede315acffc872c307eda7b0368f77d96b1eb3bea718084` |
| [core.Account](tables/core.Account.csv) | 44 | `32cc40ce93dcfa28bd8b794edce85771a31e217032877e2816d7c0427b104103` |
| [core.GlEntry](tables/core.GlEntry.csv) | 48 | `e081eeffc8e06511c2f6cd4f056f46d2a101bb1b92a0f27084a59540b4968e77` |
| [core.CardSettlement](tables/core.CardSettlement.csv) | 51 | `71900e6c638bb5a2d643de9bfcf6bcdbacdb1d65a8758961dbacb2011ecb62d7` |
| [ops.CardFileLog](tables/ops.CardFileLog.csv) | 4 | `cff33a851e8cc15ceceb9d4d01a0483ab98c974b864f6f559a8eaf5be3ad0f70` |
| [ref.TxnCodeGlMap](tables/ref.TxnCodeGlMap.csv) | 21 | `6594962706fcc31ec174243fc32d23b052539233f9798e4f7fbba774334d7515` |
| [core.DailyGlTotals](tables/core.DailyGlTotals.csv) | 34 | `8545692471645132207077bf04513416e1aa9969a62be86d36766c34741f069f` |
| [ops.ReconExceptions](tables/ops.ReconExceptions.csv) | 0 | `6191af985ed1fa8cae39eedc6c47f8ad09ed9b0bdd9b911f97e507153f0a1b00` |
| [ops.BatchRun](tables/ops.BatchRun.csv) | 68 | `e8594d172417aeda0628eb4a77afd6ba02b3ec93e4cdffde4afa8869f1cc20f4` |

## Business date 2026-03-07

Card check: settlement 338.77, KeyStone 338.77, difference 0.00.

| GL account | Debit total | Credit total | Net | Txns | KeyStone debit | KeyStone credit |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 1010 | 4398.12 | 687.31 | 3710.81 | 7 | 4398.12 | 687.31 |
| 1020 | 0.00 | 1340.00 | -1340.00 | 6 | 0.00 | 1340.00 |
| 1110 | 4917.61 | 7409.40 | -2491.79 | 12 | 4917.61 | 7409.40 |
| 1300 | 0.00 | 2389.23 | -2389.23 | 4 | 0.00 | 2389.23 |
| 2100 | 12613.73 | 9392.13 | 3221.60 | 42 | 12613.73 | 9392.13 |
| 2400 | 27.39 | 366.16 | -338.77 | 11 | 27.39 | 366.16 |
| 4100 | 0.00 | 421.63 | -421.63 | 4 | 0.00 | 421.63 |
| 5100 | 49.01 | 0.00 | 49.01 | 2 | 49.01 | 0.00 |

## Business date 2026-03-08

Card check: settlement 1281.66, KeyStone 1281.66, difference 0.00.

| GL account | Debit total | Credit total | Net | Txns | KeyStone debit | KeyStone credit |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 1010 | 8701.55 | 1113.43 | 7588.12 | 12 | 8701.55 | 1113.43 |
| 1020 | 0.00 | 460.00 | -460.00 | 2 | 0.00 | 460.00 |
| 1110 | 6259.55 | 1914.57 | 4344.98 | 7 | 6259.55 | 1914.57 |
| 1300 | 0.00 | 1250.68 | -1250.68 | 3 | 0.00 | 1250.68 |
| 2100 | 6276.05 | 14981.28 | -8705.23 | 44 | 6276.05 | 14981.28 |
| 2400 | 0.00 | 1281.66 | -1281.66 | 16 | 0.00 | 1281.66 |
| 4100 | 0.00 | 220.71 | -220.71 | 3 | 0.00 | 220.71 |
| 4300 | 0.00 | 35.00 | -35.00 | 3 | 0.00 | 35.00 |
| 5100 | 20.18 | 0.00 | 20.18 | 1 | 20.18 | 0.00 |

## Business date 2026-10-31

Card check: settlement 502.15, KeyStone 502.15, difference 0.00.

| GL account | Debit total | Credit total | Net | Txns | KeyStone debit | KeyStone credit |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 1010 | 2041.93 | 607.49 | 1434.44 | 6 | 2041.93 | 607.49 |
| 1020 | 0.00 | 660.00 | -660.00 | 4 | 0.00 | 660.00 |
| 1110 | 7861.24 | 6265.34 | 1595.90 | 14 | 7861.24 | 6265.34 |
| 1300 | 0.00 | 1237.36 | -1237.36 | 4 | 0.00 | 1237.36 |
| 2100 | 9556.90 | 9919.37 | -362.47 | 41 | 9556.90 | 9919.37 |
| 2400 | 16.20 | 518.35 | -502.15 | 11 | 16.20 | 518.35 |
| 4100 | 0.00 | 218.36 | -218.36 | 4 | 0.00 | 218.36 |
| 4300 | 0.00 | 50.00 | -50.00 | 2 | 0.00 | 50.00 |

## Business date 2026-11-01

Card check: settlement 1054.52, KeyStone 1054.52, difference 0.00.

| GL account | Debit total | Credit total | Net | Txns | KeyStone debit | KeyStone credit |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 1010 | 419.25 | 599.67 | -180.42 | 7 | 419.25 | 599.67 |
| 1020 | 0.00 | 240.00 | -240.00 | 1 | 0.00 | 240.00 |
| 1110 | 8112.39 | 6428.97 | 1683.42 | 14 | 8112.39 | 6428.97 |
| 1300 | 0.00 | 419.18 | -419.18 | 2 | 0.00 | 419.18 |
| 2100 | 8956.31 | 8616.60 | 339.71 | 44 | 8956.31 | 8616.60 |
| 2400 | 0.00 | 1054.52 | -1054.52 | 13 | 0.00 | 1054.52 |
| 4100 | 0.00 | 73.97 | -73.97 | 2 | 0.00 | 73.97 |
| 4300 | 0.00 | 140.00 | -140.00 | 4 | 0.00 | 140.00 |
| 5100 | 84.96 | 0.00 | 84.96 | 3 | 84.96 | 0.00 |

## Trigger schedule (TR_Nightly_0300_Pacific)

| Local date | Fires at (local) | Fires at (UTC) | businessDate |
| --- | --- | --- | --- |
| 2026-03-06 | 2026-03-06T03:00:00-08:00 | 2026-03-06T11:00:00Z | 2026-03-05 |
| 2026-03-07 | 2026-03-07T03:00:00-08:00 | 2026-03-07T11:00:00Z | 2026-03-06 |
| 2026-03-08 | 2026-03-08T03:00:00-07:00 | 2026-03-08T10:00:00Z | 2026-03-07 |
| 2026-03-09 | 2026-03-09T03:00:00-07:00 | 2026-03-09T10:00:00Z | 2026-03-08 |
| 2026-03-10 | 2026-03-10T03:00:00-07:00 | 2026-03-10T10:00:00Z | 2026-03-09 |
| 2026-10-30 | 2026-10-30T03:00:00-07:00 | 2026-10-30T10:00:00Z | 2026-10-29 |
| 2026-10-31 | 2026-10-31T03:00:00-07:00 | 2026-10-31T10:00:00Z | 2026-10-30 |
| 2026-11-01 | 2026-11-01T03:00:00-08:00 | 2026-11-01T11:00:00Z | 2026-10-31 |
| 2026-11-02 | 2026-11-02T03:00:00-08:00 | 2026-11-02T11:00:00Z | 2026-11-01 |
| 2026-11-03 | 2026-11-03T03:00:00-08:00 | 2026-11-03T11:00:00Z | 2026-11-02 |
