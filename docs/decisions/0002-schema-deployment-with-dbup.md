# 0002. Schema deployment with DbUp, including a small Db2 provider

Status: Accepted · Date: 2026-11-05

## Context

The PRD names DbUp for both the SQL Server reporting store and the Db2 seed. DbUp ships a SQL Server provider (`dbup-sqlserver`) but none for IBM Db2. Db2 also authenticates against operating-system accounts, so the read-only extract login cannot be created by SQL alone.

## Decision

- `tools/CreditUnion.Batch.DbDeploy` deploys both databases. `./build.ps1 seed` runs it with connection details built from `.env`. The Phase 2 test fixtures call the same `ReportingDatabaseDeployer` and `KeyStoneDb2Deployer` classes.
- **SQL Server** (`db/reporting`): `migrations/` run once and are journaled in `dbo.SchemaVersions`. `procedures/` (`CREATE OR ALTER`) and `security/` (idempotent logins, roles and grants) run on every deployment. A numeric prefix orders scripts within a folder. Login passwords are DbUp variables (`$WriterPassword$`, `$ReaderPassword$`) supplied from the environment.
- **Db2** (`db/keystone-db2`): a minimal provider on `dbup-core` (connection manager, script executor, journal `DBUP.SCHEMA_VERSIONS`) using `Net.IBM.Data.Db2`. Statements end with `;`, and `Db2ScriptSplitter` ignores semicolons in literals and comments. Compound statements are deliberately unsupported to keep the splitter trivial.
- The OS account behind `KSEXTRACT` is created by `db/keystone-db2/custom/create_extract_user.sh`, which the Db2 image runs from `/var/custom` after creating the database. Its password comes from the `KS_EXTRACT_PASSWORD` container variable.
- The Db2 container has no persistent volume (see the Phase 0 notes), so every `up` starts from an empty database.

## Consequences

- One command builds both databases in fresh containers, and re-running it is safe.
- The Db2 provider is ours to maintain (about 120 lines, unit-tested splitter).
- The Db2 Community image grants no database authorities to `PUBLIC`, so no `REVOKE` is needed. A database that does grant them would need a revoke before the read-only guarantee holds; AT-19 checks it.
- When the repo is committed, `create_extract_user.sh` should carry the executable bit (`git update-index --chmod=+x`) for containers that copy rather than bind-mount it.
