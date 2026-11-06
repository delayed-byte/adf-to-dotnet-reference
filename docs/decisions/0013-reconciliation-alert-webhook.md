# 0013. PL_Reconciliation: activity granularity, the Key Vault step, the alert body, delivery semantics and webhook URL redaction

Status: Accepted · Date: 2026-11-06

## Context

Phase 7 converts `PL_Reconciliation` on the pattern of ADR 0009 to ADR 0012. Four things are not obvious from the ADF JSON:
- the Web activity "Get Alert Webhook Url" reads a secret from Key Vault; the URL embeds a token and must never cross a Durable boundary;
- "Post Recon Alert" builds its JSON body by string interpolation, which does no escaping;
- an alert can be delivered twice or lost, depending on where a failure falls;
- the token is in the URL *path*, and .NET redacts only query strings in its HTTP telemetry (AT-17).

## Decision

### Orchestration and activity granularity

The body of `ReconciliationOrchestrator` replaces the stub. The name, the input (`ReconciliationInput`) and the output (none) are unchanged (ADR 0008). The orchestrator mirrors the ADF order and dependencies (`ReconPolicyTests` reads them from the JSON), does no I/O and reads the clock only through `context.CurrentUtcDateTime`.

| ADF activity | .NET | Retry / timeout (ADF JSON) |
| --- | --- | --- |
| Log Recon Started / Log Recon Outcome | `Recon_LogBatchRunEvent` | 3 attempts, 30 s / 5 min |
| Lookup Card Totals | `Recon_LookupCardTotals` | 3, 30 s / 5 min (`queryTimeout` 5 min) |
| Lookup GL Totals | `Recon_LookupGlTotals` | 3, 30 s / 5 min |
| Record Recon Exceptions (a Lookup that writes, ADR 0004) | `Recon_RecordReconExceptions` | 3, 30 s / 5 min |
| If Alert Required | `if (recorded.AlertRequired)` in the orchestrator | no policy in ADF |
| Get Alert Webhook Url | **not an activity** (below) | 3, 30 s / 1 min in ADF |
| Post Recon Alert | `Recon_PostReconAlert` | 4, 30 s / 2 min; HTTP request timeout 1 min |
| Mark Recon Alert Sent | `Recon_MarkReconAlertSent` | 3, 30 s / 5 min |

- The two lookups depend only on the Started event, so the orchestrator starts both and waits with `Task.WhenAll`. Record Recon Exceptions waits for both.
- The Status, RecordCount and Notes expressions are pure functions in Core (`ReconFormat`), checked against the ADF expressions by `ReconPolicyTests`. The Notes use the SQL scale: `card difference=0.01; GL items out of tolerance=0; tolerance=0.00` (ADR 0006, semantics 4), formatted `F2` in the invariant culture. Money is `decimal` everywhere.
- A failure of any activity throws its `TaskFailedException` and logs no Outcome event, as in ADF (the Outcome depends on the If succeeding). The master (Phase 8) records it.
- **Not read:** this phase reads no Db2 and no blob, so `Db2DriverConfiguration` and the Storage retry settings do not apply. SqlClient `ConnectRetryCount` is 0 through `Connections` (ADR 0009). There is no Polly and no new package.

### Get Alert Webhook Url is configuration, not a call

In ADF the step is a GET to `https://kv-cu-batch-prod.vault.azure.net/secrets/recon-alert-webhook-url` with the factory's managed identity, `secureOutput: true`. In .NET:
- the URL is `ReconAlertWebhookOptions.Url` (`ReconAlertWebhook:Url`). In Azure it is a Key Vault reference or Key Vault configuration provider for the secret `recon-alert-webhook-url`, resolved by the host at startup and on refresh, not by the pipeline;
- **no activity exists**, so the Key Vault read has no per-run retry, and its ADF policy (retry 2, 30 s, 1 min) has no counterpart. `ReconPolicyTests` still reads the step from the JSON and fails if its method, URL, authentication or `secureOutput` change, so the decision is revisited if the source of truth moves;
- the URL is read **inside the posting activity** by `HttpReconAlertClient`. It is not an activity input, an activity output, a return value or an exception message. `ReconAlert`, the only input of `Recon_PostReconAlert`, has no URL, and a test fails if any recon contract gains a `Uri`, `Url` or `Token` property. Orchestration history therefore never holds it;
- a missing, relative or non-http(s) URL raises `ReconAlertConfigurationException` (message names the setting, not the value), which is **not retried**. A setting that cannot be bound is reported the same way, without the binder's message.

**Difference from ADF:** a missing secret fails the Post activity, not a separate earlier step, and ADF's retry of the secret read does not exist. A Key Vault outage at run time cannot occur; one at startup stops the host.

### Post Recon Alert

- `POST`, `Content-Type: application/json` exactly (no `charset`), body UTF-8 without BOM. No other header, no credential: the token in the URL is the credential.
- **Timeouts:** `HttpClient.Timeout` = `httpRequestTimeout` (**1 min**); `AttemptTimeout` = the activity `timeout` (**2 min**) bounds the whole attempt.
- **Retry:** the Durable `RetryPolicy` on the call is the only layer: 4 attempts (`retry` 3), fixed 30 s. The handler chain is `SocketsHttpHandler` (no redirects, no cookies, no propagator) only; no resilience handler and no HTTP-client loggers (`RemoveAllLoggers`). `ReconAlertClientTests` walks the production handler chain.
- **Response body:** not read. ADF reads it but the pipeline does not use it.

| Outcome | Exception | Retried? |
| --- | --- | --- |
| No connection | `ReconAlertUnavailableException` | yes |
| No response within `httpRequestTimeout`, or the attempt exceeds 2 min | `TimeoutException` | yes |
| 408, 429, 5xx | `ReconAlertUnavailableException` | yes |
| Any other non-2xx: 400, 401, 403, 404, 410, 3xx | `ReconAlertRejectedException` | **no** |
| Missing or unusable URL | `ReconAlertConfigurationException` | **no** |

**Difference from ADF**, which retries every failure three times. A 404 is what a rotated token looks like, and a 400 means the body was refused; repeating either cannot help. Redirects are not followed, so the alert goes to the configured host only (ADF's behaviour is unconfirmed).

### The alert body and its escaping

`ReconFormat.AlertBody` writes the ADF body in its property order, without whitespace: `type`, `businessDate`, `batchRunId`, `cardDifference`, `glItemsOutOfTolerance`, `exceptionCount`, `summary`. `ReconPolicyTests` fills the ADF expression's six `@{…}` placeholders with the same values and requires the same bytes for ordinary input.
- `cardDifference` is a JSON number printed with two decimals (`0.01`, `-12.50`); the golden alert compares it as a decimal.
- ADF's `@{}` does no JSON escaping, so a quote, a backslash or a newline in `summary` yields an invalid document (or, with a crafted value, an extra property). The `summary` is built by the procedure from `CheckName`, `ItemKey` (a GL account number and a side) and the difference, so this cannot happen with today's data, but a GL account number is free text in KeyStone. **Decision:** the summary is written as a JSON string through `Utf8JsonWriter`, escaped; non-ASCII is not escaped (`UnsafeRelaxedJsonEscaping` only means "no HTML escaping"). The golden alert has no special characters, so it is unaffected.
- `LEFT(…, 1000)` in the procedure can cut a surrogate pair; the unpaired half becomes U+FFFD, because JSON cannot carry it.
- `ItemsOutOfTolerance` is `SUM(CASE …)`, which is NULL when nothing was compared (a date with no GL data). ADF's interpolation of a NULL gives `"glItemsOutOfTolerance":,` and an empty string in the notes. **Decision:** NULL reads as 0 (nothing can be out of tolerance in an empty set), in the body and the notes.
- A tolerance that `decimal(19,2)` cannot hold exactly (negative, more than two decimals, out of range) raises `ReconInputException` and is not retried. ADF would pass the string and let SQL round it, so the notes would print one tolerance while the check used another. The default is `0.00`.

### Alert delivery semantics

Two procedures define what a rerun does (read from `db/reporting/procedures/052_ops.usp_RecordReconExceptions.sql`):
- `usp_RecordReconExceptions` inserts a row per out-of-tolerance item that is not already there (unique on date, check, item, difference), and `AlertRequired` is true while **a current exception has no `AlertSentUtc`**. It does not depend on whether this call inserted anything.
- `usp_MarkReconAlertSent` stamps every unalerted exception of the date.

So a rerun is silent exactly when every current exception has been marked. A changed difference is a new row and alerts again.

**What the code guarantees**
- A run never marks an exception sent unless a 2xx came back for a POST in the same orchestration. The Mark activity runs only after the Post activity completed.
- A POST is retried only for failures that can be transient (table above), at most 4 attempts.
- Retrying `Record Recon Exceptions` is safe, including when an earlier attempt committed and lost its reply: the retry inserts nothing and still reports the alert as due (`ReconOrchestratorTests`).
- After a delivered and marked alert, a rerun under a new batch run ID inserts no row and posts nothing (AT-12, AT-13 and the unit tests).
- A failed run leaves the exception unalerted, so the next run alerts it, once: a lost alert is not silently dropped (`ReconciliationConversionTests`).

**What it cannot guarantee: at-least-once is the best available**

| Failure | Result |
| --- | --- |
| The POST reached the webhook, but the response timed out or the connection broke | The retry sends the same body again: **duplicate**. |
| The worker crashes during the Post activity after the POST was delivered | Durable runs the activity again: **duplicate**. |
| The POST was delivered, but Mark fails on all 3 attempts | The run fails, the exception stays unalerted, and the next run posts it again: **duplicate**. |
| Two runs for one date overlap | Both may see an unalerted exception and post: **duplicate**. Neither alert is lost. |
| All 4 POST attempts fail | The run fails, nothing is marked, and the next run posts: **delayed, not lost**. |

The alternative, marking before posting, would turn every one of these into a *lost* alert, which is worse for a reconciliation exception. Every duplicate carries the same `businessDate`, `batchRunId` and `summary`, so a receiver that wants exactly-once can de-duplicate on `(businessDate, batchRunId)`; an `Idempotency-Key` header is not added because the ADF request has none and the receivers named in the PRD (Teams or email) ignore it. The AT-13 requirement of exactly one alert, and the rerun's silence, hold whenever the webhook accepts the first POST and the Mark succeeds; the tests show both.

### Webhook URL redaction (AT-17)

What was checked, with a harness that records every `ILogger` entry and every `ActivitySource` span:
- **Spans.** `System.Net.Http` creates a span for each request while anything listens, and it records the request URL. .NET redacts only the *query string* (`url.full`); the path is kept, and the token is in the path. `ReconAlertClientTests` has a positive control: a default `HttpClient` against the stub puts the token into a captured span. This is the leak.
- **Logs.** The factory's `System.Net.Http.HttpClient.*` loggers log the request URI (and headers at Trace). `RemoveAllLoggers()` removes them, as for KeyBridge (ADR 0012).
- **Exception messages.** A probe test pins that the platform's own `HttpRequestException` (refused connection) and `TaskCanceledException` (timeout) do not carry the path today. The client does not attach or log them anyway: it reports `HttpRequestError`, a status code or "timeout".
- **Our messages and logs** name the status, the date and counts, never the URL, a header or a body.

**Fix:** the primary handler sets `SocketsHttpHandler.ActivityHeadersPropagator = null`. That removes .NET's `DiagnosticsHandler` from the chain, so this client produces no span at all, and also sends no `traceparent` or `Request-Id` header to the webhook (internal trace IDs do not go to a third party). The alternative, `System.Net.Http.EnableActivityPropagation=false`, is process-wide and would also remove the spans of KeyBridge and the Durable worker's calls. The client's own logs and the orchestration's activity spans stay.

**Proof:**
- `ReconAlertClientTests.The_production_registration_…` runs the production registration at Trace level and scans logs and spans for the token and the URL.
- `ReconciliationConversionTests.Reconciliation_runs_log_no_secret_…` applies the AT-17 rules (all secrets, the webhook URL and path, every member, account and card number) to Reconciliation runs on two scenarios, including a retried 503. The AT-17 cases themselves need the Phase 8 master.
- Mutation check: removing `ActivityHeadersPropagator = null` fails both tests.

### Idempotency

- Rerun for a business date: above. `ops.ReconExceptions` rows are append-only (trigger) and unique per exception, so a retried or repeated Record adds nothing.
- `ops.BatchRun` grows on every attempt of a Log activity (append-only by design); a Log activity whose insert committed but whose reply was lost would add a second row, as in Phases 4 to 6.

## Differences from ADF a reviewer should know

- The Key Vault step is configuration; its retry and timeout do not exist (above).
- Rejected requests (4xx other than 408 and 429, 3xx) and bad settings are not retried; ADF retries everything.
- The summary is JSON-escaped; ADF is not, and would send invalid JSON for a quote or backslash.
- NULL `ItemsOutOfTolerance` is 0; ADF would send invalid JSON and an empty note.
- A negative tolerance or one with more than two decimals fails; ADF passes it to SQL, which rounds.
- Money in notes and the body always has two decimals. How ADF prints a `decimal(19,2)` value from a Lookup (`0.10` or `0.1`) is unconfirmed; ADR 0006 semantics 4 states the scale rule, and the golden notes are checked at that scale.
- The two lookups run in parallel, as the dependencies say. `Task.WhenAll` completes when both have finished, so a failure of one still lets the other run to its end; ADF's behaviour when one parallel branch fails is unconfirmed.
- Redirects are not followed; the response body is not read.

## Consequences

- A change to a policy, a dependency, an expression, the Web requests or the body in the ADF JSON fails `ReconPolicyTests` until the code and this ADR follow.
- **Phase 8:** a failed Reconciliation arrives as a `TaskFailedException` whose inner type says why (`ReconAlertUnavailableException`, `ReconAlertRejectedException`, `ReconAlertConfigurationException`, `ReconInputException`, SQL errors). The master decides whether a recon alert failure fails the batch; the exceptions are already recorded and the alert is retried by the next run. A master rerun for the same date alerts only for exceptions not yet alerted. Keep host logging of activity inputs and outputs off; the alert summary is in an activity input.
- Replacing the alert channel with a real Teams or email endpoint changes `HttpReconAlertClient` only. If that endpoint offers an idempotency key, it would be added there.
