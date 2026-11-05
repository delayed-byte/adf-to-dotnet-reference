# card-quarantine-trailer-mismatch

Three settlement files; the second claims one more detail record in its trailer than it contains. That file is quarantined with an error manifest; the other two load.

- Acceptance tests: AT-06
- Seed: 20261106; generator version 1
- KeyBridge mapping: contract default (contracts/keybridge/__files/gl-mappings.json), version 2026-11-01

## Runs and expected outcomes

| Run | Entry point | Business date | Trigger (UTC) | Status | Error code | Failed step | Not run | KeyBridge calls | Alerts |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 1 | PL_IngestCardSettlement | 2026-11-04 | manual | Succeeded | - | - | - | 0 | 0 |

## Inputs

| Db2 table | Rows | Rows per date (date column) |
| --- | ---: | --- |
| KS.TXN_CODE (added) | 0 | - |
| KS.MEMBER | 31 | - |
| KS.ACCOUNT | 66 | 2026-11-04: 40; 26 dormant (older dates) |
| KS.POSTED_TXN | 150 | 2026-11-04: 150 |
| KS.GL_ENTRY | 13 | 2026-11-04: 13 |

| Settlement file | Detail rows | Trailer count | Trailer total | Signed total (SALE - RFND) |
| --- | ---: | ---: | ---: | ---: |
| card-landing/2026-11-04/settlement_001.csv | 16 | 16 | 901.54 | 901.54 |
| card-landing/2026-11-04/settlement_002.csv | 16 | 17 | 957.21 | 795.09 |
| card-landing/2026-11-04/settlement_003.csv | 16 | 16 | 857.21 | 592.25 |

## Golden tables

| Table | Rows | SHA-256 (ADR 0006) |
| --- | ---: | --- |
| [core.CardSettlement](tables/core.CardSettlement.csv) | 32 | `7a8bdc020b6d151b596df2497a03c9699c524b01da2d11c8f535cfb4627407a9` |
| [ops.CardFileLog](tables/ops.CardFileLog.csv) | 3 | `4eb0a6cfdeb98d6d7bf573360e211270005b6cdeb27aaf58942bd1c0e88e3bd1` |
| [ops.BatchRun](tables/ops.BatchRun.csv) | 3 | `de16afd4983bf9b53d0f57be302288d8da9040aab52213a81f930db12776f7ea` |

## Business date 2026-11-04

Card check: settlement 1493.79, KeyStone 0.00, difference 1493.79.

