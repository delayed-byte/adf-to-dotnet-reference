-- Reporting and reconciliation store (Azure SQL in production, SQL Server 2022 locally).
--   ops  : run control, audit trail, configuration, reconciliation exceptions
--   stg  : transient landing tables, one per source; reloaded every run
--   core : conformed data keyed on source keys (idempotent upserts)
--   ref  : reference data cached from KeyBridge
CREATE SCHEMA ops AUTHORIZATION dbo;
GO
CREATE SCHEMA stg AUTHORIZATION dbo;
GO
CREATE SCHEMA core AUTHORIZATION dbo;
GO
CREATE SCHEMA ref AUTHORIZATION dbo;
GO
