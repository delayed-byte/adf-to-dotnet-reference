# KeyBridge placeholder contract

KeyBridge's real request and response formats are proprietary and not yet available. This is the
placeholder contract the team owns. All calls go through `IKeyBridgeClient`, so replacing it with
the real specification is a contained change. WireMock mappings that implement it are added in
`mappings/` (Phase 2).

## GET `/v1/reference/transaction-codes/gl-mappings`

Returns the complete transaction-code-to-GL-account mapping. Used by `PL_MapAndAggregate` and
cached in `ref.TxnCodeGlMap` by `ref.usp_UpsertTxnCodeGlMap`.

Request headers:

| Header | Value |
| --- | --- |
| `Accept` | `application/json` |
| `X-Correlation-Id` | The batch run ID |
| `Authorization` | Basic credentials (secret `keybridge-api-password`) |

Response `200 application/json`:

```json
{
  "version": "2026-11-01",
  "mappings": [
    { "transactionCode": "CPOS", "glAccountNumber": "2100", "side": "DEBIT",  "allocationRatio": 1.0, "description": "Member shares" },
    { "transactionCode": "CPOS", "glAccountNumber": "2400", "side": "CREDIT", "allocationRatio": 1.0, "description": "Card settlement payable" }
  ]
}
```

| Field | Rules |
| --- | --- |
| `transactionCode` | 1–4 characters |
| `glAccountNumber` | 1–10 characters |
| `side` | `DEBIT` or `CREDIT` |
| `allocationRatio` | 0 < ratio ≤ 1, at most 6 decimals; ratios for a code and side sum to exactly 1 |
| `description` | Optional, at most 100 characters |

Every transaction code must have both a debit side and a credit side. A response that breaks any
rule is rejected with `KEYBRIDGE_MAPPING_INVALID` and the cached mapping is left unchanged.

Transient failures (HTTP 408, 429 and 5xx, timeouts) are retried according to the Web activity
policy in `adf/pipeline/PL_MapAndAggregate.json` (3 retries, 30 seconds apart).
