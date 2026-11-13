# Architecture

Three views of the same system:

1. [Azure deployment](#1-azure-deployment): the resources and connections, as [infra/main.bicep](../infra/main.bicep) describes them.
2. [Nightly batch flow](#2-nightly-batch-flow): how the orchestrators run.
3. [Code structure](#3-code-structure): the projects and how they depend on each other.

The Azure view is a design. It has **never been deployed** ([ADR 0020](decisions/0020-azure-hosting-plan-and-bicep-template.md)). Locally, Docker containers stand in for every Azure service ([table at the end](#local-stand-ins)).

## 1. Azure deployment

```mermaid
flowchart LR
    subgraph rg["Batch resource group (infra/main.bicep)"]
        direction TB
        subgraph app["Function App · EP1 Windows · .NET 10 isolated"]
            timer["Timer trigger<br/>TR_Nightly_0300_Pacific<br/>03:00 Pacific Standard Time"]
            orch["Durable orchestrators<br/>PL_Master_NightlyBatch and 4 children"]
            acts["Durable activities"]
            timer --> orch --> acts
        end
        mi["User-assigned<br/>managed identity"]
        hostst[("Host storage<br/>task hub, timer lease,<br/>deployments/app.zip<br/>shared key off")]
        appi["Application Insights<br/>+ Log Analytics"]
    end

    subgraph shared["Existing shared resources (referenced)"]
        kv["Key Vault<br/>LS_KeyVault"]
        datast[("Data storage<br/>card-landing, card-archive,<br/>card-quarantine, evidence")]
    end

    subgraph cu["Credit union network (outside this repository)"]
        db2[("KeyStone Db2<br/>reporting copy<br/>TLS, port 50001")]
        sql[("Reporting SQL<br/>Azure SQL / SQL Server")]
        kb["KeyBridge API"]
    end

    hook["Alert webhook<br/>Teams or email"]

    app -.->|"runs as"| mi
    mi -->|"Blob, Queue, Table data roles"| hostst
    mi -->|"Key Vault Secrets User"| kv
    mi -->|"Metrics Publisher"| appi
    app -->|"Key Vault references<br/>for connection strings,<br/>passwords, webhook URL"| kv
    acts -->|"connection string<br/>(account key, stopgap)"| datast
    acts -->|"VNet integration<br/>read-only KSEXTRACT"| db2
    acts -->|"cu_batch_writer"| sql
    acts -->|"Basic auth, GET GL mappings"| kb
    acts -->|"POST on reconciliation breach"| hook
```

**What the arrows mean.**
- The **managed identity** reaches host storage, Key Vault and Application Insights. Nothing in that path uses a key.
- The **data storage account** is still reached with a connection string that contains the account key. That is a stopgap until the blob code uses the identity (ADR 0020).
- **Db2** needs the VNet integration subnet and TLS. Both are outside the template ([ADR 0019](decisions/0019-keystone-db2-secret.md)).

## 2. Nightly batch flow

Each ADF pipeline is an orchestrator with the same name. Each ADF activity that does I/O is a Durable activity ([ADR 0008](decisions/0008-acceptance-test-entry-points.md), [ADR 0014](decisions/0014-master-orchestration-and-failure-propagation.md)).

```mermaid
flowchart TD
    T(["Timer 03:00 Pacific<br/>businessDate = previous Pacific day"]) --> M0

    subgraph master["PL_Master_NightlyBatch"]
        M0["Log Batch Started"] --> PAR
        subgraph PAR["in parallel (Task.WhenAll)"]
            direction LR
            EX["PL_ExtractKeyStone"]
            CA["PL_IngestCardSettlement"]
        end
        PAR --> MA["PL_MapAndAggregate"]
        MA --> RE["PL_Reconciliation"]
        RE --> EV["Export Run Evidence"]
        EV --> OK["Log Batch Succeeded"]
    end

    PAR -.->|"child failed"| F["Log Failed At step<br/>then Fail: BATCH_STEP_FAILED"]
    MA -.-> F
    RE -.-> F
    EV -.-> F

    EX --- EXd["Lookup extract config<br/>ForEach table, batch 3:<br/>Db2 → SqlBulkCopy staging<br/>Merge KeyStone to core"]
    CA --- CAd["List landing files, filter<br/>ForEach file, batch 4:<br/>CSV → raw staging, validate<br/>valid → promote + archive<br/>invalid → manifest + quarantine<br/>Merge card settlement to core"]
    MA --- MAd["KeyBridge GL mapping → cache<br/>Unmapped codes → Fail TXN_CODE_UNMAPPED<br/>Aggregate daily GL totals (C#)"]
    RE --- REd["Card and GL totals in parallel<br/>Record exceptions<br/>Breach → POST alert, mark sent"]
    EV --- EVd["ops.usp_GetRunEvidence → JSON<br/>evidence/{businessDate}/batch-run-{id}.json"]

    classDef detail fill:none,stroke-dasharray:3 3
    class EXd,CAd,MAd,REd,EVd detail
```

**Rules every step follows:**
- Every activity call has the ADF retry policy: retries + 1 attempts at a fixed interval ([ADR 0008](decisions/0008-acceptance-test-entry-points.md)).
- The ADF timeout is enforced for each attempt.
- Every pipeline logs its start and its outcome to `ops.BatchRun` in the reporting store.

## 3. Code structure

```mermaid
flowchart TB
    subgraph host["CreditUnion.Batch.Functions (Azure Functions isolated worker)"]
        trig["Triggers<br/>NightlyBatchTrigger"]
        orchs["Orchestrators<br/>one per ADF pipeline"]
        acts["Activities<br/>thin wrappers"]
        di["BatchServiceCollectionExtensions<br/>composition root"]
    end

    subgraph core["CreditUnion.Batch.Core (no Azure Functions dependency)"]
        pipes["Pipelines<br/>typed inputs, PipelineNames"]
        steps["Steps and policies<br/>ADF retry and timeout values"]
        logic["Pure logic<br/>BusinessCalendar, filters,<br/>aggregator, reconciliation rules"]
        ports["Interfaces<br/>I…Store, IKeyBridgeClient,<br/>IReconAlertClient"]
        opts["Options<br/>one per ADF linked service"]
    end

    subgraph ks["CreditUnion.Batch.KeyStone (adapters)"]
        db2a["Db2StagingCopier<br/>Db2DriverConfiguration"]
        sqla["Sql…Store<br/>Microsoft.Data.SqlClient"]
        blob["AzureCardBlobStore<br/>AzureEvidenceBlobStore"]
        http["HttpKeyBridgeClient<br/>HttpReconAlertClient"]
    end

    subgraph tests["Tests"]
        harness["TestHarness<br/>in-process Durable host,<br/>Testcontainers, golden compare"]
        at["AcceptanceTests<br/>written before each pipeline"]
        ut["UnitTests<br/>ADF JSON, docs, infra consistency"]
    end

    trig --> orchs --> acts --> steps
    orchs --> pipes
    orchs --> logic
    steps --> ports
    di --> ks
    di --> opts
    ks -.->|"implements"| ports
    at --> harness --> di
    ut -.->|"reads adf/, docs/, infra/"| core
```

**Dependency direction.**
- `Core` depends on nothing in Azure Functions. The acceptance tests run the production composition root (`AddCreditUnionBatch`) inside the in-process harness ([ADR 0001](decisions/0001-hosting-model.md), [ADR 0008](decisions/0008-acceptance-test-entry-points.md)).
- Orchestrators do no I/O and read no clock ([ADR 0017](decisions/0017-orchestrator-determinism-check.md)).

## Local stand-ins

| Azure (diagram 1) | Local (`./build.ps1 up`, or Testcontainers in `./build.ps1 test`) |
| --- | --- |
| KeyStone Db2 | Db2 Community Edition container with the synthetic schema in `db/keystone-db2` |
| Reporting SQL | SQL Server 2022 Developer container with `db/reporting` |
| Host storage and data storage | Azurite |
| KeyBridge API and alert webhook | WireMock (`contracts/keybridge`) |
| Function App | Azure Functions Core Tools (`./build.ps1 run`) or the in-process test host |
| Key Vault | User-secrets and environment variables (`.env`) |
| Application Insights | Aspire dashboard container. OpenTelemetry export is not implemented yet, so it shows no batch traces. |
