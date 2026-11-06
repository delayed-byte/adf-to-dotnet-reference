# 0006. Test harness, scenario data and golden outputs

Status: Accepted · Date: 2026-11-05

## Context

ADF cannot run without an Azure subscription, so golden outputs cannot be captured from real runs. The PRD says to derive them from the pipeline specification and have a person review them. It also says comparisons cover row counts, an order-independent SHA-256 per table and column-level diffs that show the first 20 mismatches. The Db2 container starts once per test run and is reset between scenarios, every test is checked for idempotency, and all data is synthetic and seeded.

## Decision

**Layout.**

| Path | Contents |
| --- | --- |
| `tests/TestData/generator` | `CreditUnion.Batch.TestData`: the seeded generator and the oracle. No database, Azure or test-framework dependency. `./build.ps1 golden` regenerates the committed files. |
| `tests/TestData/inputs/{scenario}` | `scenario.json` (runs), `db2/KS.*.csv`, `blobs/{container}/…` and, only when overriding the contract, `keybridge/gl-mappings.json` |
| `tests/TestData/golden/{scenario}` | `manifest.json` (tables, run outcomes, blobs, schedule), `tables/*.csv`, `blobs/` (error manifests), `evidence/run-N.json`, `alerts/run-N-alert-M.json`, `summary.md` |
| `tests/CreditUnion.Batch.TestHarness` | Testcontainers environment, loaders, snapshots, `GoldenAssert`, WireMock.Net stubs and the TCP fault proxy |

The scenario catalogue and its mapping to AT-01–AT-19 is in `tests/TestData/README.md`.

**Determinism.**
- Data comes from SplitMix64 (implemented in the repository, with published test vectors). Each part of a scenario draws from its own forked stream.
- The generator uses the invariant culture and never reads the clock.
- A unit test regenerates every scenario and requires the committed files to match **byte for byte**, so golden data changes only through the generator, and the generator change is reviewed along with the diff.

**How golden outputs are derived.**
- `BatchOracle` is an executable, in-memory statement of the five pipelines. It follows the ADF activities and the Phase 1 procedures step by step, including merge rules, validation, file moves, `ops.BatchRun` logging, recon and evidence.
- The world builder makes inputs consistent: KeyStone's GL entries equal the mapping-derived totals, and the settlement files contain exactly the day's card transactions. Each scenario then introduces the one inconsistency it is about.
- Because the oracle and the later implementation share an author, the golden outputs are checked independently:
  - `GoldenConsistencyTests` replays every scenario through the real Phase 1 procedures: merges, settlement validation, mapping cache, unmapped-code check, recon and evidence.
  - The same tests recompute `core.DailyGlTotals` with a separate T-SQL statement of the data-flow rule.
  - Hand-checked unit cases cover rounding, settlement validation and the DST schedule.
  - The extract tables are compared with the rows read back from Db2 by the ADR 0005 rules.

**Canonical values.** Every value has one text form, used for golden files, inputs, snapshots and checksums:

| Type | Form |
| --- | --- |
| CHAR(n) | Padded to n |
| VARCHAR, NVARCHAR | Exact |
| DECIMAL(p,s) | Exactly s fraction digits, no sign on zero |
| DATE | `yyyy-MM-dd` |
| DATETIME2(p), TIMESTAMP(p) | `yyyy-MM-ddTHH:mm:ss` plus exactly p fraction digits |
| Integers | Invariant digits |
| BIT | `true` or `false` |

Formatting never rounds: a value that does not fit its type throws.

**CSV format.**
- UTF-8 without BOM, LF line endings, a header row, RFC 4180 quoting.
- NULL is the unquoted token `\N`; the empty string is `""`.
- A field is quoted when it is empty, equals `\N`, contains a comma, quote or line break, or starts or ends with whitespace, so padding stays visible.
- A CR outside quotes is rejected, which catches git line-ending conversion. `.gitattributes` keeps these files LF.

**Checksum.**
- Each row becomes one line: values joined by `|`, NULL as `\N`, and `\`, `|`, LF and CR escaped.
- The lines are sorted ordinally, which makes the checksum independent of row order.
- The digest input is the header line followed by the sorted lines, each LF-terminated, encoded as UTF-8; the result is SHA-256 as lowercase hex.
- A known-answer test pins the algorithm.
- This is not the evidence checksum of `ops.usp_GetRunEvidence`, which is PK-ordered and UTF-16LE. The oracle ports that one too (`EvidenceChecksum`), and the replay proves both agree.

**Comparison.**
- Tables are matched on their key, or compared as a multiset when they have none (`ops.BatchRun`).
- The report gives row counts, both checksums and the first 20 mismatches out of the total.
- Snapshots select business columns only; load-audit columns are excluded (ADR 0003). Batch run IDs become run ordinals: the `Run`, `DetectedRun` and `AlertRun` columns, `{batchRunId}` in `ops.BatchRun.ObjectName`, and `{run:N}` in blob names, metadata and JSON.
- The master's Started note (trigger time) is not compared. `ErrorMessage` is checked through each run's `messages` (contains), because ADF wraps child errors in its own wording.
- JSON is compared semantically: numbers as decimals, and a missing property equals null. Error-manifest arrays are multisets, because the procedure's `ORDER BY` ties. Evidence compares only the deterministic sections (Files, RowCounts, Checksums, Reconciliation, Exceptions), as multisets, with timestamps normalised.

**Pipelines, reruns and idempotency.**
- The golden output is the state after the scenario's runs.
- A test of one pipeline compares the tables that pipeline owns (`GoldenTables.OwnedBy`) and its own `ops.BatchRun` rows. It may set up preconditions by running the upstream pipelines; the phases are ordered so that those already exist.
- The idempotency rerun compares every table except the run-scoped audit tables, which grow by design.
- Faults (AT-04 connection refused, AT-11 KeyBridge 503) must leave the outputs identical to the scenario's golden output.

**Environment.**
- One `BatchEnvironment` per test assembly, started as an xUnit assembly fixture, using the same image tags as `docker-compose.yml` and passwords generated per run.
- Both schemas are deployed by the `./build.ps1 seed` deployers (ADR 0002).
- Reset between scenarios: the reporting store's run tables are cleared with `TRUNCATE` as sa, which bypasses the append-only triggers that the batch logins cannot get past. The Db2 scenario rows are deleted; the migration's reference data stays.
- `CU_TESTS_USE_COMPOSE=1` reuses the `./build.ps1 up` stack for fast local loops.

**Stubs and faults.**
- The KeyBridge and alert-webhook stubs run in-process on WireMock.Net. KeyBridge requires Basic credentials and returns 401 otherwise. The webhook URL embeds a secret token, so AT-17 can prove it is never logged.
- `FailNextRequests(n, status)` drives the retry tests.
- The compose stack's WireMock serves the same contract body through `contracts/keybridge/mappings`.
- `FaultInjectingTcpProxy` sits in front of Db2 and resets the next N connections.

**ADF semantics the golden output encodes.** ADF could not be observed, so these choices need sign-off (`tests/TestData/REVIEW.md`):

1. Banker's rounding (`MidpointRounding.ToEven`) is applied to the **exact decimal** product, as the PRD says. ADF's `round()` returns a double: 4.70 × 0.25 = 1.175 gives 1.18 here, but would give 1.17 on a double (scenario `map-midpoint-rounding`).
2. Rounding happens per allocation, before summing.
3. A child pipeline's error code is propagated to the master's Failed event (`TXN_CODE_UNMAPPED`), and the master fails with `BATCH_STEP_FAILED`.
4. Decimals in `ops.BatchRun` notes keep their SQL scale (`card difference=0.00`).
5. Get Metadata is non-recursive; folders and non-matching names are skipped, and a missing folder behaves like an empty one. File names match `settlement_*.csv` case-insensitively, as ADF's `startsWith` and `endsWith` do (decided in ADR 0007).
6. Empty settlement fields are NULL (the dataset's `nullValue` is `""`).
7. Db2 values are copied verbatim (ADR 0005).

## Consequences

- Editing a golden file by hand fails the drift test. To change a golden output, change the generator, regenerate and review the diff.
- During the production migration, ADF outputs captured for the same inputs are compared with these golden files by the same `TableComparison`. Any difference is either a misreading recorded here or an ADF defect.
- The cross-check found two Phase 1 defects, both fixed (see ADR 0005): a collation conflict in `ops.usp_GetRunEvidence` and the padded NULL in its card checksum.
- `perf-extract-1m` commits only its manifest (counts and checksums). Its inputs, 135 MB, are regenerated into the git-ignored `tests/TestData/generated`. Loading a million rows quickly into Db2 is Phase 4 work; today's loader is row by row.
