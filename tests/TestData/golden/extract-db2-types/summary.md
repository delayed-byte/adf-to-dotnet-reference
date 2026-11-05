# extract-db2-types

Hand-written Db2 rows covering the type rules of ADR 0005: CHAR padding (trailing and leading), VARCHAR trailing spaces, empty string vs NULL, NULL in every nullable column, DECIMAL(31,8) at full precision, DECIMAL(15,2) limits, TIMESTAMP(6) microseconds, DST-ambiguous and non-existent local times, DATE limits, BIGINT limits, quotes, pipes, backslashes, line breaks and text outside Windows-1252.

- Acceptance tests: AT-03
- Seed: 20261105; generator version 1
- KeyBridge mapping: contract default (contracts/keybridge/__files/gl-mappings.json), version 2026-11-01

## Runs and expected outcomes

| Run | Entry point | Business date | Trigger (UTC) | Status | Error code | Failed step | Not run | KeyBridge calls | Alerts |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 1 | PL_ExtractKeyStone | 2026-11-04 | manual | Succeeded | - | - | - | 0 | 0 |

## Inputs

| Db2 table | Rows | Rows per date (date column) |
| --- | ---: | --- |
| KS.TXN_CODE (added) | 0 | - |
| KS.MEMBER | 2 | - |
| KS.ACCOUNT | 7 | 2026-11-03: 1, 2026-11-04: 6 |
| KS.POSTED_TXN | 6 | 2026-11-03: 1, 2026-11-04: 5 |
| KS.GL_ENTRY | 4 | 2026-11-03: 1, 2026-11-04: 3 |

## Golden tables

| Table | Rows | SHA-256 (ADR 0006) |
| --- | ---: | --- |
| [core.PostedTransaction](tables/core.PostedTransaction.csv) | 5 | `b4d018af3a540e2adcd98abb30c4ce65d45fc0a928837060dfed329b51a776f0` |
| [core.Account](tables/core.Account.csv) | 6 | `55747868a48d4c421064d7d5e04a436849e760f5220cbe70458cec47f65722b3` |
| [core.GlEntry](tables/core.GlEntry.csv) | 3 | `a02ed86b24c1dcc437c90be24abf17f3c4ef0eeaa32ba31142c6401943146676` |
| [ops.BatchRun](tables/ops.BatchRun.csv) | 8 | `4705ce7dd6b9179b44d11f3777c2d365a4d7a7d098300a63a7e53e94f1f84889` |

## Business date 2026-11-04

