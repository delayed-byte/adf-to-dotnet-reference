# 0005. Db2 type handling in the KeyStone extract

Status: Accepted · Date: 2026-11-05

## Context

`PL_ExtractKeyStone` copies Db2 rows into the `stg.Ks*` tables, which mirror the Db2 names and types (ADR 0003). The PRD requires the rules for CHAR padding, DECIMAL precision, DATE and TIMESTAMP to be written down before the extract is coded; AT-03 checks them.

The rules below were checked against the real stack: Db2 12.1 Community, `Net.IBM.Data.Db2` 10.0.0.400, `Microsoft.Data.SqlClient` 7.1.1 and SQL Server 2022 CU14. `TypeHandlingProbeTests` pins each observation, so a driver upgrade that changes one fails the build.

| # | Observed behaviour |
| --- | --- |
| 1 | `DB2DataReader.GetString` returns CHAR(n) space-padded to n (`'BR '`, `'2100      '`). VARCHAR keeps trailing spaces. `''` and NULL are distinct. |
| 2 | **`GetDecimal` and `GetValue` silently round DECIMAL(31,8)** beyond System.Decimal's precision: `99999999999999999999999.99999999` comes back as `100000000000000000000000.00000` with no exception. `GetDB2Decimal` is exact. |
| 3 | A **string** parameter bound to DECIMAL goes through System.Decimal and loses digits (`…123.45678901` is stored as `…123.45678900`). A `DB2Decimal` parameter is exact. |
| 4 | TIMESTAMP(6) is returned as a `DateTime` with `Kind = Unspecified` and all six fraction digits; DATE is returned as a `DateTime` at midnight. Neither carries a time zone. Times in the spring-forward gap (`2026-03-08 02:30`) are stored and returned unchanged. `GetSchemaTable` reports scale 3 for TIMESTAMP(6), so schema metadata must not drive conversion. |
| 5 | A Db2 timestamp at hour 24 (`2026-11-04-24.00.00`) makes `GetDateTime` and `GetValue` throw `InvalidCastException`. |
| 6 | The database uses code set UTF-8 with `string_units = SYSTEM`, so VARCHAR(n) holds n **bytes**. |
| 7 | `SqlBulkCopy` writes `SqlDecimal` into `decimal(31,8)` exactly. `SqlDataReader.GetDecimal` overflows on such values; `GetSqlDecimal` reads them exactly. |
| 8 | Under the reporting database's default collation (`SQL_Latin1_General_CP1_CI_AS`), `varchar` **silently best-fits** characters outside Windows-1252: `Ω` becomes `O`, `✓` becomes `?`, `日本` becomes `??`. A `Latin1_General_100_CI_AS_SC_UTF8` column keeps them. |
| 9 | SQL Server comparisons, and therefore `EXCEPT` in the merge procedures, ignore trailing spaces and, under a case-insensitive collation, letter case. |

## Decision

Copy every value verbatim: no trimming, re-padding, rounding or time-zone conversion. A value that cannot be represented exactly fails the copy instead of being changed.

| Db2 type | Read with | Write to SQL Server as | Rule |
| --- | --- | --- | --- |
| CHAR(n) | `GetString` | `char(n)` | Keep the padding; never `Trim`, `TrimStart` or `TrimEnd`. CHAR columns hold ASCII codes and identifiers; a non-ASCII CHAR value fails the copy. |
| VARCHAR(n) | `GetString` | `varchar(n)` | Keep trailing and leading spaces; `''` stays `''`. Free-text columns (DESCRIPTION, MEMO) use the UTF-8 collation (migration 0007), so every character and Db2's byte length both fit. Identifier columns (CARD_PROCESSOR_REF, BATCH_REF) are ASCII. |
| DECIMAL(p,s), any p | `GetDB2Decimal`, then `SqlDecimal.Parse(value.ToString())` | `SqlDecimal` into `decimal(p,s)` | Never `GetDecimal` or `GetValue`, never `double`, never a string parameter. Source and target scales must match; a larger source scale is a configuration error. Business logic may convert to `System.Decimal` only for columns of precision 28 or less. |
| DATE | `GetDateTime`, then `DateOnly` | `date` | No time zone. `0001-01-01` and `9999-12-31` are values, not nulls. |
| TIMESTAMP(6) | `GetDateTime` (`Kind = Unspecified`) | `datetime2(6)` | KeyStone local wall-clock time, copied unchanged (not converted to UTC). Hour 24 fails the copy. |
| BIGINT, INTEGER | `GetInt64`, `GetInt32` | `bigint`, `int` | |
| NULL (any type) | `IsDBNull` | NULL | Never replaced by a default value. |

**Time zone of KeyStone timestamps.** The TIMESTAMP columns carry no zone; we assume they are KeyStone's local time (America/Los_Angeles), which must be confirmed with the credit union. They are stored as-is for three reasons:

- ADF's Db2 copy does the same, so verbatim storage is the equivalent behaviour.
- Converting a zone-less local time is ambiguous in the repeated 01:00 hour and impossible in the skipped 02:00 hour.
- Business-date selection uses DATE columns that KeyStone assigns (`POSTING_DATE`, `LAST_ACTIVITY_DATE`) and never derives a date from a timestamp. A transaction posted at 23:45 can belong to the next business date (scenario `dst-transitions`).

The PRD rule "timestamps stored in UTC" applies to the timestamps the batch creates (the `*Utc` columns). Source timestamps keep their `*Ts` names.

**Errors.** Hour-24 timestamps, non-ASCII CHAR values and scale mismatches are data errors. They are not retried (PRD: validation errors are not retried) and they name the table, key and column, never the value. Account numbers are masked per CLAUDE.md.

## Consequences

- Migration 0007 moves six free-text columns (`stg.Ks*` and `core`) to `Latin1_General_100_CI_AS_SC_UTF8`. Codes stay on the default collation, so joins with `ops` and `ref` need no `COLLATE` clauses.
- `ops.usp_GetRunEvidence` now states an explicit collation on its checksum lines, because `STRING_AGG` cannot resolve mixed collations. The Phase 2 cross-check also found that `ISNULL(Mcc, N'\N')` hashed a NULL `char(4)` as `'\N  '`; it now converts to `nvarchar` first, as the procedure's own comment specifies.
- AT-03 (scenario `extract-db2-types`) covers every rule. It includes the DECIMAL(31,8) extremes, values with 31 significant digits, padding, empty versus NULL, DST-gap and repeated-hour timestamps, line breaks and text outside Windows-1252.
- **Known limitation:** because of observation 9, a rerun does not propagate a source correction that changes only trailing spaces or letter case; first loads are exact. This is accepted for v1. If KeyStone corrects data that way, switch the merges' `EXCEPT` to binary comparisons.
- **ADF divergence to confirm:** ADF's interim type for Db2 DECIMAL is .NET `decimal`, so production ADF may already round 31-digit values. The .NET conversion is deliberately lossless. Compare ACCRUED_INTEREST against captured ADF output during the real migration.
- **Remapping to real KeyStone:** confirm the timestamp zone, check that every CHAR column is ASCII, and check that TIMESTAMP precision is 7 or less (`DateTime` resolution; otherwise read `DB2TimeStamp`).
