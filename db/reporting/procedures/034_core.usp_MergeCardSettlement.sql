-- Upserts validated settlement rows for a business date into core.CardSettlement, keyed on
-- the processor reference number. Idempotent: unchanged rows are not touched.
CREATE OR ALTER PROCEDURE core.usp_MergeCardSettlement
    @BusinessDate  date,
    @BatchRunId    uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF EXISTS (SELECT 1 FROM stg.CardSettlement WHERE BusinessDate = @BusinessDate
               GROUP BY ProcessorRefNbr HAVING COUNT(*) > 1)
        THROW 54100, 'The same processor reference number appears in more than one settlement file.', 1;

    DECLARE @now datetime2(3) = SYSUTCDATETIME();
    DECLARE @actions TABLE (MergeAction nvarchar(10) NOT NULL);
    DECLARE @staged bigint = (SELECT COUNT_BIG(*) FROM stg.CardSettlement WHERE BusinessDate = @BusinessDate);

    BEGIN TRANSACTION;

    MERGE core.CardSettlement WITH (HOLDLOCK) AS t
    USING (SELECT * FROM stg.CardSettlement WHERE BusinessDate = @BusinessDate) AS s
        ON t.ProcessorRefNbr = s.ProcessorRefNbr
    WHEN MATCHED AND EXISTS (
            SELECT s.SettlementDate, s.TxnDate, s.CardBin, s.CardLast4, s.Amount, s.TxnType,
                   s.MerchantName, s.Mcc, s.CurrencyCode, s.SourceFile
            EXCEPT
            SELECT t.SettlementDate, t.TxnDate, t.CardBin, t.CardLast4, t.Amount, t.TxnType,
                   t.MerchantName, t.Mcc, t.CurrencyCode, t.SourceFile)
        THEN UPDATE SET
            SettlementDate = s.SettlementDate, TxnDate = s.TxnDate, CardBin = s.CardBin, CardLast4 = s.CardLast4,
            Amount = s.Amount, TxnType = s.TxnType, MerchantName = s.MerchantName, Mcc = s.Mcc,
            CurrencyCode = s.CurrencyCode, SourceFile = s.SourceFile, LoadedBatchRunId = @BatchRunId, LoadedUtc = @now
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (ProcessorRefNbr, SettlementDate, TxnDate, CardBin, CardLast4, Amount, TxnType, MerchantName,
                Mcc, CurrencyCode, SourceFile, LoadedBatchRunId, LoadedUtc)
        VALUES (s.ProcessorRefNbr, s.SettlementDate, s.TxnDate, s.CardBin, s.CardLast4, s.Amount, s.TxnType, s.MerchantName,
                s.Mcc, s.CurrencyCode, s.SourceFile, @BatchRunId, @now)
    OUTPUT $action INTO @actions;

    DECLARE @ins bigint = (SELECT COUNT_BIG(*) FROM @actions WHERE MergeAction = 'INSERT');
    DECLARE @upd bigint = (SELECT COUNT_BIG(*) FROM @actions WHERE MergeAction = 'UPDATE');
    DECLARE @notes nvarchar(2000) = CONCAT(N'staged=', @staged, N'; inserted=', @ins, N'; updated=', @upd, N'; unchanged=', @staged - @ins - @upd);

    EXEC ops.usp_LogBatchRunEvent @BatchRunId = @BatchRunId, @BusinessDate = @BusinessDate, @PipelineName = 'PL_IngestCardSettlement',
        @Status = 'Succeeded', @StepName = 'MergeCardSettlement', @ObjectName = N'core.CardSettlement', @RecordCount = @staged, @Notes = @notes;

    COMMIT TRANSACTION;
END;
GO
