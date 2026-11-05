-- Staging tables for KeyStone extracts mirror the Db2 column names and types exactly
-- (lossless: CHAR(n) -> char(n), DECIMAL(p,s) -> decimal(p,s), TIMESTAMP(6) -> datetime2(6)).
-- They are truncated before each copy.
CREATE TABLE stg.KsAccount (
    ACCOUNT_NBR         char(12)       NOT NULL,
    MEMBER_NBR          char(10)       NOT NULL,
    ACCOUNT_CATEGORY    char(1)        NOT NULL,
    PRODUCT_CODE        char(6)        NOT NULL,
    DESCRIPTION         varchar(60)    NULL,
    OPEN_DATE           date           NOT NULL,
    CLOSE_DATE          date           NULL,
    CURRENT_BALANCE     decimal(15,2)  NOT NULL,
    AVAILABLE_BALANCE   decimal(15,2)  NULL,
    INTEREST_RATE       decimal(9,6)   NOT NULL,
    ACCRUED_INTEREST    decimal(31,8)  NOT NULL,
    STATUS              char(1)        NOT NULL,
    LAST_ACTIVITY_DATE  date           NOT NULL,
    LAST_MAINT_TS       datetime2(6)   NOT NULL
);
GO

CREATE TABLE stg.KsPostedTxn (
    TXN_ID              bigint         NOT NULL,
    ACCOUNT_NBR         char(12)       NOT NULL,
    TXN_CODE            char(4)        NOT NULL,
    POSTING_DATE        date           NOT NULL,
    EFFECTIVE_DATE      date           NOT NULL,
    POSTED_TS           datetime2(6)   NOT NULL,
    AMOUNT              decimal(15,2)  NOT NULL,
    DESCRIPTION         varchar(60)    NULL,
    CHANNEL             char(3)        NOT NULL,
    CARD_PROCESSOR_REF  varchar(24)    NULL,
    REVERSAL_OF_TXN_ID  bigint         NULL
);
GO

CREATE TABLE stg.KsGlEntry (
    GL_ENTRY_ID     bigint         NOT NULL,
    GL_ACCOUNT_NBR  char(10)       NOT NULL,
    POSTING_DATE    date           NOT NULL,
    DEBIT_AMOUNT    decimal(15,2)  NOT NULL,
    CREDIT_AMOUNT   decimal(15,2)  NOT NULL,
    SOURCE_CODE     char(4)        NOT NULL,
    BATCH_REF       varchar(20)    NULL,
    MEMO            varchar(80)    NULL,
    CREATED_TS      datetime2(6)   NOT NULL
);
GO

-- Card settlement files land here untyped (every field as text), one row per record,
-- so that validation can report every problem instead of failing on the first bad value.
-- File format: contracts/card-processor/settlement-file-format.md
CREATE TABLE stg.CardSettlementRaw (
    BusinessDate  date           NOT NULL,
    SourceFile    nvarchar(400)  NOT NULL,
    RecordType    nvarchar(10)   NULL,
    Field01       nvarchar(200)  NULL,
    Field02       nvarchar(200)  NULL,
    Field03       nvarchar(200)  NULL,
    Field04       nvarchar(200)  NULL,
    Field05       nvarchar(200)  NULL,
    Field06       nvarchar(200)  NULL,
    Field07       nvarchar(200)  NULL,
    Field08       nvarchar(200)  NULL,
    Field09       nvarchar(200)  NULL,
    LoadedUtc     datetime2(3)   NOT NULL CONSTRAINT DF_CardSettlementRaw_LoadedUtc DEFAULT (SYSUTCDATETIME())
);
GO
CREATE INDEX IX_CardSettlementRaw_File ON stg.CardSettlementRaw (BusinessDate, SourceFile);
GO

CREATE TABLE stg.CardSettlementValidationError (
    BusinessDate     date              NOT NULL,
    SourceFile       nvarchar(400)     NOT NULL,
    BatchRunId       uniqueidentifier  NOT NULL,
    RecordType       nvarchar(10)      NULL,
    ProcessorRefNbr  nvarchar(200)     NULL,
    ErrorCode        varchar(50)       NOT NULL,
    ErrorMessage     nvarchar(1000)    NOT NULL
);
GO
CREATE INDEX IX_CardSettlementValidationError_File ON stg.CardSettlementValidationError (BusinessDate, SourceFile);
GO

-- Typed rows from files that passed validation.
CREATE TABLE stg.CardSettlement (
    BusinessDate     date           NOT NULL,
    SourceFile       nvarchar(400)  NOT NULL,
    ProcessorRefNbr  varchar(24)    NOT NULL,
    SettlementDate   date           NOT NULL,
    TxnDate          date           NOT NULL,
    CardBin          char(6)        NOT NULL,
    CardLast4        char(4)        NOT NULL,
    Amount           decimal(15,2)  NOT NULL,
    TxnType          char(4)        NOT NULL,
    MerchantName     nvarchar(60)   NULL,
    Mcc              char(4)        NULL,
    CurrencyCode     char(3)        NOT NULL
);
GO
