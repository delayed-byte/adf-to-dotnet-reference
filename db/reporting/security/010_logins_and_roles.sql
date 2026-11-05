-- Least-privilege access to the reporting store (NCUA Part 748 App. A alignment).
--   cu_batch_writer : used by the batch; writes stg/core/ref, appends to ops, runs procedures
--   cu_report_reader: read-only consumers; any write is rejected
-- Run on every deployment. Passwords are injected by the deployer from the environment
-- ($WriterPassword$ / $ReaderPassword$) and are never committed.

IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'cu_batch_writer')
    CREATE LOGIN cu_batch_writer WITH PASSWORD = N'$WriterPassword$', CHECK_POLICY = ON, DEFAULT_DATABASE = [$DatabaseName$];
ELSE
    ALTER LOGIN cu_batch_writer WITH PASSWORD = N'$WriterPassword$';
GO

IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'cu_report_reader')
    CREATE LOGIN cu_report_reader WITH PASSWORD = N'$ReaderPassword$', CHECK_POLICY = ON, DEFAULT_DATABASE = [$DatabaseName$];
ELSE
    ALTER LOGIN cu_report_reader WITH PASSWORD = N'$ReaderPassword$';
GO

IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'cu_batch_writer')
    CREATE USER cu_batch_writer FOR LOGIN cu_batch_writer;
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'cu_report_reader')
    CREATE USER cu_report_reader FOR LOGIN cu_report_reader;
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'batch_writer' AND type = 'R')
    CREATE ROLE batch_writer;
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'report_reader' AND type = 'R')
    CREATE ROLE report_reader;
GO

ALTER ROLE batch_writer ADD MEMBER cu_batch_writer;
ALTER ROLE report_reader ADD MEMBER cu_report_reader;
GO

-- Writer: full DML on stg/core/ref, ALTER on stg only (TRUNCATE before copy), append-only on ops.
GRANT SELECT, INSERT, UPDATE, DELETE ON SCHEMA::stg TO batch_writer;
GRANT ALTER ON SCHEMA::stg TO batch_writer;
GRANT SELECT, INSERT, UPDATE, DELETE ON SCHEMA::core TO batch_writer;
GRANT SELECT, INSERT, UPDATE, DELETE ON SCHEMA::ref TO batch_writer;
GRANT SELECT, INSERT ON SCHEMA::ops TO batch_writer;
GRANT UPDATE ON ops.ReconExceptions TO batch_writer;
GRANT EXECUTE ON SCHEMA::ops TO batch_writer;
GRANT EXECUTE ON SCHEMA::stg TO batch_writer;
GRANT EXECUTE ON SCHEMA::core TO batch_writer;
GRANT EXECUTE ON SCHEMA::ref TO batch_writer;
DENY UPDATE, DELETE ON ops.BatchRun TO batch_writer;
DENY UPDATE, DELETE ON ops.CardFileLog TO batch_writer;
DENY DELETE ON ops.ReconExceptions TO batch_writer;
DENY UPDATE, DELETE, INSERT ON ops.ExtractConfig TO batch_writer;
DENY UPDATE, DELETE, INSERT ON ops.ReconSignRule TO batch_writer;
DENY UPDATE, DELETE, INSERT ON ops.RetentionPolicy TO batch_writer;
GO

-- Reader: SELECT on published schemas; every write explicitly denied.
GRANT SELECT ON SCHEMA::core TO report_reader;
GRANT SELECT ON SCHEMA::ref TO report_reader;
GRANT SELECT ON SCHEMA::ops TO report_reader;
DENY INSERT, UPDATE, DELETE, ALTER, EXECUTE ON SCHEMA::core TO report_reader;
DENY INSERT, UPDATE, DELETE, ALTER, EXECUTE ON SCHEMA::ref TO report_reader;
DENY INSERT, UPDATE, DELETE, ALTER, EXECUTE ON SCHEMA::ops TO report_reader;
DENY SELECT, INSERT, UPDATE, DELETE, ALTER, EXECUTE ON SCHEMA::stg TO report_reader;
GO
