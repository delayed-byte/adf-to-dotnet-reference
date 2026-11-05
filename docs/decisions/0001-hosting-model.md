# 0001. Hosting model: Azure Durable Functions (isolated worker)

Status: Accepted · Date: 2026-11-05

## Context

Each ADF pipeline must become code that runs locally with no Azure subscription and can move to Azure later by changing configuration only.

## Decision

- Pipelines become Durable Functions orchestrators (isolated worker, .NET 10, Functions v4); activities become Durable activity functions that delegate to plain service classes in `CreditUnion.Batch.Core`.
- Locally the host runs under Azure Functions Core Tools with Azurite as the Durable storage backend.
- Business logic has no Functions dependency, so it also runs in the in-process test harness.
- Package versions are managed centrally (`Directory.Packages.props`); container image tags are pinned in `docker-compose.yml`.

## Consequences

- Orchestrators must stay deterministic; enforced by analyzers and a replay unit test (Phase 8).
- Moving to Azure means replacing connection strings and adding a Key Vault configuration provider.
