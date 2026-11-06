# 0011. DF_MapAndAggregateGl: a streaming C# aggregator, not a stored procedure

Status: Accepted · Date: 2026-11-06

## Context

`PL_MapAndAggregate` ends with the Mapping Data Flow `DF_MapAndAggregateGl`. It joins the business date's `core.PostedTransaction` rows to `ref.TxnCodeGlMap`, derives `round(Amount * AllocationRatio, 2, 7)` (rounding option 7 is ROUND_HALF_EVEN), splits the result into debit or credit by `DrCr`, and aggregates per GL account into `core.DailyGlTotals`. A pre-SQL delete runs first, so a rerun replaces the date's totals.

The PRD allows set-based SQL in a stored procedure, or a C# service for small volumes. ADR 0004 left the rounding to this ADR. ADR 0006 (semantics 1 and 2) fixed what the golden outputs encode: banker's rounding on the exact decimal product, once per allocation, before summing.

## Options

| | A. T-SQL procedure (`core.usp_AggregateDailyGlTotals`) | B. C# aggregator in Core, thin SQL store (chosen) |
| --- | --- | --- |
| Banker's rounding | T-SQL `ROUND` rounds half away from zero. Half to even needs a hand-written `CASE` on `ROUND(x, 2, 1)` and the parity of the cent, which is easy to get subtly wrong. | `decimal.Round(amount * ratio, 2, MidpointRounding.ToEven)`. The mode is stated explicitly, as CLAUDE.md requires. |
| Exactness | `decimal(15,2) * decimal(9,6)` is `decimal(25,8)`: exact | At most 24 significant digits, so `decimal` is exact |
| Tests | Only against a SQL Server container | Pure unit tests: hand-checked cases, property tests against an integer reference, every golden date. No database needed. |
| Independent check | `GoldenConsistencyTests` already restates the rule in T-SQL. A procedure would most likely repeat the same `CASE`, so the cross-check would stop being independent. | The C# rule and the harness's T-SQL rule are written separately and in different languages. Both must equal the golden tables. |
| Data movement | None: the work stays in the server | The date's rows are streamed over TDS (TxnId, TxnCode, PostingDate, Amount). Memory grows with the number of GL accounts, not with the number of transactions. |
| Deployment | A new procedure under `db/reporting/procedures`, redeployed on every seed | None. The store uses plain parameterized SQL. |

**Decision: B.** The rule that matters (half to even, per allocation, exact) is stated once, in `DailyGlTotalsAggregator` (`CreditUnion.Batch.Core.Map`), and tested without a database. `SqlMapAndAggregateStore` (`CreditUnion.Batch.KeyStone.Map`) only reads the two sources and writes the sink.

**Volume.** A credit union posts thousands to low millions of transactions a day. Streaming them through the aggregator is linear, and the result is one row per GL account. This was **not measured** against the database: `perf-extract-1m` covers Extract only.

Revisit this decision if:
- a day's run approaches the activity's 2-hour timeout; or
- volumes grow by orders of magnitude.

In that case, the join and the allocation can move into SQL, with the C# aggregator kept as the reference implementation in tests.

## Rules, from the data flow script and the golden tables

`MapPolicyTests` pins each script line listed here, so if the data flow changes, a test fails.

| Data flow | .NET | Decision |
| --- | --- | --- |
| `Transactions` query: `WHERE PostingDate = $businessDate` | `ReadPostedAmountsAsync`, with a parameter instead of string concatenation | **Business date.** The filter is `PostingDate`, the KeyStone posting date copied verbatim (ADR 0005). It is not `EffectiveDate` or `PostedTs`, and it is not converted between time zones. `businessDate` is the Pacific `DateOnly` from the input. A late posting whose effective date is earlier belongs to its posting date. |
| `GlMap` source, `format: 'table'` | `ReadGlMapAsync`: every row | The mapping is read in full, as the data flow does. |
| `join(… == …, joinType:'inner', matchType:'exact', ignoreSpaces: false)` | Ordinal dictionary lookup on the padded `char(4)` values | **Unmatched rows (difference from ADF).** ADF's inner join drops a transaction that has no mapping row, silently. The aggregator throws `GlAggregationException`, which names the codes, writes nothing and is not retried. The unmapped-code check runs before this step, so such a row can only appear in two cases. First, the mapping changes between the check and the data flow. Second, a code differs from its mapping only in case: `ref.usp_GetUnmappedTxnCodes` compares under the database's case-insensitive collation, while the join is case-sensitive. Losing money silently is worse than failing. |
| `derive(AllocatedAmount = toDecimal(round(Amount * AllocationRatio, 2, 7), 15, 2))` | `DailyGlTotalsAggregator.Allocate` | **Rounding.** Half to even, on the exact decimal product, once per allocation (ADR 0006 semantics 1 and 2). Rounding is symmetric in sign (-1.175 gives -1.18). See "No floating point" below. |
| `iif(DrCr == 'D', …)`, `iif(DrCr == 'C', …)` | The debit or credit side, by `DrCr` | Only `D` and `C` exist (`CK_TxnCodeGlMap_DrCr`); any other value is refused. |
| `aggregate(groupBy(GlAccountNbr), sum, sum, countDistinct(TxnId))` | Per-account sums. `TxnCount` adds 1 for each distinct GL account a transaction touches. | **TxnCount.** It counts distinct `TxnId`, not joined rows: a code that debits and credits the same account counts once. Counting per transaction equals `countDistinct` because `TxnId` is the key of `core.PostedTransaction`. |
| `NetAmount = DebitTotal - CreditTotal` | As stated | — |
| Sink `preSQLs: DELETE … WHERE BusinessDate = …`, then insert | `ReplaceDailyGlTotalsAsync`: delete and insert in **one transaction** | **Difference from ADF.** ADF runs the pre-SQL and the write as separate steps, so a failed write can leave the date empty. Here a failed attempt leaves the previous totals in place, and readers never see a half-written date. |

**Other cases, decided from the data flow and the golden tables:**
- **Reversals.** The data flow has no reversal rule. A reversal is its own transaction, with its own code (`CRFD` reverses `CPOS`) and amount, and it is aggregated like any other row. Negative amounts keep their sign.
- **Zero values.** A zero amount, or an allocation that rounds to 0.00 (e.g. 0.01 × 0.4), still counts in `TxnCount`, and its GL account still gets a row, which may be all zeros. A zero allocation ratio cannot occur (`CK_TxnCodeGlMap_Ratio`).
- **A day with no transactions** writes no rows, but the delete still runs, so a rerun clears the totals of a date that no longer has postings. ADF is assumed to run `preSQLs` even when the source is empty; this is **not confirmed**.
- **Precision.** Allocations are `decimal(15,2)` and totals `decimal(19,2)`. The store refuses a value with more than two decimals, instead of letting `SqlParameter` round it, and the aggregator cannot produce one.
- **The write** is a parameterized multi-row `INSERT` of 300 rows per statement, under SQL Server's 2,100-parameter limit. `SqlBulkCopy` with its default options skips constraints and triggers, which requires `ALTER` on `core`; `cu_batch_writer` does not have it, and should not.

**No floating point.** ADF's `round()` returns a double, and the script converts it straight back to decimal. The .NET code does not copy that: every amount, ratio and total is `decimal`, and the rounding mode is stated (`MidpointRounding.ToEven`). The golden value for 4.70 × 0.25 = 1.175 is 1.18, as the PRD requires. 1.175 has no exact binary representation, so whether ADF itself gives 1.17 or 1.18 depends on how its runtime rounds a double. The scenario summary and ADR 0006 assume 1.17. This is **unconfirmed**; confirm it against a production ADF run (ADR 0007).

## Retries, connections and idempotency

- **Retries.** "Aggregate Daily GL Totals" is one activity, `Map_AggregateDailyGlTotals`: retry 1 at 60 s (two attempts), 2-hour timeout per attempt (`AttemptTimeout`). `GlAggregationException` is not retried.
- **Drivers.** SqlClient `ConnectRetryCount` is 0 through `Connections.CreateReportingConnection`. This pipeline reads no Db2 and no blob, so `Db2DriverConfiguration` and the Storage SDK retry settings are not needed.
- **Idempotency.** A retry or a rerun for the same `businessDate` replaces the date's rows with the same values. `core.DailyGlTotals` has no audit columns, so a rerun on unchanged inputs leaves it byte-identical. AT-08 to AT-11 assert this through their rerun.

## Consequences

- **Where the rule lives.** It is stated once in `src` (`DailyGlTotalsAggregator`). The oracle (`GlAllocation`) and `ProcedureReplay`'s T-SQL are test-side restatements, and all three must agree with the golden tables. `DailyGlTotalsAggregatorTests` also reproduces every golden date from the golden transactions and mapping.
- **If the data flow changes** (new columns, a filter, a different rounding option), `MapPolicyTests` fails until this ADR and the aggregator follow.
- **Phase 7.** Reconciliation reads `core.DailyGlTotals` by `BusinessDate`, per GL account and side (`ops.fn_ReconDetail`). The table is complete for a date or unchanged, never partial.
