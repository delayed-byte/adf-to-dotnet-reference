# Progress

## Phase 0 – Scaffold (2026-11-05)

- Solution `AdfToDotnet.sln` with `CreditUnion.Batch.Core`, `.KeyStone`, `.Functions` and `.UnitTests`, `.AcceptanceTests` (xUnit v3, Shouldly), .NET 10.
- `Directory.Build.props`: nullable, warnings as errors, analyzers; `Directory.Packages.props`: central pinned versions.
- `docker-compose.yml`: Db2 12.1.0.0, SQL Server 2022 CU14, Azurite 3.33.0, WireMock 3.9.1, Aspire dashboard 9.0.
- `build.ps1` (`up | down | build | seed | test | run | scan`), `.gitattributes`, `.gitignore`, `CLAUDE.md`, `README.md`, ADR 0001.
- Repository folder layout per the PRD created (`adf/`, `db/`, `contracts/`, `tests/TestData/`).
- Db2 runs without a persistent volume: a recreated container cannot re-initialise an existing `/database` volume (`db2icrt` fails), so it starts fresh on every `up` and is re-seeded by script. SQL Server and Azurite keep volumes.
- Deviation: `docs/PRD.md` is a copy of the original PRD file (whose name contains a space).
