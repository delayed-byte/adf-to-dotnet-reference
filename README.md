# ADF → .NET reference implementation

A worked example of moving Azure Data Factory (ADF) pipelines to .NET C#. The setting is a credit union that runs Corelation KeyStone. The pipelines run as Azure Durable Functions, and acceptance tests prove each converted pipeline against golden outputs.

**What this is, and what it is not**

- It is a **reference implementation**. It is not production code.
- All data is **synthetic**. The Db2 schema is invented. It is not KeyStone's licensed schema. The KeyBridge contract is a placeholder.
- It demonstrates controls in line with NCUA expectations: a read-only extract login, separate writer and reader logins, masked logs, an append-only audit trail, and an evidence file for each run. **It does not claim compliance** with any regulation.
- Nothing is deployed to Azure. Everything runs on one developer machine in Docker containers.
- ADF could never be run here. The ADF JSON in `adf/` was written by hand, and the golden outputs were derived from it. An engineering review signed them off, not a business reviewer ([ADR 0007](docs/decisions/0007-golden-output-sign-off.md)). A business reviewer must confirm them before any go-live.

## The one command

On a machine that meets the prerequisites below:

```powershell
git clone <repository-url> adf-to-dotnet-reference
cd adf-to-dotnet-reference
./build.ps1 test
```

`./build.ps1 test` builds the solution, starts its own Db2, SQL Server and Azurite containers through Testcontainers, runs every test except the performance test, and stops the containers. It does **not** need `./build.ps1 up`.

The first run pulls the container images (about 10 GB). That download was not timed, because the images were already present on the test machine.

**How long it takes.** On the test machine (details under Commands), the run took 6 min 54 s from a fresh clone with the build already done. About 3 min 40 s of that is Db2 starting, measured from Docker events. The first build on a cold NuGet cache took 2 min 4 s on top.

**What a pass looks like.** The run ends with a summary like this. At the commit this was written for, it showed 652 tests, none failed, none skipped:

```text
Test run summary: Passed!
  total: 652
  failed: 0
  succeeded: 652
  skipped: 0
```

> **Known problem in the committed repository.** A fresh clone of commit `6229a3c` fails 34 of these 652 tests, because `.gitignore` excludes 11 test-data files. See the first item under "Known limitations" for the cause and the check that proved it. Until that is fixed, `./build.ps1 test` does not pass on a fresh clone. It passes in the working tree where the files exist.

## Prerequisites

| Need | Version | Notes |
| --- | --- | --- |
| Windows | 10 or 11, x64 | The scripts and Docker setup were only run on Windows 11. |
| .NET SDK | 10.0.403 or a later 10.0.x feature band | [global.json](global.json) pins 10.0.403 with `rollForward: latestFeature`. |
| Docker Desktop | With the WSL 2 backend | Tested with Docker Desktop 4.88.1, engine 29.7.2. |
| WSL 2 memory | At least 8 GB | The Db2 container alone needs about 4 GB. See below. |
| PowerShell | 7 or later | `./build.ps1` is a PowerShell 7 script. Tested with 7.6.9. |
| Azure Functions Core Tools | v4 | Only for `./build.ps1 run`. Tested with 4.16.0. |
| Disk | About 10 GB for images | Db2 6.3 GB, SQL Server 2.2 GB, the rest under 1 GB each. |
| RAM | 16 GB recommended | |

Install with winget. These package IDs exist, and winget lists the same SDK and Core Tools versions as above on the day this was written.

```powershell
winget install Microsoft.DotNet.SDK.10 Docker.DockerDesktop Microsoft.Azure.FunctionsCoreTools Microsoft.PowerShell
```

**WSL memory.** WSL 2 gives Docker half of the machine's RAM by default. On the machine this was tested on (32 GB), that is 15.5 GB, so no setting was needed. If your machine has 16 GB or less, set the limit in `%UserProfile%\.wslconfig`, then run `wsl --shutdown` and restart Docker Desktop:

```ini
[wsl2]
memory=8GB
```

**Repository location.** Keep the clone on the Windows file system, not inside a WSL distribution, and **keep its path short**. Clone it to something like `C:\src\adf-to-dotnet-reference`. The Functions build copies a file whose path is 169 characters long below the repository root, and Windows stops at 260. A root path of more than about 90 characters makes `./build.ps1 build` fail with `MSB3030: Could not copy the file ... because it was not found`. This was seen when the clone sat in a deeply nested temporary folder (the path was 279 characters). Turning on Windows long-path support should lift the limit, but that was not tried.

**Core Tools through npm.** winget is the route used by the PRD. If you use `npm i -g azure-functions-core-tools@4` on Node 24 instead, the post-install step downloaded the 4.16.0 zip but did not extract it. Extract the zip by hand into `%APPDATA%\npm\node_modules\azure-functions-core-tools\bin`. Also add `%APPDATA%\npm` to `PATH`, because the npm global folder is not on it by default.

## Commands

Run all of them from the repository root in PowerShell 7.

| Command | What it does | Needs `up`? | Time |
| --- | --- | --- | --- |
| `./build.ps1 up` | Starts Db2, SQL Server, Azurite, WireMock and the Aspire dashboard. Creates `.env` with random passwords on the first run. Waits until the containers are healthy. | | About 3 min (170 s measured from an empty Db2, images already pulled) |
| `./build.ps1 seed` | Deploys the Db2 and SQL Server schemas with DbUp. Safe to repeat. | Yes | About 10 s (8 s and 12 s measured) |
| `./build.ps1 build` | `dotnet build` of the whole solution, with warnings as errors. | No | About 2 min on a cold NuGet cache (124 s measured), about 1 min when little changed |
| `./build.ps1 test` | Runs all tests except `Category=Performance`. Uses Testcontainers. | No | About 7 min (6 min 54 s measured), mostly Db2 starting |
| `./build.ps1 test -Performance` | Runs only the one-million-row extract test. Its 135 MB of input is generated into the git-ignored `tests/TestData/generated`. | No | About 5 min 24 s against a running stack (324 s measured, mostly loading a million rows into Db2). Without `up`, add the Db2 start time. |
| `./build.ps1 golden` | Regenerates the test inputs and golden outputs from the seeded generator. Add `-Scenario NAME` for one scenario. | No | About 7 s |
| `./build.ps1 run` | Builds and starts the Functions host (`func start`). | Yes | |
| `./build.ps1 scan` | Secret scan with gitleaks, run in Docker. | No | About 17 s with the image already pulled |
| `./build.ps1 down` | Stops the containers. Add `-Volumes` to delete the SQL Server and Azurite data too. | | few seconds |

Timings come from one development machine (an Intel Pentium Gold G5420, 2 cores, 32 GB RAM). A faster machine will be quicker.

### The fast loop

`./build.ps1 test` starts and stops three containers every time, and Db2 is the slow one. To reuse a running stack instead:

```powershell
./build.ps1 up
$env:CU_TESTS_USE_COMPOSE = '1'
./build.ps1 test        # 218 s measured for the whole suite (3 min 38 s)
```

In this mode the tests use the containers from `up` and the passwords in `.env`. The tests wipe the reporting store and the scenario rows in Db2 before each scenario, so do not point this at data you want to keep.

## Running the batch under `func start`

This is the closest thing to a deployment. The Functions host runs the real Durable Functions pipelines against the local containers.

1. Start the stack and deploy the schemas: `./build.ps1 up`, then `./build.ps1 seed`.

   After `seed`, Db2 holds only reference data, so a batch would process an empty day. To have something to process, load the `standard-day` scenario by running one acceptance test against the stack. It extracts 5,000 transactions, 800 accounts and 300 GL entries into the reporting store and leaves three settlement files in the landing container for business date 2026-11-04. It wipes the reporting store first.

   ```powershell
   $env:CU_TESTS_USE_COMPOSE = '1'
   dotnet test --project tests/CreditUnion.Batch.AcceptanceTests --nologo -- --filter-trait "AT=AT-02"
   Remove-Item Env:CU_TESTS_USE_COMPOSE
   ```
2. Set the settings the host needs, as **environment variables** in the shell you will run it from. The host does not read user-secrets: `Program.cs` does not add that provider, so a `dotnet user-secrets` value is ignored. `local.settings.json` is committed and holds no secrets, so do not add passwords to it.

   ```powershell
   $e = @{}
   Get-Content .env | ForEach-Object { if ($_ -match '^([A-Z0-9_]+)=(.*)$') { $e[$Matches[1]] = $Matches[2] } }
   $env:KeyStoneDb2__ConnectionString = "Server=localhost:50000;Database=KEYSTONE;UID=ksextract;PWD=$($e.DB2_EXTRACT_PASSWORD);"
   $env:ReportingSql__ConnectionString = "Server=localhost,14330;Database=Reporting;User Id=cu_batch_writer;Password=$($e.SQL_WRITER_PASSWORD);Encrypt=True;TrustServerCertificate=True;"
   $env:BlobStorage__ConnectionString = 'UseDevelopmentStorage=true'
   $env:KeyBridge__BaseUrl = 'http://localhost:8081/api'
   $env:KeyBridge__Password = 'local-stub-password'
   $env:ReconAlertWebhook__Url = 'http://localhost:8081/hooks/recon-alerts/local-token'
   ```

   `local.settings.json` already sets `AzureWebJobsStorage` (Azurite), `FUNCTIONS_WORKER_RUNTIME` and `WEBSITE_TIME_ZONE=America/Los_Angeles`. The WireMock container stands in for KeyBridge and the alert webhook, and accepts any Basic credential.
3. Start the host: `./build.ps1 run`. Leave it running. It builds the project, then prints a warning that `func start` against an isolated project "may not correctly load function extensions". The functions do load and run (checked); ignore the warning. It took about 30 seconds to print `Job host started` on a warm build, and the console then lists the functions and the next five occurrences of `TR_Nightly_0300_Pacific` in Pacific time.
4. In a second PowerShell window, change to `src/CreditUnion.Batch.Functions` (`func durable` looks for `host.json` in the current folder) and start a batch for one business date. The input must be a file: through the npm shim, inline JSON loses its quotes and the master rejects it. The instance ID must be a new GUID, because it becomes the batch run ID.

   ```powershell
   $inputFile = Join-Path $env:TEMP 'batch-input.json'
   Set-Content -Path $inputFile -Value '{"BusinessDate":"2026-11-04"}' -Encoding ascii
   func durable start-new --function-name PL_Master_NightlyBatch --id ([guid]::NewGuid()) --input "@$inputFile" --task-hub-name CreditUnionBatch
   ```

   `ReconTolerance` and `RetainUntil` are optional fields of the same input.
5. Or run the timer function once by hand. It processes the previous Pacific day, as the 03:00 trigger would:

   ```powershell
   Invoke-WebRequest -Method Post -Uri http://localhost:7071/admin/functions/TR_Nightly_0300_Pacific -ContentType 'application/json' -Body '{"input":""}'
   ```

   The response is HTTP 202. Locally it needs no key.

**Where to look afterwards**

- `ops.BatchRun` holds the audit trail. Every event of a run shares its batch run ID (the instance ID):

  ```powershell
  $pw = (Select-String -Path .env -Pattern '^SQL_READER_PASSWORD=(.*)$').Matches[0].Groups[1].Value
  docker exec reporting-sqlserver /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U cu_report_reader -P $pw -d Reporting -W -Q "SELECT TOP 20 BatchRunId, BusinessDate, PipelineName, StepName, Status, RecordCount, ObjectName FROM ops.BatchRun ORDER BY BatchRunEventId DESC"
  ```

  A successful master ends with a `Succeeded` event. Its `ObjectName` is the evidence file.
- The evidence file is the blob `evidence/{businessDate}/batch-run-{batchRunId}.json` in Azurite. It is a JSON array of `{section, item, value}` entries. This reads it with a Shared Key request, using Azurite's published development account key (a public constant, not a secret). Put the run ID in `$id` first:

  ```powershell
  $id = '<batch run id, lower case>'
  $account = 'devstoreaccount1'
  $key = [Convert]::FromBase64String('Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==')
  $path = "/evidence/2026-11-04/batch-run-$id.json"
  $date = [DateTime]::UtcNow.ToString('R'); $ver = '2021-08-06'
  $stringToSign = "GET`n`n`n`n`n`n`n`n`n`n`n`nx-ms-date:$date`nx-ms-version:$ver`n/$account/$account$path"
  $signature = [Convert]::ToBase64String([Security.Cryptography.HMACSHA256]::new($key).ComputeHash([Text.Encoding]::UTF8.GetBytes($stringToSign)))
  (Invoke-WebRequest -Uri "http://127.0.0.1:10000/$account$path" -Headers @{ 'x-ms-date' = $date; 'x-ms-version' = $ver; Authorization = "SharedKey ${account}:$signature" }).Content
  ```

  Any Azure Storage client pointed at the emulator (`UseDevelopmentStorage=true`) also works. The blob has the metadata `retain_until` = `TBD`.

**What you will see.** With `standard-day` loaded and business date 2026-11-04, the master run completed in under a minute and wrote 17 `ops.BatchRun` events: the master `Started`; Extract copying 5,000, 800 and 300 rows and merging them as unchanged (AT-02 had already extracted them); Card Settlement processing three files and merging 1,857 rows; Map `Succeeded`; Reconciliation `Reconciled` (`card difference=0.00; GL items out of tolerance=0; tolerance=0.00`); and the master `Succeeded`, with the evidence path as its `ObjectName`. The note on the first event reads `trigger=Manual`.

The timer function, run by hand, started a master for the previous Pacific day (2026-11-05, which has no data, so it finished as an empty day) and logged `Started PL_Master_NightlyBatch <id> for business date 2026-11-05 (occurrence 2026-11-06 11:00:00Z, ...)`. The occurrence is the latest 03:00 Pacific at or before the moment you fire it, not the moment itself.

The host's console log (about 33,000 lines for the two runs, mostly Azure SDK traffic) was searched for the passwords, the KeyBridge password and the webhook token used here. None appeared.

**Not in the dashboard.** The Aspire dashboard at <http://localhost:18888> starts and works, but the batch sends it nothing. OpenTelemetry export is not configured (see the limitations below). Look at the `func start` console and at `ops.BatchRun` instead.

## What the acceptance tests prove

The tests live in `tests/CreditUnion.Batch.AcceptanceTests`, one folder per pipeline plus `CrossCutting`. Each test is tagged `[Trait("AT", "AT-nn")]`, and **each one ends with a rerun under new batch run IDs** that must change nothing except the audit tables. The scenarios behind them are described in [tests/TestData/README.md](tests/TestData/README.md).

| ID | Pipeline | What it proves |
| --- | --- | --- |
| AT-01 | Extract | A seeded day (5,000 transactions, 800 accounts, 300 GL entries) reaches `core` with golden row counts and checksums. |
| AT-02 | Extract | Extracting the same business date twice creates no duplicates. |
| AT-03 | Extract | Db2 CHAR padding, NULLs, maximum-precision DECIMAL and TIMESTAMP values are stored exactly as [ADR 0005](docs/decisions/0005-db2-type-handling.md) says. |
| AT-04 | Extract | Db2 connections refused twice are retried per the ADF Copy policy (3 retries, 30 s apart), and the extract succeeds. |
| AT-05 | Card | Three valid settlement files load, and the files move to the archive. |
| AT-06 | Card | A file whose trailer count is wrong is quarantined with an error manifest, and the other files still load. |
| AT-07 | Card | An empty landing folder succeeds with zero rows and a "no files" note. |
| AT-08 | Map | With every transaction code mapped, `core.DailyGlTotals` matches the golden output to the cent. |
| AT-09 | Map | An unmapped code fails with `TXN_CODE_UNMAPPED` naming the code, and the failure is not retried. |
| AT-10 | Map | Half-cent amounts use banker's rounding (`MidpointRounding.ToEven`). |
| AT-11 | Map | KeyBridge answering 503 twice and then 200 is retried per the ADF Web policy, and the run succeeds. |
| AT-12 | Reconciliation | When card and GL totals match, the status is Reconciled and no alert is sent. |
| AT-13 | Reconciliation | A card difference of 0.01 writes one exception row and posts exactly one alert. |
| AT-14 | Master | The happy path: Extract and Card run in parallel, then Map, then Reconciliation, and the evidence file is written. |
| AT-15 | Master | When Map fails, Reconciliation does not run and `ops.BatchRun` records Failed at Map. |
| AT-16 | Master | A rerun for an earlier business date changes only that date's data. |
| AT-17 | All | No log line, span or failure detail holds a secret or a full member, account or card number (three scenarios). |
| AT-18 | Trigger | Across both 2026 daylight saving changes, the trigger fires once per Pacific day with the previous Pacific day as business date. |
| AT-19 | All | The extract login cannot write to Db2, and the reader login cannot write to the reporting store. Each write is first run as admin, to prove the test can fail. |

Other test groups: unit tests for the services, the ADF JSON validator, the policy tests that read the ADF JSON, the replay test for orchestrator determinism ([ADR 0017](docs/decisions/0017-orchestrator-determinism-check.md)), harness self-tests, and the documentation consistency test that checks these docs.

## Known limitations and open items

- **11 test-data files are not in git (found by the fresh-clone check).** `.gitignore` has the rules `evidence/` and `*.tmp`. They exclude the ten golden files `tests/TestData/golden/*/evidence/run-N.json` and the input file `tests/TestData/inputs/standard-day/blobs/card-landing/2026-11-04/settlement_004.csv.tmp`. `git ls-files` lists none of them. A fresh clone of `6229a3c` therefore fails 34 of 652 tests: the byte-for-byte drift tests, and every test that loads `standard-day` or an evidence file. Copying the 11 files into a fresh clone made all 652 pass, with none skipped. The fix is to add negation rules to `.gitignore` (for example `!tests/TestData/**/evidence/` and `!tests/TestData/**/*.tmp`) and commit the files. That is a repository change, so it was not made in the documentation phase. Remove this item when it is fixed.
- **No OpenTelemetry export.** The PRD asks for traces in the Aspire dashboard. `Program.cs` adds no OpenTelemetry, so the dashboard shows no batch traces. The batch writes `ILogger` logs and .NET's own spans only. A follow-up needs an OTLP exporter, a `batchRunId` tag and a rule for correlating the Durable worker's spans.
- **No Azure deployment.** The [deployment checklist](docs/migration-playbook.md#azure-deployment-checklist) lists what is open.
- **Golden outputs are not business-reviewed.** See above and [tests/TestData/REVIEW.md](tests/TestData/REVIEW.md).
- **Several ADF behaviours are unconfirmed.** The [playbook](docs/migration-playbook.md#differences-from-adf-and-items-to-verify) has the full checklist.
- **No retain-until index tag.** Archived, quarantined and evidence blobs carry `retain_until` metadata (`TBD`), as ADF does. [ADR 0015](docs/decisions/0015-run-evidence-export.md) explains.
- **Retention periods are open.** They come from the credit union's own records policy (`TBD`). Nothing deletes data automatically.
- **Different libraries than the PRD names.** The code has no Dapper, CsvHelper or Polly (`Http.Resilience`). Plain `SqlClient`, an own CSV reader and the Durable `RetryPolicy` do those jobs. Polly was left out on purpose ([ADR 0008](docs/decisions/0008-acceptance-test-entry-points.md), [ADR 0012](docs/decisions/0012-keybridge-web-activity-and-mapping-cache.md)). No ADR records why Dapper and CsvHelper were skipped.
- **The performance figure is one run.** 1,052,000 rows extracted in 46.3 s on a development machine, against the 100,000 rows per minute minimum.
- **Known data limitation.** A rerun does not carry a source correction that changes only trailing spaces or letter case ([ADR 0005](docs/decisions/0005-db2-type-handling.md)).
- **Two masters for one date at the same time** are not prevented. They are only unlikely, because the timer is a singleton ([ADR 0016](docs/decisions/0016-nightly-trigger-time-zone-and-missed-fires.md)).
- **No CI pipeline.** Nothing runs the tests automatically.

## Troubleshooting

| Symptom | Cause and fix |
| --- | --- |
| Db2 takes minutes to start, or the container is killed | Db2 needs about 4 GB and one to three minutes. Give WSL at least 8 GB (see Prerequisites). `./build.ps1 up` waits up to 10 minutes for healthy containers. |
| Port already in use | `up` publishes 50000 (Db2), 14330 (SQL Server), 10000 to 10002 (Azurite), 8081 (WireMock), 18888 and 4317 (dashboard). Stop whatever uses them. `./build.ps1 test` without `CU_TESTS_USE_COMPOSE` uses random ports, apart from the fault proxy's `127.0.0.1:15600-15615`. |
| `error MSB3030: Could not copy the file ... because it was not found` during the build | The clone path is too long. Move the repository to a short path (see Prerequisites). |
| `The process cannot access the file ... because it is being used by another process`, or "file is locked by .NET Host" | An old build server or test host holds a DLL. Run `dotnet build-server shutdown`, stop any running `func start`, and try again. |
| Db2 tables are empty after `docker compose up` recreated the container | Db2 has no persistent volume on purpose ([ADR 0002](docs/decisions/0002-schema-deployment-with-dbup.md)). Run `./build.ps1 seed` again. SQL Server and Azurite keep their volumes. |
| `.env` is missing, or compose says to run `up` | `./build.ps1 up` creates it. Never commit it; it is git-ignored. |
| Db2 seed scripts fail with strange characters | A CRLF line ending reached a SQL or shell file that is mounted into a Linux container. `.gitattributes` forces LF for those files. After changing `core.autocrlf` or the attributes, run `git add --renormalize .`, or clone again. |
| `func` is not found | Install Core Tools with winget, or add `%APPDATA%\npm` to `PATH` if you used npm. |
| The host starts but a pipeline fails at once with a configuration error | One of the environment variables above is missing in that shell. |
| `./build.ps1 golden` changes files | Golden files change only through the generator. Read the diff, and see [tests/TestData/REVIEW.md](tests/TestData/REVIEW.md) before committing. |

## Documentation map

| Document | Purpose |
| --- | --- |
| [docs/PRD.md](docs/PRD.md) | Scope, requirements, build phases, definition of done |
| [docs/migration-playbook.md](docs/migration-playbook.md) | How a credit union takes one real ADF pipeline through this process; deployment checklist; traceability of every ADF activity |
| [docs/decisions/](docs/decisions/) | One ADR per significant decision (0001 to 0017) |
| [docs/progress.md](docs/progress.md) | What each build phase did, tested and left open |
| [adf/](adf/) | The ADF JSON, the source of truth for behaviour |
| [db/](db/) | The synthetic Db2 schema and the SQL Server reporting store |
| [contracts/](contracts/) | The KeyBridge placeholder contract and the card settlement file format |
| [tests/TestData/README.md](tests/TestData/README.md) | Scenarios, inputs, golden outputs |
| [CLAUDE.md](CLAUDE.md) | Rules for AI coding assistants working in the repository (also in [AGENTS.md](AGENTS.md)) |
