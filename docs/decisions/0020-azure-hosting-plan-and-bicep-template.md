# 0020. Azure hosting plan and the Bicep template for the Function App

Status: Proposed · Date: 2026-11-13

## Context

ADR 0001 chose Durable Functions and left the Azure plan open. Deploying to Azure is a v1 non-goal (PRD), and there is no subscription. The playbook's deployment checklist describes the Azure side only in prose. A template turns the parts that can be decided now into code that a reviewer can read and a team can run.

These facts limit the choice of plan:

- **Activity timeouts.** The ADF JSON gives activities timeouts of up to 2 hours (`Aggregate Daily GL Totals`, `PL_MapAndAggregate`) and 1 hour (`Copy Db2 To Staging`). The .NET code enforces them for each attempt (ADR 0009). The Windows Consumption plan stops any function after 10 minutes, so it cannot run them.
- **Db2 driver.** `Net.IBM.Data.Db2` is the Windows package, with a 64-bit `clidriver`. A Linux plan, including Flex Consumption, needs the `-lnx` package. Nobody has built or tested with it.
- **Network.** The app must reach the KeyStone Db2 reporting copy over a private path (checklist), so the plan needs VNet integration.
- **Host timeout.** `host.json` set no `functionTimeout`. On Premium the default is 30 minutes, so the host would kill the 1-hour copy and the 2-hour aggregate before their own timeouts fire, and Durable would record a host timeout instead of the ADF-style `TimeoutException` that the retry policy handles.

## Decision

### Plan

**Elastic Premium EP1 on Windows**, 64-bit worker, .NET 10 isolated, Functions v4. It runs the current code with no package change, supports VNet integration, and has no fixed limit on how long a function runs.

`host.json` sets `functionTimeout` to `02:30:00`, which is longer than the longest ADF activity timeout. The per-attempt timeout in code always fires first. `InfrastructureTemplateTests` fails if a longer ADF timeout is added without raising it. Core Tools honours the same value locally. The acceptance tests do not read `host.json`.

### Template

`infra/main.bicep` deploys to one resource group. It creates:

| Resource | Notes |
| --- | --- |
| User-assigned managed identity | One identity for host storage, the package, Key Vault references and Application Insights. |
| Host storage account | `AzureWebJobsStorage`: Durable task hub, timer lease, host keys. Shared-key access is **off**. The app connects with the identity (`AzureWebJobsStorage__credential = managedidentity`). It has a `deployments` container for the app package. |
| Log Analytics and Application Insights | Local authentication is off. The app sends data with the identity (`APPLICATIONINSIGHTS_AUTHENTICATION_STRING`). |
| EP1 plan and Function App | HTTPS only, TLS 1.2, FTP and basic publishing credentials off. `WEBSITE_TIME_ZONE = Pacific Standard Time` (ADR 0016). VNet integration when a subnet is given, with all outbound traffic routed through it. |

It **references existing** resources, because the ADF factory already uses them: the Key Vault (`LS_KeyVault`) and the data storage account with the card and evidence containers (`LS_Blob_Storage`). They can be in another resource group (`sharedResourceGroupName`). A module there grants the identity **Key Vault Secrets User**. That role assumes the vault uses Azure RBAC. A vault that uses access policies needs an access policy instead.

**No Azure Files.** The app runs from a package URL in the host storage account (`WEBSITE_RUN_FROM_PACKAGE` plus `WEBSITE_RUN_FROM_PACKAGE_BLOB_MI_RESOURCE_ID`). So the content share, which needs a shared-key connection string, is not needed.

**Task hub per environment.** `AzureFunctionsJobHost__extensions__durableTask__hubName` overrides `host.json`'s `CreditUnionBatch` with `CreditUnionBatchDev`, `…Test` or `…Prod`.

### App settings follow the code as it is

Each options property that has no usable default (an empty string or a null `Uri`) is set. The ones that hold secrets are Key Vault references:

| App setting | Value |
| --- | --- |
| `KeyStoneDb2__ConnectionString` | Secret `keystone-db2-extract-connection-string` (parameter) |
| `ReportingSql__ConnectionString` | Secret `reporting-sql-writer-connection-string` (ADF name) |
| `BlobStorage__ConnectionString` | Secret `batch-blob-storage-connection-string`, which the template **writes** from the data storage account's key |
| `KeyBridge__BaseUrl` | Parameter; the ADF default is `https://keybridge.cu.internal/api` |
| `KeyBridge__Username` | Parameter, default `cu-batch` |
| `KeyBridge__Password` | Secret `keybridge-api-password` (ADF name) |
| `ReconAlertWebhook__Url` | Secret `recon-alert-webhook-url` (ADF name) |

Two of these are stopgaps that follow from open decisions, not choices of this ADR:

- **Db2 secret.** The code takes a whole connection string, so the template uses option A of ADR 0019. If option B is approved, change `KeyStoneDb2__ConnectionString` to the endpoint only and add `KeyStoneDb2__Password` referencing `keystone-db2-extract-password`.
- **Blob account key.** The code builds `BlobServiceClient` from a connection string (checklist, "Managed identity"). So the data account must allow shared-key access, and its key ends up in a Key Vault secret. After the code change to a URI and the identity, delete the secret and grant the identity **Storage Blob Data Contributor** instead.

### Out of the template

- Azure SQL, Db2, the VNet and its private DNS. These belong to the credit union's network and data platform. The template takes a subnet ID.
- Container immutability and lifecycle rules (ADR 0015, still open).
- Alerts and dashboards.
- A CI pipeline. The repository has none.

## How it is checked

- `./build.ps1 infra` compiles and lints the template with the Bicep CLI (`bicep` or `az bicep`). This needs no subscription.
- `InfrastructureTemplateTests` reads `infra/main.bicep` as text and checks:
  - every app setting for an options section names a real property;
  - every property without a default is set;
  - the secrets are the ADF secret names (Db2 excepted, above);
  - `WEBSITE_TIME_ZONE` matches the ADF trigger;
  - `functionTimeout` is longer than every ADF activity timeout.
- **Not verified.** Nothing was deployed, and `what-if` needs a subscription. Unverified items:
  - .NET 10 isolated on a Windows EP plan;
  - running from a package URL with no Azure Files on EP;
  - identity-based host storage with the Durable Azure Storage backend;
  - the Db2 driver writing `db2dsdriver.cfg` under `%TEMP%` (ADR 0009).

## Consequences

- An EP1 instance runs all the time (at least one instance is always ready), which costs more than Consumption for a job that runs once a night. Flex Consumption would cost less but needs the Linux Db2 driver to be built and tested first. Revisit then.
- Deploying the code is a separate step:
  1. Publish the Functions project and zip it.
  2. Upload the zip to the `deployments` container. This needs Storage Blob Data Contributor for whoever deploys.
  3. Restart the app.
- Whoever deploys needs rights to create role assignments (Owner, or User Access Administrator) in both resource groups.
