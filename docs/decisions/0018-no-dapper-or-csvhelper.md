# 0018. Data access without Dapper and CSV parsing without CsvHelper

Status: Proposed (retrospective) · Date: 2026-11-06

## Context

The PRD's technology table names Dapper for Lookup and Stored Procedure activities, and CsvHelper for the CSV Copy. The code uses neither. Phases 3 to 8 built the stores on plain `Microsoft.Data.SqlClient` and the CSV Copy on an own reader, `SettlementCsvReader` (ADR 0010). No ADR recorded that choice or why.

This ADR is written after the fact. **The original reason was not recorded, so this ADR does not claim one.** It records what the code does today and the reasons that hold for the code as it stands. The repository owner should confirm it, or ask for the libraries to be adopted.

## What the code does

| Concern | Today | Where |
| --- | --- | --- |
| Lookup, Stored Procedure, pre-copy deletes | `SqlCommand` with explicitly typed `SqlParameter`s, and `SqlDataReader` mapped by column name into records | Seven classes in `CreditUnion.Batch.KeyStone`: `SqlCardSettlementStore`, `SqlMapAndAggregateStore`, `SqlReconciliationStore`, `SqlExtractReportingStore`, `SqlNightlyBatchStore`, `BatchRunLog`, `Db2StagingCopier` |
| Copy, Db2 source | `DB2DataReader` straight into `SqlBulkCopy` | `Db2StagingCopier` (ADR 0009) |
| Copy, CSV source | `SettlementCsvReader` (about 160 lines, streaming) into `SqlBulkCopy` | ADR 0010; 14 tests in `SettlementCsvReaderTests` |
| Connections | `Connections` sets `ConnectRetryCount = 0` on every SQL connection | ADR 0009 |

No call uses `AddWithValue`. Money parameters are `SqlDbType.Decimal` with `Precision = 19, Scale = 2`, and dates are `SqlDbType.Date` built from `DateOnly`.

## Decision (proposed)

Keep plain `SqlClient` and `SettlementCsvReader`. Do not add Dapper or CsvHelper.

## Reasons that hold for the current code

These are observations about the code, not the reasons the authors had.

**Dapper**

- Dapper would remove the column mapping, but not the parameter declarations: each money and date parameter is declared with an explicit SQL type, scale and precision. Dapper infers parameter types unless each one is declared, so the saving is small.
- Each command maps a handful of columns, and the acceptance tests run them against real SQL Server.
- The bulk paths (`SqlBulkCopy`, `DB2DataReader`) do not use Dapper in any case.
- One fewer package to pin and scan.

**CsvHelper**

- ADR 0010 fixes ten parsing cases to match the ADF dataset. They include: LF is the only row delimiter and a CR is data; text after a closing quote is data; over-long values fail before they are logged; an error message never contains a value. Each case is pinned by a test.
- **Not tested:** whether CsvHelper can be configured to reproduce every one of those cases, and whether its exceptions can carry a field value into a log. Adopting it needs both checked, with the existing `SettlementCsvReaderTests` as the bar.
- The reader yields strings only. Staging columns are `nvarchar`, and SQL does the validation (ADR 0010), so CsvHelper's type conversion would not be used.

## Consequences

- The PRD table and the code differ. The playbook's mapping table points here.
- If the owner prefers the libraries, the change is local: the stores in `CreditUnion.Batch.KeyStone` and `SqlCardSettlementStore.LoadRawAsync`. The acceptance tests and golden outputs must not change.
- Polly (`Microsoft.Extensions.Http.Resilience`) is a separate decision, already recorded: ADR 0008 and ADR 0012.
