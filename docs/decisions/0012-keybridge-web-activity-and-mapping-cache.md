# 0012. PL_MapAndAggregate: activity granularity, the KeyBridge call, its error classification and the mapping cache

Status: Accepted · Date: 2026-11-06

## Context

Phase 6 converts `PL_MapAndAggregate` on the pattern of ADR 0009 (Extract) and ADR 0010 (Card). ADR 0011 covers the data flow. This ADR covers everything before it:
- how the activities, the If Condition and the Fail activity map to Durable;
- how the Web activity "Get GL Mapping From KeyBridge" becomes an HTTP call: timeouts, authentication, which failures are retried, and what may be logged;
- how the mapping travels from the Web activity to `ref.TxnCodeGlMap`.

## Decision

### Orchestration and activity granularity

The body of `MapAndAggregateOrchestrator` replaces the stub. The name, the input (`PipelineRunInput`) and the output (none) are unchanged (ADR 0008).

The orchestrator calls one activity per ADF activity that does I/O, in the ADF order and with the ADF dependencies, which `MapPolicyTests` reads from the JSON. It does no I/O and reads the clock only through `context.CurrentUtcDateTime`.

| ADF activity | .NET | Retry / timeout (ADF JSON) |
| --- | --- | --- |
| Log Map Started / Log Map Succeeded | `Map_LogBatchRunEvent` | 3 attempts, 30 s / 5 min |
| Get GL Mapping From KeyBridge (Web) | `Map_GetGlMappingFromKeyBridge` | 4 attempts, 30 s / 5 min; HTTP request timeout 1 min |
| Cache GL Mapping | `Map_CacheGlMapping` | 3, 30 s / 5 min |
| Lookup Unmapped Txn Codes | `Map_LookupUnmappedTxnCodes` | 3, 30 s / 5 min (`queryTimeout` is also 5 min) |
| If Any Txn Code Unmapped | `if (UnmappedCount > 0)` in the orchestrator | no policy in ADF |
| Log Unmapped Txn Codes | `Map_LogUnmappedTxnCodes` | 3, 30 s / 5 min |
| Fail Txn Code Unmapped | `throw new PipelineFailedException("TXN_CODE_UNMAPPED", "Unmapped transaction codes: …")` | no policy in ADF |
| Aggregate Daily GL Totals (data flow) | `Map_AggregateDailyGlTotals` (ADR 0011) | 2, 60 s / 2 h |

**Policies.** `MapActivityPolicies.Adf` holds the values. `AdfRetry` turns them into a `RetryPolicy` with `retry + 1` attempts at a fixed interval. `MapPolicyTests` reads every value from the JSON, together with the dependency chain, the Fail expression, the Web request and the data flow script lines. Its completeness test fails if an activity with a policy is added or renamed.

**The unmapped branch:**
- It logs the `Failed` event first (`StepName` CheckTxnCodeMapping, `RecordCount` = UnmappedCount, `ErrorCode` TXN_CODE_UNMAPPED, `ErrorMessage` from the same `concat` as the Fail message), then throws.
- The Fail activity depends on the log with condition **`Completed`**, so the code catches a failed log (after its own retries), writes a replay-safe warning, and still fails with TXN_CODE_UNMAPPED.
- The throw happens in the orchestrator, not in an activity, so it is never retried and nothing is aggregated (PRD: validation errors are not retried). AT-09 asserts that nothing was retried.
- The detail names transaction codes only, which are not sensitive.

**Other failures** (KeyBridge, cache, lookup, data flow) throw the activity's `TaskFailedException` and log no `Failed` event, because ADF has no failure branch for them. The master (Phase 8) records them, as in Phases 4 and 5.

### The KeyBridge call (`HttpKeyBridgeClient`, typed `HttpClient`)

**Endpoint and credentials.**
- `keyBridgeBaseUrl` and the Key Vault secret `keybridge-api-password` come from `KeyBridgeOptions` (configuration, ADR 0008), not from the input. The username defaults to the ADF JSON's `cu-batch`.
- The URL is the ADF `concat`: base URL + `/v1/reference/transaction-codes/gl-mappings`.
- A base URL that is missing, relative, not http(s), or carries **user info, a query or a fragment** is refused with `KeyBridgeConfigurationException`, before any request and without echoing the URL. Credentials in the URL would otherwise end up in spans (`url.full`).

**The request.** `GET`, with `Accept: application/json`, `X-Correlation-Id: {batchRunId}` and `Authorization: Basic …`, built for each request from the options.

**Timeouts.** The ADF JSON has two, and both apply:
- `HttpClient.Timeout` = `httpRequestTimeout` (**1 min**): how long ADF waits for a response.
- `AttemptTimeout` = the activity `timeout` (**5 min**): it bounds the whole attempt, including reading the body.

The phase brief mentioned only the 5 min; the JSON is the source of truth.

**Handler pipeline** (`BatchServiceCollectionExtensions.AddKeyBridgeClient`):
- `SocketsHttpHandler` with `AllowAutoRedirect = false`, so the credential is sent only to the configured host. ADF's redirect behaviour is unconfirmed.
- `UseCookies = false`.
- No retry or resilience handler. The Durable `RetryPolicy` is the only retry layer (ADR 0008): 4 attempts give 4 calls, never 16.
- `RemoveAllLoggers()`, so the factory never logs request URLs or headers.

`KeyBridgeClientTests` walks the handler chain built by the production registration and fails if a logging, retry or resilience handler appears.

**Error classification.** No message contains the URL, a header, the credential or the body, and the original driver exception is not attached.

| Outcome | Exception | Retried? |
| --- | --- | --- |
| No connection, or the body breaks off | `KeyBridgeUnavailableException` | yes |
| No response within `httpRequestTimeout`, or the attempt exceeds 5 min | `TimeoutException` | yes |
| 408, 429, 5xx (AT-11: 503) | `KeyBridgeUnavailableException` | yes |
| Any other non-2xx: 401, 403, 404, other 4xx, 3xx | `KeyBridgeRejectedException` | **no** |
| 2xx, but the body is not JSON, has no `mappings`, or is over 4 MB (ADF's Web activity output limit) | `KeyBridgeResponseException` | **no** |
| Missing or unsafe settings | `KeyBridgeConfigurationException` | **no** |

**Difference from ADF.** ADF retries any failed attempt, including a 401. Here only transient failures are retried, as `contracts/keybridge/README.md` states (408, 429, 5xx, timeouts) and as the PRD requires (validation errors are not retried). Retrying a 401 three times, 30 s apart, would fail the same way 90 s later and send three more failed logins to KeyBridge, which could lock the service account. A rejected request fails the pipeline at once, with the status code in the message.

### Passing and caching the mapping

**Output of the Web activity.** It returns `KeyBridgeGlMappings(Version, MappingsJson)`:
- `MappingsJson` is the raw JSON text of the `mappings` property. ADF passes `@string(output.mappings)`, which re-serializes it, so whitespace can differ; the procedure parses it with `OPENJSON`, so the result is the same.
- `version` is the string, or null when it is null or missing. **Difference from ADF:** ADF fails the expression `output.version` when the property is missing; the procedure accepts NULL, so the run continues. Unconfirmed against ADF.

**Size.** The mapping travels through the Durable history (activity output, then activity input). It is reference data (transaction codes, GL accounts, ratios), not member data, and the 4 MB limit bounds it. On the Azure Storage backend, Durable moves large payloads to blobs by itself.

**Cache GL Mapping** calls the Phase 1 procedure `ref.usp_UpsertTxnCodeGlMap` unchanged. It validates the whole set before touching the cache: JSON, non-empty, field ranges, no duplicates, and ratios summing to 1 per code and side. It then MERGEs, updating a row only when a value differs and deleting rows KeyBridge no longer returns, so an identical rerun leaves every row unchanged, including `RefreshedBatchRunId`.

Its `KEYBRIDGE_MAPPING_INVALID` errors (53000 to 53004) become `GlMappingInvalidException`, which is **not retried** (**difference from ADF**, which would retry twice). The cache stays unchanged, which an acceptance test checks against SQL Server.

**Lookup Unmapped Txn Codes** returns the procedure's first row (`UnmappedCount`, `UnmappedCodes`), which the orchestrator branches on.

### Data protection

**What is logged.** Logs carry status codes, byte counts, the mapping version, transaction codes, GL account numbers and the date.

**What can never reach a log line, span or exception message:**
- the password and the Basic credential;
- connection strings;
- member, account or card numbers. Map does not read them: the transaction source selects TxnId, TxnCode, PostingDate and Amount only.

**Spans.** `System.Net.Http`'s spans carry the method, the status and `url.full`. That URL is the base URL plus the fixed path, with no credentials and no query, because both are refused at configuration.

**Tests:**
- `KeyBridgeClientTests` checks every exception message and runs the production registration at Trace level.
- `MapAndAggregateConversionTests` runs Map on three scenarios, including a retried 503 and a 401, and applies the AT-17 rules to every log line and span. The AT-17 cases themselves need the Phase 8 master.

### Dependencies

- **Microsoft.Extensions.Http** is now pinned at **10.0.9** and referenced by the Functions project.
- It was already in the graph at 6.0.0, through `Grpc.Net.ClientFactory` from the Durable worker. Version 6.0 has no `RemoveAllLoggers` and logs request headers at Trace without redaction. Central transitive pinning moves every project to 10.0.9, the line of the other `Microsoft.Extensions` packages.
- **Microsoft.Extensions.Http.Resilience** (Polly), listed in the PRD tech stack, is deliberately **not** added. ADR 0008 forbids a second retry layer, and the per-attempt timeouts need no resilience pipeline.

## Consequences

- A change to a policy, a dependency, the Fail expression, the Web request or the data flow script in the ADF JSON fails `MapPolicyTests` until the code and this ADR follow.
- Replacing the placeholder KeyBridge contract with the real specification changes `HttpKeyBridgeClient` and `KeyBridgeGlMappings` only. The orchestrator and the cache procedure keep their shape.
- Phase 8 wraps this orchestrator as a sub-orchestration. TXN_CODE_UNMAPPED arrives as a `TaskFailedException` whose inner failure is `PipelineFailedException`. `PipelineFailedException.TryParse` on the inner failure recovers the code for the master's Failed event (ADR 0007).
