# Test data: scenarios, inputs and golden outputs

Everything under `inputs/` and `golden/` is produced by the seeded generator in `generator/` and must not be edited by hand; a unit test fails if a committed file differs from the generator's output by a single byte. Formats, the checksum and the derivation rules are in [ADR 0006](../../docs/decisions/0006-test-harness-and-golden-outputs.md); the Db2 type rules are in [ADR 0005](../../docs/decisions/0005-db2-type-handling.md). Golden outputs were signed off by engineering review, since no business reviewer was available (see [REVIEW.md](REVIEW.md) and [ADR 0007](../../docs/decisions/0007-golden-output-sign-off.md)).

```powershell
./build.ps1 golden                              # regenerate every committed scenario
./build.ps1 golden -Scenario perf-extract-1m    # performance scenario (inputs to generated/, git-ignored)
dotnet run --project tests/TestData/generator -- --check   # report drift without writing
```

## Scenarios

| Scenario | Acceptance tests | Runs | What it proves |
| --- | --- | --- | --- |
| [standard-day](golden/standard-day/summary.md) | AT-01, AT-02, AT-04 (with refused connections), AT-05, AT-08, AT-11 (with KeyBridge 503s), AT-12, AT-14, AT-17 | Master, 2026-11-04 | 5,000 transactions, 800 accounts, 300 GL entries, three valid settlement files; distractors from the previous day and in the landing folder; everything reconciles |
| [extract-db2-types](golden/extract-db2-types/summary.md) | AT-03 | Extract | Every ADR 0005 type rule, one row per edge case |
| [card-quarantine-trailer-mismatch](golden/card-quarantine-trailer-mismatch/summary.md) | AT-06 | Card | One file's trailer count is one too high: quarantined with an error manifest; the others load, including the upper-case `SETTLEMENT_003.CSV` (case-insensitive match, ADR 0007) |
| [card-empty-landing](golden/card-empty-landing/summary.md) | AT-07 | Card | No folder for the date; another date's file is left alone; zero rows and a "no files" note |
| [map-unmapped-code](golden/map-unmapped-code/summary.md) | AT-09, AT-15 | Master | New code P2PD is not in the KeyBridge mapping: `TXN_CODE_UNMAPPED` naming it; Reconciliation not run |
| [map-midpoint-rounding](golden/map-midpoint-rounding/summary.md) | AT-10 | Master | Half-cent allocations; banker's rounding worksheet in the summary |
| [recon-card-off-by-one-cent](golden/recon-card-off-by-one-cent/summary.md) | AT-13 | Master | Card totals differ by 0.01: one exception, exactly one alert |
| [rerun-earlier-date](golden/rerun-earlier-date/summary.md) | AT-16 | Master 11-03, 11-04, 11-03 | The rerun changes nothing and touches only 2026-11-03 |
| [dst-transitions](golden/dst-transitions/summary.md) | AT-18 | Four trigger-driven masters | Fires once per Pacific day across both 2026 DST changes; near-midnight and repeated-hour transactions land on KeyStone's posting date |
| [perf-extract-1m](golden/perf-extract-1m/summary.md) | NFR performance (`Category=Performance`) | Extract | 1,000,000 transactions; only counts and checksums are committed |

AT-19 (least privilege) needs no data: it uses the logins the harness creates. AT-17 (no secrets or full numbers in logs) runs on standard-day with the secrets in `TestSecrets`.

## Using a scenario in a test

```csharp
var scenario = Scenario.Load("standard-day");
await env.LoadAsync(scenario);                       // reset, then Db2 rows and landing files
var runIds = new[] { Guid.NewGuid() };              // one per scenario run, run 1 first
// ... run the pipeline(s) for scenario.Runs ...
await GoldenAssert.TablesMatchAsync(env.Reporting, scenario, runIds, pipelines: [Pipelines.Extract]);
await GoldenAssert.BlobsMatchAsync(env.Blobs, scenario, runIds);
```

`scenario.ExpectedRun(n)` gives each run's expected status, error code, failed step, pipelines run and not run, stage order, error messages, KeyBridge call count, alerts and evidence file.

## Synthetic data

Member numbers are `9xxxxxxxxx`, account numbers `99xxxxxxxxxx`, cards appear only as the test BIN `400000` plus four digits, and no SSN or tax ID exists anywhere. Names come from fixed lists of placeholder names. A unit test scans every committed file for SSN-like patterns and Luhn-valid card numbers.
