-- Configuration for PL_ExtractKeyStone: one row per KeyStone source table.
-- All Db2 table and column names live here (not in code) so remapping to the real
-- KeyStone schema is a data change.
CREATE TABLE ops.ExtractConfig (
    ExtractConfigId  int            NOT NULL CONSTRAINT PK_ExtractConfig PRIMARY KEY,
    SourceName       varchar(50)    NOT NULL CONSTRAINT UQ_ExtractConfig_SourceName UNIQUE,
    SourceSchema     varchar(30)    NOT NULL,
    SourceTable      varchar(60)    NOT NULL,
    SourceColumns    varchar(2000)  NOT NULL,
    KeyColumns       varchar(200)   NOT NULL,
    DateColumn       varchar(60)    NOT NULL,
    TargetSchema     varchar(30)    NOT NULL,
    TargetTable      varchar(60)    NOT NULL,
    SortOrder        int            NOT NULL,
    IsEnabled        bit            NOT NULL CONSTRAINT DF_ExtractConfig_IsEnabled DEFAULT (1)
);
GO

-- Append-only audit trail. Each run writes one row per event (start, per-step row counts,
-- outcome). A run is identified by BatchRunId; nothing is ever updated or deleted.
CREATE TABLE ops.BatchRun (
    BatchRunEventId  bigint            IDENTITY(1,1) NOT NULL CONSTRAINT PK_BatchRun PRIMARY KEY,
    BatchRunId       uniqueidentifier  NOT NULL,
    BusinessDate     date              NOT NULL,
    PipelineName     varchar(100)      NOT NULL,
    StepName         varchar(100)      NULL,
    Status           varchar(30)       NOT NULL,
    ObjectName       nvarchar(400)     NULL,
    RecordCount      bigint            NULL,
    ErrorCode        varchar(50)       NULL,
    ErrorMessage     nvarchar(2000)    NULL,
    Notes            nvarchar(2000)    NULL,
    StartedUtc       datetime2(3)      NULL,
    EndedUtc         datetime2(3)      NULL,
    RecordedUtc      datetime2(3)      NOT NULL CONSTRAINT DF_BatchRun_RecordedUtc DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT CK_BatchRun_Status CHECK (Status IN ('Started', 'Succeeded', 'Failed', 'Reconciled', 'Exceptions'))
);
GO
CREATE INDEX IX_BatchRun_BatchRunId ON ops.BatchRun (BatchRunId, BatchRunEventId);
CREATE INDEX IX_BatchRun_BusinessDate ON ops.BatchRun (BusinessDate, BatchRunEventId);
GO
CREATE TRIGGER ops.TR_BatchRun_AppendOnly ON ops.BatchRun
INSTEAD OF UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    THROW 51000, 'ops.BatchRun is append-only; updates and deletes are not permitted.', 1;
END;
GO

-- Append-only record of every settlement file received and what happened to it.
CREATE TABLE ops.CardFileLog (
    CardFileLogId       bigint            IDENTITY(1,1) NOT NULL CONSTRAINT PK_CardFileLog PRIMARY KEY,
    BatchRunId          uniqueidentifier  NOT NULL,
    BusinessDate        date              NOT NULL,
    SourceFile          nvarchar(400)     NOT NULL,
    Outcome             varchar(20)       NOT NULL,
    DetailRowCount      int               NULL,
    TrailerRowCount     int               NULL,
    DetailTotalAmount   decimal(19,2)     NULL,
    TrailerTotalAmount  decimal(19,2)     NULL,
    ErrorSummary        nvarchar(2000)    NULL,
    DestinationPath     nvarchar(1000)    NULL,
    RecordedUtc         datetime2(3)      NOT NULL CONSTRAINT DF_CardFileLog_RecordedUtc DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT CK_CardFileLog_Outcome CHECK (Outcome IN ('Archived', 'Quarantined'))
);
GO
CREATE INDEX IX_CardFileLog_BusinessDate ON ops.CardFileLog (BusinessDate, SourceFile);
GO
CREATE TRIGGER ops.TR_CardFileLog_AppendOnly ON ops.CardFileLog
INSTEAD OF UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    THROW 51001, 'ops.CardFileLog is append-only; updates and deletes are not permitted.', 1;
END;
GO

-- Reconciliation exceptions. One row per distinct (date, check, item, difference): a rerun
-- that finds the same difference does not add a row or re-alert. Only the alert columns may
-- change after insert, and rows are never deleted.
CREATE TABLE ops.ReconExceptions (
    ReconExceptionId    bigint            IDENTITY(1,1) NOT NULL CONSTRAINT PK_ReconExceptions PRIMARY KEY,
    BusinessDate        date              NOT NULL,
    CheckName           varchar(30)       NOT NULL,
    ItemKey             varchar(50)       NOT NULL,
    KeyStoneAmount      decimal(19,2)     NOT NULL,
    ComparedAmount      decimal(19,2)     NOT NULL,
    Difference          decimal(19,2)     NOT NULL,
    Tolerance           decimal(19,2)     NOT NULL,
    Description         nvarchar(400)     NULL,
    DetectedBatchRunId  uniqueidentifier  NOT NULL,
    DetectedUtc         datetime2(3)      NOT NULL CONSTRAINT DF_ReconExceptions_DetectedUtc DEFAULT (SYSUTCDATETIME()),
    AlertBatchRunId     uniqueidentifier  NULL,
    AlertSentUtc        datetime2(3)      NULL,
    CONSTRAINT CK_ReconExceptions_CheckName CHECK (CheckName IN ('CardSettlement', 'GeneralLedger')),
    CONSTRAINT UQ_ReconExceptions UNIQUE (BusinessDate, CheckName, ItemKey, Difference)
);
GO
CREATE TRIGGER ops.TR_ReconExceptions_Protect ON ops.ReconExceptions
AFTER UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM inserted)
        THROW 51002, 'ops.ReconExceptions rows cannot be deleted.', 1;
    IF UPDATE(BusinessDate) OR UPDATE(CheckName) OR UPDATE(ItemKey) OR UPDATE(KeyStoneAmount)
       OR UPDATE(ComparedAmount) OR UPDATE(Difference) OR UPDATE(Tolerance) OR UPDATE(Description)
       OR UPDATE(DetectedBatchRunId) OR UPDATE(DetectedUtc)
        THROW 51003, 'Only the alert columns of ops.ReconExceptions can be updated.', 1;
END;
GO

-- Sign rules for the card reconciliation: which KeyStone transaction codes and which
-- settlement transaction types count, and in which direction.
CREATE TABLE ops.ReconSignRule (
    RuleSource  varchar(20)  NOT NULL,
    Code        char(4)      NOT NULL,
    Sign        smallint     NOT NULL,
    CONSTRAINT PK_ReconSignRule PRIMARY KEY (RuleSource, Code),
    CONSTRAINT CK_ReconSignRule_Source CHECK (RuleSource IN ('KeyStone', 'Settlement')),
    CONSTRAINT CK_ReconSignRule_Sign CHECK (Sign IN (-1, 1))
);
GO

-- Records preservation (NCUA Part 749): retention periods come from the credit union's own
-- policy and are not yet known. Automatic deletion is disabled in v1 by constraint.
CREATE TABLE ops.RetentionPolicy (
    ObjectName         nvarchar(200)  NOT NULL CONSTRAINT PK_RetentionPolicy PRIMARY KEY,
    RetentionPeriod    varchar(50)    NULL,
    PolicyReference    nvarchar(400)  NOT NULL,
    AutoDeleteEnabled  bit            NOT NULL CONSTRAINT DF_RetentionPolicy_AutoDelete DEFAULT (0),
    CONSTRAINT CK_RetentionPolicy_NoAutoDelete CHECK (AutoDeleteEnabled = 0)
);
GO
