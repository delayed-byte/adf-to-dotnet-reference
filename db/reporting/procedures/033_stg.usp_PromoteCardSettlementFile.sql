-- Converts the detail records of a validated settlement file into typed stg.CardSettlement rows.
-- Refuses to promote a file that has validation errors. Safe to retry.
CREATE OR ALTER PROCEDURE stg.usp_PromoteCardSettlementFile
    @BusinessDate  date,
    @SourceFile    nvarchar(400)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF EXISTS (SELECT 1 FROM stg.CardSettlementValidationError WHERE BusinessDate = @BusinessDate AND SourceFile = @SourceFile)
        THROW 54000, 'Settlement file has validation errors and cannot be promoted.', 1;

    DECLARE @settlementDate date =
        (SELECT TRY_CONVERT(date, Field02, 112) FROM stg.CardSettlementRaw
         WHERE BusinessDate = @BusinessDate AND SourceFile = @SourceFile AND LTRIM(RTRIM(RecordType)) = N'H');

    IF @settlementDate IS NULL
        THROW 54001, 'Settlement file has no valid header; validate it before promoting.', 1;

    BEGIN TRANSACTION;

    DELETE FROM stg.CardSettlement WHERE BusinessDate = @BusinessDate AND SourceFile = @SourceFile;

    INSERT INTO stg.CardSettlement
        (BusinessDate, SourceFile, ProcessorRefNbr, SettlementDate, TxnDate, CardBin, CardLast4,
         Amount, TxnType, MerchantName, Mcc, CurrencyCode)
    SELECT
        @BusinessDate, @SourceFile, LTRIM(RTRIM(Field01)), @settlementDate, CONVERT(date, Field02, 112), Field03, Field04,
        CONVERT(decimal(15,2), Field05), Field06, NULLIF(Field07, N''), NULLIF(Field08, N''), Field09
    FROM stg.CardSettlementRaw
    WHERE BusinessDate = @BusinessDate AND SourceFile = @SourceFile AND LTRIM(RTRIM(RecordType)) = N'D';

    COMMIT TRANSACTION;
END;
GO
