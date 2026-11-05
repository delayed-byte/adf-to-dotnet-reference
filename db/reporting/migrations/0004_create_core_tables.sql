-- Conformed tables keyed on source keys. Merges are idempotent: an identical rerun changes
-- nothing (including the audit columns), because rows are only updated when a business
-- column actually differs.
CREATE TABLE core.Account (
    AccountNbr            char(12)          NOT NULL CONSTRAINT PK_Account PRIMARY KEY,
    MemberNbr             char(10)          NOT NULL,
    AccountCategory       char(1)           NOT NULL,
    ProductCode           char(6)           NOT NULL,
    Description           varchar(60)       NULL,
    OpenDate              date              NOT NULL,
    CloseDate             date              NULL,
    CurrentBalance        decimal(15,2)     NOT NULL,
    AvailableBalance      decimal(15,2)     NULL,
    InterestRate          decimal(9,6)      NOT NULL,
    AccruedInterest       decimal(31,8)     NOT NULL,
    Status                char(1)           NOT NULL,
    LastActivityDate      date              NOT NULL,
    LastMaintTs           datetime2(6)      NOT NULL,
    LoadedBatchRunId      uniqueidentifier  NOT NULL,
    LoadedUtc             datetime2(3)      NOT NULL
);
GO

CREATE TABLE core.PostedTransaction (
    TxnId                 bigint            NOT NULL CONSTRAINT PK_PostedTransaction PRIMARY KEY,
    AccountNbr            char(12)          NOT NULL,
    TxnCode               char(4)           NOT NULL,
    PostingDate           date              NOT NULL,
    EffectiveDate         date              NOT NULL,
    PostedTs              datetime2(6)      NOT NULL,
    Amount                decimal(15,2)     NOT NULL,
    Description           varchar(60)       NULL,
    Channel               char(3)           NOT NULL,
    CardProcessorRef      varchar(24)       NULL,
    ReversalOfTxnId       bigint            NULL,
    LoadedBatchRunId      uniqueidentifier  NOT NULL,
    LoadedUtc             datetime2(3)      NOT NULL
);
GO
CREATE INDEX IX_PostedTransaction_PostingDate ON core.PostedTransaction (PostingDate, TxnCode) INCLUDE (Amount);
GO

CREATE TABLE core.GlEntry (
    GlEntryId             bigint            NOT NULL CONSTRAINT PK_GlEntry PRIMARY KEY,
    GlAccountNbr          char(10)          NOT NULL,
    PostingDate           date              NOT NULL,
    DebitAmount           decimal(15,2)     NOT NULL,
    CreditAmount          decimal(15,2)     NOT NULL,
    SourceCode            char(4)           NOT NULL,
    BatchRef              varchar(20)       NULL,
    Memo                  varchar(80)       NULL,
    CreatedTs             datetime2(6)      NOT NULL,
    LoadedBatchRunId      uniqueidentifier  NOT NULL,
    LoadedUtc             datetime2(3)      NOT NULL
);
GO
CREATE INDEX IX_GlEntry_PostingDate ON core.GlEntry (PostingDate, GlAccountNbr) INCLUDE (DebitAmount, CreditAmount);
GO

-- Card settlement detail, keyed on the processor reference number.
CREATE TABLE core.CardSettlement (
    ProcessorRefNbr       varchar(24)       NOT NULL CONSTRAINT PK_CardSettlement PRIMARY KEY,
    SettlementDate        date              NOT NULL,
    TxnDate               date              NOT NULL,
    CardBin               char(6)           NOT NULL,
    CardLast4             char(4)           NOT NULL,
    Amount                decimal(15,2)     NOT NULL,
    TxnType               char(4)           NOT NULL,
    MerchantName          nvarchar(60)      NULL,
    Mcc                   char(4)           NULL,
    CurrencyCode          char(3)           NOT NULL,
    SourceFile            nvarchar(400)     NOT NULL,
    LoadedBatchRunId      uniqueidentifier  NOT NULL,
    LoadedUtc             datetime2(3)      NOT NULL
);
GO
CREATE INDEX IX_CardSettlement_SettlementDate ON core.CardSettlement (SettlementDate, TxnType) INCLUDE (Amount);
GO

-- Output of the DF_MapAndAggregateGl data flow: one row per business date and GL account.
-- NetAmount = DebitTotal - CreditTotal.
CREATE TABLE core.DailyGlTotals (
    BusinessDate          date              NOT NULL,
    GlAccountNbr          char(10)          NOT NULL,
    DebitTotal            decimal(19,2)     NOT NULL,
    CreditTotal           decimal(19,2)     NOT NULL,
    NetAmount             decimal(19,2)     NOT NULL,
    TxnCount              int               NOT NULL,
    CONSTRAINT PK_DailyGlTotals PRIMARY KEY (BusinessDate, GlAccountNbr)
);
GO
