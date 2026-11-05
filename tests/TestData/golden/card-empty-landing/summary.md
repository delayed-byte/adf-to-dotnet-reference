# card-empty-landing

No settlement files for 2026-11-04 (the folder does not exist); a file for 2026-11-03 must be left alone. Card ingestion succeeds with zero rows and logs that no files were found.

- Acceptance tests: AT-07
- Seed: 20261107; generator version 1
- KeyBridge mapping: contract default (contracts/keybridge/__files/gl-mappings.json), version 2026-11-01

## Runs and expected outcomes

| Run | Entry point | Business date | Trigger (UTC) | Status | Error code | Failed step | Not run | KeyBridge calls | Alerts |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 1 | PL_IngestCardSettlement | 2026-11-04 | manual | Succeeded | - | - | - | 0 | 0 |

## Inputs

| Db2 table | Rows | Rows per date (date column) |
| --- | ---: | --- |
| KS.TXN_CODE (added) | 0 | - |
| KS.MEMBER | 0 | - |
| KS.ACCOUNT | 0 |  |
| KS.POSTED_TXN | 0 |  |
| KS.GL_ENTRY | 0 |  |

| Settlement file | Detail rows | Trailer count | Trailer total | Signed total (SALE - RFND) |
| --- | ---: | ---: | ---: | ---: |
| card-landing/2026-11-03/settlement_001.csv | 8 | 8 | 254.99 | 254.99 |

## Golden tables

| Table | Rows | SHA-256 (ADR 0006) |
| --- | ---: | --- |
| [core.CardSettlement](tables/core.CardSettlement.csv) | 0 | `e43a75d025fd93f2ce9326370dc36917f9c8970b2e3edad1f3cf10f46ec3f76b` |
| [ops.CardFileLog](tables/ops.CardFileLog.csv) | 0 | `c57d6fe7a56519afc8a7780ebaf2f95ce557c57b1e593cb1285f80fe11b896ba` |
| [ops.BatchRun](tables/ops.BatchRun.csv) | 3 | `4b7116e1bacbd16abe92c1e97396d30c206cd1f155581f67e43e405d87130b3e` |

## Business date 2026-11-04

