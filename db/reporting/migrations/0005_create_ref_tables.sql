-- Transaction-code-to-GL mapping cached from KeyBridge (contracts/keybridge).
-- A transaction code maps to one or more GL accounts per side (D = debit, C = credit);
-- AllocationRatio values for a code and side sum to exactly 1. The derived amount for a
-- transaction is Amount * AllocationRatio rounded to 2 decimals with banker's rounding.
CREATE TABLE ref.TxnCodeGlMap (
    TxnCode          char(4)           NOT NULL,
    GlAccountNbr     char(10)          NOT NULL,
    DrCr             char(1)           NOT NULL,
    AllocationRatio  decimal(9,6)      NOT NULL,
    Description      nvarchar(100)     NULL,
    SourceVersion    varchar(50)       NULL,
    RefreshedBatchRunId uniqueidentifier NOT NULL,
    RefreshedUtc     datetime2(3)      NOT NULL,
    CONSTRAINT PK_TxnCodeGlMap PRIMARY KEY (TxnCode, GlAccountNbr, DrCr),
    CONSTRAINT CK_TxnCodeGlMap_DrCr CHECK (DrCr IN ('D', 'C')),
    CONSTRAINT CK_TxnCodeGlMap_Ratio CHECK (AllocationRatio > 0 AND AllocationRatio <= 1)
);
GO
