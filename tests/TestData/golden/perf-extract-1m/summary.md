# perf-extract-1m

Performance (Category=Performance, excluded from the default run): 1,000,000 transactions on 50,000 accounts for one business date. Only row counts and checksums are committed; inputs are regenerated with ./build.ps1 golden -Scenario perf-extract-1m.

- Acceptance tests: NFR-Performance
- Seed: 20261199; generator version 1
- KeyBridge mapping: contract default (contracts/keybridge/__files/gl-mappings.json), version 2026-11-01

## Runs and expected outcomes

| Run | Entry point | Business date | Trigger (UTC) | Status | Error code | Failed step | Not run | KeyBridge calls | Alerts |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 1 | PL_ExtractKeyStone | 2026-11-04 | manual | Succeeded | - | - | - | 0 | 0 |

## Inputs

| Db2 table | Rows | Rows per date (date column) |
| --- | ---: | --- |
| KS.TXN_CODE (added) | 0 | - |
| KS.MEMBER | 27820 | - |
| KS.ACCOUNT | 55500 | 2026-11-04: 50000; 5500 dormant (older dates) |
| KS.POSTED_TXN | 1000000 | 2026-11-04: 1000000 |
| KS.GL_ENTRY | 2000 | 2026-11-04: 2000 |

## Golden tables

| Table | Rows | SHA-256 (ADR 0006) |
| --- | ---: | --- |
| core.PostedTransaction | 1000000 | `88e9203fa8a9f2d23f969b9484cab1b81360d11f2c7a2931d7950117833e3a07` |
| core.Account | 50000 | `247ac70f29a370705f2cd800da41a7efd4fb30f8056ef67d54b33d0b4ef16d16` |
| core.GlEntry | 2000 | `fdbe41f9911348be521637cec3194596b16473c2665d9932066e82aa87271d5e` |
| ops.BatchRun | 8 | `cc17aee8d30d7115cd5f178ce877af048087e0c9c0bcf72b859ba5590d0ec754` |

## Business date 2026-11-04

