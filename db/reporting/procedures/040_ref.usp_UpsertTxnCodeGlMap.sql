-- Replaces the cached transaction-code-to-GL mapping with the set returned by KeyBridge
-- (the "mappings" array of GET /v1/reference/transaction-codes/gl-mappings; see
-- contracts/keybridge/README.md). Validates the whole set before touching the cache, so a bad
-- or empty response can never wipe the mapping. Idempotent: identical input changes nothing.
CREATE OR ALTER PROCEDURE ref.usp_UpsertTxnCodeGlMap
    @MappingJson    nvarchar(max),
    @SourceVersion  varchar(50) = NULL,
    @BatchRunId     uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF ISJSON(@MappingJson) <> 1
        THROW 53000, 'KEYBRIDGE_MAPPING_INVALID: response is not valid JSON.', 1;

    DECLARE @raw TABLE (
        TransactionCode  nvarchar(50)   NULL,
        GlAccountNumber  nvarchar(50)   NULL,
        Side             nvarchar(20)   NULL,
        AllocationRatio  decimal(9,6)   NULL,
        Description      nvarchar(100)  NULL);

    INSERT INTO @raw
    SELECT transactionCode, glAccountNumber, side, allocationRatio, description
    FROM OPENJSON(@MappingJson)
    WITH (
        transactionCode  nvarchar(50)   '$.transactionCode',
        glAccountNumber  nvarchar(50)   '$.glAccountNumber',
        side             nvarchar(20)   '$.side',
        allocationRatio  decimal(9,6)   '$.allocationRatio',
        description      nvarchar(100)  '$.description');

    IF NOT EXISTS (SELECT 1 FROM @raw)
        THROW 53001, 'KEYBRIDGE_MAPPING_INVALID: mapping set is empty.', 1;

    IF EXISTS (SELECT 1 FROM @raw
               WHERE NULLIF(LTRIM(RTRIM(TransactionCode)), N'') IS NULL OR LEN(TransactionCode) > 4
                  OR NULLIF(LTRIM(RTRIM(GlAccountNumber)), N'') IS NULL OR LEN(GlAccountNumber) > 10
                  OR ISNULL(Side, N'') NOT IN (N'DEBIT', N'CREDIT')
                  OR AllocationRatio IS NULL OR AllocationRatio <= 0 OR AllocationRatio > 1)
        THROW 53002, 'KEYBRIDGE_MAPPING_INVALID: a mapping row has a missing or out-of-range field.', 1;

    DECLARE @m TABLE (
        TxnCode          char(4)        NOT NULL,
        GlAccountNbr     char(10)       NOT NULL,
        DrCr             char(1)        NOT NULL,
        AllocationRatio  decimal(9,6)   NOT NULL,
        Description      nvarchar(100)  NULL,
        PRIMARY KEY (TxnCode, GlAccountNbr, DrCr));

    IF EXISTS (SELECT 1 FROM @raw GROUP BY TransactionCode, GlAccountNumber, Side HAVING COUNT(*) > 1)
        THROW 53003, 'KEYBRIDGE_MAPPING_INVALID: duplicate (transactionCode, glAccountNumber, side) rows.', 1;

    INSERT INTO @m (TxnCode, GlAccountNbr, DrCr, AllocationRatio, Description)
    SELECT LTRIM(RTRIM(TransactionCode)), LTRIM(RTRIM(GlAccountNumber)), CASE Side WHEN N'DEBIT' THEN 'D' ELSE 'C' END,
           AllocationRatio, Description
    FROM @raw;

    -- Every code needs both a debit and a credit side, each allocating exactly 100%.
    IF EXISTS (
        SELECT 1
        FROM (SELECT DISTINCT TxnCode FROM @m) AS c
        CROSS JOIN (VALUES ('D'), ('C')) AS s(DrCr)
        LEFT JOIN (SELECT TxnCode, DrCr, SUM(AllocationRatio) AS Total FROM @m GROUP BY TxnCode, DrCr) AS t
            ON t.TxnCode = c.TxnCode AND t.DrCr = s.DrCr
        WHERE ISNULL(t.Total, 0) <> 1)
        THROW 53004, 'KEYBRIDGE_MAPPING_INVALID: allocation ratios for a code and side must sum to 1.', 1;

    DECLARE @now datetime2(3) = SYSUTCDATETIME();

    BEGIN TRANSACTION;

    MERGE ref.TxnCodeGlMap WITH (HOLDLOCK) AS t
    USING @m AS s
        ON t.TxnCode = s.TxnCode AND t.GlAccountNbr = s.GlAccountNbr AND t.DrCr = s.DrCr
    WHEN MATCHED AND EXISTS (SELECT s.AllocationRatio, s.Description EXCEPT SELECT t.AllocationRatio, t.Description)
        THEN UPDATE SET AllocationRatio = s.AllocationRatio, Description = s.Description,
                        SourceVersion = @SourceVersion, RefreshedBatchRunId = @BatchRunId, RefreshedUtc = @now
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (TxnCode, GlAccountNbr, DrCr, AllocationRatio, Description, SourceVersion, RefreshedBatchRunId, RefreshedUtc)
        VALUES (s.TxnCode, s.GlAccountNbr, s.DrCr, s.AllocationRatio, s.Description, @SourceVersion, @BatchRunId, @now)
    WHEN NOT MATCHED BY SOURCE THEN DELETE;

    COMMIT TRANSACTION;
END;
GO
