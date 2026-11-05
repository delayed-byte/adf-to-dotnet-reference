# Card processor settlement file format (synthetic)

The card processor drops one or more files per business date into blob storage at
`card-landing/{businessDate:yyyy-MM-dd}/settlement_*.csv`. This format is a placeholder the team
owns until the real processor specification is available; the validation rules live in
`db/reporting/procedures/031_stg.usp_ValidateCardSettlementFile.sql`.

## Encoding

- UTF-8, comma-delimited, LF line endings, no column header row.
- Fields may be wrapped in double quotes; a double quote inside a quoted field is written as `""`.
- The first field of every record is the record type: `H` (header), `D` (detail) or `T` (trailer).

## Records

| Type | Position | Field | Rules |
| --- | --- | --- | --- |
| `H` | 1 | ProcessorId | Required, non-empty |
| `H` | 2 | SettlementDate | `yyyyMMdd`; must equal the business date |
| `H` | 3 | FileSequence | Informational |
| `D` | 1 | ProcessorRefNbr | Required, at most 24 characters, unique across all files of the date |
| `D` | 2 | TxnDate | `yyyyMMdd` |
| `D` | 3 | CardBin | 6 digits (test BIN only, e.g. `400000`) |
| `D` | 4 | CardLast4 | 4 digits; full card numbers never appear |
| `D` | 5 | Amount | Positive, digits with an optional `.` and 1–2 decimals (e.g. `12.5`, `12.50`) |
| `D` | 6 | TxnType | `SALE` or `RFND` |
| `D` | 7 | MerchantName | Optional, at most 60 characters |
| `D` | 8 | Mcc | Optional; 4 digits when present |
| `D` | 9 | CurrencyCode | `USD` |
| `T` | 1 | DetailCount | Whole number; must equal the number of `D` records |
| `T` | 2 | DetailTotalAmount | Sum of `Amount` over all `D` records, regardless of `TxnType` |

A valid file has exactly one `H` record, exactly one `T` record, and only `H`, `D` and `T` records.

## Example

```text
H,TESTPROC,20261104,0001
D,TP20261104000000000001,20261103,400000,1234,25.00,SALE,SYNTHETIC GROCER,5411,USD
D,TP20261104000000000002,20261104,400000,9876,10.50,RFND,"SYNTHETIC CAFE, INC",5814,USD
T,2,35.50
```

## Outcome

- **Valid** files are promoted to `stg.CardSettlement`, copied to `card-archive/{businessDate}/` and
  removed from the landing folder.
- **Invalid** files are copied to `card-quarantine/{businessDate}/` with an error manifest
  `{fileName}.errors.json` (array of `{businessDate, sourceFile, batchRunId, recordType,
  processorRefNbr, errorCode, errorMessage}`) and removed from the landing folder. Other files of
  the same date still load.
- Card reconciliation signs amounts with `ops.ReconSignRule`: `SALE` adds, `RFND` subtracts.
