# standard-day

Seeded business day 2026-11-04: 5,000 transactions on 800 accounts, 300 GL entries and three valid settlement files, with distractors (the previous day's data and files, non-matching names and a subfolder in the landing folder). Full nightly batch; everything reconciles.

- Acceptance tests: AT-01, AT-02, AT-04, AT-05, AT-08, AT-11, AT-12, AT-14, AT-17
- Seed: 20261104; generator version 1
- KeyBridge mapping: contract default (contracts/keybridge/__files/gl-mappings.json), version 2026-11-01

## Runs and expected outcomes

| Run | Entry point | Business date | Trigger (UTC) | Status | Error code | Failed step | Not run | KeyBridge calls | Alerts |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 1 | PL_Master_NightlyBatch | 2026-11-04 | manual | Succeeded | - | - | - | 1 | 0 |

## Inputs

| Db2 table | Rows | Rows per date (date column) |
| --- | ---: | --- |
| KS.TXN_CODE (added) | 0 | - |
| KS.MEMBER | 508 | - |
| KS.ACCOUNT | 1020 | 2026-11-03: 12, 2026-11-04: 800; 208 dormant (older dates) |
| KS.POSTED_TXN | 5200 | 2026-11-03: 200, 2026-11-04: 5000 |
| KS.GL_ENTRY | 320 | 2026-11-03: 20, 2026-11-04: 300 |

| Settlement file | Detail rows | Trailer count | Trailer total | Signed total (SALE - RFND) |
| --- | ---: | ---: | ---: | ---: |
| card-landing/2026-11-03/settlement_001.csv | 77 | 77 | 5438.54 | 4697.66 |
| card-landing/2026-11-04/settlement_001.csv | 619 | 619 | 41132.57 | 38085.43 |
| card-landing/2026-11-04/settlement_002.csv | 619 | 619 | 39048.50 | 35678.22 |
| card-landing/2026-11-04/settlement_003.csv | 619 | 619 | 45424.43 | 40915.71 |

Other blobs (must be left untouched): `card-landing/2026-11-04/readme.txt`, `card-landing/2026-11-04/settlement_004.csv.tmp`, `card-landing/2026-11-04/old/settlement_000.csv`

## Golden tables

| Table | Rows | SHA-256 (ADR 0006) |
| --- | ---: | --- |
| [core.PostedTransaction](tables/core.PostedTransaction.csv) | 5000 | `d3f69f043560af2e50dc22f893c4c0c2a15843cfa14408cabeca826f848d6673` |
| [core.Account](tables/core.Account.csv) | 800 | `8a358f932111281c809aafe81001fbafd873dba243f64ec7d6ebcb02de244a05` |
| [core.GlEntry](tables/core.GlEntry.csv) | 300 | `115d28bdbb87cb53a64f2f7fbf46ff0dd5714783231600c15b46656da65d3e1c` |
| [core.CardSettlement](tables/core.CardSettlement.csv) | 1857 | `efbb2e30db7348867625ac818eec26a80a2fd9e42ddb13343415539a098ff86c` |
| [ops.CardFileLog](tables/ops.CardFileLog.csv) | 3 | `3880a2a29bc97202be64680365ad089922d843f2d11e1b63a50002704efcfeb3` |
| [ref.TxnCodeGlMap](tables/ref.TxnCodeGlMap.csv) | 21 | `6594962706fcc31ec174243fc32d23b052539233f9798e4f7fbba774334d7515` |
| [core.DailyGlTotals](tables/core.DailyGlTotals.csv) | 9 | `e1007c2aff2ccb98cf4b54fe6d491b2bd468c1bf478c1cd85176a94d2d9333d1` |
| [ops.ReconExceptions](tables/ops.ReconExceptions.csv) | 0 | `6191af985ed1fa8cae39eedc6c47f8ad09ed9b0bdd9b911f97e507153f0a1b00` |
| [ops.BatchRun](tables/ops.BatchRun.csv) | 17 | `ecc1688b8c0a55cf17daf877713402cc334bacff06310cad0ead9f1514ad5750` |

## Business date 2026-11-04

Card check: settlement 114679.36, KeyStone 114679.36, difference 0.00.

| GL account | Debit total | Credit total | Net | Txns | KeyStone debit | KeyStone credit |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 1010 | 412775.88 | 128067.68 | 284708.20 | 657 | 412775.88 | 128067.68 |
| 1020 | 0.00 | 83860.00 | -83860.00 | 396 | 0.00 | 83860.00 |
| 1110 | 1109849.24 | 541813.74 | 568035.50 | 1283 | 1109849.24 | 541813.74 |
| 1300 | 0.00 | 139936.85 | -139936.85 | 341 | 0.00 | 139936.85 |
| 2100 | 1042805.48 | 1533964.28 | -491158.80 | 5000 | 1042805.48 | 1533964.28 |
| 2400 | 5463.07 | 120142.43 | -114679.36 | 1857 | 5463.07 | 120142.43 |
| 4100 | 0.00 | 24694.78 | -24694.78 | 341 | 0.00 | 24694.78 |
| 4300 | 0.00 | 4290.00 | -4290.00 | 240 | 0.00 | 4290.00 |
| 5100 | 5876.09 | 0.00 | 5876.09 | 226 | 5876.09 | 0.00 |
